/// The `anthropic.messages` wire (T119, extensions/Fx.Ext.ModelAnthropic): a hand-written cassette of
/// Anthropic's documented format (thinking with its signature, text, a tool call, then the follow-up
/// that replays them) replayed through ModelAccess under several cuts; redacted thinking, unknown
/// blocks and a mid-stream error; error responses; and request bodies for the thinking modes and the
/// cache breakpoint policy against the catalog.
module Fx.Aot.Scenarios.ModelsAnthropic

open System
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Ext.ModelAnthropic
open Fx.Models
open Fx.Sdk

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

let private show (result: Result<'T, 'E>) : string =
    match result with
    | Ok value -> $"ok {value}"
    | Error e -> $"error {e}"

let private json (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failwith (string e)

let private opus = ModelRef.create Provider.Id "claude-opus-5-5"

let private weather: ToolSpec =
    {
        Name = "get_weather"
        Description = "The current weather in a city."
        Parameters =
            json """{"type": "object", "properties": {"location": {"type": "string"}}, "required": ["location"]}"""
        Strict = false
    }

let private chat: ChatRequest =
    { ChatRequest.create opus [ Message.userText "What's the weather in Kraków?" ] with
        Tools = [ weather ]
        Reasoning =
            Some
                {
                    Effort = Some Effort.High
                    BudgetTokens = None
                    Visible = true
                }
        MaxOutputTokens = Some 16000
    }

let private signature =
    "YkRYCjRqz12BSkjc9lvNq1UwCIyiaMMdxYZpRCB+iTgJ3vXDv1exumjdcDce0kSacj2P/kScquAOHqW1CcQXTdejVczwxl5AlGplyMKTV3sS3F7CLdj3N0OMU8BgEYL8/bI166scaJn6GpzcPYeNFmsd8s5/CcdLoHG+9vDmQVCcX2o/EpHRiFqPvOWxlubGt8uENrAiFaNU8Zn4cbVWk+oVaM2lPUR5yetY9ZdatLHB4RIy"

/// The Messages API body the request must become (written by hand, as in the tests' sources/).
let private body =
    """{"model": "claude-opus-5-5", "max_tokens": 16000,
       "messages": [{"role": "user", "content": [{"type": "text", "text": "What's the weather in Kraków?"}]}],
       "tools": [{"name": "get_weather", "description": "The current weather in a city.",
                  "input_schema": {"type": "object", "properties": {"location": {"type": "string"}}, "required": ["location"]}}],
       "thinking": {"type": "adaptive", "display": "summarized"}, "output_config": {"effort": "high"}, "stream": true}"""

let private followUpBody =
    """{"model": "claude-opus-5-5", "max_tokens": 16000,
       "messages": [
         {"role": "user", "content": [{"type": "text", "text": "What's the weather in Kraków?"}]},
         {"role": "assistant", "content": [
           {"type": "thinking", "thinking": "The user wants the weather in Kraków.\n\nI should call get_weather.", "signature": "SIGNATURE"},
           {"type": "text", "text": "Let me check."},
           {"type": "tool_use", "id": "toolu_01T1x1fJ34qAmk2tNTrN7Up6", "name": "get_weather", "input": {"location": "Kraków"}}]},
         {"role": "user", "content": [{"type": "tool_result", "tool_use_id": "toolu_01T1x1fJ34qAmk2tNTrN7Up6", "content": [{"type": "text", "text": "4 °C"}]}]}],
       "tools": [{"name": "get_weather", "description": "The current weather in a city.",
                  "input_schema": {"type": "object", "properties": {"location": {"type": "string"}}, "required": ["location"]}}],
       "thinking": {"type": "adaptive", "display": "summarized"}, "output_config": {"effort": "high"}, "stream": true}"""
        .Replace("SIGNATURE", signature)

let private stream =
    """event: message_start
data: {"type":"message_start","message":{"id":"msg_01XFDUDYJgAACzvnptvVoYEL","type":"message","role":"assistant","content":[],"model":"claude-opus-5-5","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":472,"cache_creation_input_tokens":100,"cache_read_input_tokens":2048,"output_tokens":2}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

event: ping
data: {"type": "ping"}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The user wants the weather in Kraków.\n\n"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"I should call get_weather."}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIGNATURE"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Let me check."}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: content_block_start
data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_01T1x1fJ34qAmk2tNTrN7Up6","name":"get_weather","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"location\": \"Kra"}}

event: content_block_delta
data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"ków\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":2}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":89,"output_tokens_details":{"thinking_tokens":41}}}

event: message_stop
data: {"type":"message_stop"}

"""
        .Replace("SIGNATURE", signature)

let private followUpStream =
    """event: message_start
data: {"type":"message_start","message":{"id":"msg_01Q2VVx1kVz9Ey3S9t1hE8DQ","type":"message","role":"assistant","content":[],"model":"claude-opus-5-5","usage":{"input_tokens":36,"cache_read_input_tokens":584,"output_tokens":1}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"It's 4 °C in Kraków. 🌨"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":18}}

event: message_stop
data: {"type":"message_stop"}

"""

let private request (body: string) : RecordedRequest =
    {
        Method = "POST"
        Url = Provider.Endpoint + Request.MessagesPath
        Headers =
            [
                "content-type", "application/json"
                "accept", "text/event-stream"
                "anthropic-version", Provider.ApiVersion
            ]
        Body = RecordedBody.Json(json body)
    }

let private ok (body: string) : RecordedOutcome =
    RecordedOutcome.Response
        {
            Status = 200
            Headers = [ "content-type", "text/event-stream" ]
            Body = body
            End = BodyEnd.Complete
        }

let private cassette: Cassette =
    {
        Interactions =
            [
                {
                    Request = request body
                    Outcome = ok stream
                }
                {
                    Request = request followUpBody
                    Outcome = ok followUpStream
                }
            ]
    }

let private drain (stream: ChatStream) : ChatEvent list =
    let events = ResizeArray<ChatEvent>()
    let mutable reading = true

    while reading do
        match wait (stream.Next CancellationToken.None) with
        | Some e -> events.Add e
        | None -> reading <- false

    wait (stream.Close())
    List.ofSeq events

let private provider =
    ProviderDef.create Provider.Id Provider.Family Provider.Endpoint

let private run (http: Http) (request: ChatRequest) : ChatEvent list =
    let access =
        ModelAccess.create [ Anthropic.wire ] [ provider ] http (FakeClock.Create()).Clock

    drain (wait ((ModelAccess.llm access).Stream request CancellationToken.None))

let private result (events: ChatEvent list) =
    ChatAggregate.result (ChatAggregate.ofEvents events)

let private eventJson (event: ChatEvent) : string =
    escape (Json.serialize (ChatEvent.codec.Encode event))

let replay =
    Scenario.lines "models-anthropic-replay" (fun () ->
        [
            for chunking in
                [
                    Chunking.Whole
                    Chunking.Every 1
                    Chunking.Every 5
                    Chunking.At [ 3; 100; 101; 250; 777 ]
                ] do
                let http = CassetteHttp(cassette, chunking)
                let events = run http.Http chat
                $"{chunking}: {events.Length} events, coalesced {(ChatEvents.coalesce events).Length}"
                $"{chunking}: rules broken {(ChatEvents.check events).Length}"
                $"{chunking}: {show (result events)}"

            let http = CassetteHttp(cassette)
            let first = run http.Http chat

            for event in ChatEvents.coalesce first do
                $"event {eventJson event}"

            match result first with
            | Ok response ->
                for part in response.Parts do
                    match Part.state part with
                    | Some state ->
                        $"state {state.Kind} same {String.Equals(state.Data, signature, StringComparison.Ordinal)}"
                    | None -> $"part {part}"

                let next =
                    { chat with
                        Messages =
                            chat.Messages
                            @ [
                                ChatResponse.message response
                                Message.user
                                    [
                                        Part.ToolResult
                                            {
                                                CallId = "toolu_01T1x1fJ34qAmk2tNTrN7Up6"
                                                Name = "get_weather"
                                                Content = [ ToolContent.Text "4 °C" ]
                                                IsError = false
                                            }
                                    ]
                            ]
                    }

                let second = run http.Http next
                $"follow-up {show (result second)}"
                $"follow-up remaining {http.Remaining.Length}, open bodies {http.OpenBodies}"
            | Error failure -> $"failed {failure}"

            let broken =
                CassetteHttp(
                    {
                        Interactions =
                            [
                                { cassette.Interactions.Head with
                                    Outcome = ok (stream.Substring(0, 900))
                                }
                            ]
                    }
                )

            $"cut short {show (result (run broken.Http chat))}"
        ])

let private sse (events: (string * string) list) : string =
    events
    |> List.map (fun (name, data) -> Sse.render (Sse.event name data))
    |> String.concat ""

/// Decodes a stream text through the adapter's fold, as the wire does.
let private decode (text: string) : ChatEvent list =
    let ctx: WireCtx =
        {
            Http =
                {
                    Send = fun _ _ -> Task.FromResult(Error(HttpError.Refused "none"))
                }
            Provider = Provider.def
            Family = Provider.Family
            Endpoint = Provider.Endpoint
            Model = opus
            Info = None
            Timeouts = HttpTimeouts.none
            Clock = (FakeClock.Create()).Clock
            Trace = Trace.none
            Scope = Scope.root
        }

    let parser = SseParser()
    let mutable state = Decoder.initial ctx

    [
        for piece in Chunking.split (Chunking.Every 3) (Text.UTF8Encoding(false).GetBytes text) do
            match parser.Push piece with
            | Ok events ->
                for event in events do
                    let decoded = Decoder.decode (state, event)
                    state <- decoded.State
                    yield! decoded.Events
            | Error e -> failwith (string e)
    ]

let decoding =
    Scenario.lines "models-anthropic-decode" (fun () ->
        let start =
            "message_start",
            """{"type":"message_start","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":1}}}"""

        let redacted =
            decode (
                sse
                    [
                        start
                        "content_block_start",
                        """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"EmwKAhgBEgy3va3pzix/LafPsn4aDFIT2Xlxh0L5L8rLVyIw"}}"""
                        "content_block_stop", """{"type":"content_block_stop","index":0}"""
                        "content_block_start",
                        """{"type":"content_block_start","index":1,"content_block":{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}}"""
                        "content_block_stop", """{"type":"content_block_stop","index":1}"""
                        "content_block_start",
                        """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"list","input":{}}}"""
                        "content_block_delta",
                        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":""}}"""
                        "content_block_stop", """{"type":"content_block_stop","index":2}"""
                        "a_future_event", """{"type":"a_future_event"}"""
                        "message_delta",
                        """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}"""
                        "message_stop", """{"type":"message_stop"}"""
                    ]
            )

        let overloaded =
            decode (
                sse
                    [
                        start
                        "content_block_start",
                        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""
                        "content_block_delta",
                        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hal"}}"""
                        "error",
                        """{"type": "error", "error": {"type": "overloaded_error", "message": "Overloaded"}}"""
                    ]
            )

        [
            for event in redacted do
                $"redacted {eventJson event}"

            $"redacted {show (result redacted)}"

            for event in overloaded do
                $"mid-stream {eventJson event}"

            for reason in
                [
                    "end_turn"
                    "tool_use"
                    "max_tokens"
                    "stop_sequence"
                    "refusal"
                    "pause_turn"
                    "model_context_window_exceeded"
                    "new"
                ] do
                let stop = Decoder.stopReason reason (Some "##")
                $"stop {reason}: {stop}"
        ])

let errors =
    Scenario.lines "models-anthropic-errors" (fun () ->
        let error (status: int) (headers: (string * string) list) (body: string) =
            Errors.ofResponse
                {
                    Status = status
                    Headers = headers
                    Body = body
                }

        let anthropic (kind: string) (message: string) (code: string) =
            Json.serialize (
                Json.Object
                    [
                        "type", Json.String "error"
                        "error",
                        Json.Object
                            [
                                "type", Json.String kind
                                "message", Json.String message
                                if code <> "" then
                                    "details", Json.Object [ "error_code", Json.String code ]
                            ]
                    ]
            )

        [
            for status, headers, body in
                [
                    400, [], anthropic "invalid_request_error" "prompt is too long: 210000 tokens > 200000 maximum" ""
                    400, [], anthropic "invalid_request_error" "Your credit balance is too low." ""
                    401, [], anthropic "authentication_error" "invalid x-api-key" ""
                    402, [], anthropic "billing_error" "declined" ""
                    403, [], anthropic "permission_error" "no" ""
                    404, [], anthropic "not_found_error" "model: x" ""
                    413, [], anthropic "request_too_large" "too large" ""
                    429, [ "retry-after", "7" ], anthropic "rate_limit_error" "slow down" ""
                    429, [], anthropic "rate_limit_error" "cap" "enforced_spend_limit_reached"
                    500, [], anthropic "api_error" "Internal server error" ""
                    504, [], anthropic "timeout_error" "timed out" ""
                    529, [ "retry-after-ms", "250" ], anthropic "overloaded_error" "Overloaded" ""
                    502, [], "<html>Bad Gateway</html>"
                ] do
                let mapped = error status headers body
                $"{status}: {mapped} (retryable {ChatError.isRetryable mapped}, compact {ChatError.needsCompaction mapped})"

            // Through the wire: an error response ends the stream with the typed error.
            let failing =
                CassetteHttp(
                    {
                        Interactions =
                            [
                                {
                                    Request = request body
                                    Outcome =
                                        RecordedOutcome.Response
                                            {
                                                Status = 529
                                                Headers = [ "retry-after", "3" ]
                                                Body = anthropic "overloaded_error" "Overloaded" ""
                                                End = BodyEnd.Complete
                                            }
                                }
                            ]
                    }
                )

            for event in run failing.Http chat do
                $"wire {eventJson event}"
        ])

let private haiku45 = ModelRef.create Provider.Id "claude-haiku-4-5"

let requests =
    Scenario.lines "models-anthropic-requests" (fun () ->
        let catalog (model: ModelRef) = ProviderDef.model model Provider.def

        let bodyOf (info: ModelInfo option) (request: ChatRequest) =
            match Request.body Provider.Settings.defaults info request with
            | Ok body -> escape (Json.serializeCanonical body)
            | Error e -> $"error {e}"

        let cached =
            { ChatRequest.create
                  opus
                  [
                      Message.user [ Part.text "map"; Part.CacheBreakpoint CacheTtl.Short ]
                      Message.assistant
                          [
                              Part.text "ok"
                              Part.Reasoning
                                  {
                                      Text = "t"
                                      State =
                                          Some
                                              {
                                                  Family = Provider.Family
                                                  Model = opus
                                                  Kind = Request.SignatureKind
                                                  Data = "sig"
                                              }
                                  }
                              Part.CacheBreakpoint CacheTtl.Short
                          ]
                      Message.user
                          [
                              Part.CacheBreakpoint CacheTtl.Long
                              Part.text "q"
                              Part.CacheBreakpoint CacheTtl.Long
                              Part.Media
                                  {
                                      Kind = MediaKind.Pdf
                                      MediaType = "application/pdf"
                                      Source = MediaSource.Url "https://example.com/a.pdf"
                                      Name = Some "a.pdf"
                                  }
                          ]
                  ] with
                System = [ Part.text "s"; Part.CacheBreakpoint CacheTtl.Long ]
                Tools = [ weather ]
                Cache =
                    {
                        Key = None
                        Tools = Some CacheTtl.Long
                    }
                ToolChoice = ToolChoice.Tool "get_weather"
                ParallelToolCalls = Some false
                Metadata = [ "user_id", "u" ]
            }

        [
            $"cached {bodyOf None cached}"

            for effort in Effort.all do
                let reasoning =
                    Some
                        {
                            Effort = Some effort
                            BudgetTokens = None
                            Visible = (effort = Effort.High)
                        }

                let request =
                    { ChatRequest.create opus [ Message.userText "x" ] with
                        Reasoning = reasoning
                    }

                let none = "-"
                let thinking, used = Request.thinking (catalog opus) 128000 reasoning
                let budgeted, _ = Request.thinking (catalog haiku45) 64000 reasoning

                let text (json: Json option) =
                    json |> Option.map Json.serializeCanonical |> Option.defaultValue none

                let used = used |> Option.defaultValue none
                $"{effort}: opus {escape (text thinking)} {used}, haiku 4.5 {escape (text budgeted)}"

                match Fit.request (catalog opus).Value request with
                | Ok(fitted, changes) ->
                    let fittedEffort =
                        fitted.Reasoning
                        |> Option.bind (fun r -> r.Effort)
                        |> Option.map string
                        |> Option.defaultValue none

                    $"{effort}: fitted to {fittedEffort} ({changes.Length} changes)"
                | Error e -> $"{effort}: fit error {e}"
        ])

let scenarios = [ replay; decoding; errors; requests ]
