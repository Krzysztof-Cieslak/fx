namespace Fx.Machines

/// Why a machine refused a command. A rejection is an expected outcome, not a defect: nothing is
/// appended, and the sender is told why.
type Rejection =
    {
        /// A stable, machine-readable code: lowercase and dotted, e.g. `session.closed`.
        Code: string
        /// A human- and model-readable explanation.
        Message: string
    }

    override this.ToString() = this.Code + ": " + this.Message

module Rejection =
    let create (code: string) (message: string) : Rejection = { Code = code; Message = message }

/// A state machine (spec §4.4): the pure core of a session, run, strategy, ledger, grant, … Its state
/// is a fold of its events, and everything it does is data.
///
/// - `Initial`: the state of an empty log.
/// - `Evolve`: applies one event. Pure and total: it must accept every event `Handle` ever decided,
///   because events are facts that stay in logs forever. Clients share it (Fable), so it, the events
///   and the state use only FSharp.Core.
/// - `Handle`: decides what a command means in a state: the events to append, or a rejection. Pure:
///   anything it needs from the world (the time, an id, a result) arrives in the command.
/// - `React`: what should happen after an event, as data (intents). It gets the state after the
///   event. The host interprets intents with effects, and their results come back as commands.
type Machine<'Cmd, 'Evt, 'S, 'Intent> =
    {
        Initial: 'S
        Evolve: 'S -> 'Evt -> 'S
        Handle: 'S -> 'Cmd -> Result<'Evt list, Rejection>
        React: 'S -> 'Evt -> 'Intent list
    }

    override _.ToString() = "Machine"

/// What one accepted command does to a machine: the pure part of the shell loop.
type Step<'Evt, 'S, 'Intent> =
    {
        /// The events `Handle` decided, in order.
        Events: 'Evt list
        /// The state after them.
        State: 'S
        /// What `React` returned for each event, in order.
        Intents: 'Intent list
    }

    override this.ToString() =
        "Step("
        + string (List.length this.Events)
        + " events, "
        + string (List.length this.Intents)
        + " intents)"

module Machine =
    /// The state after the events, starting from a state: a left fold of `Evolve`.
    let fold (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>) (state: 'S) (events: 'Evt list) : 'S =
        List.fold machine.Evolve state events

    /// The state of a log: the fold of its events from `Initial`. A host that rebuilds an actor and
    /// a client that follows its log both compute exactly this.
    let replay (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>) (events: 'Evt list) : 'S =
        fold machine machine.Initial events

    /// Applies events in order: the state after them, and what `React` returned for each event (given
    /// the state after that event), in order.
    let apply (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>) (state: 'S) (events: 'Evt list) : 'S * 'Intent list =
        let state, intents =
            List.fold
                (fun (state, intents) event ->
                    let next = machine.Evolve state event
                    next, List.rev (machine.React next event) @ intents)
                (state, [])
                events

        state, List.rev intents

    /// What a command does in a state: `Handle`, then `Evolve` and `React` for each event. Nothing is
    /// appended; the host does that between `Handle` and `Evolve`.
    let step
        (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>)
        (state: 'S)
        (command: 'Cmd)
        : Result<Step<'Evt, 'S, 'Intent>, Rejection> =
        match machine.Handle state command with
        | Error rejection -> Error rejection
        | Ok events ->
            let next, intents = apply machine state events

            Ok
                {
                    Events = events
                    State = next
                    Intents = intents
                }

    /// Runs commands one after another, as a host would: rejected commands change nothing. Returns
    /// the final state and every accepted command's events, in order. For tests and simulations.
    let run (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>) (state: 'S) (commands: 'Cmd list) : 'S * 'Evt list =
        let state, events =
            List.fold
                (fun (state, log) command ->
                    match machine.Handle state command with
                    | Error _ -> state, log
                    | Ok events -> fold machine state events, List.rev events @ log)
                (state, [])
                commands

        state, List.rev events

    /// The same machine with its intents transformed, e.g. wrapped for a host that runs several kinds.
    let mapIntent
        (mapping: 'Intent -> 'Other)
        (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>)
        : Machine<'Cmd, 'Evt, 'S, 'Other> =
        {
            Initial = machine.Initial
            Evolve = machine.Evolve
            Handle = machine.Handle
            React = fun state event -> machine.React state event |> List.map mapping
        }

    /// The same machine with commands of another type, translated before `Handle`.
    let mapCommand
        (mapping: 'Other -> 'Cmd)
        (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>)
        : Machine<'Other, 'Evt, 'S, 'Intent> =
        {
            Initial = machine.Initial
            Evolve = machine.Evolve
            Handle = fun state command -> machine.Handle state (mapping command)
            React = machine.React
        }
