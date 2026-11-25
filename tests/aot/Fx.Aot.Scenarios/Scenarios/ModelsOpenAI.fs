/// The `openai.responses` wire (T120, extensions/Fx.Ext.ModelOpenAI): a hand-written cassette of
/// OpenAI's documented streaming format (encrypted reasoning with a two-part summary, a commentary,
/// a tool call, then the follow-up that replays them byte for byte) replayed through ModelAccess with
/// the catalog under several cuts; decoding of hosted items, done-only text, incomplete and failed
/// responses and error events; error responses; and request bodies: strict mode's dialect, cache
/// breakpoints per model, efforts and the system prompt's placement.
module Fx.Aot.Scenarios.ModelsOpenAI

open System
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Ext.ModelOpenAI
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

let private astra = ModelRef.create Provider.Id "gpt-6.2-astra"
let private sol = ModelRef.create Provider.Id "gpt-6.2-sol"
let private gpt55 = ModelRef.create Provider.Id "gpt-5.5"

let private weather: ToolSpec =
    {
        Name = "get_weather"
        Description = "The current weather in a city."
        Parameters =
            json """{"type": "object", "properties": {"location": {"type": "string"}}, "required": ["location"]}"""
        Strict = false
    }

let private chat: ChatRequest =
    { ChatRequest.create astra [ Message.userText "What's the weather in Kraków?" ] with
        Tools = [ weather ]
        Reasoning =
            Some
                {
                    Effort = Some Effort.High
                    BudgetTokens = None
                    Visible = true
                }
        MaxOutputTokens = Some 16000
        Cache =
            { CacheHints.none with
                Key = Some "weather-1"
            }
    }

let private encrypted =
    "gAAAAABpfmDsLqFtcoCkv1I3oplBkJKVb2E72WpmDzAC5cTK38TCLAIPybeUbUfE16b1hLLaj4/CjbQYpXB5q6mXTikDrHkyfkTUHEnuK+4PN2VNuqhx92pKsVzKC9Ve8lKl5ZkH6uvKd60JAZwpw5ruSZVANFUaV34DRASvcj63xy0pTo"

let private reasoningItem =
    """{"id":"rs_14b1","type":"reasoning","summary":[{"type":"summary_text","text":"**Checking the weather**\n\nKraków."},{"type":"summary_text","text":"**Calling get_weather**"}],"encrypted_content":"ENCRYPTED"}"""
        .Replace("ENCRYPTED", encrypted)

/// The Responses API body the request must become (written by hand, as in the tests' sources/).
let private body =
    """{"model": "gpt-6.2-astra",
       "input": [{"role": "user", "content": [{"type": "input_text", "text": "What's the weather in Kraków?"}]}],
       "tools": [{"type": "function", "name": "get_weather", "description": "The current weather in a city.",
                  "parameters": {"type": "object", "properties": {"location": {"type": "string"}}, "required": ["location"]}, "strict": false}],
       "reasoning": {"effort": "high", "summary": "auto"}, "include": ["reasoning.encrypted_content"],
       "max_output_tokens": 16000, "prompt_cache_key": "weather-1", "store": false, "stream": true}"""

let private followUpBody =
    """{"model": "gpt-6.2-astra",
       "input": [
         {"role": "user", "content": [{"type": "input_text", "text": "What's the weather in Kraków?"}]},
         REASONING,
         {"role": "assistant", "content": "Let me check.", "phase": "commentary"},
         {"type": "function_call", "call_id": "call_TKcq", "name": "get_weather", "arguments": "{\"location\":\"Kraków\"}"},
         {"type": "function_call_output", "call_id": "call_TKcq", "output": "4 °C"}],
       "tools": [{"type": "function", "name": "get_weather", "description": "The current weather in a city.",
                  "parameters": {"type": "object", "properties": {"location": {"type": "string"}}, "required": ["location"]}, "strict": false}],
       "reasoning": {"effort": "high", "summary": "auto"}, "include": ["reasoning.encrypted_content"],
       "max_output_tokens": 16000, "prompt_cache_key": "weather-1", "store": false, "stream": true}"""
        .Replace("REASONING", reasoningItem)

