module Fx.Machines.Host.Tests.RegistryTests

open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.Chat
open Fx.Machines.Host.Tests.Support
open Fx.Machines.Host.Tests.TestEnv

let private registry (fakes: Fakes) (log: InMemoryEventLog<Event>) (starts: int ref) =
    runIn
        fakes.Env
        (ActorRegistry.create (fun stream ->
            Interlocked.Increment(&starts.contents) |> ignore
            { spec log.Log with Stream = stream }))

let private get fakes registry stream =
    match runIn fakes.Env (ActorRegistry.get registry stream) with
    | Some actor -> actor
    | None -> failtest "stopped"

[<Tests>]
let tests =
    testList
        "ActorRegistry"
        [
            test "one actor per stream, started on first use" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let starts = ref 0
                let actors = registry fakes log starts
                let one = StreamId "chat/one"
                let two = StreamId "chat/two"
                Expect.equal starts.Value 0 "lazy"
                let first = get fakes actors one
                Expect.isTrue (obj.ReferenceEquals(first, get fakes actors one)) "the same actor"
                get fakes actors two |> ignore
                Expect.equal starts.Value 2 "one per stream"
                Expect.equal actors.Active [ one; two ] "active"

                let outcome =
                    runIn fakes.Env (ActorRegistry.send actors two (envelope "p" alice (Prompt "x")))

                Expect.equal outcome (Accepted { After = 0L; Count = 1 }) "sent"
                Expect.equal (log.Events two).Length 1 "to its stream"
                runIn fakes.Env (ActorRegistry.stop actors)
            }

            test "concurrent first uses start one actor" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let starts = ref 0
                let actors = registry fakes log starts
                let stream = StreamId "chat/busy"

                let found =
                    Array.init 8 (fun _ -> Task.Run(fun () -> get fakes actors stream))
                    |> Task.WhenAll
                    |> _.Result

                Expect.equal starts.Value 1 "started once"
                Expect.allEqual (found |> Array.map (fun actor -> obj.ReferenceEquals(actor, found[0]))) true "shared"
                runIn fakes.Env (ActorRegistry.stop actors)
            }

            test "a stopped actor is started again, from its log" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let starts = ref 0
                let actors = registry fakes log starts
                let first = get fakes actors stream

                completed (Eff.run fakes.Env (Actor.send first (envelope "p" alice (Prompt "x"))))
                |> ignore

                runIn fakes.Env (Actor.stop first)
                let second = get fakes actors stream
                Expect.isFalse (obj.ReferenceEquals(first, second)) "a new actor"
                Expect.equal starts.Value 2 "started again"
                Expect.equal (Actor.snapshot second).State.Status (Answering 1) "rebuilt"

                Expect.equal
                    (runIn fakes.Env (ActorRegistry.interrupt actors stream (envelope "i" alice Interrupt)))
                    (Accepted { After = 1L; Count = 1 })
                    "interrupted through the registry"

                runIn fakes.Env (ActorRegistry.stop actors)
            }

            test "a stopped registry stops its actors and starts none" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let starts = ref 0
                let actors = registry fakes log starts
                let actor = get fakes actors stream
                runIn fakes.Env (ActorRegistry.stop actors)
                Expect.isTrue actor.Completion.IsCompleted "stopped"
                Expect.isNone (runIn fakes.Env (ActorRegistry.get actors stream)) "no actor"

                Expect.equal
                    (runIn fakes.Env (ActorRegistry.send actors stream (envelope "p" alice (Prompt "x"))))
                    Stopped
                    "stopped"

                Expect.isEmpty actors.Active "none active"
            }
        ]
