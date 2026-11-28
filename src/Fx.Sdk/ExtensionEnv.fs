namespace Fx.Sdk

open Fx.Effects
open Fx.Variants

/// The environment extension code runs in: the capabilities the host hands an extension (spec §4.2,
/// §16.1). The host builds one per extension, attenuated to what its manifest asks for, and runs
/// that extension's hooks, methods and activation in it; only the scope (cancellation, trace span)
/// comes from the caller. So an extension's authority is this value, never the caller's.
///
/// Every extension has the clock. `IVariants` (its own slots), `IPackages` and `IGrants` work only
/// if its manifest lists them (`variants`, `packages`, `grants`); otherwise they throw
/// `CapabilityDeniedException`. `Capabilities` is the attenuated set they are implemented with: an
/// environment that wraps another passes it on (`ExtensionEnv.capabilities`).
type IExtensionEnv =
    inherit IScopeWith<IExtensionEnv>
    inherit IClock
    inherit IVariants
    inherit IPackages
    inherit IGrants

    /// The id of the extension whose code runs in this environment.
    abstract Extension: string

    /// The extension's capabilities, attenuated to its manifest.
    abstract Capabilities: ExtensionCapabilities

/// Effects over the extension environment.
[<RequireQualifiedAccess>]
module ExtensionEnv =
    /// The id of the extension whose code is running.
    let extension: Eff<IExtensionEnv, string> =
        fun env -> System.Threading.Tasks.Task.FromResult env.Extension

    /// The values of the running extension's own variant slots, with their sources (needs `variants`).
    let slots: Eff<IExtensionEnv, Resolved list> =
        fun env -> System.Threading.Tasks.Task.FromResult(env.Capabilities.OwnSlots())

    /// Runs extension code in the environment of its extension, with the caller's scope: the code
    /// gets the extension's capabilities and the caller's cancellation and trace span.
    let within (extensionEnv: IExtensionEnv) (effect: Eff<IExtensionEnv, 'T>) : Eff<#IScope, 'T> =
        fun caller -> effect (extensionEnv.WithScope caller.Scope)