let private stream =
    """event: response.created
data: {"type":"response.created","sequence_number":0,"response":{"id":"resp_bdd0","object":"response","status":"in_progress","model":"gpt-6.2-astra","output":[],"store":false,"usage":null}}

event: response.output_item.added
data: {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"rs_14b1","type":"reasoning","summary":[]}}

event: response.reasoning_summary_part.added
data: {"type":"response.reasoning_summary_part.added","sequence_number":2,"output_index":0,"summary_index":0,"part":{"type":"summary_text","text":""}}

event: response.reasoning_summary_text.delta
data: {"type":"response.reasoning_summary_text.delta","sequence_number":3,"output_index":0,"summary_index":0,"delta":"**Checking the weather**\n\nKraków.","obfuscation":"q8Zr"}

event: response.reasoning_summary_text.delta
data: {"type":"response.reasoning_summary_text.delta","sequence_number":4,"output_index":0,"summary_index":1,"delta":"**Calling get_weather**"}

event: response.output_item.done
data: {"type":"response.output_item.done","sequence_number":5,"output_index":0,"item":REASONING}

event: response.output_item.added
data: {"type":"response.output_item.added","sequence_number":6,"output_index":1,"item":{"id":"msg_503b","type":"message","status":"in_progress","content":[],"phase":"commentary","role":"assistant"}}

event: response.output_text.delta
data: {"type":"response.output_text.delta","sequence_number":7,"output_index":1,"content_index":0,"delta":"Let me","logprobs":[]}

event: response.output_text.delta
data: {"type":"response.output_text.delta","sequence_number":8,"output_index":1,"content_index":0,"delta":" check.","logprobs":[]}

event: response.output_item.done
data: {"type":"response.output_item.done","sequence_number":9,"output_index":1,"item":{"id":"msg_503b","type":"message","status":"completed","content":[{"type":"output_text","annotations":[],"text":"Let me check."}],"phase":"commentary","role":"assistant"}}

event: response.output_item.added
data: {"type":"response.output_item.added","sequence_number":10,"output_index":2,"item":{"id":"fc_4ce6","type":"function_call","status":"in_progress","arguments":"","call_id":"call_TKcq","name":"get_weather"}}

event: response.function_call_arguments.delta
data: {"type":"response.function_call_arguments.delta","sequence_number":11,"output_index":2,"delta":"{\"location\":\"Kra"}

event: response.function_call_arguments.delta
data: {"type":"response.function_call_arguments.delta","sequence_number":12,"output_index":2,"delta":"ków\"}"}

event: response.output_item.done
data: {"type":"response.output_item.done","sequence_number":13,"output_index":2,"item":{"id":"fc_4ce6","type":"function_call","status":"completed","arguments":"{\"location\":\"Kraków\"}","call_id":"call_TKcq","name":"get_weather"}}

event: response.completed
data: {"type":"response.completed","sequence_number":14,"response":{"id":"resp_bdd0","status":"completed","model":"gpt-6.2-astra","output":[],"usage":{"input_tokens":412,"input_tokens_details":{"cached_tokens":128,"cache_write_tokens":0},"output_tokens":187,"output_tokens_details":{"reasoning_tokens":128},"total_tokens":599}}}

"""
        .Replace("REASONING", reasoningItem)

let private followUpStream =
    """event: response.created
data: {"type":"response.created","sequence_number":0,"response":{"id":"resp_6e0c","status":"in_progress","model":"gpt-6.2-astra","output":[],"usage":null}}

event: response.output_item.done
data: {"type":"response.output_item.done","sequence_number":1,"output_index":0,"item":{"id":"msg_2c4e","type":"message","status":"completed","content":[{"type":"output_text","annotations":[],"text":"It's 4 °C."}],"phase":"final_answer","role":"assistant"}}

event: response.completed
data: {"type":"response.completed","sequence_number":2,"response":{"id":"resp_6e0c","status":"completed","model":"gpt-6.2-astra","output":[],"usage":{"input_tokens":640,"input_tokens_details":{"cached_tokens":384},"output_tokens":6,"output_tokens_details":{"reasoning_tokens":0}}}}

"""

