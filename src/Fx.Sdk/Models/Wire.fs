namespace Fx.Sdk

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Models

/// What a wire adapter gets with a request (spec §10.2 `WireCtx`): a connection to the provider and
/// the context of the call. `Http` reaches only the provider's endpoints and adds the resolved
/// credentials and the provider's transform to every request, so an adapter never holds
/// credentials and cannot send a payload elsewhere.
type WireCtx =
    {
        /// The provider's connection (confined to its endpoints, credentials added).
        Http: Http
        Provider: ProviderDef
        /// The wire family of this call, e.g. `anthropic.messages`.
        Family: string
        /// The base URL of the family at this provider.
        Endpoint: string
        /// The model called.
        Model: ModelRef
        /// Its catalog entry, if the provider has one.
        Info: ModelInfo option
        /// Time limits for the requests of this call.
        Timeouts: HttpTimeouts
        Clock: Clock
        Trace: Trace
        Scope: Scope
    }

    interface IHttp with
        member ctx.Http = ctx.Http

    interface IClock with
        member ctx.Clock = ctx.Clock

    interface ITrace with
        member ctx.Trace = ctx.Trace

    interface IScopeWith<WireCtx> with
        member ctx.Scope = ctx.Scope
        member ctx.WithScope scope = { ctx with Scope = scope }

    override ctx.ToString() = $"WireCtx({ctx.Family} {ctx.Model})"

/// A request that passed the model-egress hooks (spec §18): the only form in which a wire adapter
/// gets a request. Only `ModelAccess` makes one, after running the hooks, so an adapter cannot be
/// handed a payload that skipped them.
[<Sealed>]
type WireRequest internal (request: ChatRequest) =
    member _.Request = request
    override _.ToString() = "cleared " + string request

/// A chat wire adapter (spec §10.1–10.2): turns canonical requests into a provider family's HTTP
/// requests, and its responses into canonical events. Contributed by native extensions under
/// `ChatWires.contributionType`; core families are built-in F#.
[<NoEquality; NoComparison>]
type ChatWire =
    {
        /// The wire family, e.g. `anthropic.messages`, `openai.responses`.
        Family: string
        /// Starts a response. Failures are a `Failed` event of the stream (never an exception); the
        /// caller's cancellation is an `OperationCanceledException`.
        Stream: WireRequest -> Eff<WireCtx, ChatStream>
    }

    override this.ToString() = "wire " + this.Family

/// An HTTP error response, for an adapter to map to a typed error.
type ErrorResponse =
    {
        Status: int
        Headers: (string * string) list
        /// The body as text (at most 64 KiB of it).
        Body: string
    }

    override this.ToString() = $"HTTP {this.Status}"

/// What an adapter's decoder makes of one event.
type Decoded<'S> =
    {
        State: 'S
        Events: ChatEvent list
    }

    override this.ToString() = $"decoded {this.Events.Length} events"

/// The pure parts of an adapter that speaks HTTP and server-sent events; `SseWire.create` makes the
/// `ChatWire`. Each function is pure, so an adapter is tested without a network: request building
/// against cassettes, decoding against recorded event streams.
[<NoEquality; NoComparison>]
type SseAdapter<'S> =
    {
        Family: string
        /// The HTTP request for a chat request; an error if the request cannot be expressed.
        Request: WireCtx * ChatRequest -> Result<HttpRequest, ChatError>
        /// An error status (anything but 2xx) as a typed error.
        Error: ErrorResponse -> ChatError
        /// The decoder's state before the first event.
        Initial: WireCtx -> 'S
        /// One event: the new state and the canonical events it completes.
        Decode: 'S * SseEvent -> Decoded<'S>
        /// The end of the body, before any terminal event: the last events. If they hold no
        /// `Finished` or `Failed`, the stream fails with `InvalidResponse`.
        End: 'S -> ChatEvent list
    }

    override this.ToString() = "sse adapter " + this.Family

[<RequireQualifiedAccess>]
module WireCtx =
    /// Provider state from this call's family and model.
    let state (ctx: WireCtx) (kind: string) (data: string) : ProviderState =
        {
            Family = ctx.Family
            Model = ctx.Model
            Kind = kind
            Data = data
        }

    /// The endpoint URL with a path appended (`/messages`).
    let url (ctx: WireCtx) (path: string) : string = ctx.Endpoint.TrimEnd '/' + path

