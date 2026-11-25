/// Canonical requests as Responses API requests, option by option (the conformance cassettes check
/// whole bodies): the system prompt, items in order, reasoning replay byte for byte, phases, tool
/// results, media, tools and tool choice, reasoning options, sampling, metadata, cache keys and
/// cache breakpoints (also through `ModelAccess` with the catalog), and the HTTP request.
module Fx.Ext.ModelOpenAI.Tests.RequestTests

open System
open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models
open Fx.Sdk

/// The provider with these quirks.
let private withQuirks (quirks: (string * Json) list) : ProviderDef = { Provider.def with Quirks = quirks }

/// A provider of the family that knows no models.
let private gateway: ProviderDef =
    { ProviderDef.create "gateway" Provider.Family "https://gateway.example.test/v1" with
        Quirks = []
    }

let private bodyWith (provider: ProviderDef) (chat: ChatRequest) : Json =
    match bodyFor provider chat with
    | Ok body -> body
    | Error e -> failtest $"no body: {e}"

let private body (chat: ChatRequest) : Json = bodyWith Provider.def chat

let private field (name: string) (json: Json) : Json option = Json.tryField name json

let private input (body: Json) : Json list =
    match field "input" body with
    | Some(Json.Array items) -> items
    | _ -> failtest "no input"

let private canonical (json: Json option) : string =
    json |> Option.map Json.serializeCanonical |> Option.defaultValue "-"

let private text (value: string) = Json.String value

let private reasoningState (model: ModelRef) (item: string) : Part =
    Part.Reasoning
        {
            Text = "summary"
            State = Some(state model Request.ReasoningKind item)
        }

let private failsWith (chat: ChatRequest) =
    match bodyFor Provider.def chat with
    | Ok _ -> failtest "expected an error"
    | Error e -> e

/// The breakpoints of the input's blocks, in order: (item, block, ttl or "30m").
let private marks (body: Json) : string list =
    [
        for i, item in List.indexed (input body) do
            let blocks =
                match field "content" item, field "output" item with
                | Some(Json.Array blocks), _
                | _, Some(Json.Array blocks) -> blocks
                | _ -> []

            for b, block in List.indexed blocks do
                match field "prompt_cache_breakpoint" block with
                | Some mark ->
                    let ttl =
                        match field "ttl" mark with
                        | Some(Json.String ttl) -> ttl
                        | _ -> "30m"

                    $"{i}.{b} {ttl}"
                | None -> ()
    ]

