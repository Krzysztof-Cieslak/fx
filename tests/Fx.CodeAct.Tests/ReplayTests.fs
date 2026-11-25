/// Replay (AGENTS.md: strategies need replay tests): the logs a turn recorded (the session's events and
/// its context events) render, through the pure render alone, to exactly the requests the strategy sent;
/// and the same inputs give the same context log and the same requests on every run.
module Fx.CodeAct.Tests.ReplayTests

open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Prompt
open Fx.CodeAct
open Fx.CodeAct.Tests.Support

let private canonical (request: ChatRequest) =
    Json.serializeCanonical (ChatRequest.codec.Encode request)

/// A turn with each kind of step: a steer, a nudge, an error, a hybrid read, and a finish.
let private script (f: Fixture) : Task<unit> =
    task {
        f.Write("notes.md", "# Notes\nfirst\n")
        let hold = f.Model.Hold 2

        f.Model.AddAll
            [
                Say.tool "read" [ "path", Json.String "notes.md" ]
                Say.text "Should I add a line?"
                Say.cell
                    "// intent: add\ncall n = fs.read [\"notes.md\"]\ncall w = fs.write [\"notes.md\", \"# Notes\\nfirst\\nsecond\\n\"]\nw"
                Say.cell "// intent: oops\nthrow nope"
                Say.finish "Added a line"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Add a line to the notes"
        do! f.Model.Asked(2).WaitAsync(System.TimeSpan.FromSeconds 30.0)
        do! prompt f "s1" "p2" "Make it say second"
        hold.SetResult()
        let! ending = ended f "s1" 1
        completed ending |> ignore
    }

let private hybrid = [ "agent.actionSurface", Json.String "hybrid" ]

[<Tests>]
let tests =
    testList
        "CodeAct replay"
        [
            testTask "the recorded logs render to the requests that were sent" {
                do!
                    within
                        "replay"
                        hybrid
                        (fun f ->
                            task {
                                do! script f
                                let requests = f.Model.Requests

                                let! history =
                                    run
                                        f
                                        (ContextLog.load
                                            (Fx.Sessions.Host.SessionLog.typed f.Store.Log)
                                            (ContextLog.typed f.Store.Log)
                                            "s1")

                                let! settings = run f (CodeActSettings.ofVariants f.Variants)

                                let replies =
                                    history.Items
                                    |> List.indexed
                                    |> List.choose (fun (i, item) ->
                                        match item with
                                        | LogItem.Context(_, ContextEvent.Replied reply) -> Some(i, reply)
                                        | _ -> None)

                                Expect.equal replies.Length requests.Length "a reply per request"

                                for (index, reply), sent in List.zip replies requests do
                                    let transcript = Transcript.ofItems (history.Items |> List.take index)

                                    let cfg =
                                        PromptSettings.config
                                            settings.Prompt
                                            []
                                            Exclusions.none
                                            ModelCaps.minimal.Caching

                                    let rendered = Render.render cfg transcript Live.none

                                    let rendered =
                                        { rendered with
                                            Prompt =
                                                { rendered.Prompt with
                                                    Suffix = reply.Shown
                                                }
                                        }

                                    let template =
                                        { sent with
                                            System = []
                                            Messages = []
                                            Cache = { sent.Cache with Key = None }
                                        }

                                    Expect.equal
                                        (canonical (Render.request cfg template rendered))
                                        (canonical sent)
                                        $"request {reply.Step} replays"
                            })
                        ()
            }

            testTask "the same inputs give the same context log and the same requests" {
                let once () =
                    task {
                        use f = create "determinism" hybrid
                        do! script f

                        let context =
                            f.Store.Events(ContextLog.stream "s1")
                            |> List.map (fun r -> Json.serializeCanonical r.Event.Data)

                        let sessionEvents = events f "s1" |> List.map (fun r -> string r.Event)
                        return context, f.Model.Requests |> List.map canonical, sessionEvents
                    }

                let! first = once ()
                let! second = once ()
                let context1, requests1, events1 = first
                let context2, requests2, events2 = second
                Expect.equal context2 context1 "the context log"
                Expect.equal requests2 requests1 "the requests"
                Expect.equal events2 events1 "the session's events"
            }
        ]
