namespace Fx.CodeAct

open System
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Sessions
open Fx.Prompt

/// A session's history as the transcript folds it (docs/modules/prompt.md): the session's own events
/// and the context events its turns recorded, merged into one order, with a fork's parent's items up to
/// the fork point first.
type History =
    {
        Session: string
        Items: LogItem list
        /// The fold of the session's own events.
        State: SessionState
        /// The last sequence number of the session's own stream that was read.
        SessionHead: int64
    }

    override this.ToString() =
        $"history of {this.Session} ({this.Items.Length} items, session #{this.SessionHead})"

/// Where the strategy keeps context events (spec §5; docs/modules/codeact.md, "The context log"): the
/// stream `context/<session>` of the host's event store, beside the session's own stream, in version 1
/// of their JSON (`ContextCodecs.event`). The session's actor is the single writer of its stream; the
/// strategy, one turn at a time, of the context stream.
///
/// A context event's place in the history is its envelope's `Cause`: the last event of the session's
/// stream the turn had read when it recorded it. The history puts each context event after that session
/// event, in the context stream's order. Session events appended meanwhile (a steer arriving, an answer)
/// only wait in the inbox until a step boundary delivers them, so this order is the order the model saw.
[<RequireQualifiedAccess>]
module ContextLog =
    [<Literal>]
    let StreamKind = "context"

    /// The context stream of a session.
    let stream (session: string) : StreamId = StreamId.create StreamKind session

    /// Version 1 of the context stream's events.
    let schema: EventSchema<ContextEvent> = EventSchema.create ContextCodecs.event

    /// The context events of a store's log.
    let typed (log: EventLog<Payload>) : EventLog<ContextEvent> = EventLog.typed schema log

    /// The session a session stream belongs to.
    let private sessionOf (stream: StreamId) : string option =
        let prefix = Session.StreamKind + "/"

        if stream.Value.StartsWith(prefix, StringComparison.Ordinal) then
            Some(stream.Value.Substring prefix.Length)
        else
            None

    /// Merges a session's events and its context events (pure): each context event after the session
    /// event its cause names, in context order; with `upTo`, only what came before or at that session
    /// event (a fork's parent).
    let merge
        (sessionStream: StreamId)
        (events: Recorded<Event> list)
        (context: Recorded<ContextEvent> list)
        (upTo: int64 option)
        : LogItem list =
        let limit = upTo |> Option.defaultValue Int64.MaxValue

        let position (recorded: Recorded<ContextEvent>) =
            match recorded.Meta.Cause with
            | Some cause when cause.Stream = sessionStream -> cause.Seq
            | _ -> 0L

        let contextItems =
            context
            |> List.filter (fun recorded -> position recorded <= limit)
            |> List.map (fun recorded -> position recorded, LogItem.Context(recorded.Ref, recorded.Event))

        let sessionItems =
            events
            |> List.filter (fun recorded -> recorded.Seq <= limit)
            |> List.map (fun recorded -> recorded.Seq, LogItem.Session(recorded.Ref, recorded.Event))

        // Session events before context events of the same position; each list keeps its order.
        (sessionItems |> List.map (fun (at, item) -> (at, 0), item))
        @ (contextItems |> List.map (fun (at, item) -> (at, 1), item))
        |> List.indexed
        |> List.sortBy (fun (index, ((at, kind), _)) -> at, kind, index)
        |> List.map (fun (_, (_, item)) -> item)

    /// A session's history: its parents' items up to each fork point, then its own.
    let load<'R when 'R :> IScope>
        (sessions: EventLog<Event>)
        (contexts: EventLog<ContextEvent>)
        (session: string)
        : Eff<'R, History> =
        eff {
            let segments = ResizeArray<LogItem list>()
            let mutable current = Some(session, None)
            let mutable own = None
            let mutable depth = 0

            while current.IsSome do
                let id, upTo = current.Value
                let sessionStream = Session.stream id
                let! events = EventLog.readAll sessions sessionStream 0L
                let! context = EventLog.readAll contexts (stream id) 0L
                segments.Insert(0, merge sessionStream events context upTo)

                if own.IsNone then
                    own <- Some events

                depth <- depth + 1

                current <-
                    match events with
                    | { Event = Event.Started(start, _, _) } :: _ when depth < 64 ->
                        start.Parent
                        |> Option.bind (fun parent ->
                            sessionOf parent.Stream
                            |> Option.map (fun parentId -> parentId, Some parent.Seq))
                    | _ -> None

            let events = own |> Option.defaultValue []

            return
                {
                    Session = session
                    Items = List.concat segments
                    State =
                        events
                        |> List.fold (fun state r -> Session.evolve state r.Event) Session.initial
                    SessionHead = events |> List.tryLast |> Option.map (fun r -> r.Seq) |> Option.defaultValue 0L
                }
        }

    /// Records a context event after the session's head, keyed so a retry appends it once.
    let record<'R when 'R :> IClock and 'R :> IScope>
        (sessions: EventLog<Event>)
        (contexts: EventLog<ContextEvent>)
        (session: string)
        (key: string)
        (event: ContextEvent)
        : Eff<'R, unit> =
        eff {
            let sessionStream = Session.stream session
            let contextStream = stream session
            let! now = Clock.now
            let! cause = EventLog.head sessions sessionStream

            let meta =
                {
                    At = now
                    By = Attribution.system "codeact"
                    Key = IdempotencyKey key
                    Cause =
                        if cause > 0L then
                            Some(EventRef.create sessionStream cause)
                        else
                            None
                }

            let mutable attempts = 0
            let mutable recorded = false

            while not recorded do
                attempts <- attempts + 1
                let! head = EventLog.head contexts contextStream

                match! EventLog.append contexts contextStream head meta [ event ] with
                | Ok _
                | Error(Duplicate _) -> recorded <- true
                | Error(Conflict _) when attempts < 3 -> ()
                | Error e -> raise (ConcurrencyException(contextStream, e))
        }
