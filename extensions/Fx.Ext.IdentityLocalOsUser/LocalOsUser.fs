/// `fx.identity.local-os-user` (spec §3, §11.2, §16.4): the identity provider of embedded mode and of
/// local clients. A client is the person the host runs for when it runs as the same OS account as
/// the host; the host establishes how (`Credential.Connection`), and this provider decides. A native
/// extension built only on the public SDK. See README.md and docs/modules/identity.md.
module Fx.Ext.IdentityLocalOsUser.LocalOsUser

open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Identity
open Fx.Sdk

/// The provider's id: its principals are `user:local-os-user:<machine>/<account>`.
[<Literal>]
let ProviderId = "local-os-user"

/// How a client showed that it runs as the host's OS account.
[<RequireQualifiedAccess>]
type Proof =
    /// It is in the host's process (embedded mode).
    | InProcess
    /// It speaks over the standard streams of a process it started, or that started it: one
    /// account starts processes as itself.
    | Stdio
    /// The operating system says the peer process runs as the host's account (a Unix socket's or a
    /// named pipe's peer credentials).
    | PeerCredentials
    /// It presented the local listener's token, which only the host's account can read.
    | OwnerSecret

    override this.ToString() =
        match this with
        | InProcess -> "in-process"
        | Stdio -> "stdio"
        | PeerCredentials -> "peer-credentials"
        | OwnerSecret -> "owner-secret"

/// Whether a connection shows that the client runs as the host's account, and how. Peer credentials
/// that name another account refuse the client whatever else it presented.
let proof (connection: Connection) (host: HostIdentity) : Result<Proof, AuthError> =
    match connection.PeerAccount with
    | Some peer when peer <> host.Account.Id ->
        Error(AuthError.Rejected "the client runs as another account of this computer")
    | Some _ -> Ok Proof.PeerCredentials
    | None ->
        match connection.Kind with
        | ConnectionKind.InProcess -> Ok Proof.InProcess
        | ConnectionKind.Stdio -> Ok Proof.Stdio
        | ConnectionKind.UnixSocket
        | ConnectionKind.NamedPipe
        | ConnectionKind.Loopback when connection.OwnerSecret -> Ok Proof.OwnerSecret
        | kind ->
            Error(
                AuthError.NoProof(
                    "nothing shows that a "
                    + string kind
                    + " client runs as this computer's account: use a local socket or present the listener's token"
                )
            )

/// The principal of the host's account on this machine, signed in with a proof.
let principal (host: HostIdentity) (proof: Proof) : Result<Principal, AuthError> =
    let display =
        match host.Account.Name with
        | Some name -> name
        | None -> "account " + host.Account.Id

    let claims =
        Map.ofList
            [
                "machine", [ host.Machine ]
                "account", [ host.Account.Id ]
                yield! host.Account.Name |> Option.map (fun name -> "name", [ name ]) |> Option.toList
                "proof", [ string proof ]
            ]

    if not (HostIdentity.isMachine host.Machine) then
        Error(AuthError.Unavailable "the host's machine id is not set up")
    else
        Principal.create PrincipalKind.User ProviderId (host.Machine + "/" + host.Account.Id) display claims
        |> Result.mapError (fun reason ->
            AuthError.Unavailable("the host's account cannot name a principal: " + reason))

/// Says who the client is: the host's own account, if the connection shows it. The provider takes
/// no credential: a client that presents one is refused rather than having it ignored.
let verify (credential: Credential) : Result<Principal, AuthError> =
    match credential.Presented with
    | Json.Null
    | Json.Object [] ->
        proof credential.Connection credential.Host
        |> Result.bind (principal credential.Host)
    | _ -> Error(AuthError.Rejected(ProviderId + " takes no credential"))

/// The host's own account owns the host.
let roles (principal: Principal) : Role list =
    if principal.Provider = ProviderId && principal.Kind = PrincipalKind.User then
        [ Role.Owner ]
    else
        []

let provider: IdentityProvider =
    {
        Id = ProviderId
        Display = "This computer's account"
        Login = None
        Verify = fun credential -> Eff.ret (verify credential)
        Refresh = None
        Roles = fun principal -> Eff.ret (roles principal)
    }

/// The extension's id: `fx.identity.local-os-user`.
let id = Product.extensionId "identity.local-os-user"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "Signs in clients that run as the host's own OS account (embedded mode and local sockets)."
        Contributions = [ Declare.contribution IdentityProviders.contributionType ProviderId ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate =
            Eff.ret (
                Activation.create [ Contribution.create IdentityProviders.contributionType ProviderId provider ] []
            )
    }
