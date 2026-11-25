/// `fx.env.local` as the composition root wires it: activated as a native extension, its provider
/// given the host's files and processes, envd embedded in this process (`EnvdMain.embed`) or started
/// as the `fx` binary next to these tests (`envd --stdio`).
module Fx.Environments.Tests.Local

open System
open System.Collections
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Effects
open Fx.Effects.Live
open Fx.Environments
open Fx.Environments.Tests.Conformance
open Fx.Envd.Live
open Fx.Ext.EnvLocal
open Fx.Machines.Host
open Fx.Sdk

let windows = OperatingSystem.IsWindows()

let wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

/// Temporary directories under one root per test run, deleted when the process exits.
module Scratch =
    let root =
        Path.Combine(Path.GetTempPath(), "environments-tests-" + Guid.NewGuid().ToString("N"))

    /// Deletes a tree, read-only files included (git makes its objects read-only).
    let deleteTree (path: string) =
        if Directory.Exists path then
            for file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) do
                File.SetAttributes(file, FileAttributes.Normal)

            Directory.Delete(path, true)

    do
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
            try
                deleteTree root
            with _ ->
                ())

    let directory () =
        let dir = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 12))
        Directory.CreateDirectory dir |> ignore
        dir

/// The `fx` binary next to the tests.
let binary =
    Path.Combine(AppContext.BaseDirectory, Product.binaryName + (if windows then ".exe" else ""))

/// This machine's target.
let target: TargetTriple =
    match TargetTriple.ofRid RuntimeInformation.RuntimeIdentifier with
    | Some target -> target
    | None -> failwith ("unknown runtime identifier " + RuntimeInformation.RuntimeIdentifier)

let hostInfo (dataDir: string) : HostInfo =
    {
        Target = target
        DataDir = dataDir
        Home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    }

/// The process's variables, and the suite's.
let hostEnvironment () : (string * string) list =
    [
        for entry in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> do
            match entry.Key, entry.Value with
            | (:? string as name), (:? string as value) -> name, value
            | _ -> ()
    ]
    |> List.sortBy fst
    |> fun vars -> vars @ ProviderConformance.hygieneVariables

/// The provider as the extension host would have it: activated in its extension's environment.
let contributed () : Contributed<EnvProvider> =
    let env = BasicExtensionEnv.forManifest SystemClock.clock EnvLocal.manifest
    let activation = wait (Eff.run env EnvLocal.extension.Activate)

    {
        Extension = EnvLocal.manifest.Id
        Type = EnvProviders.contributionType.Name
        Id = EnvLocal.Kind
        Value =
            activation.Contributions
            |> List.pick (Contribution.tryValue EnvProviders.contributionType)
        Env = env
    }

/// An extension's contributions of a type, activated as the extension host would.
let contributionsOf
    (extension: Extension)
    (contributionType: ContributionType<'T>)
    (id: 'T -> string)
    : Contributed<'T> list =
    let env = BasicExtensionEnv.forManifest SystemClock.clock extension.Manifest
    let activation = wait (Eff.run env extension.Activate)

    activation.Contributions
    |> List.choose (Contribution.tryValue contributionType)
    |> List.map (fun value ->
        {
            Extension = extension.Manifest.Id
            Type = contributionType.Name
            Id = id value
            Value = value
            Env = env
        })

/// The sandbox drivers as the composition root lists them: the confining ones first, then `none`.
let sandboxes () : Contributed<SandboxDriver> list =
    [
        Fx.Ext.SandboxBubblewrap.Bubblewrap.extension
        Fx.Ext.SandboxSeatbelt.Seatbelt.extension
        Fx.Ext.SandboxNone.SandboxNone.extension
    ]
    |> List.collect (fun extension -> contributionsOf extension SandboxDrivers.contributionType _.Id)

/// envd in this process.
let embed (launch: EmbeddedLaunch) : Task<Result<Embedded, string>> =
    task {
        let options =
            { EnvdOptions.defaults with
                Workspace = Some launch.Workspace
                State = Some launch.State
                Shell = launch.Shell
                Environment = Some launch.Environment
            }

        match! EnvdMain.embed options with
        | Error e -> return Error e
        | Ok embedded ->
            let connection: Embedded =
                {
                    Transport = embedded.Transport
                    Stop = embedded.Stop
                }

            return Ok connection
    }

/// The settings of a host whose environments live under a data directory.
let settings (dataDir: string) : EnvsSettings =
    {
        Stream = EnvsHost.stream "tests"
        Providers = [ contributed () ]
        Sandboxes = sandboxes ()
        FsRead = LocalFs.read
        Fs = LocalFs.write
        Shell = LocalShell.create (LocalShell.defaultShell ()) dataDir
        HostInfo = hostInfo dataDir
        Binary = binary
        BinaryArgs = []
        HostEnvironment = hostEnvironment ()
        Embed = Some embed
        Components = EnvComponents.none
        StopTimeout = TimeSpan.FromSeconds 10.0
        Limits = Fx.Rpc.ConnectionLimits.defaults
    }

let start (settings: EnvsSettings) (log: EventLog<EnvEvent>) : EnvsHost<ProviderConformance.HostEnv> =
    wait (Eff.run (ProviderConformance.HostEnv.live ()) (EnvsHost.start settings log))

/// A host whose variants have these selections (e.g. `sandbox.driver`).
let startWith
    (selections: (string * Fx.Contracts.Json) list)
    (settings: EnvsSettings)
    (log: EventLog<EnvEvent>)
    : EnvsHost<ProviderConformance.HostEnv> =
    wait (Eff.run (ProviderConformance.HostEnv.withSelections selections) (EnvsHost.start settings log))

let providerEnv (dataDir: string) : ProviderEnv =
    let settings = settings dataDir
    let contributed = settings.Providers.Head

    {
        Extension = contributed.Env
        FsRead = settings.FsRead
        Fs = settings.Fs
        Shell = settings.Shell
        HostInfo = settings.HostInfo
    }

/// A fresh workspace with `hello.txt`.
let workspace () : string =
    let dir = Scratch.directory ()
    File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello\n")
    dir

/// A spec for a fresh workspace, with a transport.
let spec (transport: TransportKind) : EnvSpec =
    { (EnvSpec.create "dev" EnvLocal.Kind |> EnvSpec.withWorkspace (workspace ())) with
        Transport = Some transport
    }

/// The local provider as a conformance subject, with a transport.
let subject (transport: TransportKind) : ProviderConformance.Subject =
    let dataDir = Scratch.directory ()

    {
        Name = $"local, {transport}"
        Provider = (contributed ()).Value
        Transport = transport
        Spec = fun () -> spec transport
        SameWorkspace =
            fun other ->
                { EnvSpec.create "again" EnvLocal.Kind with
                    Workspace = other.Workspace
                    Transport = Some transport
                }
        Failing =
            fun () ->
                { (EnvSpec.create "missing" EnvLocal.Kind
                   |> EnvSpec.withWorkspace (Path.Combine(Scratch.directory (), "missing"))) with
                    Transport = Some transport
                }
        Start = start (settings dataDir)
        ProviderEnv = fun () -> providerEnv dataDir
        Lose = fun lease -> Scratch.deleteTree lease.Home
    }
