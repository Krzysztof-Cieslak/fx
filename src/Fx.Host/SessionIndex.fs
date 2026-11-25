namespace Fx.Host

open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store

/// A session the host created: where it works. Sessions' own logs say everything else.
type SessionEntry =
    {
        Session: string
        /// The workspace (project directory) it works in.
        Workspace: string
        /// The environment its hands are in (`IEnvs`).
        Environment: string
        Title: string option
    }

    override this.ToString() =
        $"session {this.Session} ({this.Workspace}, environment {this.Environment})"

/// What the session index records. Kept forever: add cases, never change an old one's JSON without a
/// new schema version and an upcaster (golden: tests/Fx.Host.Tests/golden/index-events-v1.jsonl).
[<RequireQualifiedAccess>]
type IndexEvent =
    | Created of SessionEntry

    override this.ToString() =
        match this with
        | Created entry -> "created " + string entry

/// The host's index of its sessions (stream `host/sessions`): which sessions exist and where they
/// work, so a host that starts finds the sessions with work in flight, and clients can list them.
/// The host is its only writer; a session is indexed once, keyed by its id.
[<RequireQualifiedAccess>]
module SessionIndex =
    let stream: StreamId = StreamId.create "host" "sessions"

    let private entry: Codec<SessionEntry> =
        Codec.record {
            let! session =
                Codec.field "session" (fun (e: SessionEntry) -> e.Session) Codec.string

            and! workspace =
                Codec.field "workspace" (fun (e: SessionEntry) -> e.Workspace) Codec.string

            and! environment =
                Codec.field "environment" (fun (e: SessionEntry) -> e.Environment) Codec.string

            and! title =
                Codec.optionalField "title" (fun (e: SessionEntry) -> e.Title) Codec.string

            return
                {
                    Session = session
                    Workspace = workspace
                    Environment = environment
                    Title = title
                }
        }

    /// `{"kind": "created", "session", "workspace", "environment", "title"?}`.
    let event: Codec<IndexEvent> =
        Codec.union "kind" {
            let! created = Codec.caseOf "created" IndexEvent.Created entry

            return
                fun value ->
                    match value with
                    | IndexEvent.Created e -> created e
        }

    /// Version 1.
    let schema: EventSchema<IndexEvent> = EventSchema.create event

    let typed (log: EventLog<Payload>) : EventLog<IndexEvent> = EventLog.typed schema log

    /// Indexes a session once: adding it again changes nothing.
    let add<'R when 'R :> IClock and 'R :> IScope>
        (log: EventLog<IndexEvent>)
        (by: Attribution)
        (session: SessionEntry)
        : Eff<'R, unit> =
        eff {
            let! now = Clock.now
            let mutable added = false

            while not added do
                let! head = EventLog.head log stream

                let meta =
                    {
                        At = now
                        By = by
                        Key = IdempotencyKey("session/" + session.Session)
                        Cause = None
                    }

                match! EventLog.append log stream head meta [ IndexEvent.Created session ] with
                | Ok _
                | Error(Duplicate _) -> added <- true
                | Error(Conflict _) -> ()
        }

    /// The indexed sessions, oldest first.
    let entries<'R when 'R :> IScope> (log: EventLog<IndexEvent>) : Eff<'R, SessionEntry list> =
        EventLog.readAll log stream 0L
        |> Eff.map (
            List.map (fun recorded ->
                match recorded.Event with
                | IndexEvent.Created entry -> entry)
        )
