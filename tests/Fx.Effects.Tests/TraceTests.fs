module Fx.Effects.Tests.TraceTests

open System
open System.Threading
open Expecto
open Fx.Effects
open Fx.Effects.Tests.BuilderTests
open Fx.Effects.Tests.CombinatorTests
open Fx.Effects.Tests.TestEnv

[<Tests>]
let tests =
    testList
        "Trace"
        [
            test "spans nest through the scope, and events land in the current span" {
                let fakes = create ()

                let program =
                    Trace.span
                        "turn"
                        [ Attr.int "n" 1L ]
                        (eff {
                            do! Trace.info "thinking" []
                            do! Clock.sleep (seconds 1.0)

                            let! result =
                                Trace.span
                                    "call"
                                    [ Attr.text "tool" "shell" ]
                                    (eff {
                                        do! Trace.debug "running" [ Attr.bool "cached" false ]
                                        return 42
                                    })

                            return result
                        })

                let running = Eff.run fakes.Env program
                fakes.Clock.Advance(seconds 1.0)
                Expect.equal running.Result 42 "result"

                Expect.equal
                    (fakes.Trace.Records |> List.map string)
                    [
                        "2026-01-01T00:00:00.000000Z start 0000000000000001 parent=- turn n=1"
                        "2026-01-01T00:00:00.000000Z info span=0000000000000001 thinking "
                        "2026-01-01T00:00:01.000000Z start 0000000000000002 parent=0000000000000001 call tool=shell"
                        "2026-01-01T00:00:01.000000Z debug span=0000000000000002 running cached=false"
                        "2026-01-01T00:00:01.000000Z end 0000000000000002 ok"
                        "2026-01-01T00:00:01.000000Z end 0000000000000001 ok"
                    ]
                    "records"
            }

            test "spans record failures and cancellations" {
                let fakes = create ()

                Expect.throwsT<InvalidOperationException>
                    (fun () -> runIn fakes.Env (Trace.span "bad" [] (failing "boom")) |> ignore)
                    "failure"

                use cts = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cts.Token
                    }

                let running = Eff.run env (Trace.span "slow" [] (Clock.sleep (seconds 5.0)))
                cts.Cancel()
                Expect.throwsT<Tasks.TaskCanceledException> (fun () -> running.GetAwaiter().GetResult()) "cancelled"

                let ends =
                    fakes.Trace.Records
                    |> List.choose (function
                        | SpanEnded(_, status, _) -> Some status
                        | _ -> None)

                Expect.equal
                    ends
                    [ SpanStatus.Failed("InvalidOperationException", "boom"); SpanStatus.Cancelled ]
                    "statuses"
            }

            test "Trace.none discards everything" {
                let fakes = create ()
                let env = { fakes.Env with Trace = Trace.none }

                let program =
                    Trace.span
                        "x"
                        []
                        (eff {
                            do! Trace.error "e" []
                            return 1
                        })

                Expect.equal (runIn env program) 1 "runs"
                Expect.isEmpty fakes.Trace.Records "nothing recorded"
            }
        ]
