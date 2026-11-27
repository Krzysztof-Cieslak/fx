/// `fx.exthost.bun` (spec §6.3, §16.1): the Bun extension host. A process extension, on the pinned Bun
/// with `fx.runtime.bun`'s hardened launch, that contributes the extension runtime `bun`: the host
/// activates a package whose manifest names `bun` through this process, which imports the package's
/// module (`extension.js`, its default export the extension) and serves it. Its code is TypeScript
/// (packages/exthost-bun, on @fx/sdk), carried in the binary (`ExthostFiles`) and written into its
/// package directory. See README.md and docs/modules/extensions.md.
module Fx.Ext.ExthostBun.ExthostBun

open System
open Fx
open Fx.Contracts
open Fx.Sdk

/// The extension's id: `fx.exthost.bun`.
let id = Product.extensionId "exthost.bun"

/// The extension runtime it contributes.
[<Literal>]
let RuntimeName = "bun"

/// The runtime it runs on: `fx.runtime.bun`'s.
[<Literal>]
let RuntimeId = "bun"

/// The Bun it needs: the pinned one.
let requires: ComponentRef list = [ { Id = RuntimeId; Version = "1.4.5" } ]

/// The module of a package for the `bun` runtime, next to its manifest: one ES module whose default
/// export is the extension, bundled with its dependencies (`bun build --target=bun`).
[<Literal>]
let EntryFile = "extension.js"

/// The process's entry point, relative to the package directory.
[<Literal>]
let Main = "packages/exthost-bun/src/main.ts"

/// The extension host's files, from `ExthostFiles` (generated from packages/exthost-bun/src and
/// packages/sdk/src): paths relative to the package directory, with `/`.
let sources: RuntimeFile list =
    ExthostFiles.all
    |> List.map (fun (name, content) -> { Name = name; Content = content })

/// Where a launch's owned directories and the runtime's files are, in the package directory
/// (`${root}`): the process's home, cache and temporary directories are its own, its configuration
/// files (`fx.runtime.bun`'s `bunfig.toml` and `tsconfig.json`) are in `config/`.
let ownedPlaceholders (runtime: RuntimeSpec) : (string * string) list =
    [
        "${owned.root}", "${root}"
        "${owned.cwd}", "${root}"
        "${owned.home}", "${root}/home"
        "${owned.config}", "${root}/config"
        "${owned.cache}", "${root}/cache"
        "${owned.temp}", "${root}/tmp"
        yield!
            runtime.Files
            |> List.map (fun file -> "${file." + file.Name + "}", "${root}/config/" + file.Name)
    ]

/// A runtime's launch template with its owned directories and files placed in the package directory.
let private owned (runtime: RuntimeSpec) (text: string) : string =
    ownedPlaceholders runtime
    |> List.fold (fun (t: string) (placeholder, value) -> t.Replace(placeholder, value, StringComparison.Ordinal)) text

/// The directories a launch on this package needs, relative to the package directory.
let directories: string list = [ "home"; "tmp"; "cache"; "config" ]

/// Every file of the package directory: the sources, and the runtime's files in `config/`, which its
/// launch names.
let files (runtime: RuntimeSpec) : RuntimeFile list =
    sources
    @ (runtime.Files
       |> List.map (fun file ->
           {
               Name = "config/" + file.Name
               Content = file.Content
           }))

/// How the `process` runtime starts the extension host: the runtime's hardened launch (its flags,
/// untrusted overrides and environment) running `Main`, its owned directories in the package
/// directory. `Exe` stays the runtime's (`${bin.bun}`): whoever starts it puts the installed Bun's
/// path there.
let launch (runtime: RuntimeSpec) : LaunchSpec =
    let bun = runtime.Launch

    { bun with
        Args = (bun.Args |> List.map (owned runtime)) @ [ "${root}/" + Main ]
        UntrustedOverrides = bun.UntrustedOverrides |> List.map (owned runtime)
        EnvSet = bun.EnvSet |> List.map (fun (name, value) -> name, owned runtime value)
    }

/// The manifest, launched on `runtime` (`fx.runtime.bun`'s `RuntimeSpec`). The TypeScript process
/// serves the same manifest without the launch (packages/exthost-bun/test/fixtures/manifest.json).
let manifestOn (runtime: RuntimeSpec) : Manifest =
    { Manifest.create id "1.0.0" Runtimes.Process with
        Description =
            Some
                "The Bun extension host: the extension runtime bun, which runs TypeScript extensions on the pinned Bun."
        Launch = Some(launch runtime)
        Requires = requires
        Contributions = [ Declare.contribution ExtensionRuntimes.contributionType RuntimeName ]
    }
