namespace Fx.Sdk

open System
open Fx.Contracts
open Fx.Effects

/// What one gate decides about an intent.
[<RequireQualifiedAccess>]
type Decision =
    | Allow
    /// A human must decide (e.g. a grant prompt), for this reason.
    | Ask of reason: string
    | Deny of reason: string

    override this.ToString() =
        match this with
        | Allow -> "allow"
        | Ask reason -> "ask: " + reason
        | Deny reason -> "deny: " + reason

/// A reason in a verdict: which contribution (`<extension>/<id>`) gave it, and what it said.
type Reason =
    {
        By: string
        Message: string
    }

    override this.ToString() = this.By + ": " + this.Message

/// What all gates decide together (spec §16.3): the maximum over Deny > Ask > Allow, with the reasons
/// of every gate at that level, sorted and without duplicates. So combining is commutative,
/// associative and idempotent, and `Allow` is its identity. A gate that fails denies.
[<RequireQualifiedAccess>]
type Verdict =
    | Allow
    | Ask of reasons: Reason list
    | Deny of reasons: Reason list

    override this.ToString() =
        let show (reasons: Reason list) =
            String.Join("; ", reasons |> List.map string)

        match this with
        | Allow -> "allow"
        | Ask reasons -> "ask (" + show reasons + ")"
        | Deny reasons -> "deny (" + show reasons + ")"

/// A gate (spec §16.3): decides about an intent. Gates combine with `Verdict.combine`.
type Gate<'I> =
    {
        Check: 'I -> Eff<IExtensionEnv, Decision>
    }

    override _.ToString() = "Gate"

/// A transform (spec §16.3): rewrites a value. Transforms compose in ascending `Priority` (then in
/// registration order). One that fails fails the whole pipeline: a payload that should have been
/// transformed (e.g. redacted before it leaves for a model) never goes out untransformed.
type Transform<'T> =
    {
        Priority: int
        Apply: 'T -> Eff<IExtensionEnv, 'T>
    }

    override this.ToString() = $"Transform(priority {this.Priority})"

/// An observer (spec §16.3): sees events, in parallel with the other observers. It cannot change
/// the event or the result of what emitted it, and its failure is reported, never propagated.
type Observer<'E> =
    {
        Observe: 'E -> Eff<IExtensionEnv, unit>
    }

    override _.ToString() = "Observer"

/// Interpreter middleware (spec §16.3 `Interpret`): wraps a capability interpreter (a record of
/// functions such as `Fs`) in another. The lowest `Priority` is the outermost, so it sees calls
/// first. Interpreters live in a process, so middleware comes from native extensions only.
type Middleware<'I> =
    {
        Priority: int
        Wrap: 'I -> 'I
    }

    override this.ToString() = $"Middleware(priority {this.Priority})"

/// A contribution's code failed inside an observer: who, and the cause (diagnostics only).
type ObserverFailure =
    {
        By: string
        Detail: string
    }

    override this.ToString() = this.By + ": " + this.Detail

