namespace Fx.Envd

open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// The smallest environment a protocol call needs: its scope.
type internal CallEnv =
    {
        Scope: Scope
    }

    interface IScope with
        member env.Scope = env.Scope

    override env.ToString() = $"CallEnv({env.Scope})"

module internal CallEnv =
    /// Runs an effect that needs only a scope, cancelled by the token.
    let run (cancellation: CancellationToken) (effect: Eff<CallEnv, 'T>) : Task<'T> =
        Eff.run
            {
                Scope = Scope.ofCancellation cancellation
            }
            effect

/// The environment a connection runs in (`Rpc.accept`, `EnvClient.connect`): a clock for timeouts
/// and a scope whose cancellation closes the connection.
type ConnectionEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<ConnectionEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"ConnectionEnv({env.Scope})"
