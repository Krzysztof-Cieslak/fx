namespace Fx.Machines

// What travels to and from an actor: the addressing, attribution and deduplication of commands, and
// their outcomes (spec §5: commands with idempotency keys, inputs serialized and attributed to their
// principal). Plain data, shared with clients.

/// Names one event stream: the log of one actor, e.g. `session/01JDB8Z3M4X2Q9V7K5N6R8T0WA`.
[<Struct>]
type StreamId =
    | StreamId of string

    member this.Value =
        let (StreamId name) = this
        name

    override this.ToString() = this.Value

module StreamId =
    /// `kind/id`, the naming convention for actor streams.
    let create (kind: string) (id: string) : StreamId = StreamId(kind + "/" + id)

/// Identifies one command, so that sending it again is harmless: an actor answers a key it has
/// handled with the first outcome instead of handling the command again. Clients make keys unique
/// (an `Id` is a good key); keys the host derives are documented in docs/modules/machines.md.
[<Struct>]
type IdempotencyKey =
    | IdempotencyKey of string

    member this.Value =
        let (IdempotencyKey key) = this
        key

    override this.ToString() = this.Value

/// Who an input came from. Every event records the attribution of the command that produced it.
type Attribution =
    {
        /// On whose behalf: a principal id (principals are T122; until then any stable id such as
        /// `user:alice`). The host's own inputs use `system`.
        Principal: string
        /// What delivered it: a client connection, an interpreter, a process manager.
        Via: string
    }

    override this.ToString() = this.Principal + " via " + this.Via

module Attribution =
    /// The principal of the host's own inputs.
    [<Literal>]
    let SystemPrincipal = "system"

    let create (principal: string) (via: string) : Attribution = { Principal = principal; Via = via }

    /// An input the host makes itself, e.g. an interpreter's result or a process manager's delivery.
    let system (via: string) : Attribution = create SystemPrincipal via

/// The position of an event: its stream and its sequence number (1 for the first event).
type EventRef =
    {
        Stream: StreamId
        Seq: int64
    }

    override this.ToString() =
        this.Stream.Value + "#" + string this.Seq

module EventRef =
    let create (stream: StreamId) (seq: int64) : EventRef = { Stream = stream; Seq = seq }

/// A command with what the host needs to deduplicate, attribute and trace it.
type Envelope<'Cmd> =
    {
        Command: 'Cmd
        /// The idempotency key: a command with a key the actor has handled is not handled again.
        Key: IdempotencyKey
        By: Attribution
        /// The event that led to this command, for commands the host sends in reaction to one.
        Cause: EventRef option
    }

    override this.ToString() =
        let cause =
            match this.Cause with
            | Some cause -> " after " + string cause
            | None -> ""

        string this.Command
        + " ["
        + this.Key.Value
        + " by "
        + string this.By
        + cause
        + "]"

module Envelope =
    let create (key: IdempotencyKey) (by: Attribution) (command: 'Cmd) : Envelope<'Cmd> =
        {
            Command = command
            Key = key
            By = by
            Cause = None
        }

    let withCause (cause: EventRef) (envelope: Envelope<'Cmd>) : Envelope<'Cmd> = { envelope with Cause = Some cause }

/// The events one accepted command appended: sequence numbers `After + 1` to `After + Count`.
type Appended =
    {
        /// The stream's head before the command.
        After: int64
        /// How many events it appended; 0 if it changed nothing.
        Count: int
    }

    member this.First = this.After + 1L
    member this.Last = this.After + int64 this.Count

    override this.ToString() =
        if this.Count = 0 then
            "nothing appended at " + string this.After
        else
            "appended " + string this.First + ".." + string this.Last

/// What became of a command.
type Outcome =
    /// The machine accepted it, and its events were appended.
    | Accepted of Appended
    /// The machine refused it; nothing was appended.
    | Rejected of Rejection
    /// The actor stopped before it handled the command; nothing was appended. Send it again with the
    /// same key once the actor runs again.
    | Stopped

    override this.ToString() =
        match this with
        | Accepted appended -> "accepted, " + string appended
        | Rejected rejection -> "rejected, " + string rejection
        | Stopped -> "stopped"
