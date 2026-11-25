namespace Fx.Sdk

open Fx.Effects

/// The extension environment every extension gets today: its id, a clock and the scope. The host
/// builds one per extension (`HostSettings.EnvFor`), an extension process one per extension it
/// serves, and tests of an extension one to run its activation in. Host capabilities join it as
/// the SDK grows, each attenuated by the manifest's `capabilities` (docs/modules/sdk.md).
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