[<RequireQualifiedAccess>]
module Decision =
    let codec: Codec<Decision> =
        Codec.union "decision" {
            let! allow = Codec.case0 "allow" Decision.Allow
            and! ask = Codec.case1 "ask" Decision.Ask (Field.required "reason" Codec.string)
            and! deny = Codec.case1 "deny" Decision.Deny (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | Decision.Allow -> allow
                    | Decision.Ask reason -> ask reason
                    | Decision.Deny reason -> deny reason
        }
        |> Codec.describe "What a gate decides: allow, ask a human, or deny."
        |> Codec.named "Decision"

[<RequireQualifiedAccess>]
module Verdict =
    /// 0 for Allow, 1 for Ask, 2 for Deny.
    let severity (verdict: Verdict) : int =
        match verdict with
        | Verdict.Allow -> 0
        | Verdict.Ask _ -> 1
        | Verdict.Deny _ -> 2

    let private reasons (verdict: Verdict) : Reason list =
        match verdict with
        | Verdict.Allow -> []
        | Verdict.Ask reasons
        | Verdict.Deny reasons -> reasons

    let private normalize (reasons: Reason list) : Reason list =
        reasons
        |> List.distinct
        |> List.sortWith (fun a b ->
            match String.CompareOrdinal(a.By, b.By) with
            | 0 -> String.CompareOrdinal(a.Message, b.Message)
            | c -> c)

    /// One gate's decision as a verdict, its reason attributed to the gate.
    let ofDecision (by: string) (decision: Decision) : Verdict =
        match decision with
        | Decision.Allow -> Verdict.Allow
        | Decision.Ask message -> Verdict.Ask [ { By = by; Message = message } ]
        | Decision.Deny message -> Verdict.Deny [ { By = by; Message = message } ]

    /// The maximum of two verdicts; at the same level, the union of their reasons.
    let combine (a: Verdict) (b: Verdict) : Verdict =
        match compare (severity a) (severity b) with
        | c when c > 0 -> a
        | c when c < 0 -> b
        | _ ->
            match a with
            | Verdict.Allow -> Verdict.Allow
            | Verdict.Ask _ -> Verdict.Ask(normalize (reasons a @ reasons b))
            | Verdict.Deny _ -> Verdict.Deny(normalize (reasons a @ reasons b))

    /// Combines any number of verdicts; `Allow` for none.
    let combineAll (verdicts: Verdict list) : Verdict =
        List.fold combine Verdict.Allow verdicts

    /// The verdict of a gate that failed: it denies.
    let failed (by: string) : Verdict =
        Verdict.Deny [ { By = by; Message = "the gate failed" } ]

    let private reason: Codec<Reason> =
        Codec.record {
            let! by = Codec.field "by" (fun (r: Reason) -> r.By) Codec.string
            and! message = Codec.field "message" (fun r -> r.Message) Codec.string
            return { By = by; Message = message }
        }
        |> Codec.named "Reason"

    let codec: Codec<Verdict> =
        Codec.union "verdict" {
            let! allow = Codec.case0 "allow" Verdict.Allow

            and! ask =
                Codec.case1 "ask" Verdict.Ask (Field.required "reasons" (Codec.list reason))

            and! deny =
                Codec.case1 "deny" Verdict.Deny (Field.required "reasons" (Codec.list reason))

            return
                fun value ->
                    match value with
                    | Verdict.Allow -> allow
                    | Verdict.Ask reasons -> ask reasons
                    | Verdict.Deny reasons -> deny reasons
        }
        |> Codec.named "Verdict"

[<RequireQualifiedAccess>]
module Transform =
    /// A transform from a pure function.
    let ofFunction (priority: int) (apply: 'T -> 'T) : Transform<'T> =
        {
            Priority = priority
            Apply = fun value -> Eff.ret (apply value)
        }

    /// The transform that changes nothing.
    let identity (priority: int) : Transform<'T> = ofFunction priority id

    /// `{"priority": n}`: the JSON form of a transform or a middleware.
    let settings: Codec<int> =
        Codec.record {
            let! priority = Codec.defaultField "priority" id 0 Codec.int
            return priority
        }
        |> Codec.describe "Lower priorities run first."

/// Hook points (spec §16.3): contribution types whose values are hooks. Define one per place where
/// extensions hook in, next to the code that runs it, e.g.
/// `let egress = Hook.transform "model-egress" "…" ModelRequest.codec`. The names are prefixed by
/// the kind (`gate:model-egress`), so one point can have hooks of several kinds.
[<RequireQualifiedAccess>]
module Hook =
    let private input (codec: Codec<'T>) (json: Json) (run: 'T -> Eff<IExtensionEnv, Json>) =
        match codec.Decode json with
        | Error issues -> Eff.ret (Error issues)
        | Ok value -> run value |> Eff.map Ok

    /// Gates on intents of type `'I`. Extension processes get the intent as JSON and answer a
    /// `Decision`.
    let gate (point: string) (description: string) (intent: Codec<'I>) : ContributionType<Gate<'I>> =
        let name = "gate:" + point

        {
            Name = name
            Description = description
            Kind = ContributionKind.Gate
            Privileged = false
            Schema = None
            ToJson = fun _ -> None
            OfJson =
                fun _ remote ->
                    match remote with
                    | None -> ContributionType.needsCode name
                    | Some remote ->
                        Ok
                            {
                                Check =
                                    fun value ->
                                        Remote.invoke remote (intent.Encode value)
                                        |> Eff.map (Remote.decodeOutput remote Decision.codec)
                            }
            Serve =
                Some(fun gate json ->
                    input intent json (fun value -> gate.Check value |> Eff.map Decision.codec.Encode))
        }

    /// Transforms of values of type `'T`, with their priority as their JSON form.
    let transform (point: string) (description: string) (value: Codec<'T>) : ContributionType<Transform<'T>> =
        let name = "transform:" + point

        {
            Name = name
            Description = description
            Kind = ContributionKind.Transform
            Privileged = false
            Schema = Some Transform.settings.Schema
            ToJson = fun t -> Some(Transform.settings.Encode t.Priority)
            OfJson =
                fun json remote ->
                    match remote with
                    | None -> ContributionType.needsCode name
                    | Some remote ->
                        Transform.settings.Decode(json |> Option.defaultValue (Json.Object []))
                        |> Result.map (fun priority ->
                            {
                                Priority = priority
                                Apply =
                                    fun current ->
                                        Remote.invoke remote (value.Encode current)
                                        |> Eff.map (Remote.decodeOutput remote value)
                            })
            Serve =
                Some(fun transform json ->
                    input value json (fun current -> transform.Apply current |> Eff.map value.Encode))
        }

    /// Contributions of values of type `'T`, concatenated across extensions (`Hooks.collect`).
    let contribute (name: string) (description: string) (item: Codec<'T>) : ContributionType<'T> =
        ContributionType.data name description item

    /// Observers of events of type `'E`.
    let observe (point: string) (description: string) (event: Codec<'E>) : ContributionType<Observer<'E>> =
        let name = "observe:" + point

        {
            Name = name
            Description = description
            Kind = ContributionKind.Observe
            Privileged = false
            Schema = None
            ToJson = fun _ -> None
            OfJson =
                fun _ remote ->
                    match remote with
                    | None -> ContributionType.needsCode name
                    | Some remote ->
                        Ok
                            {
                                Observe = fun value -> Remote.invoke remote (event.Encode value) |> Eff.ignore
                            }
            Serve =
                Some(fun observer json ->
                    input event json (fun value -> observer.Observe value |> Eff.map (fun () -> Json.Null)))
        }

    /// Middleware around interpreters of type `'I` (a capability record). In process only.
    let interpret (point: string) (description: string) : ContributionType<Middleware<'I>> =
        ContributionType.inProcess ("interpret:" + point) description ContributionKind.Interpret

/// Runs hooks of each kind (spec §16.3). Each contribution's code runs in its extension's
/// environment with the caller's scope (`ExtensionEnv.within`).
[<RequireQualifiedAccess>]
module Hooks =
    let private address (c: Contributed<'T>) : Address =
        {
            Extension = c.Extension
            Type = c.Type
            Id = c.Id
        }

    /// Why a contribution failed, the same in process and out: the remote failure, or the exception's
    /// type (never its message, which differs between the JIT and Native AOT).
    let private detail (e: exn) : string =
        match e with
        | :? ContributionFailedException as failed -> failed.Detail
        | e -> e.GetType().Name

    /// Runs a contribution's code in its extension's environment, catching what it throws, even
    /// before it returns an effect.
    let private guarded (c: Contributed<'T>) (run: 'T -> Eff<IExtensionEnv, 'U>) : Eff<'R, Result<'U, exn>> =
        Eff.catch (Eff.delay (fun () -> ExtensionEnv.within c.Env (run c.Value)))

    let private cancelled (scope: Scope) (e: exn) =
        e :? OperationCanceledException && scope.Cancellation.IsCancellationRequested

    /// Ascending priority; contributions with equal priorities keep their order (a stable sort).
    let private ordered (priority: 'T -> int) (items: Contributed<'T> list) : Contributed<'T> list =
        List.sortBy (fun (c: Contributed<'T>) -> priority c.Value) items

    /// Runs every gate concurrently and combines their verdicts: the maximum over Deny > Ask > Allow.
    /// A gate that fails denies; `Allow` if there are none. The result does not depend on the order of
    /// the gates. Cancelling the caller's scope cancels them and fails with the cancellation.
    let decide<'R, 'I when 'R :> IScopeWith<'R>> (gates: Contributed<Gate<'I>> list) (intent: 'I) : Eff<'R, Verdict> =
        let one (gate: Contributed<Gate<'I>>) : Eff<'R, Verdict> =
            eff {
                let! outcome = guarded gate (fun g -> g.Check intent)
                let! scope = Eff.scope

                match outcome with
                | Ok decision -> return Verdict.ofDecision gate.By decision
                | Error e when cancelled scope e -> return raise e
                | Error _ -> return Verdict.failed gate.By
            }

        Eff.all (gates |> List.map one) |> Eff.map Verdict.combineAll

    /// Applies the transforms one after another, in ascending priority (then registration order). If
    /// one fails, the pipeline fails with `ContributionFailedException`.
    let transform<'R, 'T when 'R :> IScope> (transforms: Contributed<Transform<'T>> list) (value: 'T) : Eff<'R, 'T> =
        eff {
            let mutable current = value

            for t in ordered (fun (t: Transform<'T>) -> t.Priority) transforms do
                let! outcome = guarded t (fun t -> t.Apply current)
                let! scope = Eff.scope

                match outcome with
                | Ok next -> current <- next
                | Error e when cancelled scope e -> raise e
                | Error e -> raise (ContributionFailedException(address t, detail e, e))

            return current
        }

    /// The contributed values, concatenated in registration order (the `Contribute` hook).
    let collect (items: Contributed<'T> list) : 'T list = items |> List.map (fun c -> c.Value)

    /// Runs every observer concurrently and waits for them. Failures are isolated: the others still
    /// run, and the failures are returned (in the observers' order) for diagnostics.
    let observe<'R, 'E when 'R :> IScopeWith<'R>>
        (observers: Contributed<Observer<'E>> list)
        (event: 'E)
        : Eff<'R, ObserverFailure list> =
        let one (observer: Contributed<Observer<'E>>) : Eff<'R, ObserverFailure option> =
            eff {
                let! outcome = guarded observer (fun o -> o.Observe event)
                let! scope = Eff.scope

                match outcome with
                | Ok() -> return None
                | Error e when cancelled scope e -> return raise e
                | Error e -> return Some { By = observer.By; Detail = detail e }
            }

        Eff.all (observers |> List.map one) |> Eff.map (List.choose id)

    /// Wraps an interpreter in the middleware: the lowest priority outermost.
    let interpret (middleware: Contributed<Middleware<'I>> list) (inner: 'I) : 'I =
        ordered (fun (m: Middleware<'I>) -> m.Priority) middleware
        |> List.rev
        |> List.fold (fun wrapped m -> m.Value.Wrap wrapped) inner
