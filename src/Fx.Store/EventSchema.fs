namespace Fx.Store

open System
open Fx.Contracts
open Fx.Machines

/// An event as a store keeps it: its JSON in canonical form, and the version of the event schema
/// that wrote it. Stores deal only in payloads; `EventLog.typed` turns them into typed events.
type Payload =
    {
        /// The version of the schema that wrote `Data`, from 1.
        Version: int
        /// The event's JSON, canonical (`Json.canonicalize`), so equal events are equal text.
        Data: Json
    }

    override this.ToString() =
        "v" + string this.Version + " " + Json.serialize this.Data

/// Turns the JSON of an event written by one schema version into the JSON the next version reads.
/// An `Error` names what it could not upcast. Upcasters are pure: committed events are never
/// rewritten, so every read of an old event runs them again.
type Upcaster = Json -> Result<Json, string>

/// How the events of a stream are stored: the codec of the current version, and the upcasters that
/// bring the JSON of every older version up to it (spec §12.5: readers upcast, committed data is
/// never rewritten). Build one with `EventSchema.create` and `EventSchema.evolve`.
type EventSchema<'Evt> =
    {
        /// The version new events are written with: 1 plus the number of upcasters.
        Version: int
        /// Reads and writes the JSON of the current version.
        Codec: Codec<'Evt>
        /// `Upcasters[i]` turns the JSON of version `i + 1` into that of version `i + 2`.
        Upcasters: Upcaster list
    }

    override this.ToString() =
        "EventSchema(v" + string this.Version + ")"

/// An event in a log that the reader cannot decode. A defect: events are facts kept forever, so
/// every version of a schema must read every event an earlier version wrote.
type UnreadableEventException(event: EventRef, reason: string) =
    inherit Exception(string event + ": " + reason)

    member _.Event = event
    member _.Reason = reason

module EventSchema =
    /// Version 1 of a schema.
    let create (codec: Codec<'Evt>) : EventSchema<'Evt> =
        {
            Version = 1
            Codec = codec
            Upcasters = []
        }

    /// The next version: `upcaster` turns JSON written by `schema`'s version into JSON that `codec`
    /// reads. The event type may change too (the old codec is no longer needed).
    ///
    /// Bump the version when the JSON of an existing event changes shape or meaning. Adding a case or
    /// an optional field needs no new version: the codec reads old JSON as it is.
    let evolve (upcaster: Upcaster) (codec: Codec<'Next>) (schema: EventSchema<'Evt>) : EventSchema<'Next> =
        {
            Version = schema.Version + 1
            Codec = codec
            Upcasters = schema.Upcasters @ [ upcaster ]
        }

    /// The payload of a new event: the current version and canonical JSON.
    let encode (schema: EventSchema<'Evt>) (event: 'Evt) : Payload =
        {
            Version = schema.Version
            Data = Json.canonicalize (schema.Codec.Encode event)
        }

    /// The JSON of a payload brought up to the current version.
    let upgrade (schema: EventSchema<'Evt>) (payload: Payload) : Result<Json, string> =
        if payload.Version < 1 then
            Error $"schema version {payload.Version} is not a version"
        elif payload.Version > schema.Version then
            Error $"written by schema version {payload.Version}; this reader knows versions up to {schema.Version}"
        else
            let mutable json = Ok payload.Data
            let mutable version = payload.Version

            for upcaster in List.skip (payload.Version - 1) schema.Upcasters do
                json <-
                    json
                    |> Result.bind (fun data ->
                        upcaster data
                        |> Result.mapError (fun reason -> $"upcasting from version {version}: {reason}"))

                version <- version + 1

            json

    /// The event a payload holds, upcast and decoded; an error says why it cannot be read.
    let decode (schema: EventSchema<'Evt>) (payload: Payload) : Result<'Evt, string> =
        upgrade schema payload
        |> Result.bind (fun json ->
            match schema.Codec.Decode json with
            | Ok event -> Ok event
            | Error issues -> Error(Issue.renderAtMost 5 issues))
