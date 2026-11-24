/// The environment the store tests run effects in: a fake clock, an in-memory blob store, a scope.
module Fx.Store.Tests.TestEnv

open Fx.Effects
open Fx.Effects.Fakes
open Fx.Store
open Fx.Store.Fakes

type Env =
    {
        Clock: Clock
        Blobs: Blobs
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IBlobs with
        member env.Blobs = env.Blobs

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

let create () : Env =
    {
        Clock = (FakeClock.Create()).Clock
        Blobs = InMemoryBlobs().Blobs
        Scope = Scope.root
    }

/// Runs an effect and waits for it (tests only).
let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()
