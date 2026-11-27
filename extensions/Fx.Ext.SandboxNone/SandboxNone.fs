/// `fx.sandbox.none` (spec §9.1, §16.4): the sandbox driver that confines nothing, the host's default
/// (the sandbox is opt-in, T137). Processes start as they are, and only the type-level attenuation of
/// the host's bindings applies (spec §7.1); an environment that uses it because a requested sandbox
/// cannot run carries a persistent "unsandboxed" warning. The only driver on Windows in v1
/// (AppContainer: T511). A native extension built only on the public SDK. See README.md and
/// docs/modules/sandbox.md.
module Fx.Ext.SandboxNone.SandboxNone

open Fx
open Fx.Effects
open Fx.Sdk

/// The extension's id: `fx.sandbox.none`.
let id = Product.extensionId "sandbox.none"

/// What specs and the `sandbox.driver` variant name.
[<Literal>]
let Id = SandboxDrivers.NoSandbox

/// The driver: always available, and every process starts unchanged.
let driver: SandboxDriver =
    {
        Id = Id
        Description = "No sandbox: processes can read and change everything the user can."
        Platforms = [ Os.Linux; Os.MacOs; Os.Windows ]
        Confines = false
        Probe =
            fun _ ->
                EffResult.ok
                    {
                        Driver = Id
                        Program = ""
                        Version = Product.Version
                        Features = []
                        Files = Map.empty
                        Aliases = []
                        Notes = []
                    }
        Wrap =
            fun request ->
                Ok
                    {
                        Program = request.Process.Program
                        Cwd = request.Process.Cwd
                        Env = request.Process.Env
                    }
    }

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "No sandbox (the default): processes run with the user's rights."
        Contributions = [ Declare.contribution SandboxDrivers.contributionType Id ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate = Eff.ret (Activation.create [ Contribution.create SandboxDrivers.contributionType Id driver ] [])
    }
