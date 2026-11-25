/// The request mapping, option by option: what part of the Messages API body each canonical option
/// becomes (the conformance cases check whole bodies).
module Fx.Ext.ModelAnthropic.Tests.RequestTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelAnthropic
open Fx.Ext.ModelAnthropic.Tests.Support
open Fx.Models

let private settings = Provider.Settings.defaults

let private bodyWith (settings: Provider.Settings) (info: ModelInfo option) (chat: ChatRequest) : Json =
    match Request.body settings info chat with
    | Ok body -> body
    | Error e -> failtest $"no body: {e}"

let private body (chat: ChatRequest) = bodyWith settings None chat

let private member' (name: string) (json: Json) : Json option = Json.tryField name json

let private expectMember (name: string) (expected: string) (json: Json) =
    match member' name json with
    | Some value -> Expect.equal (Json.serializeCanonical value) (Json.serializeCanonical (Support.json expected)) name
    | None -> failtest $"no {name} in {Json.serialize json}"

let private expectNo (name: string) (json: Json) =
    Expect.isNone (member' name json) $"{name} in {Json.serialize json}"

let private chat (messages: Message list) = ChatRequest.create opus messages

let private hi = chat [ Message.userText "Hi" ]

let private content (json: Json) : Json list =
    match member' "messages" json with
    | Some(Json.Array messages) ->
        messages
        |> List.collect (fun m ->
            match member' "content" m with
            | Some(Json.Array blocks) -> blocks
            | _ -> [])
    | _ -> []

let private reasoning (effort: Effort option) (budget: int option) (visible: bool) =
    Some
        {
            Effort = effort
            BudgetTokens = budget
            Visible = visible
        }

let private thinking (info: ModelInfo option) (maxTokens: int) (r: ReasoningRequest option) =
    let thinking, effort = Request.thinking info maxTokens r
    thinking |> Option.map Json.serializeCanonical, effort

let private tool (name: string) : ToolSpec =
    {
        Name = name
        Description = ""
        Parameters = json """{"type": "object"}"""
        Strict = false
    }

let private signed (text: string) (data: string) : Part =
    Part.Reasoning
        {
            Text = text
            State = Some(state opus Request.SignatureKind data)
        }

/// The `cache_control` of each block in prompt order (tools, system, messages), `-` for none.
let private cacheMarks (json: Json) : string list =
    let marks (blocks: Json list) =
        blocks
        |> List.map (fun b ->
            match member' "cache_control" b with
            | Some control ->
                match member' "ttl" control with
                | Some(Json.String ttl) -> ttl
                | _ -> "5m"
            | None -> "-")

    let array name =
        match member' name json with
        | Some(Json.Array items) -> items
        | _ -> []

    marks (array "tools") @ marks (array "system") @ marks (content json)

[<Tests>]
let tests =
    testList
        "request"
        [
            test "a minimal request" {
                Expect.equal
                    (Json.serializeCanonical (body hi))
                    (Json.serializeCanonical (
                        json
                            """{"model": "claude-opus-5-5", "max_tokens": 16384, "messages": [{"role": "user", "content": [{"type": "text", "text": "Hi"}]}], "stream": true}"""
                    ))
                    "body"
            }

            test "max_tokens: the request's, else the catalog's output limit, else the quirk's default" {
                expectMember "max_tokens" "100" (body { hi with MaxOutputTokens = Some 100 })
                expectMember "max_tokens" "128000" (bodyWith settings (Some(info opus)) hi)
                expectMember "max_tokens" "64000" (bodyWith settings (Some(info haiku45)) hi)
                expectMember "max_tokens" "777" (bodyWith { settings with DefaultMaxTokens = 777 } None hi)
            }

            test "the HTTP request: the endpoint, the API version and beta headers, no credentials" {
                let provider =
                    { Provider.def with
                        Quirks =
                            [
                                Provider.Quirk.Version, Json.String "2023-06-01"
                                Provider.Quirk.Betas, json """["a-2026-01-01", "b-2026-02-02"]"""
                            ]
                    }

                match Request.http (ctx opus provider, hi) with
                | Ok request ->
                    Expect.equal request.Url "https://api.anthropic.com/v1/messages" "url"
                    Expect.equal request.Method "POST" "method"
                    Expect.equal (HttpRequest.header "anthropic-version" request) (Some "2023-06-01") "version"

                    Expect.equal
                        (HttpRequest.header "anthropic-beta" request)
                        (Some "a-2026-01-01,b-2026-02-02")
                        "betas"

                    Expect.equal (HttpRequest.header "accept" request) (Some "text/event-stream") "accept"
                    Expect.isNone (HttpRequest.header "x-api-key" request) "no key"
                    Expect.isNone (HttpRequest.header "authorization" request) "no key"
                | Error e -> failtest (string e)

                match Request.http (ctx opus Provider.def, hi) with
                | Ok request ->
                    Expect.equal (HttpRequest.header "anthropic-version" request) (Some Provider.ApiVersion) "version"
                    Expect.isNone (HttpRequest.header "anthropic-beta" request) "no betas"
                | Error e -> failtest (string e)
            }

            test "sampling: only stop sequences on 2026-10-21; temperature, top_p and top_k on older versions; no seed" {
                let sampled =
                    { hi with
                        Sampling =
                            {
                                Temperature = Some 0.5
                                TopP = Some 0.9
                                TopK = Some 40
                                Seed = Some 7L
                                Stop = [ "END" ]
                            }
                    }

                let current = body sampled
                expectMember "stop_sequences" """["END"]""" current

                for name in [ "temperature"; "top_p"; "top_k"; "seed" ] do
                    expectNo name current

                let older = bodyWith { settings with Version = "2023-06-01" } None sampled
                expectMember "temperature" "0.5" older
                expectMember "top_p" "0.9" older
                expectMember "top_k" "40" older
                expectNo "seed" older
            }

            test "metadata: only the user id" {
                expectMember
                    "metadata"
                    """{"user_id": "u-1"}"""
                    (body
                        { hi with
                            Metadata = [ "userId", "u-1"; "x", "y" ]
                        })

                expectNo "metadata" (body { hi with Metadata = [ "x", "y" ] })
            }

            testList
                "thinking"
                [
                    test "no reasoning request: the model's default" {
                        Expect.equal (thinking (Some(info opus)) 1000 None) (None, None) "nothing"
                    }

                    test "off: disabled, without an effort or display" {
                        Expect.equal
                            (thinking (Some(info haiku)) 1000 (reasoning (Some Effort.Off) None true))
                            (Some """{"type":"disabled"}""", None)
                            "disabled"
                    }

                    test "an adaptive model: adaptive thinking and the effort; visible asks for summaries" {
                        Expect.equal
                            (thinking (Some(info opus)) 16000 (reasoning (Some Effort.High) None true))
                            (Some """{"display":"summarized","type":"adaptive"}""", Some "high")
                            "high"

                        Expect.equal
                            (thinking None 16000 (reasoning (Some Effort.Minimal) None false))
                            (Some """{"type":"adaptive"}""", Some "low")
                            "minimal is low"

                        Expect.equal
                            (thinking (Some(info opus)) 16000 (reasoning None None true))
                            (Some """{"display":"summarized","type":"adaptive"}""", None)
                            "no effort"

                        for effort, name in
                            [
                                Effort.Low, "low"
                                Effort.Medium, "medium"
                                Effort.High, "high"
                                Effort.Max, "max"
                            ] do
                            Expect.equal (Request.effortName effort) name (string effort)
                    }

                    test "a budget-only model: extended thinking, the effort as a budget" {
                        let haiku45 = Some(info haiku45)

                        Expect.equal
                            (thinking haiku45 64000 (reasoning (Some Effort.Medium) None false))
                            (Some """{"budget_tokens":8192,"type":"enabled"}""", None)
                            "medium"

                        Expect.equal
                            (thinking haiku45 64000 (reasoning (Some Effort.Max) (Some 2000) true))
                            (Some """{"budget_tokens":2000,"display":"summarized","type":"enabled"}""", None)
                            "a budget wins over the effort"

                        Expect.equal
                            (thinking haiku45 64000 (reasoning None (Some 10) false))
                            (Some """{"budget_tokens":1024,"type":"enabled"}""", None)
                            "clamped to the model's range"

                        Expect.equal
                            (thinking haiku45 5000 (reasoning (Some Effort.High) None false))
                            (Some """{"budget_tokens":4999,"type":"enabled"}""", None)
                            "below max_tokens"

                        Expect.equal
                            (thinking haiku45 1000 (reasoning (Some Effort.High) None false))
                            (None, None)
                            "no room to think: left off"

                        Expect.equal
                            (thinking haiku45 64000 (reasoning None None false))
                            (Some """{"budget_tokens":8192,"type":"enabled"}""", None)
                            "neither: the medium budget"
                    }

                    test "a model the catalog does not know, with a budget: extended thinking" {
                        Expect.equal
                            (thinking None 16000 (reasoning (Some Effort.High) (Some 5000) false))
                            (Some """{"budget_tokens":5000,"type":"enabled"}""", None)
                            "enabled"
                    }

                    test "effort and structured output share output_config" {
                        let schema = json """{"type": "object"}"""

                        expectMember
                            "output_config"
                            """{"effort": "low", "format": {"type": "json_schema", "schema": {"type": "object"}}}"""
                            (body
                                { hi with
                                    Reasoning = reasoning (Some Effort.Low) None false
                                    Output = OutputFormat.Json("x", schema, false)
                                })
                    }
                ]

            testList
                "tools"
                [
                    test "definitions: name, description if any, input schema, strict if asked" {
                        expectMember
                            "tools"
                            """[{"name": "a", "input_schema": {"type": "object"}}, {"name": "b", "description": "B.", "input_schema": {"type": "object"}, "strict": true}]"""
                            (body
                                { hi with
                                    Tools =
                                        [
                                            tool "a"
                                            { tool "b" with
                                                Description = "B."
                                                Strict = true
                                            }
                                        ]
                                })
                    }

                    test "the tool choice, and parallel calls turned off" {
                        let withChoice choice parallelCalls =
                            body
                                { hi with
                                    Tools = [ tool "a" ]
                                    ToolChoice = choice
                                    ParallelToolCalls = parallelCalls
                                }

                        expectNo "tool_choice" (withChoice ToolChoice.Auto None)
                        expectNo "tool_choice" (withChoice ToolChoice.Auto (Some true))

                        expectMember
                            "tool_choice"
                            """{"type": "auto", "disable_parallel_tool_use": true}"""
                            (withChoice ToolChoice.Auto (Some false))

                        expectMember "tool_choice" """{"type": "none"}""" (withChoice ToolChoice.None (Some false))
                        expectMember "tool_choice" """{"type": "any"}""" (withChoice ToolChoice.Required None)

                        expectMember
                            "tool_choice"
                            """{"type": "tool", "name": "a", "disable_parallel_tool_use": true}"""
                            (withChoice (ToolChoice.Tool "a") (Some false))

                        expectNo
                            "tool_choice"
                            (body
                                { hi with
                                    ToolChoice = ToolChoice.Required
                                })
                    }

                    test "calls replay their arguments as the input object; invalid arguments become {}" {
                        let call (arguments: string) =
                            Part.ToolCall
                                {
                                    Id = "toolu_1"
                                    Name = "a"
                                    Arguments = arguments
                                    State = None
                                }

                        let inputs =
                            body (
                                chat
                                    [
                                        Message.userText "go"
                                        Message.assistant [ call "{\"n\": 1.50}"; call "{\"n\": 1"; call "" ]
                                    ]
                            )
                            |> content
                            |> List.choose (member' "input")
                            |> List.map Json.serialize

                        Expect.equal inputs [ "{\"n\":1.50}"; "{}"; "{}" ] "inputs"
                    }

                    test
                        "results: error flag, text and media content, empty content left out, before other user content" {
                        let blocks =
                            body (
                                chat
                                    [
                                        Message.user
                                            [
                                                Part.text "Both done."
                                                Part.ToolResult
                                                    {
                                                        CallId = "toolu_1"
                                                        Name = "a"
                                                        Content =
                                                            [
                                                                ToolContent.Text "failed"
                                                                ToolContent.Text ""
                                                                ToolContent.Media
                                                                    {
                                                                        Kind = MediaKind.Image
                                                                        MediaType = "image/gif"
                                                                        Sources = []
                                                                        Source =
                                                                            MediaSource.Url "https://example.com/a.gif"
                                                                        Name = None
                                                                    }
                                                            ]
                                                        IsError = true
                                                        Sources = []
                                                    }
                                                Part.ToolResult
                                                    {
                                                        CallId = "toolu_2"
                                                        Name = "a"
                                                        Content = []
                                                        IsError = false
                                                        Sources = []
                                                    }
                                            ]
                                    ]
                            )
                            |> content

                        Expect.equal
                            (blocks |> List.map Json.serializeCanonical)
                            ([
                                """{"type": "tool_result", "tool_use_id": "toolu_1", "content": [{"type": "text", "text": "failed"}, {"type": "image", "source": {"type": "url", "url": "https://example.com/a.gif"}}], "is_error": true}"""
                                """{"type": "tool_result", "tool_use_id": "toolu_2"}"""
                                """{"type": "text", "text": "Both done."}"""
                             ]
                             |> List.map (json >> Json.serializeCanonical))
                            "blocks"
                    }
                ]

            testList
                "thinking replay"
                [
                    test "signed and redacted thinking go back exactly as they came, in order" {
                        let data = "EqQBCg\u00e9+/=\n\"x\"" + String.replicate 2000 "A"

                        let blocks =
                            body (
                                chat
                                    [
                                        Message.userText "q"
                                        Message.assistant
                                            [
                                                Part.Reasoning
                                                    {
                                                        Text = ""
                                                        State = Some(state opus Request.RedactedKind "R==")
                                                    }
                                                signed "Thinking  with \u2028 odd\r\nspacing " data
                                                Part.text "a"
                                            ]
                                    ]
                            )
                            |> content

                        match blocks with
                        | [ _; redacted; thinking; _ ] ->
                            Expect.equal
                                (Json.serialize redacted)
                                (Json.serialize (json """{"type":"redacted_thinking","data":"R=="}"""))
                                "redacted"

                            Expect.equal (member' "signature" thinking) (Some(Json.String data)) "signature"

                            Expect.equal
                                (member' "thinking" thinking)
                                (Some(Json.String "Thinking  with \u2028 odd\r\nspacing "))
                                "text"
                        | other -> failtest $"{other.Length} blocks"
                    }

                    test "reasoning without this family's state is left out, and so is a message left empty" {
                        let foreign =
                            Part.Reasoning
                                {
                                    Text = "elsewhere"
                                    State =
                                        Some
                                            {
                                                Family = "openai.responses"
                                                Model = ModelRef.create "openai" "gpt-6"
                                                Kind = "encrypted"
                                                Data = "x"
                                            }
                                }

                        let plain = Part.Reasoning { Text = "no state"; State = None }

                        let sent =
                            body (
                                chat
                                    [
                                        Message.userText "q"
                                        Message.assistant [ foreign; plain ]
                                        Message.userText "again"
                                    ]
                            )

                        expectMember
                            "messages"
                            """[{"role": "user", "content": [{"type": "text", "text": "q"}]}, {"role": "user", "content": [{"type": "text", "text": "again"}]}]"""
                            sent
                    }

                    test "empty text and refusals are left out; other refusals are text" {
                        expectMember
                            "messages"
                            """[{"role": "user", "content": [{"type": "text", "text": "q"}]}, {"role": "assistant", "content": [{"type": "text", "text": "No."}]}]"""
                            (body (
                                chat
                                    [
                                        Message.user [ Part.text ""; Part.text "q" ]
                                        Message.assistant [ Part.Refusal ""; Part.Refusal "No." ]
                                    ]
                            ))
                    }
                ]

            testList
                "media"
                [
                    test "images and PDFs, base64 or URL" {
                        let part kind mediaType source name =
                            Part.Media
                                {
                                    Kind = kind
                                    MediaType = mediaType
                                    Sources = []
                                    Source = source
                                    Name = name
                                }

                        Expect.equal
                            (body (
                                chat
                                    [
                                        Message.user
                                            [
                                                part
                                                    MediaKind.Image
                                                    "image/jpeg"
                                                    (MediaSource.Url "https://example.com/a.jpg")
                                                    None
                                                part
                                                    MediaKind.Pdf
                                                    "application/pdf"
                                                    (MediaSource.Base64 "JVBERi0=")
                                                    None
                                            ]
                                    ]
                             )
                             |> content
                             |> List.map Json.serializeCanonical)
                            ([
                                """{"type": "image", "source": {"type": "url", "url": "https://example.com/a.jpg"}}"""
                                """{"type": "document", "source": {"type": "base64", "media_type": "application/pdf", "data": "JVBERi0="}}"""
                             ]
                             |> List.map (json >> Json.serializeCanonical))
                            "blocks"
                    }

                    test "media the API does not take, or not resolved yet, cannot be expressed" {
                        let media kind source =
                            Request.body
                                settings
                                None
                                (chat
                                    [
                                        Message.user
                                            [
                                                Part.Media
                                                    {
                                                        Kind = kind
                                                        MediaType = "x/y"
                                                        Sources = []
                                                        Source = source
                                                        Name = None
                                                    }
                                            ]
                                    ])

                        match media MediaKind.Audio (MediaSource.Base64 "AA==") with
                        | Error(Unavailable _) -> ()
                        | other -> failtest $"audio: {other}"

                        match media MediaKind.Image (MediaSource.Blob "sha256:00") with
                        | Error(Unavailable _) -> ()
                        | other -> failtest $"blob: {other}"

                        match
                            Request.body
                                settings
                                None
                                { hi with
                                    System = [ Part.Reasoning { Text = "x"; State = None } ]
                                }
                        with
                        | Error(Unavailable _) -> ()
                        | other -> failtest $"system: {other}"
                    }
                ]

            testList
                "cache breakpoints"
                [
                    test "the block before a breakpoint gets cache_control; one with nothing before it is dropped" {
                        Expect.equal
                            (body
                                { hi with
                                    System = [ Part.CacheBreakpoint CacheTtl.Short; Part.text "s" ]
                                    Messages = [ Message.user [ Part.text "q"; Part.CacheBreakpoint CacheTtl.Long ] ]
                                }
                             |> cacheMarks)
                            [ "-"; "1h" ]
                            "marks"
                    }

                    test "never on thinking: the block before it" {
                        Expect.equal
                            (body (
                                chat
                                    [
                                        Message.userText "q"
                                        Message.assistant
                                            [ Part.text "a"; signed "t" "sig"; Part.CacheBreakpoint CacheTtl.Short ]
                                    ]
                             )
                             |> cacheMarks)
                            [ "-"; "5m"; "-" ]
                            "marks"
                    }

                    test "the tools breakpoint goes on the last tool, and needs a tool" {
                        let cached =
                            { hi with
                                Cache =
                                    {
                                        Key = Some "ignored"
                                        Tools = Some CacheTtl.Short
                                    }
                            }

                        Expect.equal
                            (body
                                { cached with
                                    Tools = [ tool "a"; tool "b" ]
                                }
                             |> cacheMarks)
                            [ "-"; "5m"; "-" ]
                            "marks"

                        Expect.equal (body cached |> cacheMarks) [ "-" ] "no tools"
                    }

                    test "at most the quirk's number: the earliest are dropped" {
                        let many =
                            chat
                                [
                                    for i in 1..4 do
                                        Message.user [ Part.text $"m{i}"; Part.CacheBreakpoint CacheTtl.Short ]
                                ]

                        Expect.equal (body many |> cacheMarks) [ "5m"; "5m"; "5m"; "5m" ] "four"

                        Expect.equal
                            (bodyWith
                                { settings with
                                    MaxCacheBreakpoints = 2
                                }
                                None
                                many
                             |> cacheMarks)
                            [ "-"; "-"; "5m"; "5m" ]
                            "two"
                    }

                    test "a 1-hour breakpoint after a 5-minute one is a 5-minute one" {
                        Expect.equal
                            (body
                                { hi with
                                    System =
                                        [
                                            Part.text "a"
                                            Part.CacheBreakpoint CacheTtl.Long
                                            Part.text "b"
                                            Part.CacheBreakpoint CacheTtl.Short
                                            Part.text "c"
                                            Part.CacheBreakpoint CacheTtl.Long
                                        ]
                                }
                             |> cacheMarks)
                            [ "1h"; "5m"; "5m"; "-" ]
                            "marks"
                    }
                ]
        ]
