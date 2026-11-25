/// The JSON of sessions: a scenario that appends every kind of event, kept as version 1 wrote it
/// (events are facts kept forever: readers must read it in every later version), and its final state.
module Fx.Sessions.Tests.CodecTests

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Sessions
open Fx.Sessions.Tests.Support

/// A session that goes through every event at least once.
let everything: Drive =
    let d1 =
        { (choice "d1" (RequestOrigin.Turn 1) Reply.AsInput Lifetime.Session) with
            Expiry =
                Some
                    {
                        At = epoch + 3600L * second
                        Then = ExpiryPolicy.Answer(pick "a")
                    }
        }

    let g1 =
        { (confirm "g1" RequestOrigin.Session Reply.ToOpener Lifetime.Session) with
            Expiry =
                Some
                    {
                        At = epoch + 20L * second
                        Then = ExpiryPolicy.Cancel
                    }
        }

    let g2 =
        { (confirm "g2" (RequestOrigin.Stream(EventRef.create (StreamId "run/7") 12L)) Reply.ToOpener Lifetime.Session) with
            Expiry =
                Some
                    {
                        At = epoch + 40L * second
                        Then = ExpiryPolicy.Answer yes
                    }
        }

    started
    |> steer alice "one"
    |> send
        bob
        (Command.SelectProfile
            {
                Profile = "sha256:edited"
                Overrides = [ "edit.guard", Json.String "require" ]
                Changed = [ "edit.guard" ]
                BasedOn = "sha256:base"
            })
    |> queue bob "two"
    |> queue bob "three"
    |> send bob (Command.Withdraw 6L)
    |> steer alice "four"
    |> send system (Command.TakeInputs(1, 1))
    |> send system (Command.OpenRequest d1)
    |> send alice (Command.Answer("d1", pick "b"))
    |> send system (Command.OpenRequest(confirm "d2" (RequestOrigin.Turn 1) Reply.AsInput Lifetime.Session))
    |> send system (Command.OpenRequest(confirm "t1" (RequestOrigin.Turn 1) Reply.ToOpener Lifetime.Turn))
    |> send system (Command.OpenRequest g1)
    |> send system (Command.OpenRequest g2)
    |> send system (Command.EndTurn(1, TurnResult.NeedInput [ "d2" ]))
    |> wait 30L
    |> send system (Command.ExpireRequest "g1")
    |> wait 30L
    |> send system (Command.ExpireRequest "g2")
    |> send bob (Command.Answer("d2", yes))
    |> send system (Command.EndTurn(2, TurnResult.Waiting([ "j1" ], "building")))
    |> send system (Command.NotifyJob("j1", "built"))
    |> send bob Command.Interrupt
    |> send alice (Command.Fork("c1", None))
    |> send system (Command.ChildStarted "c1")
    |> send alice (Command.Fork("c2", Some 2L))
    |> send system (Command.ChildFailed("c2", "it exists"))
    |> steer alice "five"
    |> send system (Command.FailTurn(4, "model unreachable"))
    |> steer alice "six"
    |> send system (Command.AbandonTurn(5, "the host stopped during the turn"))
    |> steer alice "seven"
    |> send system (Command.OpenRequest(choice "d3" (RequestOrigin.Turn 6) Reply.AsInput Lifetime.Session))
    |> send bob (Command.CancelRequest("d3", "not needed"))
    |> send
        system
        (Command.EndTurn(
            6,
            TurnResult.Done(
                "all done",
                [
                    {
                        Kind = "command"
                        Summary = "dotnet test → 214 passed"
                        Detail = Json.Object [ "exitCode", Json.int 0 ]
                    }
                ],
                Some "dotnet test"
            )
        ))
    |> send system (Command.EndTurn(7, TurnResult.Done("the cancellation, read", [], None)))
    |> send system (Command.EndTurn(8, TurnResult.Done("two, at last", [], None)))
    |> send alice Command.Close
    |> send bob Command.Reopen
    |> send alice Command.Archive

let private lines (events: Event list) : string =
    events
    |> List.map (fun event -> Json.serializeCanonical (Codecs.event.Encode event) + "\n")
    |> String.concat ""

let private tags (schema: Schema) : string list =
    let rec find (schema: Schema) =
        match schema.Kind with
        | SchemaKind.Named named -> find named.Body
        | SchemaKind.Union union -> union.Cases |> List.map _.Tag
        | _ -> []

    find schema

[<Tests>]
let tests =
    testList
        "Session codecs"
        [
            test "the scenario appends every kind of event, and its commands are accepted" {
                let seen =
                    everything.Events
                    |> List.map (fun event ->
                        match Codecs.event.Encode event with
                        | Json.Object(("kind", Json.String tag) :: _) -> tag
                        | other -> failtest (Json.serialize other))
                    |> List.distinct
                    |> List.sort

                Expect.equal seen (tags Codecs.event.Schema |> List.sort) "every case"
                Expect.equal everything.State.Status Status.Archived "to the end"
                Expect.equal everything.State.Turns 8 "eight turns"
            }

            test "version 1's events are read as they were written, forever" {
                // What version 1 wrote, kept unchanged: a new version reads it through upcasters.
                let kept = Golden.kept "events-v1.jsonl" (lines everything.Events)

                let read =
                    kept.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Codec.decodeText Codecs.event line with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.render issues))
                    |> List.ofArray

                Expect.equal read everything.Events "the same events"
                Expect.equal (lines read) kept "written back the same"
            }

            test "the state's JSON" {
                Golden.expect "state.json" (Json.serializeIndented (Codecs.state.Encode everything.State) + "\n")

                Expect.equal
                    (Codec.decodeText Codecs.state (Json.serialize (Codecs.state.Encode everything.State)))
                    (Ok everything.State)
                    "round trip"
            }

            test "commands read as clients write them" {
                let read text =
                    Codec.decodeText Codecs.command text |> Result.mapError Issue.render

                Expect.equal
                    (read """{"kind":"prompt","text":"hi"}""")
                    (Ok(Command.Prompt("hi", PromptMode.Steer)))
                    "steer by default"

                Expect.equal
                    (read """{"kind":"prompt","text":"hi","mode":"queue"}""")
                    (Ok(Command.Prompt("hi", PromptMode.Queue)))
                    "queue"

                Expect.equal
                    (read """{"kind":"answer","request":"d1","response":{"a":[1,null]}}""")
                    (Ok(Command.Answer("d1", Json.Object [ "a", Json.Array [ Json.int 1; Json.Null ] ])))
                    "any JSON answer"

                Expect.isError (read """{"kind":"prompt"}""") "the text is required"
                Expect.isError (read """{"kind":"fork","child":""}""") "a fork needs a child id"
            }
        ]