[<Tests>]
let tests =
    testList
        "request"
        [
            test "a minimal request: the input, stateless and streamed, with encrypted reasoning asked for" {
                let body = body (ChatRequest.create sol [ Message.userText "Hi" ])

                expectJson
                    body
                    (json
                        """{"model": "gpt-6.2-sol",
                            "input": [{"role": "user", "content": [{"type": "input_text", "text": "Hi"}]}],
                            "include": ["reasoning.encrypted_content"], "store": false, "stream": true}""")
                    "the body"
            }

            test "the system prompt is a developer message first, or `instructions` with the quirk" {
                let chat =
                    { ChatRequest.create sol [ Message.userText "Hi" ] with
                        System =
                            [
                                Part.text "Be brief."
                                Part.text ""
                                Part.CacheBreakpoint CacheTtl.Long
                                Part.text "Be kind."
                            ]
                    }

                let developer = body chat

                expectJson
                    (input developer).Head
                    (json
                        """{"role": "developer", "content": [
                             {"type": "input_text", "text": "Be brief.", "prompt_cache_breakpoint": {"mode": "explicit", "ttl": "12h"}},
                             {"type": "input_text", "text": "Be kind."}]}""")
                    "a developer message, with the breakpoint"

                Expect.isNone (field "instructions" developer) "no instructions"

                let instructions =
                    bodyWith (withQuirks [ Provider.Quirk.System, text "instructions" ]) chat

                Expect.equal (field "instructions" instructions) (Some(text "Be brief.\n\nBe kind.")) "instructions"
                Expect.equal (input instructions).Length 1 "only the user's message in the input"

                let ignored = bodyWith (withQuirks [ Provider.Quirk.System, text "system" ]) chat
                Expect.isNone (field "instructions" ignored) "an unknown placement is ignored"

                let media =
                    failsWith
                        { chat with
                            System =
                                [
                                    Part.Media
                                        {
                                            Kind = MediaKind.Image
                                            MediaType = "image/png"
                                            Source = MediaSource.Url "https://example.com/a.png"
                                            Name = None
                                        }
                                ]
                        }

                Expect.isTrue
                    (match media with
                     | Unavailable _ -> true
                     | _ -> false)
                    $"media in the system prompt: {media}"
            }

            test "reasoning items are replayed exactly as they came, other reasoning is left out" {
                let item =
                    Cases.reasoningItem
                        "rs_1"
                        [ "**A**\n\nOne."; "**B**" ]
                        ("gAAAA" + String('x', 100_000) + "\u0001\u2028\"\\/é🦀")

                let chat =
                    ChatRequest.create
                        sol
                        [
                            Message.userText "Hi"
                            Message.assistant
                                [
                                    reasoningState sol item
                                    // Another family's thinking, reasoning without state, state that is
                                    // not a reasoning item: none can be sent back.
                                    Part.Reasoning
                                        {
                                            Text = "thinking"
                                            State =
                                                Some
                                                    {
                                                        Family = "anthropic.messages"
                                                        Model = ModelRef.create "anthropic" "claude-opus-5-5"
                                                        Kind = "signature"
                                                        Data = "sig"
                                                    }
                                        }
                                    Part.Reasoning { Text = "plain"; State = None }
                                    Part.Reasoning
                                        {
                                            Text = "bad"
                                            State = Some(state sol Request.ReasoningKind "not json")
                                        }
                                    Part.Reasoning
                                        {
                                            Text = "message"
                                            State = Some(state sol Request.ReasoningKind """{"type": "message"}""")
                                        }
                                    Part.text "Done."
                                ]
                            Message.userText "Thanks"
                        ]

                let items = input (body chat)
                Expect.equal items.Length 4 "user, reasoning, assistant, user"
                let replayed = items[1]
                Expect.equal (Json.serialize replayed) item "the item, byte for byte"

                match field "encrypted_content" replayed, json item |> field "encrypted_content" with
                | Some(Json.String sent), Some(Json.String original) ->
                    Expect.isTrue (String.Equals(sent, original, StringComparison.Ordinal)) "ordinal equality"
                | _ -> failtest "no encrypted content"
            }

            test "assistant text carries its phase; refusals and tool calls are items in order" {
                let chat =
                    ChatRequest.create
                        sol
                        [
                            Message.userText "Hi"
                            Message.assistant
                                [
                                    Part.Text
                                        {
                                            Text = "Looking."
                                            State = Some(state sol Request.PhaseKind "commentary")
                                        }
                                    Part.ToolCall
                                        {
                                            Id = "call_1"
                                            Name = "ls"
                                            Arguments = "{\"path\": \"."
                                            State = None
                                        }
                                    Part.Text
                                        {
                                            Text = "Other family."
                                            State =
                                                Some
                                                    {
                                                        Family = "anthropic.messages"
                                                        Model = ModelRef.create "anthropic" "claude-opus-5-5"
                                                        Kind = Request.PhaseKind
                                                        Data = "commentary"
                                                    }
                                        }
                                    Part.Refusal "No."
                                    Part.text ""
                                ]
                        ]

                expectJson
                    (Json.Array(input (body chat) |> List.tail))
                    (json
                        """[{"role": "assistant", "content": "Looking.", "phase": "commentary"},
                            {"type": "function_call", "call_id": "call_1", "name": "ls", "arguments": "{\"path\": \"."},
                            {"role": "assistant", "content": "Other family."},
                            {"role": "assistant", "content": "No."}]""")
                    "the items; arguments as the model wrote them"
            }

            test "tool results: outputs first, plain text as a string, media as blocks" {
                let image: MediaPart =
                    {
                        Kind = MediaKind.Image
                        MediaType = "image/jpeg"
                        Source = MediaSource.Base64 "AAAA"
                        Name = None
                    }

                let chat =
                    ChatRequest.create
                        sol
                        [
                            Message.user
                                [
                                    Part.text "See these."
                                    Part.ToolResult
                                        {
                                            CallId = "c1"
                                            Name = "a"
                                            Content = [ ToolContent.Text "one" ]
                                            IsError = false
                                        }
                                    Part.ToolResult
                                        {
                                            CallId = "c2"
                                            Name = "b"
                                            Content = [ ToolContent.Text "shot:"; ToolContent.Media image ]
                                            IsError = true
                                        }
                                    Part.ToolResult
                                        {
                                            CallId = "c3"
                                            Name = "c"
                                            Content = []
                                            IsError = false
                                        }
                                ]
                        ]

                expectJson
                    (Json.Array(input (body chat)))
                    (json
                        """[{"type": "function_call_output", "call_id": "c1", "output": "one"},
                            {"type": "function_call_output", "call_id": "c2", "output": [
                              {"type": "input_text", "text": "shot:"},
                              {"type": "input_image", "image_url": "data:image/jpeg;base64,AAAA", "detail": "auto"}]},
                            {"type": "function_call_output", "call_id": "c3", "output": ""},
                            {"role": "user", "content": [{"type": "input_text", "text": "See these."}]}]""")
                    "the items"
            }

            test "media: images and PDFs from base64 or URLs; blobs and other kinds are unavailable" {
                let media (kind: MediaKind) (mediaType: string) (source: MediaSource) (name: string option) =
                    Part.Media
                        {
                            Kind = kind
                            MediaType = mediaType
                            Source = source
                            Name = name
                        }

                let chat =
                    ChatRequest.create
                        sol
                        [
                            Message.user
                                [
                                    media MediaKind.Pdf "application/pdf" (MediaSource.Base64 "JVBE") None
                                    media MediaKind.Pdf "application/pdf" (MediaSource.Base64 "JVBE") (Some "a.pdf")
                                    media MediaKind.Image "image/png" (MediaSource.Url "https://example.com/a.png") None
                                ]
                        ]

                expectJson
                    (input (body chat)).Head
                    (json
                        """{"role": "user", "content": [
                             {"type": "input_file", "filename": "document.pdf", "file_data": "data:application/pdf;base64,JVBE"},
                             {"type": "input_file", "filename": "a.pdf", "file_data": "data:application/pdf;base64,JVBE"},
                             {"type": "input_image", "image_url": "https://example.com/a.png", "detail": "auto"}]}""")
                    "the blocks"

                for part, role in
                    [
                        media MediaKind.Image "image/png" (MediaSource.Blob "sha256:00") None, Role.User
                        media MediaKind.Audio "audio/wav" (MediaSource.Base64 "UklG") None, Role.User
                        media MediaKind.Image "image/png" (MediaSource.Base64 "AAAA") None, Role.Assistant
                    ] do
                    match failsWith (ChatRequest.create sol [ { Role = role; Parts = [ part ] } ]) with
                    | Unavailable _ -> ()
                    | other -> failtest $"{part} from the {role}: {other}"
            }

            test "tools, tool choice and parallel calls" {
                let tool: ToolSpec =
                    {
                        Name = "ls"
                        Description = ""
                        Parameters = json """{"type": "object", "properties": {"path": {"type": "string"}}}"""
                        Strict = false
                    }

                let chat =
                    { ChatRequest.create sol [ Message.userText "Hi" ] with
                        Tools = [ tool ]
                    }

                expectJson
                    (field "tools" (body chat)).Value
                    (json
                        """[{"type": "function", "name": "ls", "parameters": {"type": "object", "properties": {"path": {"type": "string"}}}, "strict": false}]""")
                    "strict is always sent; no empty description"

                let choice (choice: ToolChoice) =
                    field "tool_choice" (body { chat with ToolChoice = choice }) |> canonical

                Expect.equal (choice ToolChoice.Auto) "-" "auto: the default"
                Expect.equal (choice ToolChoice.None) "\"none\"" "none"
                Expect.equal (choice ToolChoice.Required) "\"required\"" "required"
                Expect.equal (choice (ToolChoice.Tool "ls")) """{"name":"ls","type":"function"}""" "a function"

                let noTools =
                    body
                        { ChatRequest.create sol [ Message.userText "Hi" ] with
                            ToolChoice = ToolChoice.Required
                            ParallelToolCalls = Some false
                        }

                Expect.isNone (field "tool_choice" noTools) "no choice without tools"
                Expect.isNone (field "parallel_tool_calls" noTools) "no parallel calls without tools"

                Expect.equal
                    (field
                        "parallel_tool_calls"
                        (body
                            { chat with
                                ParallelToolCalls = Some false
                            }))
                    (Some(Json.Bool false))
                    "parallel calls off"
            }

            test "reasoning: efforts by name, summaries when visible, budgets nowhere" {
                let reasoning (effort: Effort option) (visible: bool) =
                    Request.reasoning (
                        Some
                            {
                                Effort = effort
                                BudgetTokens = Some 4096
                                Visible = visible
                            }
                    )
                    |> canonical

                Expect.equal (Request.reasoning None) None "the model's default"
                Expect.equal (reasoning None false) "-" "nothing to say"
                Expect.equal (reasoning None true) """{"summary":"auto"}""" "summaries only"

                for effort, name in
                    [
                        Effort.Off, "none"
                        Effort.Minimal, "minimal"
                        Effort.Low, "low"
                        Effort.Medium, "medium"
                        Effort.High, "high"
                        Effort.Max, "max"
                    ] do
                    Expect.equal (reasoning (Some effort) false) $$"""{"effort":"{{name}}"}""" $"{effort}"

                Expect.equal (reasoning (Some Effort.High) true) """{"effort":"high","summary":"auto"}""" "both"
            }

            test "encrypted reasoning is asked for unless the catalog says the model does not reason" {
                let plain =
                    { Provider.def with
                        Catalog =
                            {
                                Models =
                                    [
                                        { ModelInfo.create
                                              (ModelRef.create Provider.Id "plain")
                                              { Context = 1000; Output = 100 } with
                                            Family = Some Provider.Family
                                        }
                                    ]
                                Discover = None
                            }
                    }

                let included (provider: ProviderDef) (model: ModelRef) =
                    bodyWith provider (ChatRequest.create model [ Message.userText "Hi" ])
                    |> field "include"
                    |> canonical

                Expect.equal (included Provider.def sol) """["reasoning.encrypted_content"]""" "a reasoning model"

                Expect.equal
                    (included gateway (ModelRef.create "gateway" "any"))
                    """["reasoning.encrypted_content"]"""
                    "unknown"

                Expect.equal (included plain (ModelRef.create Provider.Id "plain")) "-" "a model that does not reason"
            }

            test "sampling: temperature and top-p only; output formats; the output limit" {
                let chat =
                    { ChatRequest.create (ModelRef.create "gateway" "any") [ Message.userText "Hi" ] with
                        Sampling =
                            {
                                Temperature = Some 0.25
                                TopP = Some 0.9
                                TopK = Some 40
                                Seed = Some 7L
                                Stop = [ "###" ]
                            }
                        MaxOutputTokens = Some 77
                        Output =
                            OutputFormat.Json(
                                "a name with spaces/and slashes",
                                json """{"type": "object", "properties": {"a": {"type": "string", "minLength": 2}}}""",
                                true
                            )
                    }

                let body = bodyWith gateway chat
                Expect.equal (field "temperature" body) (Some(Json.float 0.25)) "temperature"
                Expect.equal (field "top_p" body) (Some(Json.float 0.9)) "top_p"

                for name in [ "top_k"; "seed"; "stop"; "stop_sequences" ] do
                    Expect.isNone (field name body) name

                Expect.equal (field "max_output_tokens" body) (Some(Json.int 77)) "the limit"

                expectJson
                    (field "text" body).Value
                    (json
                        """{"format": {"type": "json_schema", "name": "a_name_with_spaces_and_slashes",
                            "schema": {"type": "object", "properties": {"a": {"type": "string", "minLength": 2}}}, "strict": false}}""")
                    "a schema strict mode cannot take goes as it is, not strict"

                Expect.equal (Request.formatName "") "output" "an empty name"
                Expect.equal (Request.formatName (String('a', 80))).Length 64 "a long name"
            }

            test "metadata: the user is the safety identifier; what the API refuses is left out" {
                let chat =
                    { ChatRequest.create sol [ Message.userText "Hi" ] with
                        Metadata =
                            [
                                "userId", "u-1"
                                String('k', 65), "too long a key"
                                "long", String('v', 513)
                                yield! [ for i in 1..20 -> $"k{i}", $"v{i}" ]
                            ]
                    }

                let body = body chat
                Expect.equal (field "safety_identifier" body) (Some(text "u-1")) "the user"

                match field "metadata" body with
                | Some(Json.Object pairs) ->
                    Expect.equal (pairs |> List.map fst) [ for i in 1..16 -> $"k{i}" ] "16 pairs at most"
                | other -> failtest $"metadata: {other}"

                let none = bodyWith Provider.def (ChatRequest.create sol [ Message.userText "Hi" ])
                Expect.isNone (field "metadata" none) "no metadata"
                Expect.isNone (field "safety_identifier" none) "no user"
            }

            test "the cache key is sent for every model" {
                let chat =
                    { ChatRequest.create gpt55 [ Message.userText "Hi" ] with
                        Cache =
                            {
                                Key = Some "k"
                                Tools = Some CacheTtl.Long
                            }
                    }

                Expect.equal (field "prompt_cache_key" (body chat)) (Some(text "k")) "the key"

                Expect.equal
                    (field
                        "prompt_cache_key"
                        (bodyWith
                            gateway
                            { chat with
                                Model = ModelRef.create "gateway" "x"
                            }))
                    (Some(text "k"))
                    "unknown"
            }

            test "cache breakpoints mark the last block before them, for the models that take them" {
                let chat (model: ModelRef) =
                    { ChatRequest.create
                          model
                          [
                              Message.user
                                  [
                                      Part.CacheBreakpoint CacheTtl.Long
                                      Part.text "a"
                                      Part.CacheBreakpoint CacheTtl.Short
                                  ]
                              Message.assistant
                                  [
                                      Part.text "b"
                                      // After the assistant's text: the last block before it is the user's.
                                      Part.CacheBreakpoint CacheTtl.Long
                                  ]
                              Message.user
                                  [
                                      Part.ToolResult
                                          {
                                              CallId = "c"
                                              Name = "t"
                                              Content = [ ToolContent.Text "out" ]
                                              IsError = false
                                          }
                                      Part.CacheBreakpoint CacheTtl.Short
                                      Part.text "q"
                                      Part.CacheBreakpoint CacheTtl.Short
                                      Part.CacheBreakpoint CacheTtl.Long
                                  ]
                          ] with
                        System =
                            [
                                Part.CacheBreakpoint CacheTtl.Short
                                Part.text "s"
                                Part.CacheBreakpoint CacheTtl.Short
                            ]
                    }

                // A breakpoint before any block is dropped; one at the start of a message marks the
                // block before it (the system prompt's), and one after the assistant's text the
                // user's block before it, with the longer lifetime; a tool output with a breakpoint
                // is sent as blocks.
                let expected = [ "0.0 12h"; "1.0 12h"; "3.0 30m"; "4.0 12h" ]
                let catalog = body (chat sol)
                Expect.equal (marks catalog) expected "GPT-6.2 Sol"

                expectJson
                    (List.item 3 (input catalog))
                    (json
                        """{"type": "function_call_output", "call_id": "c", "output": [
                             {"type": "input_text", "text": "out", "prompt_cache_breakpoint": {"mode": "explicit"}}]}""")
                    "the output as blocks"

                Expect.isEmpty (marks (body (chat gpt55))) "GPT-5.5 caches implicitly only"

                let unknown = ModelRef.create "gateway" "new"
                Expect.isEmpty (marks (bodyWith gateway (chat unknown))) "a model the catalog does not know"

                Expect.equal
                    (marks (
                        bodyWith
                            { gateway with
                                Quirks = [ Provider.Quirk.CacheBreakpoints, Json.Bool true ]
                            }
                            (chat unknown)
                    ))
                    expected
                    "the quirk turns them on"

                Expect.isEmpty
                    (marks (bodyWith (withQuirks [ Provider.Quirk.CacheBreakpoints, Json.Bool false ]) (chat sol)))
                    "or off"
            }

            test "the HTTP request: the endpoint, the content type, and the organization and project quirks" {
                let provider =
                    withQuirks
                        [
                            Provider.Quirk.Organization, text "org-1"
                            Provider.Quirk.Project, text "proj_2"
                        ]

                match Request.http (ctx sol provider, ChatRequest.create sol [ Message.userText "Hi" ]) with
                | Ok request ->
                    Expect.equal request.Method "POST" "method"
                    Expect.equal request.Url "https://api.openai.com/v1/responses" "url"

                    for name, value in
                        [
                            "content-type", "application/json"
                            "accept", "text/event-stream"
                            "openai-organization", "org-1"
                            "openai-project", "proj_2"
                        ] do
                        Expect.equal (HttpHeaders.tryFind name request.Headers) (Some value) name

                    Expect.isNone (HttpHeaders.tryFind "authorization" request.Headers) "no credentials"
                | Error e -> failtest $"{e}"

                match Request.http (ctx sol Provider.def, ChatRequest.create sol [ Message.userText "Hi" ]) with
                | Ok request ->
                    Expect.isNone (HttpHeaders.tryFind "openai-organization" request.Headers) "no organization"
                    Expect.isNone (HttpHeaders.tryFind "openai-project" request.Headers) "no project"
                | Error e -> failtest $"{e}"
            }

            test "through ModelAccess with the catalog, breakpoints reach the wire (cassette)" {
                let chat = Cases.cachedConversation sol

                let cassette: Cassette =
                    {
                        Interactions =
                            [
                                {
                                    Request = Cases.recorded "cache-breakpoints.request.json"
                                    Outcome = Cases.stream "cache-breakpoints.sse"
                                }
                            ]
                    }

                for chunking in [ Chunking.Whole; Chunking.Every 1; Chunking.Every 13 ] do
                    let http = CassetteHttp(cassette, chunking)
                    let clock = (FakeClock.Create()).Clock
                    let access = ModelAccess.create [ OpenAI.wire ] [ Provider.def ] http.Http clock
                    let stream = wait ((ModelAccess.llm access).Stream chat CancellationToken.None)
                    let events = wait (ChatStream.collect stream CancellationToken.None)

                    match ChatAggregate.result events with
                    | Ok response ->
                        Expect.equal
                            response.Usage
                            {
                                Input = 4212
                                Output = 15
                                CacheRead = 2048
                                CacheWrite = 2150
                                Reasoning = 0
                            }
                            "cache reads and writes"

                        Expect.equal (ChatResponse.text response) "`src/Program.fs` defines `main`." "the answer"
                    | Error failure -> failtest $"{chunking}: {failure}"

                    Expect.isEmpty http.Remaining "the request matched"
                    Expect.equal http.OpenBodies 0 "closed"
            }
        ]
