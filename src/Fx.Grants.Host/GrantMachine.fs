namespace Fx.Grants.Host

open Fx.Machines
open Fx.Store
open Fx.Grants

/// The grants log as a `Machine` (spec §4.4), its stream and its event schema.
[<RequireQualifiedAccess>]
module GrantMachine =
    /// The machine over a vocabulary of actions. `Handle` refuses with the error's stable code
    /// (`grants.*`) and message.
    let machine (actions: Actions) : Machine<GrantCommand, GrantEvent, GrantsState, GrantIntent> =
        {
            Initial = GrantLog.initial
            Evolve = GrantLog.evolve
            Handle =
                fun state command ->
                    GrantLog.decide actions state command
                    |> Result.mapError (fun e -> Rejection.create e.Code e.Message)
            React = GrantLog.react
        }

    /// The stream of a store's grants log: `grants/<store id>`.
    let stream (store: string) : StreamId = StreamId.create "grants" store

    /// Version 1 of the grants log's event schema. Committed events are never rewritten: a new
    /// version needs an upcaster (docs/modules/store.md).
    let schema: EventSchema<GrantEvent> = EventSchema.create Codecs.event
