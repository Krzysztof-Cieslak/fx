namespace Fx.Host

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Fx.Effects
open Fx.Sdk
open Fx.Extensions

/// A built-in whose package is more than its manifest (spec §16.4): files the binary carries, written
/// into its package directory (`Bundled.extract`) before it starts, and the directories it needs there.
type BundledPackage =
    {
        /// The extension's id.
        Id: string
        /// Its package directory: `<data>/extensions/<id>`.
        Root: string
        /// Paths relative to the package directory, with `/`.
        Files: RuntimeFile list
        Directories: string list
    }

    override this.ToString() = $"bundled {this.Id} in {this.Root}"

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

    /// The bundled packages with files, their directories under `extensions` (`HostPaths.Extensions`):
    /// `fx.exthost.bun`, the Bun extension host, its TypeScript and `fx.runtime.bun`'s configuration
    /// files.
    let bundled (extensions: string) : BundledPackage list =
        [
            {
                Id = Fx.Ext.ExthostBun.ExthostBun.id
                Root = Path.Combine(extensions, Fx.Ext.ExthostBun.ExthostBun.id)
                Files = Fx.Ext.ExthostBun.ExthostBun.files Fx.Ext.RuntimeBun.RuntimeBun.runtime
                Directories = Fx.Ext.ExthostBun.ExthostBun.directories
            }
        ]

    let private rootOf (extensions: string) (id: string) : string option =
        bundled extensions |> List.tryFind (fun p -> p.Id = id) |> Option.map _.Root

    /// The built-ins, in bootstrap order, bundled packages' directories under `extensions`:
    /// `fx.egress.exclusions` reads its settings through `exclusions` (the host's resolution, until
    /// extensions read their own slots: T109b). `fx.exthost.bun` is a process extension on the pinned
    /// Bun, which the bootstrap's components phase installs.
    let candidates (extensions: string) (exclusions: Eff<IExtensionEnv, ExclusionSettings>) : Candidate list =
        [
            Package.Native Fx.Ext.EnvLocal.EnvLocal.extension
            // Sandbox drivers: `auto` takes the first that confines processes on an environment's OS,
            // so the confining ones come before `none`.
            Package.Native Fx.Ext.SandboxBubblewrap.Bubblewrap.extension
            Package.Native Fx.Ext.SandboxSeatbelt.Seatbelt.extension
            Package.Native Fx.Ext.SandboxNone.SandboxNone.extension
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
            Package.Manifest(
                Fx.Ext.ExthostBun.ExthostBun.manifestOn Fx.Ext.RuntimeBun.RuntimeBun.runtime,
                rootOf extensions Fx.Ext.ExthostBun.ExthostBun.id
            )
        ]
        |> List.map (Candidate.create Source.Builtin)

    /// The ids of the built-ins.
    let ids: string list =
        candidates "extensions" (Eff.ret ContentExclusions.none)
        |> List.map (fun c -> (Package.manifest c.Package).Id)

/// Writes bundled packages' files into their directories (spec §16.4: extracted with hash checks).
[<RequireQualifiedAccess>]
module Bundled =
    let private utf8 = UTF8Encoding(false)

    let private sha256 (bytes: byte array) : string =
        Convert.ToHexStringLower(SHA256.HashData bytes)

    /// Writes a bundled package's files into its directory, with hash checks: a file already there
    /// whose SHA-256 is the content's stays; any other is written and read back, and must then have
    /// it. Its directories are created. An `Error` names the path that failed.
    let extract<'R when 'R :> IFs and 'R :> IScope> (package: BundledPackage) : Eff<'R, Result<unit, string>> =
        eff {
            let mutable failure = None

            let path (relative: string) =
                Path.Combine(package.Root, relative.Replace('/', Path.DirectorySeparatorChar))

            let directories =
                package.Root :: (package.Directories |> List.map path)
                @ (package.Files
                   |> List.map (fun file -> Path.GetDirectoryName(path file.Name) |> nonNull))
                |> List.distinct

            for directory in directories do
                if failure.IsNone then
                    match! Fs.createDirectory directory with
                    | Ok() -> ()
                    | Error e -> failure <- Some $"{directory}: {e}"

            for file in package.Files do
                if failure.IsNone then
                    let target = path file.Name
                    let bytes = utf8.GetBytes file.Content
                    let expected = sha256 bytes
                    let! existing = Fs.readFile target

                    let intact =
                        match existing with
                        | Ok content -> sha256 content = expected
                        | Error _ -> false

                    if not intact then
                        match! Fs.writeFile target bytes with
                        | Error e -> failure <- Some $"{target}: {e}"
                        | Ok() ->
                            match! Fs.readFile target with
                            | Ok written when sha256 written = expected -> ()
                            | Ok _ ->
                                failure <- Some $"{target}: written, but its content does not have the expected hash"
                            | Error e -> failure <- Some $"{target}: {e}"

            return
                match failure with
                | Some failure -> Error failure
                | None -> Ok()
        }
