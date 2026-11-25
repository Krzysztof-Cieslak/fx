/// Installer environments of real interpreters, as a composition root builds them.
module Fx.Components.Live.Tests.LiveEnv

open System
open System.IO
open Fx.Components.Fakes
open Fx.Components.Live
open Fx.Components.Tests.Support
open Fx.Effects
open Fx.Effects.Live

/// A new, empty temporary directory.
let tempDirectory () =
    let dir =
        Path.Combine(Path.GetTempPath(), "components-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

/// Deletes a temporary directory, ignoring failures (e.g. a file still open on Windows).
let removeDirectory (dir: string) =
    try
        for file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
            File.SetAttributes(file, FileAttributes.Normal)

        Directory.Delete(dir, true)
    with _ ->
        ()

/// Real filesystem, install files and clock; a fake fetch and a shell that runs probes by printing
/// the program's file (unless `shell` is given).
let create (fetch: FakeFetch) (shell: Shell option) : InstallerEnv =
    {
        Clock = SystemClock.clock
        Trace = Trace.none
        FsRead = LocalFs.read
        Fs = LocalFs.write
        Shell = shell |> Option.defaultWith (fun () -> (probeShell LocalFs.read).Shell)
        Fetch = fetch.Fetch
        InstallFiles = LocalInstallFiles.files
        Scope = Scope.root
    }
