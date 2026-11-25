module Fx.Grants.Host.Tests.StoreTests

open Expecto
open Fx.Machines.Host
open Fx.Store
open Fx.Grants.Host

[<Tests>]
let tests =
    GrantStoreConformance.tests "host-local in memory" (fun () ->
        GrantStoreLog.hostLocal (InMemoryEventLog<Payload>()).Log)
