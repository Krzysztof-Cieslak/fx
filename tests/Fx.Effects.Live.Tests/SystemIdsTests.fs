module Fx.Effects.Live.Tests.SystemIdsTests

open System
open Expecto
open Fx.Effects
open Fx.Effects.Live
open Fx.Effects.Tests.Conformance

[<Tests>]
let tests =
    testList
        "SystemIds"
        [
            IdsConformance.tests "SystemIds" (fun () -> SystemIds.create SystemClock.clock)

            test "ids carry the current time" {
                let before = Instant.unixMilliseconds (SystemClock.clock.Now())
                let id = (SystemIds.create SystemClock.clock).NewId()
                let after = Instant.unixMilliseconds (SystemClock.clock.Now())
                let stamp = Instant.unixMilliseconds (Id.timestamp id)
                Expect.isTrue (before <= stamp && stamp <= after) $"{before} <= {stamp} <= {after}"
            }
        ]