let private request (body: string) : RecordedRequest =
    {
        Method = "POST"
        Url = Provider.Endpoint + Request.ResponsesPath
        Headers = [ "content-type", "application/json"; "accept", "text/event-stream" ]
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
        | Some event -> events.Add event
        | None -> reading <- false

    wait (stream.Close())
    List.ofSeq events

let private run (http: Http) (request: ChatRequest) : ChatEvent list =
    let access =
        { ModelAccess.create [ OpenAI.wire ] [ Provider.def ] http (FakeClock.Create()).Clock with
            Replay = OpenAI.withReplay Replay.defaults
        }

    drain (wait ((ModelAccess.llm access).Stream request CancellationToken.None))

let private result (events: ChatEvent list) =
    ChatAggregate.result (ChatAggregate.ofEvents events)

let private eventJson (event: ChatEvent) : string =
    escape (Json.serialize (ChatEvent.codec.Encode event))

let replay =
    Scenario.lines "models-openai-replay" (fun () ->
        [
            for chunking in
                [
                    Chunking.Whole
                    Chunking.Every 1
                    Chunking.Every 7
                    Chunking.At [ 5; 300; 301; 1200 ]
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
            | Error failure -> $"first failed: {failure}"
            | Ok response ->
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
                                                CallId = "call_TKcq"
                                                Name = "get_weather"
                                                Content = [ ToolContent.Text "4 °C" ]
                                                IsError = false
                                            }
                                    ]
                            ]
                    }

                let second = run http.Http next
                $"follow-up: {show (result second)}, remaining {http.Remaining.Length}, open bodies {http.OpenBodies}"

                let sent = http.Sent |> List.last |> HttpRequest.bodyText |> json

                let replayed =
                    match Json.tryField "input" sent with
                    | Some(Json.Array items) ->
                        items |> List.tryItem 1 |> Option.bind (Json.tryField "encrypted_content")
                    | _ -> None

                let same =
                    match replayed with
                    | Some(Json.String data) -> String.Equals(data, encrypted, StringComparison.Ordinal)
                    | _ -> false

                $"encrypted reasoning replayed byte for byte: {same}"

            let broken =
                {
                    Interactions =
                        [
                            {
                                Request = request body
                                Outcome =
                                    RecordedOutcome.Response
                                        {
                                            Status = 200
                                            Headers = []
                                            Body = stream.Substring(0, stream.Length / 2)
                                            End = BodyEnd.Reset
                                        }
                            }
                        ]
                }

            $"broken off: {show (result (run (CassetteHttp(broken)).Http chat))}"
        ])

let private sse (events: (string * string) list) : string =
    events
    |> List.map (fun (name, data) -> Sse.render (Sse.event name data))
    |> String.concat ""

let private decode (text: string) : ChatEvent list =
    let ctx: WireCtx =
        {
            Http =
                {
                    Send = fun _ _ -> Task.FromResult(Error(HttpError.Refused "no network"))
                }
            Provider = Provider.def
            Family = Provider.Family
            Endpoint = Provider.Endpoint
            Model = sol
            Info = ProviderDef.model sol Provider.def
            Timeouts = HttpTimeouts.none
            Clock = (FakeClock.Create()).Clock
            Trace = Trace.none
            Scope = Scope.root
        }

    let parser = SseParser()
    let mutable state = Decoder.initial ctx
    let events = ResizeArray<ChatEvent>()

    match parser.Push(Text.UTF8Encoding(false).GetBytes text) with
    | Ok parsed ->
        for event in parsed do
            let decoded = Decoder.decode (state, event)
            state <- decoded.State
            events.AddRange decoded.Events
    | Error e -> failwith (string e)

    List.ofSeq events

/// An event of this kind with these members, written with `'` for `"`.
let private event (kind: string) (members: string) =
    kind, "{\"type\":\"" + kind + "\"," + members.Replace('\'', '"') + "}"

let private created =
    event "response.created" "'response':{'id':'resp_1','model':'gpt-6.2-sol','output':[],'usage':null}"

let decoding =
    Scenario.lines "models-openai-decode" (fun () ->
        let cases =
            [
                "hosted item, done-only text",
                [
                    created
                    event "response.output_item.added" "'output_index':0,'item':{'id':'ws','type':'web_search_call'}"
                    event "response.output_item.done" "'output_index':0,'item':{'id':'ws','type':'web_search_call'}"
                    event "response.output_text.done" "'output_index':1,'content_index':0,'text':'Found.'"
                    event
                        "response.output_item.done"
                        "'output_index':1,'item':{'type':'message','content':[{'type':'output_text','text':'Found.'},{'type':'refusal','refusal':'No.'}],'phase':'final_answer'}"
                    event "response.completed" "'response':{'usage':{'input_tokens':9,'output_tokens':2}}"
                ]
                "incomplete",
                [
                    created
                    event "response.output_item.added" "'output_index':0,'item':{'type':'reasoning','summary':[]}"
                    event "response.reasoning_text.delta" "'output_index':0,'content_index':0,'delta':'thinking'"
                    event "response.incomplete" "'response':{'incomplete_details':{'reason':'max_output_tokens'}}"
                ]
                "failed",
                [
                    created
                    event
                        "response.failed"
                        "'response':{'error':{'code':'rate_limit_exceeded','message':'Please try again in 1m6.24s.'},'usage':{'input_tokens':3,'output_tokens':0}}"
                ]
                "error event",
                [
                    created
                    event "error" "'code':'insufficient_quota','message':'quota','param':null"
                ]
                "broken",
                [
                    created
                    event "response.function_call_arguments.delta" "'output_index':4,'delta':'x'"
                ]
            ]

        [
            for name, events in cases do
                let decoded = decode (sse events)

                for e in decoded do
                    $"{name}: {eventJson e}"

                $"{name}: {show (result decoded)}"

            for reason in [ "max_output_tokens"; "content_filter"; "steered" ] do
                $"incomplete {reason}: {Decoder.incompleteReason reason}"
        ])

