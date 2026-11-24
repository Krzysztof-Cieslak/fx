/// A test environment with every core capability, interpreted by the deterministic fakes. It is also
/// the reference example of a concrete environment: a record of capability values that implements
/// the "has" interfaces, plus `IScopeWith` so structured combinators can derive child scopes.
module Fx.Effects.Tests.TestEnv

open System
open Fx.Effects
open Fx.Effects.Fakes

type Env =
    {
        Clock: Clock
        Ids: Ids
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        Secrets: Secrets
        Trace: Trace
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

    interface ISecrets with
        member env.Secrets = env.Secrets

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

/// The fakes behind an `Env`, for tests to drive and inspect.
type Fakes =
    {
        Env: Env
        Clock: FakeClock
        Fs: InMemoryFs
        Shell: ScriptedShell
        Trace: TraceCollector
    }

    override _.ToString() = "Fakes"

/// A fresh environment of fakes. Unexpected shell commands throw.
let createWith (respond: Command -> Result<Output, ShellError> option) : Fakes =
    let clock = FakeClock.Create()
    let fs = InMemoryFs(clock.Clock, false)
    let shell = ScriptedShell respond
    let trace = TraceCollector clock.Clock

    {
        Env =
            {
                Clock = clock.Clock
                Ids = FakeIds(42UL, clock.Clock).Ids
                FsRead = fs.FsRead
                Fs = fs.Fs
                Shell = shell.Shell
                Secrets = Fx.Effects.Fakes.InMemorySecrets.create [ "github/token", "ghp_test" ]
                Trace = trace.Trace
                Scope = Scope.root
            }
        Clock = clock
        Fs = fs
        Shell = shell
        Trace = trace
    }

let create () = createWith (fun _ -> None)

/// Runs an effect in the environment and waits for it (tests only).
let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()

/// The root path of this OS (`/` or `C:\`), for filesystem tests.
let root = if OperatingSystem.IsWindows() then @"C:\" else "/"

/// A path under the root.
let path (segments: string list) =
    IO.Path.Combine(Array.ofList (root :: segments))
