module Fx.Components.Live.Tests.LiveConformanceTests

open System.IO
open System.Threading
open Expecto
open Fx.Components.Fakes
open Fx.Components.Live.Tests.LiveEnv
open Fx.Components.Tests.Conformance
open Fx.Effects.Live

[<Tests>]
let tests =
    testSequenced
    <| InstallerConformance.tests "local disk" (fun () ->
        let outside = tempDirectory ()
        let fetch = FakeFetch(LocalFs.write)

        {
            Env = create fetch None
            Fetch = fetch
            Root = Path.Combine(outside, "runtimes")
            Outside = outside
            Advance = fun () -> Thread.Sleep 20
            Release = fun () -> removeDirectory outside
        })
