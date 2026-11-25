/// The built-ins load the way a host loads them: their manifests match their activations, their
/// contributions are privileged, and a project extension cannot contribute a store, a method or a
/// source (spec §11.5, §16.5).
module Fx.Auth.Tests.LoadTests

open System
open Expecto
open Fx
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Extensions
open Fx.Rpc
open Fx.Sdk

type private HostEnv =
    {
        Clock: Clock
        Shell: Shell
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IShell with
        member env.Shell = env.Shell

    interface IScopeWith<HostEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

let private bootstrap (candidates: Candidate list) : Host =
    let clock = FakeClock.Create()

    let trust: TrustContext =
        {
            ProjectTrusted = true
            RequirePins = false
            Placement = "host"
            Sdk = Versions.Sdk
            Protocols = [ Versions.Protocol ]
        }

    let processes: ProcessRuntime.ProcessSettings =
        {
            Limits = ConnectionLimits.defaults
            Info =
                {
                    Name = Product.binaryName
                    Version = Product.Version
                }
            HostEnvironment = []
            Workspace = None
            IsWindows = OperatingSystem.IsWindows()
            StopTimeout = TimeSpan.FromSeconds 10.0
            StderrBytes = 4096
        }

    let settings =
        { ExtensionHost.settings trust (BasicExtensionEnv.forManifest clock.Clock) processes with
            Catalog = Catalog.create AuthMethods.contributionTypes
        }

    let env =
        {
            Clock = clock.Clock
            Shell = (ScriptedShell(fun _ -> None)).Shell
            Scope = Scope.root
        }

    (Eff.run env (ExtensionHost.bootstrap settings candidates))
        .WaitAsync(TimeSpan.FromSeconds 30.0)
        .GetAwaiter()
        .GetResult()

let private builtins =
    [
        Fx.Ext.AuthStandard.AuthStandard.extension
        Fx.Ext.StoreKeyring.StoreKeyring.extension
        Fx.Ext.StoreFile.StoreFile.extension
        Fx.Ext.StoreEphemeral.StoreEphemeral.extension
    ]

[<Tests>]
let tests =
    testList
        "loading the built-ins"
        [
            test "the built-ins load and contribute what they declare" {
                let host =
                    bootstrap (
                        builtins
                        |> List.map (fun e -> Candidate.create Source.Builtin (Package.Native e))
                    )

                Expect.equal
                    (ExtensionHost.contributions CredentialStores.contributionType host
                     |> List.map (fun c -> c.Extension, c.Id))
                    [
                        Product.extensionId "store.keyring", "keyring"
                        Product.extensionId "store.file", "file"
                        Product.extensionId "store.ephemeral", "ephemeral"
                    ]
                    "stores"

                Expect.equal
                    (ExtensionHost.contributions AuthMethods.contributionType host |> List.map _.Id)
                    [ "api_key"; "env" ]
                    "methods"

                Expect.equal
                    (ExtensionHost.contributions SecretSources.contributionType host |> List.map _.Id)
                    [ "store"; "env" ]
                    "sources"

                for contributionType in AuthMethods.contributionTypes do
                    Expect.isTrue contributionType.Privileged contributionType.Name
            }

            test "a project extension cannot keep credentials" {
                let project =
                    { Fx.Ext.StoreEphemeral.StoreEphemeral.extension with
                        Manifest =
                            { Fx.Ext.StoreEphemeral.StoreEphemeral.manifest with
                                Id = "acme.store"
                            }
                    }

                let host =
                    bootstrap
                        [
                            Candidate.create Source.Builtin (Package.Native Fx.Ext.StoreFile.StoreFile.extension)
                            Candidate.create Source.Project (Package.Native project)
                        ]

                Expect.equal
                    (ExtensionHost.contributions CredentialStores.contributionType host
                     |> List.map _.Id)
                    [ "file" ]
                    "only the built-in's"

                match host.Report |> List.find (fun r -> r.Id = "acme.store") |> _.Outcome with
                | Outcome.Rejected rejection -> Expect.stringContains (string rejection) "trust.privileged" "privileged"
                | other -> failtest (string other)
            }
        ]
