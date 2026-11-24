module Fx.Machines.Tests.MachineTests

open Expecto
open Fx.Machines

/// A small machine: adds numbers until closed, splitting large additions into steps of ten, and asks
/// for a celebration at every multiple of 50.
type Command =
    | Add of int
    | Close

    override this.ToString() =
        match this with
        | Add n -> $"Add {n}"
        | Close -> "Close"

type Event =
    | Added of int
    | Closed

    override this.ToString() =
        match this with
        | Added n -> $"Added {n}"
        | Closed -> "Closed"

type State =
    {
        Total: int
        IsClosed: bool
    }

    override this.ToString() = $"State({this.Total}, {this.IsClosed})"

type Intent =
    | Celebrate of int

    override this.ToString() =
        match this with
        | Celebrate n -> $"Celebrate {n}"

let counter: Machine<Command, Event, State, Intent> =
    {
        Initial = { Total = 0; IsClosed = false }
        Evolve =
            fun state event ->
                match event with
                | Added n -> { state with Total = state.Total + n }
                | Closed -> { state with IsClosed = true }
        Handle =
            fun state command ->
                match command with
                | _ when state.IsClosed -> Error(Rejection.create "counter.closed" "The counter is closed.")
                | Add n when n < 0 -> Error(Rejection.create "counter.negative" $"Cannot add {n}.")
                | Add 0 -> Ok []
                | Add n -> Ok [ for step in 0..10 .. n - 1 -> Added(min 10 (n - step)) ]
                | Close -> Ok [ Closed ]
        React =
            fun state event ->
                match event with
                | Added n when state.Total / 50 > (state.Total - n) / 50 -> [ Celebrate(state.Total / 50 * 50) ]
                | _ -> []
    }

let private commandOf (n: int) =
    if n = 99 then Close else Add(n % 40 - 5)

[<Tests>]
let tests =
    testList
        "Machine"
        [
            test "step handles, then evolves and reacts to each event in order" {
                match Machine.step counter { Total = 35; IsClosed = false } (Add 25) with
                | Ok step ->
                    Expect.equal step.Events [ Added 10; Added 10; Added 5 ] "events"
                    Expect.equal step.State { Total = 60; IsClosed = false } "state"
                    Expect.equal step.Intents [ Celebrate 50 ] "React sees the state after each event"
                    Expect.equal (string step) "Step(3 events, 1 intents)" "text"
                | Error rejection -> failtest (string rejection)
            }

            test "a rejected step changes nothing" {
                let closed = { Total = 3; IsClosed = true }

                Expect.equal
                    (Machine.step counter closed (Add 1))
                    (Error(Rejection.create "counter.closed" "The counter is closed."))
                    "rejected"

                Expect.equal (string (Rejection.create "a.b" "Why.")) "a.b: Why." "text"
            }

            test "apply collects intents across events" {
                let state, intents =
                    Machine.apply counter counter.Initial [ Added 30; Added 30; Added 50; Closed ]

                Expect.equal state { Total = 110; IsClosed = true } "state"
                Expect.equal intents [ Celebrate 50; Celebrate 100 ] "intents"
                Expect.equal (Machine.apply counter state []) (state, []) "no events"
            }

            test "replay folds from the initial state" {
                Expect.equal (Machine.replay counter []) counter.Initial "empty log"
                Expect.equal (Machine.replay counter [ Added 1; Added 2; Closed ]) { Total = 3; IsClosed = true } "log"
                Expect.equal (Machine.fold counter { Total = 10; IsClosed = false } [ Added 1 ]).Total 11 "fold"
            }

            testProperty "the state after any commands is the replay of their events" (fun (inputs: byte list) ->
                let commands = inputs |> List.map (int >> commandOf)
                let state, events = Machine.run counter counter.Initial commands
                state = Machine.replay counter events)

            testProperty
                "handle is deterministic: the same state and command decide the same"
                (fun (total: int) (n: int) ->
                    let state = { Total = total; IsClosed = false }
                    Machine.step counter state (Add(n % 1000)) = Machine.step counter state (Add(n % 1000)))

            testProperty "running commands one by one is stepping through them" (fun (inputs: byte list) ->
                let commands = inputs |> List.map (int >> commandOf)

                let stepped =
                    commands
                    |> List.fold
                        (fun state command ->
                            match Machine.step counter state command with
                            | Ok step -> step.State
                            | Error _ -> state)
                        counter.Initial

                stepped = fst (Machine.run counter counter.Initial commands))

            test "mapIntent and mapCommand adapt a machine" {
                let adapted =
                    counter
                    |> Machine.mapIntent (fun (Celebrate n) -> $"party at {n}")
                    |> Machine.mapCommand (fun (n: int) -> if n < 0 then Close else Add n)

                match Machine.step adapted { Total = 45; IsClosed = false } 10 with
                | Ok step -> Expect.equal step.Intents [ "party at 50" ] "mapped"
                | Error rejection -> failtest (string rejection)

                Expect.equal
                    (fst (Machine.run adapted adapted.Initial [ 5; -1; 5 ]))
                    { Total = 5; IsClosed = true }
                    "commands"
            }
        ]

[<Tests>]
let inputs =
    testList
        "Inputs"
        [
            test "streams, keys and event references print as their names" {
                let stream = StreamId.create "session" "01J"
                Expect.equal (string stream) "session/01J" "stream"
                Expect.equal stream.Value "session/01J" "value"
                Expect.equal (string (IdempotencyKey "k-1")) "k-1" "key"
                Expect.equal (string (EventRef.create stream 7L)) "session/01J#7" "event"
            }

            test "attribution, envelopes and outcomes print what they carry" {
                let by = Attribution.create "user:alice" "tui"
                Expect.equal (string by) "user:alice via tui" "attribution"
                Expect.equal (string (Attribution.system "interpreter")) "system via interpreter" "system"

                let envelope =
                    Envelope.create (IdempotencyKey "k") by (Add 1)
                    |> Envelope.withCause (EventRef.create (StreamId "run/1") 3L)

                Expect.equal (string envelope) "Add 1 [k by user:alice via tui after run/1#3]" "envelope"
                Expect.equal (string (Accepted { After = 4L; Count = 2 })) "accepted, appended 5..6" "accepted"
                Expect.equal (string (Accepted { After = 4L; Count = 0 })) "accepted, nothing appended at 4" "nothing"
                Expect.equal (string (Rejected(Rejection.create "x.y" "No."))) "rejected, x.y: No." "rejected"
                Expect.equal (string Stopped) "stopped" "stopped"
            }

            test "an appended range knows its first and last sequence numbers" {
                let appended = { After = 9L; Count = 3 }
                Expect.equal (appended.First, appended.Last) (10L, 12L) "range"
            }
        ]
