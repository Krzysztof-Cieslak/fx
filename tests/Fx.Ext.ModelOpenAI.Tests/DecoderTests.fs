/// The stream decoder over the documented event streams: the example stream of the models module,
/// every case's stream in pieces of any size, and the events and orders the API may send.
module Fx.Ext.ModelOpenAI.Tests.DecoderTests

open System
open System.IO
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models
open Fx.Sdk

let private sse (events: (string * string) list) : string =
    events
    |> List.map (fun (name, data) -> Sse.render (Sse.event name data))
    |> String.concat ""

let private created =
    "response.created",
    """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","model":"gpt-6.2-sol","status":"in_progress","output":[],"usage":null}}"""

let private completed =
    "response.completed",
    """{"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":4,"cache_write_tokens":2},"output_tokens":5,"output_tokens_details":{"reasoning_tokens":9}}}}"""

/// An event of this kind with these members, written with `'` for `"` (and `\\'` for `\\"`).
let private event (kind: string) (members: string) =
    kind, "{\"type\":\"" + kind + "\"," + members.Replace('\'', '"') + "}"

let private decodeSol (events: (string * string) list) =
    decode (ctx sol Provider.def) (sse events)

let private failure (events: ChatEvent list) : ChatError =
    match fold events with
    | Error failure -> failure.Error
    | Ok response -> failtest $"expected a failure, got {response}"

let private success (events: ChatEvent list) : ChatResponse =
    match fold events with
    | Ok response -> response
    | Error failure -> failtest $"expected a response, got {failure}"

