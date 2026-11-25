/// `fx replay <session>`: arguments, and the operation on a recorded session.
module Fx.Replay.Tests.CommandTests

open System
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Replay
open Fx.Replay.Tests.Support

[<Tests>]
let tests =
    testList
        "fx replay"
        [
            test "arguments" {
                Expect.equal
                    (ReplayCommand.parse [ "s1" ])
                    (Ok
                        {
                            Session = "s1"
                            Json = false
                            StallSeconds = 30
                        })
                    "a session"

                Expect.equal
                    (ReplayCommand.parse [ "--json"; "s1"; "--stall"; "5" ])
                    (Ok
                        {
                            Session = "s1"
                            Json = true
                            StallSeconds = 5
                        })
                    "options"

                Expect.isError (ReplayCommand.parse []) "no session"
                Expect.isError (ReplayCommand.parse [ "s1"; "s2" ]) "two sessions"
                Expect.isError (ReplayCommand.parse [ "s1"; "--stall"; "0" ]) "a stall of nothing"
                Expect.isError (ReplayCommand.parse [ "--fast"; "s1" ]) "an unknown option"
            }

            testTask "replays a recorded session: text, JSON, exit codes" {
                let f = create []

                try
                    let! session = SessionTests.cellsOnly f
                    do! close f

                    let! text = Eff.run driver (ReplayCommand.run settings f.Recording [ session ])
                    Expect.equal text.ExitCode 0 "matched"
                    Expect.stringContains text.Stdout "replay matches the recording" "the report"

                    let! json =
                        Eff.run driver (ReplayCommand.run settings f.Recording [ "--json"; session ])

                    Expect.equal json.ExitCode 0 "matched"

                    match
                        Json.parse json.Stdout
                        |> Result.mapError string
                        |> Result.bind (fun j ->
                            ReplayCommand.reportCodec.Decode j |> Result.mapError (Issue.renderAtMost 3))
                    with
                    | Ok report ->
                        Expect.equal report.Session session "the session"
                        Expect.isNone report.Divergence "no divergence"
                        Expect.equal report.Turns 1 "a turn"
                    | Error e -> failtest e

                    let! missing = Eff.run driver (ReplayCommand.run settings f.Recording [ "nobody" ])
                    Expect.equal missing.ExitCode 1 "not recorded"
                    Expect.stringContains missing.Stdout "not recorded" "why"

                    let! wrong = Eff.run driver (ReplayCommand.run settings f.Recording [])
                    Expect.equal wrong.ExitCode 2 "usage"
                    Expect.stringContains wrong.Stderr "replay needs a session" "why"
                finally
                    (f :> IDisposable).Dispose()
            }
        ]
