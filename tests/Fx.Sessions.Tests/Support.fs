/// A pure driver for the session machine: commands go through `Handle`, `Evolve` and `React` as an
/// actor would run them, on a clock that moves one second per command unless told otherwise.
module Fx.Sessions.Tests.Support

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Sessions

let alice = Attribution.create "user:alice" "tui"
let bob = Attribution.create "user:bob" "web"
let system = Attribution.system "interpreter"

let machine = Session.machine RequestKinds.builtIn

/// One second, in the machine's microseconds.
let second = 1_000_000L

/// The time the drive starts at: 2026-01-01T00:00:00Z.
let epoch = 1_767_225_600_000_000L

/// A session driven by commands: its state, every event appended, every intent, and what each
/// command came to.
type Drive =
    {
        State: SessionState
        Events: Event list
        Intents: Intent list
        /// What the last command came to.
        Last: Result<Event list, Rejection>
        Now: int64
    }

    override this.ToString() = string this.State

let fresh: Drive =
    {
        State = Session.initial
        Events = []
        Intents = []
        Last = Ok []
        Now = epoch
    }

/// Sends a command by someone, one second after the last one.
let send (by: Attribution) (command: Command) (drive: Drive) : Drive =
    let now = drive.Now + second

    match Machine.step machine drive.State { Command = command; By = by; At = now } with
    | Ok step ->
        { drive with
            State = step.State
            Events = drive.Events @ step.Events
            Intents = drive.Intents @ step.Intents
            Last = Ok step.Events
            Now = now
        }
    | Error rejection ->
        { drive with
            Last = Error rejection
            Now = now
        }

/// Moves the clock.
let wait (seconds: int64) (drive: Drive) : Drive =
    { drive with
        Now = drive.Now + seconds * second
    }

let started: Drive =
    fresh
    |> send
        alice
        (Command.Start
            {
                Id = "s1"
                Profile = "sha256:base"
                Overrides = []
                Parent = None
                Title = Some "test"
            })

let steer (by: Attribution) (text: string) =
    send by (Command.Prompt(text, PromptMode.Steer))

let queue (by: Attribution) (text: string) =
    send by (Command.Prompt(text, PromptMode.Queue))

/// The events the last command appended, printed.
let appended (drive: Drive) : string list =
    match drive.Last with
    | Ok events -> events |> List.map string
    | Error rejection -> failtest ("rejected: " + string rejection)

/// The code the last command was rejected with.
let rejected (drive: Drive) : string =
    match drive.Last with
    | Ok events -> failtest ("accepted: " + (events |> List.map string |> String.concat "; "))
    | Error rejection -> rejection.Code

let ids (inputs: Input list) = inputs |> List.map _.Id

/// A request of the built-in `confirm` kind.
let confirm (id: string) (origin: RequestOrigin) (reply: Reply) (lifetime: Lifetime) : RequestSpec =
    RequestKind.request RequestKinds.confirm id ("confirm " + id) origin reply lifetime { Message = "Go on?" }

/// A request of the built-in `choice` kind.
let choice (id: string) (origin: RequestOrigin) (reply: Reply) (lifetime: Lifetime) : RequestSpec =
    RequestKind.request
        RequestKinds.choice
        id
        ("choose " + id)
        origin
        reply
        lifetime
        {
            Question = "Which?"
            Options = [ "a"; "b" ]
        }

let yes = Json.Bool true
let pick (option: string) = Json.String option