[<Tests>]
let tests =
    testList
        "decoder"
        [
            test "the example stream of the models module (tests/Fx.Models.Tests/streams)" {
                let text =
                    File
                        .ReadAllText(Path.Combine(root, "tests", "Fx.Models.Tests", "streams", "openai-responses.sse"))
                        .Replace("\r\n", "\n")

                let model = ModelRef.create Provider.Id "gpt-5.1"
                let events = decode (ctx model Provider.def) text
                Expect.isEmpty (ChatEvents.check events) "the rules"
                let response = success events

                Expect.equal response.Id (Some "resp_67ccd2bed1ec8190b14f964abc0542670bb6a6b452d3795b") "id"
                Expect.equal response.Model (Some "gpt-5.1") "model"
                Expect.equal response.Stop StopReason.ToolUse "stop"

                Expect.equal
                    response.Usage
                    {
                        Input = 328
                        Output = 148
                        CacheRead = 256
                        CacheWrite = 0
                        Reasoning = 64
                    }
                    "usage"

                match response.Parts with
                | [ Part.Reasoning r; Part.Text t; Part.ToolCall c ] ->
                    Expect.equal r.Text "**Checking the weather**" "summary"

                    Expect.equal
                        (r.State
                         |> Option.map (fun s -> json s.Data |> Json.tryField "encrypted_content"))
                        (Some(Some(Json.String "gAAAAABoEXAMPLEencryptedReasoningContent0123456789abcdef==")))
                        "encrypted reasoning"

                    Expect.equal t.Text "Checking Kraków." "text"
                    Expect.isNone t.State "no phase"

                    Expect.equal
                        (c.Id, c.Name, c.Arguments)
                        ("call_9UvGB0hWPVgaRaA3E2jqDhZp", "get_weather", "{\"location\":\"Kraków\"}")
                        "call"
                | parts -> failtest $"parts: {parts}"
            }

            test "every case's stream gives the same events in pieces of any size" {
                for file in Directory.GetFiles(sourceDirectory "sources", "*.sse") |> Array.sort do
                    let text = File.ReadAllText(file).Replace("\r\n", "\n")
                    let context = ctx sol Provider.def
                    let whole = ChatEvents.coalesce (decode context text)

                    for size in [ 1; 2; 5; 17; 128 ] do
                        Expect.equal
                            (ChatEvents.coalesce (decodeIn size context text))
                            whole
                            $"{Path.GetFileName file} in pieces of {size}"
            }

            test "text that comes only in done events is sent once" {
                let events =
                    decodeSol
                        [
                            created
                            event
                                "response.output_item.added"
                                "'output_index':0,'item':{'id':'m','type':'message','content':[],'role':'assistant'}"
                            event "response.output_text.done" "'output_index':0,'content_index':0,'text':'Whole.'"
                            event
                                "response.output_item.done"
                                "'output_index':0,'item':{'id':'m','type':'message','content':[{'type':'output_text','text':'Whole.'},{'type':'output_text','text':' More.'}],'role':'assistant','phase':'final_answer'}"
                            completed
                        ]

                let response = success events

                Expect.equal
                    response.Parts
                    [
                        Part.Text
                            {
                                Text = "Whole."
                                State = Some(state sol Request.PhaseKind "final_answer")
                            }
                        Part.Text
                            {
                                Text = " More."
                                State = Some(state sol Request.PhaseKind "final_answer")
                            }
                    ]
                    "two parts, each once, with the item's phase"
            }

            test "items that were never added, and arguments only in the done item" {
                let response =
                    success (
                        decodeSol
                            [
                                created
                                event
                                    "response.output_item.done"
                                    "'output_index':0,'item':{'id':'rs','type':'reasoning','summary':[{'type':'summary_text','text':'A'},{'type':'summary_text','text':''},{'type':'summary_text','text':'B'}],'encrypted_content':'E'}"
                                event
                                    "response.output_item.done"
                                    "'output_index':1,'item':{'id':'fc','type':'function_call','arguments':'{\\'a\\':1}','call_id':'call_1','name':'f'}"
                                completed
                            ]
                    )

                match response.Parts with
                | [ Part.Reasoning r; Part.ToolCall c ] ->
                    Expect.equal r.Text "A\n\nB" "the summaries, joined"
                    Expect.equal (r.State |> Option.map (fun s -> s.Kind)) (Some Request.ReasoningKind) "state"
                    Expect.equal c.Arguments "{\"a\":1}" "arguments"
                | parts -> failtest $"parts: {parts}"

                Expect.equal response.Stop StopReason.ToolUse "tool use"
            }

            test
                "reasoning: summary parts and reasoning content joined by a blank line; no state without encrypted content" {
                let response =
                    success (
                        decodeSol
                            [
                                created
                                event
                                    "response.output_item.added"
                                    "'output_index':0,'item':{'id':'rs','type':'reasoning','summary':[]}"
                                event
                                    "response.reasoning_summary_text.delta"
                                    "'output_index':0,'summary_index':0,'delta':'One'"
                                event
                                    "response.reasoning_summary_text.delta"
                                    "'output_index':0,'summary_index':0,'delta':' more'"
                                event
                                    "response.reasoning_summary_part.added"
                                    "'output_index':0,'summary_index':1,'part':{'type':'summary_text','text':''}"
                                event
                                    "response.reasoning_summary_text.delta"
                                    "'output_index':0,'summary_index':1,'delta':'Two'"
                                event "response.reasoning_text.delta" "'output_index':0,'content_index':0,'delta':'raw'"
                                event
                                    "response.output_item.done"
                                    "'output_index':0,'item':{'id':'rs','type':'reasoning','summary':[]}"
                                completed
                            ]
                    )

                Expect.equal
                    response.Parts
                    [
                        Part.Reasoning
                            {
                                Text = "One more\n\nTwo\n\nraw"
                                State = None
                            }
                    ]
                    "the part"
            }

            test "hosted tools' items leave no gap; unknown events are ignored" {
                let response =
                    success (
                        decodeSol
                            [
                                created
                                event "response.queued" "'response':{}"
                                event
                                    "response.output_item.added"
                                    "'output_index':0,'item':{'id':'ws','type':'web_search_call','status':'in_progress'}"
                                event "response.web_search_call.searching" "'output_index':0,'item_id':'ws'"
                                event
                                    "response.output_item.done"
                                    "'output_index':0,'item':{'id':'ws','type':'web_search_call','status':'completed'}"
                                event
                                    "response.output_text.delta"
                                    "'output_index':1,'content_index':0,'delta':'Found it.'"
                                event
                                    "response.output_text.annotation.added"
                                    "'output_index':1,'content_index':0,'annotation':{}"
                                event "response.something_new" "'output_index':7"
                                event
                                    "response.output_item.done"
                                    "'output_index':1,'item':{'id':'m','type':'message','content':[{'type':'output_text','text':'Found it.'}],'role':'assistant'}"
                                completed
                            ]
                    )

                Expect.equal response.Parts [ Part.text "Found it." ] "part 0"
                Expect.equal response.Stop StopReason.EndTurn "end turn"
            }

            test "usage: cached tokens and cache writes are part of the input; reasoning at most the output" {
                let response = success (decodeSol [ created; completed ])

                Expect.equal
                    response.Usage
                    {
                        Input = 10
                        Output = 5
                        CacheRead = 4
                        CacheWrite = 2
                        Reasoning = 5
                    }
                    "usage"

                Expect.equal (Decoder.usage Json.Null) None "no usage"
            }

            test "incomplete responses: open parts end, the reason is the stop" {
                for reason, stop in
                    [
                        "max_output_tokens", StopReason.MaxTokens
                        "content_filter", StopReason.ContentFilter
                        "max_messages", StopReason.Other "max_messages"
                    ] do
                    let events =
                        decodeSol
                            [
                                created
                                event
                                    "response.output_item.added"
                                    "'output_index':0,'item':{'id':'fc','type':'function_call','arguments':'','call_id':'c','name':'f'}"
                                event "response.function_call_arguments.delta" "'output_index':0,'delta':'{\\'a\\':'"
                                event
                                    "response.incomplete"
                                    ("'response':{'incomplete_details':{'reason':'" + reason + "'}}")
                            ]

                    Expect.isEmpty (ChatEvents.check events) "the rules"
                    let response = success events
                    Expect.equal response.Stop stop reason

                    Expect.equal
                        response.Parts
                        [
                            Part.ToolCall
                                {
                                    Id = "c"
                                    Name = "f"
                                    Arguments = "{\"a\":"
                                    State = None
                                }
                        ]
                        "cut off"
            }

            test "failures: response.failed with its usage, error events of both shapes" {
                let failed =
                    decodeSol
                        [
                            created
                            event
                                "response.failed"
                                "'response':{'status':'failed','error':{'code':'rate_limit_exceeded','message':'Slow down. Please try again in 1.5s.'},'usage':{'input_tokens':3,'output_tokens':1}}"
                        ]

                Expect.isEmpty (ChatEvents.check failed) "the rules"

                Expect.equal
                    (failure failed)
                    (RateLimited
                        {
                            RetryAfter = Some(TimeSpan.FromSeconds 1.5)
                            Message = "Slow down. Please try again in 1.5s."
                        })
                    "rate limited"

                Expect.contains
                    failed
                    (ChatEvent.UsageUpdated
                        { Usage.zero with
                            Input = 3
                            Output = 1
                        })
                    "usage"

                Expect.equal
                    (failure (
                        decodeSol
                            [
                                created
                                event "error" "'code':'server_is_overloaded','message':'Busy','param':null"
                            ]
                    ))
                    (Overloaded { RetryAfter = None; Message = "Busy" })
                    "an error event"

                Expect.equal
                    (failure (
                        decodeSol
                            [
                                event
                                    "error"
                                    "'error':{'type':'invalid_request_error','code':'context_length_exceeded','message':'Too long'}"
                            ]
                    ))
                    (ContextOverflow
                        {
                            Tokens = None
                            Limit = None
                            Message = "Too long"
                        })
                    "an error event with an error object"

                Expect.equal
                    (failure (decodeSol [ created; event "response.failed" "'response':{'status':'failed'}" ]))
                    (ServerError(500, "the response failed without an error"))
                    "a failure without an error"
            }

            test "broken streams are invalid responses" {
                let invalid (events: (string * string) list) =
                    match failure (decodeSol events) with
                    | InvalidResponse _ -> ()
                    | other -> failtest $"{other}"

                invalid [ "response.created", "{not json" ]

                invalid
                    [
                        event "response.output_item.added" "'output_index':0,'item':{'type':'reasoning'}"
                        event "response.output_item.added" "'output_index':0,'item':{'type':'reasoning'}"
                    ]

                invalid
                    [
                        event "response.function_call_arguments.delta" "'output_index':3,'delta':'x'"
                    ]

                invalid
                    [
                        event "response.output_item.added" "'output_index':0,'item':{'type':'function_call'}"
                    ]

                invalid [ event "response.output_text.delta" "'delta':'x'" ]

                invalid
                    [
                        event "response.output_item.added" "'output_index':0,'item':{'id':'rs','type':'reasoning'}"
                        event "response.output_text.delta" "'output_index':0,'content_index':0,'delta':'x'"
                    ]

                invalid
                    [
                        event "response.output_item.added" "'output_index':0,'item':{'id':'rs','type':'reasoning'}"
                        event "response.output_item.done" "'output_index':0,'item':{'id':'rs','type':'reasoning'}"
                        event "response.output_item.done" "'output_index':0,'item':{'id':'rs','type':'reasoning'}"
                    ]

                // A stream that ends without a terminal event: the driver fails it (`End` adds nothing).
                let unfinished = decodeSol [ created ]
                Expect.isEmpty (OpenAI.adapter.End(Decoder.initial (ctx sol Provider.def))) "End"
                Expect.isError (fold unfinished) "unfinished"
            }

            test "a second response.created is ignored; the response's id and model come from the first" {
                let response =
                    success (
                        decodeSol
                            [
                                created
                                "response.created",
                                """{"type":"response.created","response":{"id":"resp_2","model":"other"}}"""
                                completed
                            ]
                    )

                Expect.equal (response.Id, response.Model) (Some "resp_1", Some "gpt-6.2-sol") "the first"
            }
        ]
