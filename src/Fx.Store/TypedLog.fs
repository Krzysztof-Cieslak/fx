namespace Fx.Store

open Fx.Machines
open Fx.Machines.Host

/// Typed views of stored logs. Extends `Fx.Machines.Host.EventLog`.
module EventLog =
    /// Reads a stored event as a typed one. Throws `UnreadableEventException` if the schema cannot.
    let decode (schema: EventSchema<'Evt>) (recorded: Recorded<Payload>) : Recorded<'Evt> =
        match EventSchema.decode schema recorded.Event with
        | Ok event ->
            {
                Stream = recorded.Stream
                Seq = recorded.Seq
                Event = event
                Meta = recorded.Meta
            }
        | Error reason -> raise (UnreadableEventException(recorded.Ref, reason))

    /// A log of typed events over a log of payloads (a durable store): appends encode with the
    /// schema's current version, reads upcast and decode. Reading an event the schema cannot decode
    /// fails the read with `UnreadableEventException`.
    let typed (schema: EventSchema<'Evt>) (log: EventLog<Payload>) : EventLog<'Evt> =
        {
            Append =
                fun stream expected meta events cancellation ->
                    log.Append stream expected meta (List.map (EventSchema.encode schema) events) cancellation
            Read =
                fun stream after max cancellation ->
                    task {
                        let! page = log.Read stream after max cancellation
                        return List.map (decode schema) page
                    }
            Head = log.Head
            WaitPast = log.WaitPast
            FindCommit = log.FindCommit
        }
