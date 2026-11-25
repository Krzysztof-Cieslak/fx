/// The canonical wire (`canonical`): the reference chat wire adapter. It speaks the canonical model
/// itself over HTTP: the request body is the `ChatRequest`'s JSON, the response an event stream whose
/// events are `ChatEvent`s as JSON, and errors are `ChatError`s as JSON. It is the wire of a model
/// served by fx itself (a gateway, a test server), the subject of the wire-conformance suite's own
/// tests, and the reference for writing adapters with `SseWire`. Not bundled.
module Fx.Ext.ModelCanonical.Canonical

open System
open System.Text
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// The family name.
[<Literal>]
let Family = "canonical"

let private utf8 = UTF8Encoding(false)

/// The path of the chat endpoint under the provider's base URL.
[<Literal>]
let ChatPath = "/chat"

/// The JSON of an error response: `{"error": <ChatError>}`.
let errorBody (error: ChatError) : string =
    Json.serialize (Json.Object [ "error", ChatError.codec.Encode error ])

/// The HTTP request for a chat request.
let request (ctx: WireCtx, chat: ChatRequest) : Result<HttpRequest, ChatError> =
    let body = utf8.GetBytes(Json.serialize (ChatRequest.codec.Encode chat))

    HttpRequest.post (WireCtx.url ctx ChatPath) body
    |> HttpRequest.withHeader "content-type" "application/json"
    |> HttpRequest.withHeader "accept" "text/event-stream"
    |> Ok

/// An error response: the body's `ChatError` if it has one (with the `Retry-After` header if the body
/// names no wait), else what the status says.
let error (response: ErrorResponse) : ChatError =
    let fromBody =
        match Json.parse response.Body with
        | Ok(Json.Object members) ->
            members
            |> List.tryFind (fun (name, _) -> name = "error")
            |> Option.bind (fun (_, json) -> ChatError.codec.Decode json |> Result.toOption)
        | _ -> None

    let retryAfter = ErrorMapping.retryAfter response.Headers

    match fromBody with
    | Some(RateLimited r) when r.RetryAfter.IsNone -> RateLimited { r with RetryAfter = retryAfter }
    | Some(Overloaded r) when r.RetryAfter.IsNone -> Overloaded { r with RetryAfter = retryAfter }
    | Some error -> error
    | None -> ErrorMapping.ofStatus response.Status response.Headers response.Body

/// One event of the stream: a `ChatEvent` as JSON. Provider state is stamped with this call's family
/// and model: the server says only what kind of state it is and its data.
let decode (ctx: WireCtx) (event: SseEvent) : ChatEvent list =
    match Codec.decodeText ChatEvent.codec event.Data with
    | Ok(ChatEvent.PartState(index, state)) -> [ ChatEvent.PartState(index, WireCtx.state ctx state.Kind state.Data) ]
    | Ok event -> [ event ]
    | Error issues ->
        [
            ChatEvent.Failed(InvalidResponse("an event does not decode: " + Issue.renderAtMost 3 issues))
        ]

/// The adapter, as pure functions.
let adapter: SseAdapter<WireCtx option> =
    {
        Family = Family
        Request = request
        Error = error
        Initial = Some
        Decode =
            fun (state, event) ->
                {
                    State = state
                    Events =
                        match state with
                        | Some ctx -> decode ctx event
                        | None -> []
                }
        End = fun _ -> []
    }

/// The wire.
let wire: ChatWire = SseWire.create adapter

/// The event-stream text of canonical events: what a canonical server sends.
let render (events: ChatEvent list) : string =
    events
    |> List.map (fun event ->
        let json = ChatEvent.codec.Encode event

        let kind =
            match json with
            | Json.Object members ->
                members
                |> List.tryFind (fun (name, _) -> name = "kind")
                |> Option.bind (fun (_, value) ->
                    match value with
                    | Json.String kind -> Some kind
                    | _ -> None)
                |> Option.defaultValue "message"
            | _ -> "message"

        Sse.render (Sse.event kind (Json.serialize json)))
    |> String.concat ""

/// A canonical server's answer to a request, for tests and examples: it echoes the last user text,
/// reasoning first (with state that names the conversation's length), and counts characters as
/// tokens. A request whose last user text starts with `call ` gets a call of the first tool instead.
let echo (chat: ChatRequest) : ChatEvent list =
    let lastText =
        chat.Messages
        |> List.filter (fun m -> m.Role = Role.User)
        |> List.tryLast
        |> Option.map (fun m ->
            m.Parts
            |> List.choose (fun p ->
                match p with
                | Part.Text t -> Some t.Text
                | Part.ToolResult r ->
                    r.Content
                    |> List.choose (fun c ->
                        match c with
                        | ToolContent.Text text -> Some text
                        | ToolContent.Media _ -> None)
                    |> String.concat ""
                    |> Some
                | _ -> None)
            |> String.concat "")
        |> Option.defaultValue ""

    let input =
        chat.Messages
        |> List.sumBy (fun m ->
            m.Parts
            |> List.sumBy (fun p ->
                match p with
                | Part.Text t -> t.Text.Length
                | _ -> 0))

    let state =
        {
            Family = Family
            Model = chat.Model
            Kind = "echo"
            Data = $"turn-{chat.Messages.Length}"
        }

    let call =
        match chat.Tools with
        | tool :: _ when lastText.StartsWith("call ", StringComparison.Ordinal) -> Some tool
        | _ -> None

    [
        ChatEvent.Started
            {
                Id = Some $"echo-{chat.Messages.Length}"
                Model = Some chat.Model.Model
            }
        ChatEvent.PartStarted(0, PartStart.Reasoning)
        ChatEvent.Delta(0, "echoing")
        ChatEvent.PartState(0, state)
        ChatEvent.PartEnded 0

        match call with
        | Some tool ->
            ChatEvent.PartStarted(1, PartStart.ToolCall("call-1", tool.Name))
            ChatEvent.Delta(1, "{\"text\":")
            ChatEvent.Delta(1, Json.quote (lastText.Substring 5) + "}")
            ChatEvent.PartEnded 1
        | None ->
            ChatEvent.PartStarted(1, PartStart.Text)

            for word in lastText.Split(' ') |> Array.indexed do
                ChatEvent.Delta(1, (if fst word = 0 then "" else " ") + snd word)

            ChatEvent.PartEnded 1

        ChatEvent.UsageUpdated
            { Usage.zero with
                Input = input
                Output = lastText.Length + 7
            }

        ChatEvent.Finished(
            if call.IsSome then
                StopReason.ToolUse
            else
                StopReason.EndTurn
        )
    ]

let id = Product.extensionId "model.canonical"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "The canonical chat wire: the canonical model over HTTP and server-sent events."
        Contributions = [ Declare.contribution ChatWires.contributionType Family ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate = Eff.ret (Activation.create [ Contribution.create ChatWires.contributionType Family wire ] [])
    }
