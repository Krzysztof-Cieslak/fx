/// `fx.identity.local-os-user`: the identity-provider conformance suite, its rules, its manifest, and
/// how a host loads it (a privileged contribution: only from the binary, host config or policy).
module Fx.Ext.IdentityLocalOsUser.Tests.ProviderTests

open System
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Extensions
open Fx.Rpc
open Fx.Sdk
open Fx.Identity
open Fx.Identity.Host.Tests.Conformance
open Fx.Ext.IdentityLocalOsUser

let host: HostIdentity =
    {
        Machine = "7Q2M9X4KD1"
        Account = { Id = "1000"; Name = Some "alice" }
    }

let credential (connection: Connection) : Credential =
    {
        Presented = Json.Null
        Connection = connection
        Host = host
    }

let private over (kind: ConnectionKind) (peer: string option) (secret: bool) : Credential =
    credential
        {
            Kind = kind
            PeerAccount = peer
            OwnerSecret = secret
        }

let alice = PrincipalId "user:local-os-user:7Q2M9X4KD1/1000"

let private verify credential = LocalOsUser.verify credential

let private claim (name: string) (credential: Credential) : string list =
    match verify credential with
    | Ok principal -> principal.Claims |> Map.tryFind name |> Option.defaultValue []
    | Error e -> failtest (string e)

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

/// Loads candidates the way a host does, with the identity's contribution types in the catalog.
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
            Catalog = Catalog.create IdentityProviders.contributionTypes
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

[<Tests>]
let conformance =
    IdentityProviderConformance.tests
        LocalOsUser.ProviderId
        LocalOsUser.provider
        {
            Accepted =
                [
                    "in process", credential Connection.inProcess, alice
                    "stdio", over ConnectionKind.Stdio None false, alice
                    "a Unix socket's peer", over ConnectionKind.UnixSocket (Some "1000") false, alice
                    "a named pipe's client", over ConnectionKind.NamedPipe (Some "1000") true, alice
                    "the listener's token over loopback", over ConnectionKind.Loopback None true, alice
                    "the listener's token over a Unix socket without peer credentials",
                    over ConnectionKind.UnixSocket None true,
                    alice
                ]
            Refused =
                [
                    "another account's peer", over ConnectionKind.UnixSocket (Some "1001") false
                    "another account's peer with the listener's token",
                    over ConnectionKind.NamedPipe (Some "S-1-5-18") true
                    "loopback without the listener's token", over ConnectionKind.Loopback None false
                    "a Unix socket without peer credentials or the token", over ConnectionKind.UnixSocket None false
                    "a remote connection", over ConnectionKind.Remote None true
                    "a credential it does not take",
                    { credential Connection.inProcess with
                        Presented = Json.Object [ "password", Json.String "x" ]
                    }
                ]
        }

[<Tests>]
let tests =
    testList
        "local-os-user"
        [
            test "the principal is the host's account on this machine" {
                match verify (credential Connection.inProcess) with
                | Error e -> failtest (string e)
                | Ok principal ->
                    Expect.equal principal.Id alice "<machine>/<account>"
                    Expect.equal principal.Kind PrincipalKind.User "a person"
                    Expect.equal principal.Display "alice" "the account's name"
                    Expect.equal principal.Claims["machine"] [ "7Q2M9X4KD1" ] "machine"
                    Expect.equal principal.Claims["account"] [ "1000" ] "account"
                    Expect.equal principal.Claims["name"] [ "alice" ] "name"
                    Expect.equal (LocalOsUser.roles principal) [ Role.Owner ] "owns the host"
            }

            test "the proof is a claim" {
                Expect.equal (claim "proof" (credential Connection.inProcess)) [ "in-process" ] "in process"
                Expect.equal (claim "proof" (over ConnectionKind.Stdio None false)) [ "stdio" ] "stdio"

                Expect.equal
                    (claim "proof" (over ConnectionKind.UnixSocket (Some "1000") true))
                    [ "peer-credentials" ]
                    "peer credentials win"

                Expect.equal (claim "proof" (over ConnectionKind.Loopback None true)) [ "owner-secret" ] "the token"
            }

            test "a Windows account" {
                let windows =
                    {
                        Machine = "DESKTOP-1"
                        Account =
                            {
                                Id = "S-1-5-21-1004336348-1177238915-682003330-1001"
                                Name = Some "alice"
                            }
                    }

                match
                    verify
                        { over ConnectionKind.NamedPipe (Some windows.Account.Id) false with
                            Host = windows
                        }
                with
                | Ok principal ->
                    Expect.equal
                        principal.Id.Value
                        "user:local-os-user:DESKTOP-1/S-1-5-21-1004336348-1177238915-682003330-1001"
                        "the SID"
                | Error e -> failtest (string e)
            }

            test "an account without a name shows its id" {
                match
                    verify
                        { credential Connection.inProcess with
                            Host =
                                { host with
                                    Account = { Id = "1000"; Name = None }
                                }
                        }
                with
                | Ok principal ->
                    Expect.equal principal.Display "account 1000" "display"
                    Expect.isFalse (principal.Claims.ContainsKey "name") "no name claim"
                | Error e -> failtest (string e)
            }

            test "another account is refused, not unproven" {
                match verify (over ConnectionKind.UnixSocket (Some "0") true) with
                | Error(AuthError.Rejected _) -> ()
                | other -> failtest (string other)

                match verify (over ConnectionKind.Loopback None false) with
                | Error(AuthError.NoProof _) -> ()
                | other -> failtest (string other)
            }

            test "a host without a machine id cannot name principals" {
                match
                    verify
                        { credential Connection.inProcess with
                            Host = { host with Machine = "" }
                        }
                with
                | Error(AuthError.Unavailable _) -> ()
                | other -> failtest (string other)
            }

            test "the manifest" {
                let manifest = LocalOsUser.manifest
                Expect.equal manifest.Id (Product.extensionId "identity.local-os-user") "id from the naming constant"
                Expect.equal manifest.Runtime Runtimes.Native "native"

                Expect.equal
                    manifest.Contributions
                    [
                        Declare.contribution IdentityProviders.contributionType LocalOsUser.ProviderId
                    ]
                    "one identity provider"

                Expect.isTrue IdentityProviders.contributionType.Privileged "privileged"
            }

            test "a host loads it from the binary; a project may not contribute identity" {
                let project: Extension =
                    { LocalOsUser.extension with
                        Manifest =
                            { LocalOsUser.manifest with
                                Id = "acme.identity"
                            }
                    }

                let host =
                    bootstrap
                        [
                            Candidate.create Source.Builtin (Package.Native LocalOsUser.extension)
                            Candidate.create Source.Project (Package.Native project)
                        ]

                let providers = ExtensionHost.contributions IdentityProviders.contributionType host
                Expect.equal (providers |> List.map _.Id) [ LocalOsUser.ProviderId ] "the built-in's"
                Expect.equal (providers |> List.map _.Extension) [ LocalOsUser.id ] "from the extension"

                match host.Report |> List.find (fun r -> r.Id = "acme.identity") |> _.Outcome with
                | Outcome.Rejected rejection -> Expect.stringContains (string rejection) "trust.privileged" "privileged"
                | other -> failtest (string other)
            }
        ]