[<RequireQualifiedAccess>]
module SseWire =
    let private utf8 = UTF8Encoding(false)

    /// The most of an error body read for the error.
    let errorBodyLimit = 64 * 1024

    /// Turns an event-stream response into a canonical stream, pulling the body as events are asked
    /// for. Exactly one terminal event: the adapter's, else a transport failure, a stream that cannot
    /// be read, or one that ended early. After it, the body is closed.
    let events (adapter: SseAdapter<'S>) (ctx: WireCtx) (body: HttpBody) : ChatStream =
        let parser = SseParser()
        let pending = Queue<ChatEvent>()
        let mutable state = adapter.Initial ctx
        let mutable terminal = false
        let mutable ended = false

        let enqueue (events: ChatEvent list) =
            for event in events do
                if not terminal then
                    pending.Enqueue event

                    if ChatEvent.isTerminal event then
                        terminal <- true

        let stop () =
            ended <- true
            body.Close()

        let next (cancellation: CancellationToken) : Task<ChatEvent option> =
            task {
                while pending.Count = 0 && not ended do
                    if terminal then
                        do! stop ()
                    else
                        match! body.Read cancellation with
                        | Error e ->
                            enqueue [ ChatEvent.Failed(ChatError.Transport e) ]
                            do! stop ()
                        | Ok None ->
                            let truncated = parser.Finish()
                            enqueue (adapter.End state)

                            if truncated then
                                enqueue [ ChatEvent.Failed(InvalidResponse "the event stream ended inside an event") ]
                            else
                                enqueue
                                    [
                                        ChatEvent.Failed(
                                            InvalidResponse "the stream ended before the response finished"
                                        )
                                    ]

                            do! stop ()
                        | Ok(Some bytes) ->
                            match parser.Push bytes with
                            | Error e ->
                                enqueue [ ChatEvent.Failed(InvalidResponse(string e)) ]
                                do! stop ()
                            | Ok events ->
                                for event in events do
                                    if not terminal then
                                        let decoded = adapter.Decode(state, event)
                                        state <- decoded.State
                                        enqueue decoded.Events

                if pending.Count = 0 then
                    return None
                else
                    let event = pending.Dequeue()

                    if pending.Count = 0 && terminal && not ended then
                        do! stop ()

                    return Some event
            }

        { Next = next; Close = body.Close }

    /// Reads an error response (bounded) and maps it with the adapter.
    let private failure (adapter: SseAdapter<'S>) (response: HttpResponse) (cancellation: CancellationToken) =
        task {
            let! body = HttpBody.readAll errorBodyLimit response.Body cancellation

            let text =
                match body with
                | Ok bytes -> utf8.GetString bytes
                | Error _ -> ""

            return
                adapter.Error
                    {
                        Status = response.Status
                        Headers = response.Headers
                        Body = text
                    }
        }

    /// The wire of an adapter: builds the request, sends it with the call's timeouts, maps error
    /// statuses, and streams the events of a successful response.
    let create (adapter: SseAdapter<'S>) : ChatWire =
        {
            Family = adapter.Family
            Stream =
                fun request ->
                    fun ctx ->
                        task {
                            match adapter.Request(ctx, request.Request) with
                            | Error e -> return ChatStream.failed e
                            | Ok http ->
                                let http =
                                    if http.Timeouts = HttpTimeouts.none then
                                        { http with Timeouts = ctx.Timeouts }
                                    else
                                        http

                                match! ctx.Http.Send http ctx.Scope.Cancellation with
                                | Error e -> return ChatStream.failed (ChatError.Transport e)
                                | Ok response when response.Status < 200 || response.Status > 299 ->
                                    let! error = failure adapter response ctx.Scope.Cancellation
                                    return ChatStream.failed error
                                | Ok response -> return events adapter ctx response.Body
                        }
        }

/// The contribution types of models (spec §16.2 "Models"). All are privileged (spec §16.5): only
/// extensions from host config, policy or the binary may contribute them.
[<RequireQualifiedAccess>]
module ChatWires =
    /// Chat wire adapters, by family (the contribution id is the family). Code that runs in the
    /// host's process: native extensions only.
    let contributionType: ContributionType<ChatWire> =
        ContributionType.inProcess
            "model.chat-wire"
            "A chat wire adapter: canonical requests to a provider family's HTTP, responses to canonical events."
            ContributionKind.Code
        |> ContributionType.privileged

[<RequireQualifiedAccess>]
module ProviderDefs =
    /// Provider definitions: data, so declarative extensions (and host config) can add providers.
    let contributionType: ContributionType<ProviderDef> =
        ContributionType.data
            "model.provider"
            "A model provider: endpoints per wire family, catalog, auth methods, quirks and request transform."
            ProviderDef.codec
        |> ContributionType.privileged
