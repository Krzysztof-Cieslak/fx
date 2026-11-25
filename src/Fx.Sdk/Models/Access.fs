namespace Fx.Sdk

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Models

/// The model-egress hooks in effect (spec §18): every request bound for a model passes the
/// transforms (in priority order), then the gates see what would leave.
[<NoEquality; NoComparison>]
type EgressHooks =
    {
        Transforms: Contributed<Transform<ChatRequest>> list
        Gates: Contributed<Gate<ChatRequest>> list
    }

    override this.ToString() =
        $"egress({this.Transforms.Length} transforms, {this.Gates.Length} gates)"

/// The model-egress hook points (spec §16.2 "model-egress hooks", §18). Privileged: only extensions
/// from host config, policy or the binary hook into them. T124 adds the built-in content exclusions.
[<RequireQualifiedAccess>]
module ModelEgress =
    /// Rewrites every request before it leaves for a model (content exclusions, redaction). A
    /// transform that fails stops the request: nothing leaves untransformed.
    let transforms: ContributionType<Transform<ChatRequest>> =
        Hook.transform
            "model-egress"
            "Rewrites every request before it leaves for a model (content exclusions, redaction)."
            ChatRequest.codec
        |> ContributionType.privileged

    /// Decides whether a request may leave for a model, seeing it after the transforms. Ask or Deny
    /// stops it (`ChatError.EgressBlocked`); a gate that fails denies.
    let gates: ContributionType<Gate<ChatRequest>> =
        Hook.gate
            "model-egress"
            "Decides whether a request may leave for a model, after the transforms."
            ChatRequest.codec
        |> ContributionType.privileged

    /// No hooks: what a host without egress extensions runs (the pipeline still runs).
    let none: EgressHooks = { Transforms = []; Gates = [] }

    /// Runs the hooks: the request a wire may get, or why it may not leave.
    let internal clear<'R when 'R :> IScopeWith<'R>>
        (hooks: EgressHooks)
        (request: ChatRequest)
        : Eff<'R, Result<WireRequest, ChatError>> =
        eff {
            let! transformed = Eff.catch (Hooks.transform hooks.Transforms request)
            let! scope = Eff.scope

            match transformed with
            | Error(:? OperationCanceledException as e) when scope.Cancellation.IsCancellationRequested ->
                return raise e
            | Error(:? ContributionFailedException as e) ->
                return Error(EgressBlocked { Ask = false; Reasons = [ e.Message ] })
            | Error e -> return raise e
            | Ok request ->
                match! Hooks.decide hooks.Gates request with
                | Verdict.Allow -> return Ok(WireRequest request)
                | Verdict.Ask reasons ->
                    return
                        Error(
                            EgressBlocked
                                {
                                    Ask = true
                                    Reasons = reasons |> List.map string
                                }
                        )
                | Verdict.Deny reasons ->
                    return
                        Error(
                            EgressBlocked
                                {
                                    Ask = false
                                    Reasons = reasons |> List.map string
                                }
                        )
        }

/// The host's access to models: what `ModelAccess.stream` needs. The composition root builds it from
/// the contributions in effect (`ChatWires`, `ProviderDefs`, `ModelEgress`) and the host's HTTP and
/// credentials; sessions, agents and extensions get only the `Llm` made from it.
[<NoEquality; NoComparison>]
type ModelAccess =
    {
        Wires: ChatWire list
        Providers: ProviderDef list
        Egress: EgressHooks
        /// The host's HTTP client.
        Http: Http
        /// Resolves a provider's credentials for one call (spec §11.3; the auth methods are T121).
        Auth: ProviderDef -> CancellationToken -> Task<Result<ResolvedAuth, ChatError>>
        /// Time limits for every request.
        Timeouts: HttpTimeouts
        /// Which provider state may be replayed to which model.
        Replay: ReplayPolicy
        Clock: Clock
        Trace: Trace
    }

    override this.ToString() =
        $"ModelAccess({this.Wires.Length} wires, {this.Providers.Length} providers, {this.Egress})"

