namespace Fx.Sdk

open Fx.Effects

/// The extension environment the host builds: its id, a clock, its capabilities (attenuated to its
/// manifest) and the scope. The host builds one per extension (`HostSettings.EnvFor`), an extension
/// process one per extension it serves (its capabilities reach the host over the protocol), and
/// tests of an extension one to run its activation in.
type BasicExtensionEnv =
    {
        Extension: string
        Clock: Clock
        Capabilities: ExtensionCapabilities
        Scope: Scope
    }

    interface IExtensionEnv with
        member env.Extension = env.Extension
        member env.Capabilities = env.Capabilities

    interface IClock with
        member env.Clock = env.Clock

    interface Fx.Variants.IVariants with
        member env.Variants = env.Capabilities.Variants

    interface IPackages with
        member env.Packages = env.Capabilities.Packages

    interface IGrants with
        member env.Grants = env.Capabilities.Grants

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Scope

        member env.WithScope scope =
            { env with Scope = scope } :> IExtensionEnv

    override env.ToString() = $"BasicExtensionEnv({env.Extension})"

[<RequireQualifiedAccess>]
module BasicExtensionEnv =
    /// An environment with these capabilities.
    let create (clock: Clock) (capabilities: ExtensionCapabilities) : IExtensionEnv =
        {
            Extension = capabilities.Extension
            Clock = clock
            Capabilities = capabilities
            Scope = Scope.root
        }
        :> IExtensionEnv

    /// The environment of an extension with the clock only (`ExtensionCapabilities.none`): for tests
    /// of an extension, and for hosts that offer nothing else.
    let ofId (clock: Clock) (extension: string) : IExtensionEnv =
        create clock (ExtensionCapabilities.none extension)

    /// The environment of the manifest's extension with the clock only.
    let forManifest (clock: Clock) (manifest: Manifest) : IExtensionEnv = ofId clock manifest.Id

    /// The environment of the manifest's extension, attenuated to its `capabilities` from what the
    /// host offers.
    let attenuated (clock: Clock) (services: HostServices) (manifest: Manifest) : IExtensionEnv =
        create clock (ExtensionCapabilities.attenuate services manifest.Id manifest.Capabilities)
