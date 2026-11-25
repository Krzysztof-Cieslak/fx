/// CodeAct turns through `ModelAccess` and the real wire adapters (`anthropic.messages`,
/// `openai.responses`) with their providers' catalogs, on HTTP answered with event streams in each
/// provider's documented format: the strategy's requests (tools from codecs, tool results, the stable
/// prefix with cache breakpoints or cache key) encode, and the replies decode, end to end. No API keys.
module Fx.CodeAct.Tests.ProviderTests

open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Models
open Fx.Sdk
open Fx.Sessions
open Fx.CodeAct.Tests.Support

let private sse (events: (string * string) list) : string =
    events
    |> List.map (fun (name, data) -> $"event: {name}\ndata: {data}\n\n")
    |> String.concat ""

/// An Anthropic Messages stream with one tool call.
let private anthropicCall (n: int) (name: string) (arguments: string) : string =
    sse
        [
            "message_start",
            $"{{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_{n}\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":400,\"output_tokens\":1}}}}}}"
            "content_block_start",
            $"{{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{{\"type\":\"tool_use\",\"id\":\"toolu_{n}\",\"name\":\"{name}\",\"input\":{{}}}}}}"
            "content_block_delta",
            $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{Json.quote arguments}}}}}"
            "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}"
            "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":30}}"
            "message_stop", "{\"type\":\"message_stop\"}"
        ]

/// An OpenAI Responses stream with one function call.
let private openAICall (n: int) (name: string) (arguments: string) : string =
    let item (status: string) (args: string) =
        $"{{\"id\":\"fc_{n}\",\"type\":\"function_call\",\"status\":\"{status}\",\"arguments\":{Json.quote args},\"call_id\":\"call_{n}\",\"name\":\"{name}\"}}"

    let added = item "in_progress" ""
    let finished = item "completed" arguments

    sse
        [
            "response.created",
            $"{{\"type\":\"response.created\",\"sequence_number\":0,\"response\":{{\"id\":\"resp_{n}\",\"object\":\"response\",\"status\":\"in_progress\",\"model\":\"gpt-6.2-sol\",\"output\":[],\"store\":false,\"usage\":null}}}}"
            "response.output_item.added",
            $"{{\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{added}}}"
            "response.function_call_arguments.done",
            $"{{\"type\":\"response.function_call_arguments.done\",\"sequence_number\":2,\"item_id\":\"fc_{n}\",\"output_index\":0,\"arguments\":{Json.quote arguments}}}"
            "response.output_item.done",
            $"{{\"type\":\"response.output_item.done\",\"sequence_number\":3,\"output_index\":0,\"item\":{finished}}}"
            "response.completed",
            $"{{\"type\":\"response.completed\",\"sequence_number\":4,\"response\":{{\"id\":\"resp_{n}\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-6.2-sol\",\"output\":[],\"store\":false,\"usage\":{{\"input_tokens\":400,\"input_tokens_details\":{{\"cached_tokens\":0,\"cache_write_tokens\":0}},\"output_tokens\":30,\"output_tokens_details\":{{\"reasoning_tokens\":0}},\"total_tokens\":430}}}}}}"
        ]

let private cellArguments (code: string) =
    Json.serialize (Json.Object [ "code", Json.String code ])

/// The two steps of the turn: a cell that reads a file, then one that finishes.
let private cells =
    [
        cellArguments "// intent: read the notes\ncall n = fs.read [\"notes.md\"]\nn"
        cellArguments "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Read the notes\"}"
    ]