/// The single entry point to models (spec §10.3, §18). For each request it finds the provider and the
/// model's wire, fits the request to the model's capabilities, applies the replay rules, runs the
/// model-egress hooks, resolves credentials, and only then hands the request to the wire, with an HTTP
/// connection confined to the provider's endpoints. Wires get requests only from here.
[<RequireQualifiedAccess>]
module ModelAccess =
    /// The contribution types of models, for the host's catalog.
    let contributionTypes: ContributionTypeInfo list =
        [
            ContributionType.info ChatWires.contributionType
            ContributionType.info ProviderDefs.contributionType
            ContributionType.info ModelEgress.transforms
            ContributionType.info ModelEgress.gates
        ]

    /// Sends no credentials (local models, tests).
    let noAuth: ProviderDef -> CancellationToken -> Task<Result<ResolvedAuth, ChatError>> =
        fun _ _ -> Task.FromResult(Ok ResolvedAuth.none)

    /// Access with these wires and providers, no egress hooks, no credentials and no time limits.
    let create (wires: ChatWire list) (providers: ProviderDef list) (http: Http) (clock: Clock) : ModelAccess =
        {
            Wires = wires
            Providers = providers
            Egress = ModelEgress.none
            Http = http
            Auth = noAuth
            Timeouts = HttpTimeouts.none
            Replay = Replay.defaults
            Clock = clock
            Trace = Trace.none
        }

    /// An HTTP connection that reaches only the provider's endpoints, applying its transform and then
    /// the credentials to every request.
    let confine (provider: ProviderDef) (auth: ResolvedAuth) (inner: Http) : Http =
        {
            Send =
                fun request cancellation ->
                    if not (ProviderDef.allows request.Url provider) then
                        Task.FromResult(Error(HttpError.Refused $"{request.Url} is not an endpoint of {provider.Id}"))
                    else
                        task {
                            let request =
                                match provider.Transform with
                                | Some transform -> RequestTransform.apply transform request
                                | None -> request

                            let! request = ResolvedAuth.apply auth request cancellation
                            return! inner.Send request cancellation
                        }
        }

    /// Which family serves the model at the provider, and its endpoint.
    let private route (provider: ProviderDef) (info: ModelInfo option) : (string * string) option =
        match info |> Option.bind (fun i -> i.Family) with
        | Some family -> ProviderDef.endpoint family provider |> Option.map (fun e -> family, e)
        | None -> List.tryHead provider.Endpoints

    let private event (access: ModelAccess) (span: SpanId) (message: string) (text: string) =
        access.Trace.Event (Some span) Level.Info message [ Attr.text "change" text ]

    /// Starts a response to a request: the single entry point that invokes wires. Failures before the
    /// wire (no provider, wire or capability, blocked egress, no credentials) are a stream that fails
    /// at once; nothing is sent for them.
    let stream<'R when 'R :> IScopeWith<'R>> (access: ModelAccess) (request: ChatRequest) : Eff<'R, ChatStream> =
        fun env ->
            let span =
                access.Trace.StartSpan
                    env.Scope.Span
                    "model.chat"
                    [
                        Attr.text "provider" request.Model.Provider
                        Attr.text "model" request.Model.Model
                    ]

            let finish (status: SpanStatus) (stream: ChatStream) =
                access.Trace.EndSpan span status
                stream

            let failed (error: ChatError) =
                access.Trace.Event (Some span) Level.Warning "model.unavailable" [ Attr.text "error" (string error) ]
                finish SpanStatus.Ok (ChatStream.failed error)

            task {
                try
                    match access.Providers |> List.tryFind (fun p -> p.Id = request.Model.Provider) with
                    | None -> return failed (Unavailable $"no provider is named {request.Model.Provider}")
                    | Some provider ->
                        let info = ProviderDef.model request.Model provider

                        match route provider info with
                        | None -> return failed (Unavailable $"{provider.Id} has no endpoint for {request.Model}")
                        | Some(family, endpoint) ->
                            match access.Wires |> List.tryFind (fun w -> w.Family = family) with
                            | None -> return failed (Unavailable $"no wire speaks {family}")
                            | Some wire ->
                                let fitted =
                                    match info with
                                    | Some info -> Fit.request info request
                                    | None -> Ok(request, [])

                                match fitted with
                                | Error e -> return failed e
                                | Ok(fitted, adjustments) ->
                                    for adjustment in adjustments do
                                        event access span "model.adjusted" (string adjustment)

                                    let messages, changes =
                                        Replay.prepare access.Replay request.Model family fitted.Messages

                                    for change in changes do
                                        event access span "model.replay" (string change)

                                    let prepared = { fitted with Messages = messages }
                                    let! cleared = ModelEgress.clear access.Egress prepared env

                                    match cleared with
                                    | Error e -> return failed e
                                    | Ok cleared ->
                                        match! access.Auth provider env.Scope.Cancellation with
                                        | Error e -> return failed e
                                        | Ok auth ->
                                            let ctx: WireCtx =
                                                {
                                                    Http = confine provider auth access.Http
                                                    Provider = provider
                                                    Family = family
                                                    Endpoint = endpoint
                                                    Model = request.Model
                                                    Info = info
                                                    Timeouts = access.Timeouts
                                                    Clock = access.Clock
                                                    Trace = access.Trace
                                                    Scope = { env.Scope with Span = Some span }
                                                }

                                            let! stream = Eff.run ctx (wire.Stream cleared)
                                            return finish SpanStatus.Ok stream
                with e ->
                    let status =
                        match e with
                        | :? OperationCanceledException -> SpanStatus.Cancelled
                        | e -> SpanStatus.Failed(e.GetType().Name, "")

                    access.Trace.EndSpan span status
                    return raise e
            }

    type private ScopeEnv =
        {
            Scope: Scope
        }

        interface IScopeWith<ScopeEnv> with
            member env.Scope = env.Scope
            member env.WithScope scope = { Scope = scope }

    /// The `Llm` capability over this access: what sessions, agents and extensions get.
    let llm (access: ModelAccess) : Llm =
        {
            Stream =
                fun request cancellation ->
                    stream
                        access
                        request
                        {
                            Scope = Scope.ofCancellation cancellation
                        }
        }
