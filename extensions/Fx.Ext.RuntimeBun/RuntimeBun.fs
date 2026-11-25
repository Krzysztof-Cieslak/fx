/// `fx.runtime.bun` (spec §6.3): the pinned Bun, installed by the component installer, and its
/// hardened launch, as data. A declarative extension: its manifest carries the component and the
/// runtime, and nothing of it runs. Kernels, extension hosts and the workflow worker (T115, T117)
/// launch Bun through `RuntimeSpec.launch` with `runtime`. See README.md and
/// docs/modules/runtimes.md.
module Fx.Ext.RuntimeBun.RuntimeBun

open Fx
open Fx.Contracts
open Fx.Sdk

/// The extension's id: `fx.runtime.bun`.
let id = Product.extensionId "runtime.bun"

/// The component's id, which is also the runtime's.
[<Literal>]
let ComponentId = "bun"

let private digest (hex: string) : Digest =
    match Digest.parse ("sha256:" + hex) with
    | Ok digest -> digest
    | Error message -> invalidArg (nameof hex) message

let private version =
    match SemVer.parse Pins.Version with
    | Ok version -> version
    | Error message -> invalidOp message

/// The pinned Bun for every product target: one archive per target (strip the top directory), the
/// executable `bun` (`bun.exe`), and a probe that compares `bun --revision` exactly.
let bunComponent: Component =
    let targets = TargetTriple.all
    let used = targets |> List.map (Pins.assetFor >> Pins.find)

    { Component.create
          ComponentId
          version
          [
              for target in targets do
                  let asset = Pins.find (Pins.assetFor target)

                  {
                      Platforms = [ target ]
                      Method =
                          Archive
                              {
                                  Url = $"{Pins.ReleaseUrl}/{asset.Name}.zip"
                                  Digest = digest asset.Sha256
                                  Format = ArchiveFormat.Zip
                                  Strip = 1
                              }
                  }
          ] with
        Layout = { Executables = [ "bun${exeSuffix}" ] }
        Exports =
            {
                Bin = [ "bun", "bun${exeSuffix}" ]
                Env = []
            }
        Probe =
            Some
                {
                    Argv = [ "bun${exeSuffix}"; "--revision" ]
                    Expect = Some Pins.Revision
                }
        Platforms = targets
        // The largest target's, as an upper bound.
        Size =
            {
                Download = Some(used |> List.map (fun a -> a.Bytes) |> List.max)
                Installed = Some(used |> List.map (fun a -> a.ExecutableBytes) |> List.max)
            }
    }

/// Bun's configuration for every launch, given with `--config`, so a project's `bunfig.toml` is
/// never read. It repeats the hardening flags, so that either one alone is enough.
let bunfig: RuntimeFile =
    {
        Name = "bunfig.toml"
        Content =
            String.concat
                "\n"
                [
                    "# Written by the launcher before every launch: project and global bunfig.toml files are not read."
                    "telemetry = false"
                    "env = false"
                    ""
                    "[install]"
                    "auto = \"disable\""
                    ""
                    "[run]"
                    "noOrphans = true"
                    ""
                ]
    }

/// The `tsconfig.json` Bun uses instead of the project's while the project is not trusted
/// (`--tsconfig-override`): a project's `paths` cannot redirect imports before trust.
let tsconfig: RuntimeFile =
    {
        Name = "tsconfig.json"
        Content = "{ \"compilerOptions\": {} }\n"
    }

/// The hardened launch (spec §6.3). Each flag exists in the pinned version (`bun --help` lists it;
/// the live tests check) and was verified with a positive control (docs/spikes/bun-kernel.md):
///
/// - `--config`: our `bunfig.toml`, never the project's; `--no-env-file`: no `.env` files;
///   `--no-install`: no automatic installs; `--no-orphans`: exit with the parent and take
///   descendants along; `--no-macros`: no macros at bundle or load time.
/// - While the project is untrusted, `--tsconfig-override` replaces the project's `tsconfig.json`.
/// - The environment is built fresh (never the launcher's): a few allowlisted names, then fixed
///   values that point every home, config, cache and temporary directory into owned directories
///   and turn telemetry and crash reports off. Every injection vector is denied.
let launch: LaunchSpec =
    {
        Exe = "${bin.bun}"
        Args =
            [
                "--config=${file.bunfig.toml}"
                "--no-env-file"
                "--no-install"
                "--no-orphans"
                "--no-macros"
            ]
        UntrustedOverrides = [ "--tsconfig-override=${file.tsconfig.json}" ]
        EnvAllowlist =
            [
                // Programs the runtime starts (a cell's child process) resolve commands with it; Bun
                // itself is started by its absolute path.
                "PATH"
                // The locale and time zone of the user, for text and dates (child processes too).
                "LANG"
                "LC_ALL"
                "LC_CTYPE"
                "TZ"
                // Windows: Winsock, CNG and other system DLLs find their files through SystemRoot
                // (windir is its older alias, SystemDrive the drive); ComSpec and PATHEXT are how
                // child processes are started and found; the last three describe the machine to the
                // programs Bun starts. None of them configures Bun.
                "SystemRoot"
                "windir"
                "SystemDrive"
                "ComSpec"
                "PATHEXT"
                "NUMBER_OF_PROCESSORS"
                "PROCESSOR_ARCHITECTURE"
                "OS"
            ]
        EnvSet =
            [
                // No telemetry, no crash-report uploads (on by default on macOS and Windows).
                "DO_NOT_TRACK", "1"
                "BUN_ENABLE_CRASH_REPORTING", "0"
                // Output goes to a protocol or a model, not a terminal.
                "NO_COLOR", "1"
                // Global bunfig, .npmrc and caches resolve under the owned home: out of reach of the
                // user's own and of other launches.
                "HOME", "${owned.home}"
                "USERPROFILE", "${owned.home}"
                "XDG_CONFIG_HOME", "${owned.home}"
                "XDG_CACHE_HOME", "${owned.cache}"
                "BUN_RUNTIME_TRANSPILER_CACHE_PATH", "${owned.cache}"
                "BUN_INSTALL_CACHE_DIR", "${owned.cache}"
                "TMPDIR", "${owned.temp}"
                "TEMP", "${owned.temp}"
                "TMP", "${owned.temp}"
            ]
        EnvDeny = InjectionVectors.patterns
        Cwd = LaunchCwd.Owned
    }

/// The Bun runtime.
let runtime: RuntimeSpec =
    {
        Component = bunComponent
        Launch = launch
        Files = [ bunfig; tsconfig ]
    }

/// The package's manifest: declarative, with the component and the runtime as values. The host's
/// planner reads the component (`component`), launchers the runtime (`runtime`).
let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Declarative with
        Description =
            Some
                "The pinned Bun runtime: its component (one archive per target, digests pinned) and its hardened launch."
        Contributions =
            [
                Declare.value ComponentDefinitions.contributionType $"{ComponentId}-{Pins.Version}" bunComponent
                Declare.value RuntimeSpecs.contributionType ComponentId runtime
            ]
    }

/// The manifest as it is written into the package (`Manifest.fileName`), with its hash.
let manifestText: string =
    Manifest.serialize
        { manifest with
            Hash = Some(Manifest.hash manifest)
        }

/// Every issue with the package's data: its manifest and its runtime. Empty for what ships.
let check () : Issue list =
    Manifest.check manifest @ RuntimeSpec.validate runtime