let errors =
    Scenario.lines "models-openai-errors" (fun () ->
        let openai (kind: string) (code: string) (message: string) =
            Json.serialize (
                Json.Object
                    [
                        "error",
                        Json.Object
                            [
                                "message", Json.String message
                                "type", Json.String kind
                                "param", Json.Null
                                "code", (if code = "" then Json.Null else Json.String code)
                            ]
                    ]
            )

        [
            for status, headers, body in
                [
                    429, [], openai "insufficient_quota" "insufficient_quota" "quota"
                    429, [ "retry-after", "3" ], openai "requests" "rate_limit_exceeded" "slow"
                    429, [], openai "rate_limit_error" "slow_down" "Please try again in 820ms."
                    400, [], openai "invalid_request_error" "context_length_exceeded" "too long"
                    400,
                    [],
                    openai
                        "invalid_request_error"
                        ""
                        "This model's maximum context length is 128000 tokens. However, your messages resulted in 130567 tokens."
                    400, [], openai "invalid_request_error" "invalid_prompt" "flagged"
                    401, [], openai "invalid_request_error" "invalid_api_key" "key"
                    403, [], openai "request_forbidden" "unsupported_country_region_territory" "region"
                    404, [], openai "invalid_request_error" "model_not_found" "model"
                    500, [], openai "server_error" "" "oops"
                    503, [ "retry-after-ms", "1500" ], openai "service_unavailable_error" "server_is_overloaded" "busy"
                    502, [], "<html>Bad Gateway</html>"
                    413, [], ""
                ] do
                let error =
                    Errors.ofResponse
                        {
                            Status = status
                            Headers = headers
                            Body = body
                        }

                $"{status}: {escape (string error)} (retryable {ChatError.isRetryable error}, compaction {ChatError.needsCompaction error})"
        ])

type private WriteFile = { Path: string; Create: bool }

let requests =
    Scenario.lines "models-openai-requests" (fun () ->
        let bodyOf (provider: ProviderDef) (request: ChatRequest) =
            match
                Request.body (Provider.Settings.ofProvider provider) (ProviderDef.model request.Model provider) request
            with
            | Ok body -> escape (Json.serializeCanonical body)
            | Error e -> $"error {e}"

        let writeFile: Codec<WriteFile> =
            Codec.record {
                let! path = Codec.field "path" (fun (w: WriteFile) -> w.Path) Codec.string
                and! create = Codec.defaultField "create" (fun w -> w.Create) false Codec.bool
                return { Path = path; Create = create }
            }

        let strictTool =
            { ToolSpec.ofCodec "write_file" "Writes a file." writeFile with
                Strict = true
            }

        let cached (model: ModelRef) =
            { ChatRequest.create
                  model
                  [
                      Message.user [ Part.text "map"; Part.CacheBreakpoint CacheTtl.Short ]
                      Message.assistant [ Part.text "ok" ]
                      Message.user [ Part.text "q"; Part.CacheBreakpoint CacheTtl.Long ]
                  ] with
                System = [ Part.text "sys"; Part.CacheBreakpoint CacheTtl.Long ]
                Tools = [ strictTool ]
            }

        [
            $"strict tool: {escape (Json.serializeCanonical (Request.tool strictTool))}"
            match Codec.decodeText writeFile """{"path": "a", "create": null}""" with
            | Ok w -> $"strict decode: path {w.Path}, create {w.Create}"
            | Error issues -> $"strict decode: {Issue.renderAtMost 3 issues}"
            $"sol: {bodyOf Provider.def (cached sol)}"
            $"gpt-5.5: {bodyOf Provider.def (cached gpt55)}"
            let instructions =
                { Provider.def with
                    Quirks = [ Provider.Quirk.System, Json.String "instructions" ]
                }

            $"instructions: {bodyOf instructions (cached sol)}"

            for effort in Effort.all do
                let reasoning =
                    Some
                        {
                            Effort = Some effort
                            BudgetTokens = Some 2048
                            Visible = effort = Effort.High
                        }

                let fitted =
                    match
                        Fit.request (ProviderDef.model astra Provider.def).Value { chat with Reasoning = reasoning }
                    with
                    | Ok(fitted, changes) ->
                        let used =
                            fitted.Reasoning
                            |> Option.bind (fun r -> r.Effort)
                            |> Option.map string
                            |> Option.defaultValue "-"

                        $"{used} ({changes.Length} changes)"
                    | Error e -> $"error {e}"

                let sent =
                    Request.reasoning reasoning
                    |> Option.map Json.serializeCanonical
                    |> Option.defaultValue "-"

                $"{effort}: {escape sent}, astra fitted to {fitted}"
        ])

let scenarios = [ replay; decoding; errors; requests ]
