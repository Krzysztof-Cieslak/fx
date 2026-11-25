/// `fx.env.local`: the provider conformance suite in process (embedded mode) and piped (the `fx`
/// binary's envd as a child), and what is particular to local environments.
module Fx.Environments.Tests.LocalTests

open System
open System.IO
open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Environments
open Fx.Environments.Tests.Conformance
open Fx.Environments.Tests.Local
open Fx.Ext.EnvLocal
open Fx.Machines.Host
open Fx.Sdk
open Fx.Store

[<Tests>]
let conformance =
    testList
        "local provider"
        [
            testSequenced (ProviderConformance.tests (subject TransportKind.InProcess))
            testSequenced (ProviderConformance.tests (subject TransportKind.Piped))
        ]

let private run (dataDir: string) (effect: Eff<IProviderEnv, 'T>) : 'T =
    wait (ProviderEnv.within (providerEnv dataDir) effect (ProviderConformance.HostEnv.live ()))

let private request (id: string) (path: string) : EnvRequest =
    {
        Id = id
        Spec = EnvSpec.create "dev" EnvLocal.Kind |> EnvSpec.withWorkspace path
    }

[<Tests>]
let local =
    testList
        "fx.env.local"
        [
            test "its package is valid" { Expect.isEmpty (EnvLocal.check ()) "issues" }

            test "leases a home under the data directory, and writes the lease there" {
                let dataDir = Scratch.directory ()
                let workspace = workspace ()

                match
                    run dataDir (EnvLocal.create (request "env-1" (workspace + string Path.DirectorySeparatorChar)))
                with
                | Ok lease ->
                    Expect.equal lease.Workspace workspace "the workspace, normalized"
                    Expect.equal lease.Home (Path.Combine(dataDir, "environments", "env-1")) "the home"
                    Expect.isTrue (File.Exists(Path.Combine(lease.Home, EnvLocal.LeaseFile))) "the lease file"
                    Expect.equal (run dataDir EnvLocal.list) (Ok [ lease ]) "listed from its file"
                    Expect.equal (run dataDir (EnvLocal.destroy lease)) (Ok()) "destroyed"
                    Expect.isFalse (Directory.Exists lease.Home) "the home is gone"
                    Expect.isTrue (File.Exists(Path.Combine(workspace, "hello.txt"))) "the workspace stays"
                | Error e -> failtest (string e)
            }

            test "expands ~ to the user's home" {
                let info = hostInfo "/data"

                Expect.equal
                    (EnvLocal.workspacePath
                        info
                        { EnvSpec.defaultWorkspace with
                            Path = Some "~"
                        })
                    (Ok(Path.TrimEndingDirectorySeparator info.Home))
                    "~"

                Expect.equal
                    (EnvLocal.workspacePath
                        info
                        { EnvSpec.defaultWorkspace with
                            Path = Some "~/src/app"
                        })
                    (Ok(Path.Combine(info.Home, "src", "app")))
                    "~/src/app"

                match
                    EnvLocal.workspacePath
                        info
                        { EnvSpec.defaultWorkspace with
                            Path = Some "src/app"
                        }
                with
                | Error(ProviderError.Invalid [ issue ]) ->
                    Expect.equal (JsonPath.render issue.Path) "$.workspace.path" "where"
                | other -> failtest $"{other}"

                match EnvLocal.workspacePath info EnvSpec.defaultWorkspace with
                | Error(ProviderError.Invalid _) -> ()
                | other -> failtest $"a path is required: {other}"
            }

            test "refuses workspaces that are missing or not directories" {
                let dataDir = Scratch.directory ()
                let file = Path.Combine(workspace (), "hello.txt")

                for path in [ Path.Combine(dataDir, "missing"); file ] do
                    match run dataDir (EnvLocal.create (request "env-1" path)) with
                    | Error(ProviderError.Invalid _) -> ()
                    | other -> failtest $"{path}: {other}"

                Expect.equal (run dataDir EnvLocal.list) (Ok []) "nothing was leased"
            }

            test "never deletes a home it did not create" {
                let dataDir = Scratch.directory ()
                let elsewhere = workspace ()

                let lease =
                    {
                        Id = "env-1"
                        Provider = EnvLocal.Kind
                        Workspace = elsewhere
                        Home = elsewhere
                        Data = Map.empty
                    }

                match run dataDir (EnvLocal.destroy lease) with
                | Error(ProviderError.Invalid _) -> ()
                | other -> failtest $"{other}"

                Expect.isTrue (File.Exists(Path.Combine(elsewhere, "hello.txt"))) "untouched"

                match run dataDir (EnvLocal.create (request "../escape" elsewhere)) with
                | Error(ProviderError.Invalid _) -> ()
                | other -> failtest $"ids name directories: {other}"
            }

            test "takes no settings" {
                Expect.equal (Codec.decode EnvLocal.config (Json.Object [])) (Ok(Json.Object [])) "{}"
                Expect.isError (Codec.decode EnvLocal.config (Json.Object [ "x", Json.Bool true ])) "anything else"
            }

            test "the Windows shell comes from the shell.windows variant" {
                // Off Windows envd ignores it; on Windows the default (pwsh) is passed with --shell.
                let dataDir = Scratch.directory ()
                let log = EnvLog.typed (InMemoryEventLog<Payload>().Log)
                let host = start (settings dataDir) log

                try
                    let envs = EnvsHost.envs host
                    let record = wait (envs.Create (spec TransportKind.Piped) CancellationToken.None)

                    match record with
                    | Ok record ->
                        let handle = wait (envs.Open record.Id CancellationToken.None)

                        match handle with
                        | Ok handle when windows ->
                            Expect.equal
                                (IO.Path.GetFileNameWithoutExtension handle.Client.Platform.Shell.Program)
                                "pwsh"
                                "PowerShell 7"
                        | Ok handle -> Expect.equal handle.Client.Platform.Shell.Program "/bin/sh" "sh"
                        | Error e -> failtest (string e)

                        wait (envs.Destroy record.Id CancellationToken.None) |> ignore
                    | Error e -> failtest (string e)
                finally
                    wait (EnvsHost.stop host)
            }
        ]
