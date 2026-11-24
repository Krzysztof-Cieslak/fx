namespace Fx.Extensions

open Fx.Effects
open Fx.Sdk

/// The extension environment every extension gets today: its id, a clock and the scope. The SDK adds
/// capabilities (T109), each attenuated by the manifest's `capabilities`.
type BasicExtensionEnv =
    {
        Extension: string
        Clock: Clock
        Scope: Scope
    }

    interface IExtensionEnv with
        member env.Extension = env.Extension

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Scope

        member env.WithScope scope =
            { env with Scope = scope } :> IExtensionEnv

    override env.ToString() = $"BasicExtensionEnv({env.Extension})"

[<RequireQualifiedAccess>]
module BasicExtensionEnv =
    /// The environment of the manifest's extension, with this clock.
    let forManifest (clock: Clock) (manifest: Manifest) : IExtensionEnv =
        {
            Extension = manifest.Id
            Clock = clock
            Scope = Scope.root
        }
        :> IExtensionEnv
