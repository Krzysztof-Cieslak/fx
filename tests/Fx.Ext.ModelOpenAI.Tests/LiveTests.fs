/// The OpenAI API for real, through `ModelAccess` and the live HTTP client, recording every exchange
/// (credentials redacted) as conformance cases. Runs only with OPENAI_API_KEY set; skipped otherwise
/// (CI has no key).
///
/// Each recorded case is written to `artifacts/cassettes/openai.responses/live-<name>.json` and
/// immediately replayed through the conformance checks. Review it, then copy it into
/// `cassettes/openai.responses/` to make it part of the suite (`OPENAI_MODEL` picks the model,
/// `gpt-6-luna` by default). The requests carry no cache breakpoints: the conformance suite replays
/// cases without the catalog, which decides where breakpoints go.
module Fx.Ext.ModelOpenAI.Tests.LiveTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models
open Fx.Models.Tests.Conformance.WireConformance
open Fx.Sdk

let private key () =
    match Environment.GetEnvironmentVariable "OPENAI_API_KEY" with
    | null
    | "" -> skiptest "set OPENAI_API_KEY to call the OpenAI API and record cassettes"
    | key -> key

let private model () =
    match Environment.GetEnvironmentVariable "OPENAI_MODEL" with
    | null
    | "" -> luna
    | id -> ModelRef.create Provider.Id id

let private recordings =
    Path.Combine(root, "artifacts", "cassettes", Provider.Family)

/// Pulls a stream to its end (each pull bounded), then closes it.
let private drain (stream: ChatStream) : ChatEvent list =
    let events = ResizeArray<ChatEvent>()
    let mutable reading = true

    try
        while reading do
            match stream.Next(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes 5.0).GetAwaiter().GetResult() with
            | Some event -> events.Add event
            | None -> reading <- false
    finally
        stream.Close().WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

    List.ofSeq events

/// A recorded request as a case expects it: without the credential headers (the suite sends none).
let private withoutCredentials (interaction: Interaction) : Interaction =
    { interaction with
        Request =
            { interaction.Request with
                Headers =
                    interaction.Request.Headers
                    |> List.filter (fun (name, _) ->
                        not (
                            Cassette.sensitiveRequestHeaders
                            |> List.exists (fun h -> String.Equals(h, name, StringComparison.OrdinalIgnoreCase))
                        ))
            }
    }

/// Calls the API over `http` with the request (and, if `followUp` gives messages, again after the
/// response), records the exchange as a case, checks it holds no credentials, and replays it through
/// the conformance checks.
let private recordOver
    (http: Http)
    (key: string)
    (name: string)
    (chat: ChatRequest)
    (followUp: ChatResponse -> Message list option)
    : ChatResponse * WireCase =
    let recorder = CassetteRecorder(http)

    let access =
        { ModelAccess.create [ OpenAI.wire ] [ Provider.def ] recorder.Http SystemClock.clock with
            Auth =
                fun _ _ ->
                    Task.FromResult(
                        Ok
                            {
                                Headers = [ "authorization", "Bearer " + key ]
                                Query = []
                                Sign = None
                            }
                    )
            Timeouts =
                {
                    Response = Some(TimeSpan.FromMinutes 2.0)
                    Idle = Some(TimeSpan.FromMinutes 2.0)
                }
            Replay = OpenAI.withReplay Replay.defaults
        }

    // The case keeps the request as the wire got it (fitted to the catalog), so that replaying it
    // without the catalog sends the same body.
    let chat =
        match ProviderDef.model chat.Model Provider.def with
        | Some info ->
            match Fit.request info chat with
            | Ok(fitted, _) -> fitted
            | Error e -> failtest $"cannot fit: {e}"
        | None -> chat

    let llm = ModelAccess.llm access

    let call (request: ChatRequest) =
        drain (wait (llm.Stream request CancellationToken.None))

    let first = call chat
    Expect.isEmpty (ChatEvents.check first) "the stream follows the rules"

    let response =
        match fold first with
        | Ok response -> response
        | Error failure -> failtest $"the call failed: {failure}"

    let follow =
        match followUp response with
        | Some messages ->
            let second =
                call
                    { chat with
                        Messages = chat.Messages @ [ ChatResponse.message response ] @ messages
                    }

            Expect.isEmpty (ChatEvents.check second) "the follow-up follows the rules"
            Expect.isOk (fold second) "the follow-up, with the encrypted reasoning replayed, succeeds"

            Some
                {
                    Messages = messages
                    StatesVerbatim = false
                }
        | None -> None

    let recorded = recorder.Cassette()

    for interaction in recorded.Interactions do
        Expect.equal
            (HttpHeaders.tryFind "authorization" interaction.Request.Headers)
            (Some Cassette.Redacted)
            "the recorder redacts the key"

    let case =
        {
            Name = "live-" + name
            Endpoint = Provider.Endpoint
            Request = chat
            Cassette =
                {
                    Interactions = recorded.Interactions |> List.map withoutCredentials
                }
            Expect = Expected.Response response
            FollowUp = follow
        }

    Expect.isFalse ((WireCase.render case).Contains key) "the recorded case holds no credentials"

    for check in caseChecks OpenAI.wire case do
        check.Run()

    response, case

