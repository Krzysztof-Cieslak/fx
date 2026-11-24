module Fx.Store.Tests.EventSchemaTests

open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.Conformance
open Fx.Store
open Fx.Store.Tests.Notes

let private json (text: string) =
    match Json.parse text with
    | Ok json -> json
    | Error error -> failtest (string error)

let private payload (version: int) (text: string) : Payload = { Version = version; Data = json text }

let private meta (key: string) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 1_764_000_000_123_456L
        By = Attribution.create "user:alice" "tui"
        Key = IdempotencyKey key
        Cause = None
    }

let private wait (task: System.Threading.Tasks.Task<'T>) = task.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "EventSchema"
        [
            test "new events are written with the current version, as canonical JSON" {
                Expect.equal schema.Version 2 "version"

                let written = EventSchema.encode schema (Edited(2, "b"))
                Expect.equal written.Version 2 "written version"
                Expect.equal (Json.serialize written.Data) """{"body":"b","index":2,"kind":"edited"}""" "canonical"
                Expect.equal (string written) """v2 {"body":"b","index":2,"kind":"edited"}""" "text"
            }

            test "old events are upcast on read; events of the current version are read as they are" {
                Expect.equal
                    (EventSchema.decode schema (payload 1 """{"kind":"added","text":"milk"}"""))
                    (Ok(Added "milk"))
                    "version 1"

                Expect.equal
                    (EventSchema.decode schema (payload 1 """{"kind":"removed","index":0}"""))
                    (Ok(Removed 0))
                    "version 1, another case"

                Expect.equal
                    (EventSchema.decode schema (payload 2 """{"kind":"added","body":"eggs"}"""))
                    (Ok(Added "eggs"))
                    "version 2"
            }

            test "upcasters run in order, from the event's version up" {
                let tag (n: int) : Upcaster =
                    fun json ->
                        match json with
                        | Json.Array items -> Ok(Json.Array(items @ [ Json.int n ]))
                        | other -> Error $"not an array: {other}"

                let ints = Codec.list Codec.int

                let schema =
                    EventSchema.create ints
                    |> EventSchema.evolve (tag 1) ints
                    |> EventSchema.evolve (tag 2) ints
                    |> EventSchema.evolve (tag 3) ints

                Expect.equal schema.Version 4 "version"
                Expect.equal (EventSchema.decode schema (payload 1 "[]")) (Ok [ 1; 2; 3 ]) "from 1"
                Expect.equal (EventSchema.decode schema (payload 3 "[]")) (Ok [ 3 ]) "from 3"
                Expect.equal (EventSchema.decode schema (payload 4 "[]")) (Ok []) "current"
            }

            test "events that cannot be read say why" {
                let reason (p: Payload) =
                    match EventSchema.decode schema p with
                    | Ok event -> failtest $"decoded {event}"
                    | Error reason -> reason

                Expect.equal
                    (reason (payload 3 """{"kind":"added","body":"x"}"""))
                    "written by schema version 3; this reader knows versions up to 2"
                    "newer"

                Expect.equal (reason (payload 0 "{}")) "schema version 0 is not a version" "zero"

                Expect.equal
                    (reason (payload 1 """{"kind":"added"}"""))
                    "upcasting from version 1: an added note without text"
                    "upcaster"

                Expect.stringContains
                    (reason (payload 2 """{"kind":"shouted","body":"x"}"""))
                    "expected one of \"added\", \"removed\", \"edited\""
                    "codec"
            }

            EventLogConformance.tests "typed over an in-memory payload log" (fun () ->
                EventLog.typed (EventSchema.create Codec.string) (InMemoryEventLog<Payload>().Log))

            test "a typed log stores payloads and reads events" {
                let store = InMemoryEventLog<Payload>()
                let log = EventLog.typed schema store.Log

                Expect.equal
                    (wait (log.Append stream 0L (meta "k1") [ Added "a"; Edited(0, "b") ] CancellationToken.None))
                    (Ok 2L)
                    "appended"

                Expect.equal
                    (store.Events stream |> List.map (fun e -> string e.Event))
                    [
                        """v2 {"body":"a","kind":"added"}"""
                        """v2 {"body":"b","index":0,"kind":"edited"}"""
                    ]
                    "stored"

                Expect.equal
                    (wait (log.Read stream 0L 10 CancellationToken.None)
                     |> List.map (fun e -> e.Event))
                    [ Added "a"; Edited(0, "b") ]
                    "read"
            }

            test "a typed log reads what an older version wrote, and the payloads stay as they were" {
                let store = InMemoryEventLog<Payload>()
                let old = EventLog.typed schemaV1 store.Log

                wait (old.Append stream 0L (meta "k1") [ AddedV1 "milk"; RemovedV1 0 ] CancellationToken.None)
                |> ignore

                let current = EventLog.typed schema store.Log

                wait (current.Append stream 2L (meta "k2") [ Added "eggs" ] CancellationToken.None)
                |> ignore

                Expect.equal
                    (wait (current.Read stream 0L 10 CancellationToken.None)
                     |> List.map (fun e -> e.Event))
                    [ Added "milk"; Removed 0; Added "eggs" ]
                    "upcast"

                Expect.equal (store.Events stream |> List.map (fun e -> e.Event.Version)) [ 1; 1; 2 ] "never rewritten"
            }

            test "reading an event the schema cannot decode is a defect" {
                let store = InMemoryEventLog<Payload>()

                wait (store.Log.Append stream 0L (meta "k1") [ payload 9 "{}" ] CancellationToken.None)
                |> ignore

                let log = EventLog.typed schema store.Log

                try
                    wait (log.Read stream 0L 10 CancellationToken.None) |> ignore
                    failtest "read an unreadable event"
                with :? UnreadableEventException as e ->
                    Expect.equal e.Event (EventRef.create stream 1L) "which event"
                    Expect.stringContains e.Message "notes/1#1: written by schema version 9" "message"
            }
        ]
