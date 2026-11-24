/// An environment of real interpreters, as a composition root builds one.
module Fx.Effects.Live.Tests.LiveEnv

open System
open System.IO
open Fx.Effects
open Fx.Effects.Live

type Env =
    {
        Clock: Clock
        Ids: Ids
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IShell with
        member env.Shell = env.Shell

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

/// A new, empty temporary directory.
let tempDirectory () =
    let dir =
        Path.Combine(Path.GetTempPath(), "effects-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

/// Deletes a temporary directory, ignoring failures (e.g. a process still holding a file on Windows).
let removeDirectory (dir: string) =
    try
        Directory.Delete(dir, true)
    with _ ->
        ()

/// Real interpreters, with the shell working in `cwd`.
let create (cwd: string) : Env =
    {
        Clock = SystemClock.clock
        Ids = SystemIds.create SystemClock.clock
        FsRead = LocalFs.read
        Fs = LocalFs.write
        Shell = LocalShell.create (LocalShell.defaultShell ()) cwd
        Scope = Scope.root
    }

let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()