/// Records a case against the OpenAI API into `artifacts/cassettes/openai.responses/`.
let private record (name: string) (chat: ChatRequest) (followUp: ChatResponse -> Message list option) =
    let key = key ()
    use client = SystemHttp.client ()
    let response, case = recordOver (SystemHttp.create client) key name chat followUp
    Directory.CreateDirectory recordings |> ignore
    File.WriteAllText(Path.Combine(recordings, case.Name + ".json"), WireCase.render case + "\n")
    response

let private weather: ToolSpec =
    {
        Name = "get_weather"
        Description = "The current weather in a city."
        Parameters =
            json """{"type": "object", "properties": {"location": {"type": "string"}}, "required": ["location"]}"""
        Strict = true
    }

let private toolResults (response: ChatResponse) : Message list option =
    match ChatResponse.toolCalls response with
    | [] -> None
    | calls ->
        Some
            [
                Message.user
                    [
                        for call in calls do
                            Part.ToolResult
                                {
                                    CallId = call.Id
                                    Name = call.Name
                                    Content = [ ToolContent.Text "4 °C, light snow" ]
                                    IsError = false
                                    Sources = []
                                }
                    ]
            ]

[<Tests>]
let tests =
    testList
        "live (OPENAI_API_KEY)"
        [
            // The recording path without the network: a hand-written cassette stands in for the API.
            test "recording redacts the key, and the recorded case replays" {
                let case = Cases.all |> List.find (fun c -> c.Name = "reasoning-tool-call")
                let key = "sk-proj-not-a-real-key"
                let http = CassetteHttp(case.Cassette, Chunking.Every 7)

                let response, recorded =
                    recordOver http.Http key "offline" case.Request (fun _ -> Some case.FollowUp.Value.Messages)

                Expect.equal response.Stop StopReason.ToolUse "the response"
                Expect.equal recorded.Cassette.Interactions.Length 2 "both calls"
                Expect.isEmpty http.Remaining "every interaction was used"
            }

            test "text" {
                let response =
                    record
                        "text"
                        { ChatRequest.create (model ()) [ Message.userText "Reply with the single word: pong" ] with
                            System = [ Part.text "You answer tersely." ]
                            MaxOutputTokens = Some 512
                            Cache =
                                { CacheHints.none with
                                    Key = Some "fx-live-smoke"
                                }
                        }
                        (fun _ -> None)

                Expect.stringContains ((ChatResponse.text response).ToLowerInvariant()) "pong" "the answer"
            }

            test "encrypted reasoning and a tool call, replayed in the follow-up" {
                let response =
                    record
                        "reasoning-tool-call"
                        { ChatRequest.create
                              (model ())
                              [ Message.userText "What's the weather in Kraków? Use the tool." ] with
                            Tools = [ weather ]
                            ToolChoice = ToolChoice.Required
                            Reasoning =
                                Some
                                    {
                                        Effort = Some Effort.Low
                                        BudgetTokens = None
                                        Visible = true
                                    }
                            MaxOutputTokens = Some 8000
                        }
                        toolResults

                Expect.equal response.Stop StopReason.ToolUse "it called the tool"
            }
        ]
