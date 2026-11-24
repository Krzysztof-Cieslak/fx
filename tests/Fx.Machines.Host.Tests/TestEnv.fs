/// The environment actors run in during tests: a fake clock and an in-memory trace.
module Fx.Machines.Host.Tests.TestEnv

open System
open Fx.Effects
open Fx.Effects.Fakes

type Env =
    {
        Clock: Clock
        Trace: Trace
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

type Fakes =
    {
        Env: Env
        Clock: FakeClock
        Trace: TraceCollector
    }

    override _.ToString() = "Fakes"

let create () : Fakes =
    let clock = FakeClock.Create()
    let trace = TraceCollector clock.Clock

    {
        Env =
            {
                Clock = clock.Clock
                Trace = trace.Trace
                Scope = Scope.root
            }
        Clock = clock
        Trace = trace
    }

/// Runs an effect and waits for it (tests only).
let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()

let seconds (n: float) = TimeSpan.FromSeconds n
