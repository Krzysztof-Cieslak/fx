module Fx.Components.Tests.ConformanceTests

open System
open System.IO
open Expecto
open Fx.Components.Fakes
open Fx.Components.Tests.Conformance
open Fx.Components.Tests.Support
open Fx.Effects
open Fx.Effects.Fakes

/// The root of this OS's paths (`/` or `C:\`), for the in-memory filesystem.
let private fsRoot = if OperatingSystem.IsWindows() then @"C:\" else "/"

[<Tests>]
let tests =
    InstallerConformance.tests "in memory" (fun () ->
        let clock = FakeClock.Create()
        let fs = InMemoryFs(clock.Clock, false)
        let files = InMemoryInstallFiles(fs.Fs, fs.FsRead)
        let fetch = FakeFetch(fs.Fs)
        let outside = Path.Combine(fsRoot, "base")

        {
            Env =
                {
                    Clock = clock.Clock
                    Trace = Trace.none
                    FsRead = fs.FsRead
                    Fs = fs.Fs
                    Shell = (probeShell fs.FsRead).Shell
                    Fetch = fetch.Fetch
                    InstallFiles = files.InstallFiles
                    Scope = Scope.root
                }
            Fetch = fetch
            Root = Path.Combine(outside, "runtimes")
            Outside = outside
            // Slowly enough that work on other threads (a released download) keeps up.
            Advance =
                fun () ->
                    clock.Advance(TimeSpan.FromMilliseconds 100.0)
                    Threading.Thread.Sleep 1
            Release = ignore
        })