/// Runs the turn over a provider; returns the JSON bodies the wire sent.
let private through
    (name: string)
    (wire: ChatWire)
    (provider: ProviderDef)
    (model: string)
    (reply: int -> string -> string -> string)
    : Task<Json list> =
    task {
        let clock = Fx.Effects.Live.SystemClock.clock
        let mutable n = 0

        let http =
            InMemoryHttp(
                clock,
                fun _ ->
                    n <- n + 1

                    match List.tryItem (n - 1) cells with
                    | Some arguments ->
                        HttpReply.text 200 (reply n "cell" arguments)
                        |> HttpReply.withHeader "content-type" "text/event-stream"
                        |> Ok
                        |> Some
                    | None -> None
            )

        let access = ModelAccess.create [ wire ] [ provider ] http.Http clock

        let catalog (ref: ModelRef) =
            provider.Catalog.Models |> List.tryFind (fun m -> m.Ref = ref)

        use f =
            createOver
                name
                [ "model.role.main", Json.String $"{provider.Id}/{model}" ]
                (fun _ -> scriptKernel kernelOptions)
                (Some(ModelAccess.llm access))
                catalog

        f.Write("notes.md", "# Notes\n")
        do! start f "s1"
        do! prompt f "s1" "p1" "Read the notes"
        let! ending = ended f "s1" 1
        Expect.equal (completed ending) (TurnResult.Done("Read the notes", [], None)) "done"

        return
            http.Requests
            |> List.map (fun request ->
                match Json.parse (HttpRequest.bodyText request) with
                | Ok json -> json
                | Error e -> failtest $"{e}")
    }

let rec private find (name: string) (json: Json) : Json list =
    match json with
    | Json.Object members ->
        (members |> List.filter (fun (k, _) -> k = name) |> List.map snd)
        @ (members |> List.collect (fun (_, v) -> find name v))
    | Json.Array items -> items |> List.collect (find name)
    | _ -> []

let private field (name: string) (json: Json) : Json option =
    match json with
    | Json.Object members -> members |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd
    | _ -> None

let private strings (json: Json list) =
    json
    |> List.choose (fun j ->
        match j with
        | Json.String s -> Some s
        | _ -> None)

[<Tests>]
let tests =
    testList
        "CodeAct through the provider wires"
        [
            testTask "anthropic.messages: tool use and results, tools from codecs, cache breakpoints" {
                let! bodies =
                    through
                        "anthropic"
                        Fx.Ext.ModelAnthropic.Anthropic.wire
                        Fx.Ext.ModelAnthropic.Provider.def
                        "claude-sonnet-5-5"
                        anthropicCall

                match bodies with
                | [ first; second ] ->
                    Expect.equal (strings (find "name" (field "tools" first |> Option.get))) [ "cell" ] "the cell tool"
                    Expect.isNonEmpty (find "input_schema" first) "its schema"
                    Expect.isNonEmpty (find "cache_control" first) "cache breakpoints"
                    Expect.equal (field "system" first) (field "system" second) "a stable system prefix"
                    Expect.equal (strings (find "tool_use_id" second)) [ "toolu_1" ] "the result answers the call"
                    Expect.equal (strings (find "id" second)) [ "toolu_1" ] "the call, replayed"
                    Expect.stringContains (Json.serialize second) "# Notes" "the receipt"
                | other -> failtest $"{other.Length} requests"
            }

            testTask "openai.responses: function calls and outputs, tools from codecs, a stable cache key" {
                let! bodies =
                    through
                        "openai"
                        Fx.Ext.ModelOpenAI.OpenAI.wire
                        Fx.Ext.ModelOpenAI.Provider.def
                        "gpt-6.2-sol"
                        openAICall

                match bodies with
                | [ first; second ] ->
                    Expect.equal (strings (find "name" (field "tools" first |> Option.get))) [ "cell" ] "the cell tool"
                    Expect.equal (field "store" first) (Some(Json.Bool false)) "stateless"
                    let key = field "prompt_cache_key" first
                    Expect.isSome key "a cache key"
                    Expect.equal (field "prompt_cache_key" second) key "the same prefix, the same key"
                    Expect.equal (strings (find "call_id" second)) [ "call_1"; "call_1" ] "the call and its output"
                    Expect.stringContains (Json.serialize second) "# Notes" "the receipt"
                | other -> failtest $"{other.Length} requests"
            }
        ]
