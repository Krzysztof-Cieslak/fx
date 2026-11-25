/// Plan → lockfile → install → launch, offline: the Bun component goes through the planner and the
/// real installer on a real disk, with the download served from memory (a tiny stand-in archive
/// whose digest replaces the pinned one) and the probe answered by a fake shell. Then the hardened
/// launch is resolved from what the installer returned.
module Fx.Ext.RuntimeBun.Tests.InstallTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Expecto
open Fx.Components
open Fx.Components.Fakes
open Fx.Components.Live.Tests.LiveEnv
open Fx.Components.Tests.Support
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Ext.RuntimeBun
open Fx.Machines.Host
open Fx.Sdk

/// The target of this machine (the test host's runtime identifier).
let machine () : TargetTriple =
    match TargetTriple.ofRid RuntimeInformation.RuntimeIdentifier with
    | Some target -> target
    | None -> failtest $"no target for {RuntimeInformation.RuntimeIdentifier}"

let private requirement: Requirement =
    {
        Ref = { Id = "bun"; Version = "^1.4" }
        By = "test"
    }

/// An environment with a filesystem and a scope, for `RuntimeSpec.prepare`.
type FsEnv =
    {
        FsRead: FsRead
        Fs: Fs
        Scope: Scope
    }

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IScopeWith<FsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = "FsEnv"

let localFs: FsEnv =
    {
        FsRead = LocalFs.read
        Fs = LocalFs.write
        Scope = Scope.root
    }

/// Installs the components a lockfile pins for this machine with an installer on `root`, and
/// returns Bun as the installer hands it over.
let install (env: InstallerEnv) (root: string) (lockfile: Lockfile) (target: TargetTriple) : InstalledComponent =
    // GitHub's release downloads answer 503 now and then: retry for about a minute.
    let settings =
        { InstallerSettings.create root "test" (fun _ -> Eff.ret (ApprovalAnswer.Approved "test")) with
            DownloadAttempts = 6
            RetryDelay = TimeSpan.FromSeconds 2.0
        }

    let actor =
        runIn env (ComponentInstaller.start settings (InMemoryEventLog<InstallerEvent>()).Log)

    let mutable keys = 0

    let components =
        ComponentInstaller.components actor lockfile target (fun () ->
            keys <- keys + 1
            string keys)

    try
        match
            (components.Ensure
                {
                    Holder = "test"
                    Requires = [ requirement.Ref ]
                }
                CancellationToken.None)
                .WaitAsync(TimeSpan.FromMinutes 10.0)
                .GetAwaiter()
                .GetResult()
        with
        | Ok [ installed ] -> installed
        | Ok installed -> failtest $"expected Bun alone, got {installed.Length} components"
        | Error e -> failtest $"the install failed: {e}"
    finally
        runIn env (Actor.stop actor)

[<Tests>]
let tests =
    testList
        "Install (offline)"
        [
            test "the lockfile for every target is reproducible (golden)" {
                let plan components =
                    match Planner.plan PlanSettings.defaults components [ requirement ] (List.rev TargetTriple.all) with
                    | Ok lockfile -> lockfile
                    | Error errors -> failtest (errors |> List.map string |> String.concat "; ")

                let lockfile = plan [ RuntimeBun.bunComponent ]
                let again = plan [ RuntimeBun.bunComponent; RuntimeBun.bunComponent ]
                Expect.equal (Lockfile.render again) (Lockfile.render lockfile) "the same bytes"
                Expect.equal (Lockfile.hash again) (Lockfile.hash lockfile) "the same hash"
                BunTests.Golden.expect "bun.lock.json" (Lockfile.render lockfile)
            }

            test "plan, install and launch on this machine with a stand-in archive" {
                let target = machine ()
                let asset = Pins.assetFor target
                let exe = "bun" + TargetTriple.exeSuffix target

                let archive = zip [ Dir asset; File($"{asset}/{exe}", "a stand-in for bun", true) ]

                // The pinned digest is the real archive's: put the stand-in's in its place.
                let standIn =
                    { RuntimeBun.bunComponent with
                        Methods =
                            RuntimeBun.bunComponent.Methods
                            |> List.map (fun choice ->
                                match choice.Method with
                                | Archive spec when choice.Platforms = [ target ] ->
                                    { choice with
                                        Method = Archive { spec with Digest = digestOf archive }
                                    }
                                | _ -> choice)
                    }

                let lockfile =
                    match Planner.plan PlanSettings.defaults [ standIn ] [ requirement ] [ target ] with
                    | Ok lockfile -> lockfile
                    | Error errors -> failtest (errors |> List.map string |> String.concat "; ")

                let outside = tempDirectory ()

                try
                    let fetch = FakeFetch(LocalFs.write)

                    fetch.Serve(
                        $"https://github.com/oven-sh/bun/releases/download/bun-v{Pins.Version}/{asset}.zip",
                        archive
                    )

                    let probes = ResizeArray<Command>()

                    let shell =
                        ScriptedShell(fun command ->
                            probes.Add command

                            match command.Spec.Program with
                            | Exec(file, [ "--revision" ]) when Path.GetFileName file = exe ->
                                Some(ScriptedShell.ok (Pins.Revision + "\n"))
                            | _ -> None)

                    let installed =
                        install (create fetch (Some shell.Shell)) (Path.Combine(outside, "runtimes")) lockfile target

                    Expect.equal installed.Id "bun" "id"
                    Expect.equal (string installed.Version) Pins.Version "version"

                    let bun = installed.Bin |> List.find (fun (name, _) -> name = "bun") |> snd
                    Expect.equal (File.ReadAllText bun) "a stand-in for bun" "the archive's top directory is stripped"
                    Expect.equal probes.Count 1 "probed once"

                    let owned = OwnedDirs.under (Path.Combine(outside, "owned", "s1"))
                    Expect.equal (runIn localFs (RuntimeSpec.prepare RuntimeBun.runtime owned)) (Ok()) "prepared"

                    let session =
                        {
                            Target = target
                            Owned = owned
                            Workspace = None
                            ProjectTrusted = false
                            HostEnvironment = RuntimeConformance.hostile
                        }

                    match RuntimeSpec.launch RuntimeBun.runtime installed session [ "--version" ] with
                    | Error issues -> failtest (Issue.render issues)
                    | Ok proc ->
                        let bunfig = Path.Combine(owned.Config, "bunfig.toml")
                        let tsconfig = Path.Combine(owned.Config, "tsconfig.json")

                        Expect.equal
                            proc.Program
                            (Exec(
                                bun,
                                [
                                    "--config=" + bunfig
                                    "--no-env-file"
                                    "--no-install"
                                    "--no-orphans"
                                    "--no-macros"
                                    "--tsconfig-override=" + tsconfig
                                    "--version"
                                ]
                            ))
                            "argv"

                        Expect.equal (File.ReadAllText bunfig) RuntimeBun.bunfig.Content "the bunfig is written"
                        Expect.equal (File.ReadAllText tsconfig) RuntimeBun.tsconfig.Content "the tsconfig is written"
                        Expect.equal proc.Cwd (Some owned.Cwd) "owned cwd"
                        Expect.isTrue (Directory.Exists owned.Cwd) "which exists"
                finally
                    removeDirectory outside
            }
        ]
