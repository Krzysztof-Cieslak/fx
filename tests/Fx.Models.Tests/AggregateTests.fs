module Fx.Models.Tests.AggregateTests

open System
open System.Threading
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Models
open Fx.Models.Fakes
open Fx.Models.Tests.Support
open Fx.Models.Tests.Samples

let private config =
    { FsCheckConfig.defaultConfig with
        maxTest =
            match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
            | null -> 300
            | count -> int count
    }

let private fold events =
    ChatAggregate.result (ChatAggregate.ofEvents events)

let private violations events =
    ChatAggregate.violations (ChatAggregate.ofEvents events)

/// Splits every delta of a stream into pieces at the given cut points.
let private resplit (cuts: int list) (events: ChatEvent list) : ChatEvent list =
    events
    |> List.collect (fun event ->
        match event with
        | ChatEvent.Delta(index, text) when text.Length > 1 ->
            let points =
                cuts
                |> List.map (fun c -> 1 + abs c % (text.Length - 1))
                |> List.distinct
                |> List.sort

            (0 :: points @ [ text.Length ])
            |> List.pairwise
            |> List.map (fun (a, b) -> ChatEvent.Delta(index, text.Substring(a, b - a)))
        | event -> [ event ])

let private textPart (text: string) = Part.text text

[<Tests>]
let tests =
    testList
        "Aggregation"
        [
            test "the sample stream folds into the sample response" {
                Expect.equal (fold events) (Ok response) "response"
                Expect.equal (ChatEvents.check events) [] "no broken rules"
            }

            testPropertyWithConfig config "text, reasoning and tool calls do not depend on how deltas are cut"
            <| fun (cuts: int list) ->
                let split = resplit cuts events
                Expect.equal (fold split) (Ok response) "response"
                Expect.equal (ChatEvents.coalesce split) (ChatEvents.coalesce events) "coalesced"

            test "a response's canonical stream folds back into it" {
                Expect.equal (fold (ChatEvents.ofResponse response)) (Ok response) "response"

                let failure =
                    {
                        Error = QuotaExhausted "credits"
                        Id = Some "r"
                        Model = Some "m"
                        Parts = [ textPart "half" ]
                        Usage = usage
                    }

                Expect.equal (fold (ChatEvents.ofFailure failure)) (Error failure) "failure"
            }

            test "parts may be open at the same time, and are ordered by index" {
                let stream =
                    [
                        ChatEvent.PartStarted(0, PartStart.ToolCall("a", "one"))
                        ChatEvent.PartStarted(1, PartStart.ToolCall("b", "two"))
                        ChatEvent.Delta(1, "{\"y\":")
                        ChatEvent.Delta(0, "{\"x\":1}")
                        ChatEvent.Delta(1, "2}")
                        ChatEvent.PartEnded 1
                        ChatEvent.PartEnded 0
                        ChatEvent.Finished StopReason.ToolUse
                    ]

                match fold stream with
                | Ok r ->
                    Expect.equal
                        (ChatResponse.toolCalls r |> List.map (fun c -> c.Id, c.Arguments))
                        [ "a", "{\"x\":1}"; "b", "{\"y\":2}" ]
                        "calls"
                | Error e -> failtest (string e)
            }

            test "a failure keeps what arrived before it" {
                let stream =
                    [
                        ChatEvent.PartStarted(0, PartStart.Text)
                        ChatEvent.Delta(0, "partial")
                        ChatEvent.UsageUpdated { Usage.zero with Output = 3 }
                        ChatEvent.Failed(QuotaExhausted "out")
                    ]

                match fold stream with
                | Error f ->
                    Expect.equal f.Error (QuotaExhausted "out") "error"
                    Expect.equal f.Parts [ textPart "partial" ] "parts"
                    Expect.equal f.Usage.Output 3 "usage"
                | Ok r -> failtest $"expected a failure, got {r}"
            }

            test "every broken rule is reported" {
                let cases =
                    [
                        [ ChatEvent.Delta(0, "x"); ChatEvent.Finished StopReason.EndTurn ], "part 0 was never started"
                        [
                            ChatEvent.PartStarted(1, PartStart.Text)
                            ChatEvent.Finished StopReason.EndTurn
                        ],
                        "part 1 started out of order (expected part 0)"
                        [
                            ChatEvent.PartStarted(0, PartStart.Text)
                            ChatEvent.PartStarted(0, PartStart.Text)
                            ChatEvent.PartEnded 0
                            ChatEvent.Finished StopReason.EndTurn
                        ],
                        "part 0 started twice"
                        [
                            ChatEvent.PartStarted(0, PartStart.Text)
                            ChatEvent.PartEnded 0
                            ChatEvent.Delta(0, "late")
                            ChatEvent.Finished StopReason.EndTurn
                        ],
                        "part 0 already ended"
                        [
                            ChatEvent.PartStarted(0, PartStart.Reasoning)
                            ChatEvent.PartState(0, state "a")
                            ChatEvent.PartState(0, state "b")
                            ChatEvent.PartEnded 0
                            ChatEvent.Finished StopReason.EndTurn
                        ],
                        "part 0 got its state twice"
                        [
                            ChatEvent.PartStarted(0, PartStart.Refusal)
                            ChatEvent.PartState(0, state "a")
                            ChatEvent.PartEnded 0
                            ChatEvent.Finished StopReason.Refusal
                        ],
                        "part 0 is a refusal, which has no state"
                        [
                            ChatEvent.UsageUpdated { Usage.zero with Output = 5 }
                            ChatEvent.UsageUpdated { Usage.zero with Output = 4 }
                            ChatEvent.Finished StopReason.EndTurn
                        ],
                        "the usage decreased"
                        [
                            ChatEvent.PartStarted(0, PartStart.Text)
                            ChatEvent.Finished StopReason.EndTurn
                        ],
                        "finished with parts still open: 0"
                        [ ChatEvent.Finished StopReason.ToolUse ], "stopped for tool use without a tool call"
                        [
                            ChatEvent.Finished StopReason.EndTurn
                            ChatEvent.PartStarted(0, PartStart.Text)
                        ],
                        "an event after the end of the stream"
                        [
                            ChatEvent.UsageUpdated Usage.zero
                            ChatEvent.Started { Id = None; Model = None }
                            ChatEvent.Finished StopReason.EndTurn
                        ],
                        "started after other events"
                    ]

                for stream, expected in cases do
                    match violations stream with
                    | [ actual ] -> Expect.stringContains actual expected expected
                    | other -> failtest $"{expected}: got {other}"

                    match fold stream with
                    | Error { Error = InvalidResponse message } -> Expect.stringContains message expected "the failure"
                    | other -> failtest $"{expected}: expected an invalid response, got {other}"
            }

            test "a stream without an end is not a response" {
                Expect.equal
                    (ChatEvents.check [ ChatEvent.PartStarted(0, PartStart.Text) ])
                    [ "the stream has no Finished or Failed event" ]
                    "check"

                match fold [] with
                | Error { Error = InvalidResponse _ } -> ()
                | other -> failtest $"got {other}"
            }

            test "coalesce merges adjacent deltas of a part and drops empty ones" {
                Expect.equal
                    (ChatEvents.coalesce
                        [
                            ChatEvent.Delta(0, "a")
                            ChatEvent.Delta(0, "")
                            ChatEvent.Delta(0, "b")
                            ChatEvent.Delta(1, "c")
                            ChatEvent.Delta(0, "d")
                        ])
                    [ ChatEvent.Delta(0, "ab"); ChatEvent.Delta(1, "c"); ChatEvent.Delta(0, "d") ]
                    "merged"
            }

            test "ChatStream: replays events, taps them, and collects them into an aggregate" {
                let seen = ResizeArray<ChatEvent>()
                let stream = ChatStream.ofEvents events |> ChatStream.tap seen.Add
                let state = wait (ChatStream.collect stream CancellationToken.None)
                Expect.equal (ChatAggregate.result state) (Ok response) "collected"
                Expect.equal (List.ofSeq seen) events "tapped"
            }

            test "Llm.complete over the scripted model" {
                let llm = ScriptedLlm.Always response

                let env: LlmEnv =
                    {
                        Llm = llm.Llm
                        Scope = Fx.Effects.Scope.root
                    }

                let result = wait (Fx.Effects.Eff.run env (Llm.complete Samples.request))
                Expect.equal result (Ok response) "response"
                Expect.equal llm.Requests [ Samples.request ] "recorded"
            }
        ]
