namespace Fx.Grants.Host

open System
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Sdk
open Fx.Grants

/// Grant stores (`GrantStore`, Fx.Sdk) and the machine host's event logs, one in terms of the other:
/// the host-local store is a stream of the host's event store, and the grants actor runs on any
/// store through an event log.
[<RequireQualifiedAccess>]
module GrantStoreLog =
    let private metaOf (meta: EventMeta) : GrantEventMeta =
        {
            At = meta.At.UnixMicroseconds
            Principal = meta.By.Principal
            Via = meta.By.Via
            Key = meta.Key.Value
            Cause = meta.Cause |> Option.map string
        }

    /// `stream#seq`, as `EventRef` prints it.
    let private causeOf (cause: string) : EventRef =
        let at = cause.LastIndexOf '#'

        match Int64.TryParse(cause.AsSpan(at + 1)) with
        | true, seq when at > 0 -> EventRef.create (StreamId(cause.Substring(0, at))) seq
        | _ -> invalidOp ("A grant store gave a malformed cause: " + cause + ".")

    let private eventMetaOf (meta: GrantEventMeta) : EventMeta =
        {
            At = Instant.ofUnixMicroseconds meta.At
            By = Attribution.create meta.Principal meta.Via
            Key = IdempotencyKey meta.Key
            Cause = meta.Cause |> Option.map causeOf
        }

    /// A grant store on one stream of an event log.
    let ofEventLog (id: string) (log: EventLog<GrantEvent>) (stream: StreamId) : GrantStore =
        {
            Id = id
            Append =
                fun expected meta events cancellation ->
                    task {
                        let! appended = log.Append stream expected (eventMetaOf meta) events cancellation

                        return
                            appended
                            |> Result.mapError (fun error ->
                                match error with
                                | AppendError.Conflict(expected, actual) -> GrantAppendError.Conflict(expected, actual)
                                | AppendError.Duplicate(key, first) ->
                                    GrantAppendError.Duplicate(key.Value, first.After, first.Count))
                    }
            Read =
                fun after max cancellation ->
                    task {
                        let! page = log.Read stream after max cancellation

                        return
                            page
                            |> List.map (fun recorded ->
                                {
                                    Seq = recorded.Seq
                                    Event = recorded.Event
                                    Meta = metaOf recorded.Meta
                                })
                    }
            Head = fun cancellation -> log.Head stream cancellation
            WaitPast = fun after cancellation -> log.WaitPast stream after cancellation
        }

    /// The built-in host-local store (spec §13.2): the stream `grants/host` of the host's event store,
    /// read and written with the grants log's event schema.
    let hostLocal (log: EventLog<Payload>) : GrantStore =
        ofEventLog "host" (EventLog.typed GrantMachine.schema log) (GrantMachine.stream "host")

    /// An event log over a grant store, for its one stream: what the grants actor runs on. A store
    /// that refuses appends (`ReadOnly`) stops the actor, as any failed append does.
    let toEventLog (store: GrantStore) (stream: StreamId) : EventLog<GrantEvent> =
        let check (asked: StreamId) =
            if asked <> stream then
                invalidArg (nameof stream) ("Grant store " + store.Id + " holds only " + string stream + ".")

        {
            Append =
                fun asked expected meta events cancellation ->
                    check asked

                    task {
                        let! appended = store.Append expected (metaOf meta) events cancellation

                        return
                            appended
                            |> Result.mapError (fun error ->
                                match error with
                                | GrantAppendError.Conflict(expected, actual) -> AppendError.Conflict(expected, actual)
                                | GrantAppendError.Duplicate(key, after, count) ->
                                    AppendError.Duplicate(IdempotencyKey key, { After = after; Count = count })
                                | GrantAppendError.ReadOnly reason ->
                                    invalidOp ("Grant store " + store.Id + " is read-only: " + reason))
                    }
            Read =
                fun asked after max cancellation ->
                    check asked

                    task {
                        let! page = store.Read after max cancellation

                        return
                            page
                            |> List.map (fun stored ->
                                {
                                    Stream = stream
                                    Seq = stored.Seq
                                    Event = stored.Event
                                    Meta = eventMetaOf stored.Meta
                                })
                    }
            Head =
                fun asked cancellation ->
                    check asked
                    store.Head cancellation
            WaitPast =
                fun asked after cancellation ->
                    check asked
                    store.WaitPast after cancellation
        }
