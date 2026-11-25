module Fx.Grants.Host.Tests.StoreTests

open System.Threading
open Expecto
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Sdk
open Fx.Grants
open Fx.Grants.Host
open Fx.Grants.Host.Tests.Support

let private grantMeta (key: string) : GrantEventMeta =
    {
        At = 1_800_000_000_000_000L
        Principal = "user:alice"
        Via = "tui"
        Key = key
        Cause = None
    }

let private expired (n: int) : GrantEvent =
    GrantEvent.Expired
        {
            Grant = GrantId("g" + string n)
            At = int64 n
        }

[<Tests>]
let tests =
    testList
        "Grant stores"
        [
            GrantStoreConformance.tests "host-local in memory" (fun () ->
                GrantStoreLog.hostLocal (InMemoryEventLog<Payload>()).Log)

            test "the actor's event log finds the commit a key appended" {
                let store = GrantStoreLog.hostLocal (InMemoryEventLog<Payload>()).Log
                let stream = GrantMachine.stream "host"
                let log = GrantStoreLog.toEventLog store stream

                let find key =
                    wait (log.FindCommit stream (IdempotencyKey key) CancellationToken.None)

                Expect.isNone (find "k1") "an empty store"
                wait (store.Append 0L (grantMeta "k1") [ expired 1; expired 2 ] none) |> ignore
                wait (store.Append 2L (grantMeta "k2") [ expired 3 ] none) |> ignore
                Expect.equal (find "k1") (Some { After = 0L; Count = 2 }) "the first commit"
                Expect.equal (find "k2") (Some { After = 2L; Count = 1 }) "the second commit"
                Expect.isNone (find "k3") "a key never appended"
            }
        ]
