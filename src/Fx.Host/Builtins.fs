namespace Fx.Host

open Fx.Effects
open Fx.Sdk
open Fx.Extensions

/// The extensions built into the binary (spec §16.4): a static list, never loaded from assemblies
/// (Native AOT). Each goes through the same bootstrap as any other extension (admission, then
/// activation), and uses only the public SDK.
[<RequireQualifiedAccess>]
module Builtins =
    /// The contribution types the host knows besides the extension system's own (`Catalog.core`):
    /// models (wires, providers, egress hooks), identity providers, grants, and service credentials
    /// (auth methods, credential stores, secret sources).
    let catalog: Catalog =
        Catalog.create (
            ModelAccess.contributionTypes
            @ IdentityProviders.contributionTypes
            @ GrantPolicy.contributionTypes
            @ AuthMethods.contributionTypes
        )

    /// The built-ins, in bootstrap order: `fx.egress.exclusions` reads its settings through `exclusions`
    /// (the host's resolution, until extensions read their own slots: T109b).
    let candidates (exclusions: Eff<IExtensionEnv, ExclusionSettings>) : Candidate list =
        [
            Package.Native Fx.Ext.EnvLocal.EnvLocal.extension
            Package.Native Fx.Ext.IdentityLocalOsUser.LocalOsUser.extension
            Package.Native Fx.Ext.StoreKeyring.StoreKeyring.extension
            Package.Native Fx.Ext.StoreFile.StoreFile.extension
            Package.Native Fx.Ext.StoreEphemeral.StoreEphemeral.extension
            Package.Native Fx.Ext.AuthStandard.AuthStandard.extension
            Package.Native Fx.Ext.ModelAnthropic.Anthropic.extension
            Package.Native Fx.Ext.ModelOpenAI.OpenAI.extension
            Package.Native(Fx.Ext.EgressExclusions.EgressExclusions.create exclusions)
            Package.Native Fx.Ext.InterpreterBunTs.BunTs.extension
            Package.Manifest(Fx.Ext.RuntimeBun.RuntimeBun.manifest, None)
        ]
        |> List.map (Candidate.create Source.Builtin)

    /// The ids of the built-ins.
    let ids: string list =
        candidates (Eff.ret ContentExclusions.none)
        |> List.map (fun c -> (Package.manifest c.Package).Id)
