/// A small session-like machine for the actor tests: prompts start turns, a "model" answers after a
/// delay on the fake clock, prompts sent during a turn queue up, and an interrupt ends the turn.
module Fx.Machines.Host.Tests.Chat

open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.TestEnv

type Status =
    | Idle
    | Answering of turn: int
    | Closed

    override this.ToString() =
        match this with
        | Idle -> "Idle"
        | Answering turn -> $"Answering {turn}"
        | Closed -> "Closed"

type State =
    {
        Status: Status
        Turns: int
        Queue: string list
        Replies: string list
    }

    override this.ToString() =
        let queue = String.concat "|" this.Queue
        let replies = String.concat "|" this.Replies
        $"{this.Status}, {this.Turns} turns, queue [{queue}], replies [{replies}]"

type Command =
    | Prompt of text: string
    | Interrupt
    | Answer of turn: int * reply: string
    | Close

    override this.ToString() =
        match this with
        | Prompt text -> $"Prompt {text}"
        | Interrupt -> "Interrupt"
        | Answer(turn, reply) -> $"Answer {turn} {reply}"
        | Close -> "Close"

type Event =
    | Prompted of turn: int * text: string * fromQueue: bool
    | Queued of text: string
    | Answered of turn: int * reply: string
    | Interrupted of turn: int
    | ChatClosed

    override this.ToString() =
        match this with
        | Prompted(turn, text, fromQueue) -> $"""Prompted {turn} {text}{if fromQueue then " (queued)" else ""}"""
        | Queued text -> $"Queued {text}"
        | Answered(turn, reply) -> $"Answered {turn} {reply}"
        | Interrupted turn -> $"Interrupted {turn}"
        | ChatClosed -> "Closed"

type Intent =
    | Ask of turn: int * text: string

    override this.ToString() =
        match this with
        | Ask(turn, text) -> $"Ask {turn} {text}"

let private reject code message = Error(Rejection.create code message)

let machine: Machine<Command, Event, State, Intent> =
    {
        Initial =
            {
                Status = Idle
                Turns = 0
                Queue = []
                Replies = []
            }
        Evolve =
            fun state event ->
                match event with
                | Prompted(turn, _, fromQueue) ->
                    { state with
                        Status = Answering turn
                        Turns = turn
                        Queue = if fromQueue then List.tail state.Queue else state.Queue
                    }
                | Queued text ->
                    { state with
                        Queue = state.Queue @ [ text ]
                    }
                | Answered(_, reply) ->
                    { state with
                        Status = Idle
                        Replies = state.Replies @ [ reply ]
                    }
                | Interrupted _ -> { state with Status = Idle }
                | ChatClosed -> { state with Status = Closed }
        Handle =
            fun state command ->
                match state.Status, command with
                | Closed, Close -> Ok []
                | Closed, _ -> reject "chat.closed" "The chat is closed."
                | _, Prompt "" -> reject "chat.empty" "Say something."
                | Idle, Prompt text -> Ok [ Prompted(state.Turns + 1, text, false) ]
                | Answering _, Prompt text -> Ok [ Queued text ]
                | Answering turn, Interrupt -> Ok [ Interrupted turn ]
                | Idle, Interrupt -> reject "chat.idle" "Nothing to interrupt."
                | Answering current, Answer(turn, reply) when turn = current ->
                    Ok(
                        match state.Queue with
                        | next :: _ -> [ Answered(turn, reply); Prompted(turn + 1, next, true) ]
                        | [] -> [ Answered(turn, reply) ]
                    )
                | _, Answer(turn, _) -> reject "chat.stale" $"Turn {turn} is not running."
                | _, Close -> Ok [ ChatClosed ]
        React =
            fun _ event ->
                match event with
                | Prompted(turn, text, _) -> [ Ask(turn, text) ]
                | _ -> []
    }

/// The "model": thinks one second per character of the prompt, then echoes it.
let interpret (intent: Intent) : Eff<Env, Command list> =
    eff {
        match intent with
        | Ask(turn, text) ->
            do! Clock.sleep (seconds (float text.Length))
            return [ Answer(turn, "re:" + text) ]
    }

let stream = StreamId.create "chat" "1"

let spec (log: EventLog<Event>) : ActorSpec<Env, Command, Event, State, Intent> =
    ActorSpec.create stream machine log interpret

let alice = Attribution.create "user:alice" "tui"
let bob = Attribution.create "user:bob" "web"

let envelope (key: string) (by: Attribution) (command: Command) =
    Envelope.create (IdempotencyKey key) by command
