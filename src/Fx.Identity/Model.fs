namespace Fx.Identity

open Fx.Contracts

/// Who acts (spec §11.2): a person, an unattended device, or a program. Only people answer, grant and
/// revoke permissions (spec §13.2); a person who signs in through a device (a paired phone) is a
/// `User`, the device being how they proved it.
[<RequireQualifiedAccess>]
type PrincipalKind =
    | User
    | Device
    | Service

    override this.ToString() =
        match this with
        | User -> "user"
        | Device -> "device"
        | Service -> "service"

/// A principal's stable id (spec §11.2), namespaced by kind and identity provider:
/// `<kind>:<provider>:<subject>`, e.g. `user:local-os-user:7Q2M9X4KD1/1000`. Events record it for
/// ever (`Attribution.Principal`, a grant's `By`), so it never changes for one principal, and it says
/// what kind of principal acted without a lookup.
[<Struct>]
type PrincipalId =
    | PrincipalId of string

    member this.Value =
        let (PrincipalId id) = this
        id

    override this.ToString() = this.Value

module PrincipalId =
    /// The id of the host's own inputs (`Attribution.SystemPrincipal`): never a person.
    [<Literal>]
    let System = "system"

    /// The longest provider id.
    [<Literal>]
    let MaxProvider = 64

    /// The longest subject.
    [<Literal>]
    let MaxSubject = 256

    let kindName (kind: PrincipalKind) : string = string kind

    let kindOfName (name: string) : PrincipalKind option =
        match name with
        | "user" -> Some PrincipalKind.User
        | "device" -> Some PrincipalKind.Device
        | "service" -> Some PrincipalKind.Service
        | _ -> None

    let private isLowerAlnum (c: char) =
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')

    /// Whether the text is an identity provider's id: lowercase letters and digits in segments
    /// joined by `.` or `-` (`local-os-user`, `com.example.sso`), at most `MaxProvider` characters.
    let isProvider (text: string) : bool =
        let mutable ok = text.Length > 0 && text.Length <= MaxProvider
        let mutable previous = '.'

        for c in text do
            if isLowerAlnum c then
                previous <- c
            elif (c = '.' || c = '-') && isLowerAlnum previous then
                previous <- c
            else
                ok <- false

        ok && isLowerAlnum previous

    /// Whether the text is a subject: printable ASCII without spaces (`!` to `~`), at most
    /// `MaxSubject` characters. Providers encode anything else (an email, a URL) to fit.
    let isSubject (text: string) : bool =
        text.Length > 0
        && text.Length <= MaxSubject
        && text |> Seq.forall (fun c -> c >= '!' && c <= '~')

    /// The id of a provider's subject.
    let create (kind: PrincipalKind) (provider: string) (subject: string) : Result<PrincipalId, string> =
        if not (isProvider provider) then
            Error("the provider id " + provider + " is not lowercase dotted or dashed segments")
        elif not (isSubject subject) then
            Error(
                "a subject has 1 to "
                + string MaxSubject
                + " printable ASCII characters without spaces"
            )
        else
            Ok(PrincipalId(kindName kind + ":" + provider + ":" + subject))

    /// The kind, provider and subject of an id in the canonical form.
    let tryParts (text: string) : (PrincipalKind * string * string) option =
        match text.IndexOf ':' with
        | -1 -> None
        | first ->
            match text.IndexOf(':', first + 1) with
            | -1 -> None
            | second ->
                let provider = text.Substring(first + 1, second - first - 1)
                let subject = text.Substring(second + 1)

                match kindOfName (text.Substring(0, first)) with
                | Some kind when isProvider provider && isSubject subject -> Some(kind, provider, subject)
                | _ -> None

    /// An id in the canonical form, `<kind>:<provider>:<subject>`.
    let parse (text: string) : Result<PrincipalId, string> =
        match tryParts text with
        | Some _ -> Ok(PrincipalId text)
        | None -> Error("not a principal id (<kind>:<provider>:<subject>): " + text)

    /// The kind an id names: what comes before its first colon. Ids recorded before principals existed
    /// (tests wrote `user:alice`) name a kind without a provider; `system` and other ids name none.
    let kindOf (id: string) : PrincipalKind option =
        match id.IndexOf ':' with
        | -1 -> None
        | colon when colon < id.Length - 1 -> kindOfName (id.Substring(0, colon))
        | _ -> None

    /// Whether the id names a person: the only principals that answer, grant and revoke (spec §13.2).
    let isPerson (id: string) : bool = kindOf id = Some PrincipalKind.User

/// Who is connecting (spec §11.2): a principal an identity provider vouches for.
type Principal =
    {
        Id: PrincipalId
        /// How clients show the principal, e.g. a user name.
        Display: string
        /// The same kind as the id's.
        Kind: PrincipalKind
        /// The identity provider's id: the same as the id's.
        Provider: string
        /// What the provider knows, by name: an account, a machine, how the client proved it.
        Claims: Map<string, string list>
    }

    override this.ToString() =
        this.Display + " (" + this.Id.Value + ")"

module Principal =
    [<Literal>]
    let MaxDisplay = 200

    [<Literal>]
    let MaxClaims = 32

    [<Literal>]
    let MaxClaimValues = 16

    [<Literal>]
    let MaxClaimText = 512

    let private claimsProblem (claims: Map<string, string list>) : string option =
        if claims.Count > MaxClaims then
            Some("at most " + string MaxClaims + " claims")
        else
            claims
            |> Map.toList
            |> List.tryPick (fun (name, values) ->
                if name.Length = 0 || name.Length > MaxClaimText then
                    Some("a claim's name has 1 to " + string MaxClaimText + " characters")
                elif values.Length > MaxClaimValues then
                    Some("the claim " + name + " has more than " + string MaxClaimValues + " values")
                elif values |> List.exists (fun v -> v.Length > MaxClaimText) then
                    Some(
                        "a value of the claim "
                        + name
                        + " is longer than "
                        + string MaxClaimText
                        + " characters"
                    )
                else
                    None)

    /// Problems with a principal, if any: its id is canonical and names its kind and provider, its
    /// display text and claims are within bounds.
    let validate (principal: Principal) : Result<unit, string> =
        match PrincipalId.tryParts principal.Id.Value with
        | None -> Error("not a principal id (<kind>:<provider>:<subject>): " + principal.Id.Value)
        | Some(kind, provider, _) when kind <> principal.Kind || provider <> principal.Provider ->
            Error(
                "the id "
                + principal.Id.Value
                + " names another kind or provider than the principal's"
            )
        | Some _ when System.String.IsNullOrWhiteSpace principal.Display -> Error "the display text cannot be empty"
        | Some _ when principal.Display.Length > MaxDisplay ->
            Error("the display text has at most " + string MaxDisplay + " characters")
        | Some _ ->
            match claimsProblem principal.Claims with
            | Some problem -> Error problem
            | None -> Ok()

    /// A principal of a provider's subject.
    let create
        (kind: PrincipalKind)
        (provider: string)
        (subject: string)
        (display: string)
        (claims: Map<string, string list>)
        : Result<Principal, string> =
        PrincipalId.create kind provider subject
        |> Result.bind (fun id ->
            let principal =
                {
                    Id = id
                    Display = display
                    Kind = kind
                    Provider = provider
                    Claims = claims
                }

            validate principal |> Result.map (fun () -> principal))

/// What a principal may do on this host (spec §11.2 `Roles`). Ordered: each role may do what the ones
/// before it may. The host server checks a method's least role (T129); multi-client takeover and
/// sharing refine them (T507).
[<RequireQualifiedAccess>]
type Role =
    /// Reads sessions, runs and logs; changes nothing.
    | Viewer
    /// Also drives sessions and runs: prompts, steers, interrupts, answers questions.
    | Collaborator
    /// Also answers, grants and revokes permissions and manages the host: its config, extensions,
    /// other clients' tokens.
    | Owner

    override this.ToString() =
        match this with
        | Viewer -> "viewer"
        | Collaborator -> "collaborator"
        | Owner -> "owner"

module Role =
    let rank (role: Role) : int =
        match role with
        | Role.Viewer -> 0
        | Role.Collaborator -> 1
        | Role.Owner -> 2

    /// The most a list of roles allows, if any.
    let highest (roles: Role list) : Role option =
        match roles with
        | [] -> None
        | _ -> Some(roles |> List.maxBy rank)

    /// Whether the roles allow what needs `least`.
    let allows (least: Role) (roles: Role list) : bool =
        roles |> List.exists (fun role -> rank role >= rank least)

/// An account of the host machine's operating system.
type OsAccount =
    {
        /// The decimal user id on Unix, the security identifier (`S-1-5-21-…`) on Windows.
        Id: string
        /// The login name, when known.
        Name: string option
    }

    override this.ToString() =
        match this.Name with
        | Some name -> name + " (" + this.Id + ")"
        | None -> this.Id

/// The host, as identity providers see it.
type HostIdentity =
    {
        /// A stable id of this host installation, so that the same account on two machines is two
        /// principals (`[A-Za-z0-9._-]`, at most 64 characters).
        Machine: string
        /// The OS account the host runs as.
        Account: OsAccount
    }

    override this.ToString() =
        this.Machine + " as " + string this.Account

module HostIdentity =
    /// Whether the text is a machine id.
    let isMachine (text: string) : bool =
        text.Length > 0
        && text.Length <= 64
        && text
           |> Seq.forall (fun c ->
               (c >= 'a' && c <= 'z')
               || (c >= 'A' && c <= 'Z')
               || (c >= '0' && c <= '9')
               || c = '.'
               || c = '_'
               || c = '-')

/// How a client is connected to the host.
[<RequireQualifiedAccess>]
type ConnectionKind =
    /// In the host's own process (embedded mode, spec §3).
    | InProcess
    /// A Unix domain socket of the local listener.
    | UnixSocket
    /// A Windows named pipe of the local listener.
    | NamedPipe
    /// Loopback TCP of the local listener.
    | Loopback
    /// The standard streams of a process the host started, or that started the host.
    | Stdio
    /// Anything else: a relay, another machine.
    | Remote

    override this.ToString() =
        match this with
        | InProcess -> "in-process"
        | UnixSocket -> "unix-socket"
        | NamedPipe -> "named-pipe"
        | Loopback -> "loopback"
        | Stdio -> "stdio"
        | Remote -> "remote"

/// What the host established about a connection before the client said anything: never taken from
/// the client.
type Connection =
    {
        Kind: ConnectionKind
        /// The OS account (`OsAccount.Id`) of the process at the other end, as the operating system
        /// reports it: `SO_PEERCRED` (Linux) or `LOCAL_PEERCRED` (macOS) on Unix sockets, the client
        /// process's token on Windows named pipes and Unix sockets.
        PeerAccount: string option
        /// Whether the client presented a secret that only the host's OS account can read: the
        /// listener's token, kept in an owner-only file.
        OwnerSecret: bool
    }

    override this.ToString() =
        let peer =
            match this.PeerAccount with
            | Some account -> ", peer " + account
            | None -> ""

        let secret = if this.OwnerSecret then ", owner secret" else ""
        string this.Kind + peer + secret

module Connection =
    /// A connection with nothing established but its kind.
    let ofKind (kind: ConnectionKind) : Connection =
        {
            Kind = kind
            PeerAccount = None
            OwnerSecret = false
        }

    /// Embedded mode: the client is the host's own process.
    let inProcess: Connection = ofKind ConnectionKind.InProcess

/// What an identity provider verifies (spec §11.2 `Credential`): what the client presented and what
/// the host knows about the connection and itself. Only the host builds it.
type Credential =
    {
        /// What the client sent for the provider in `initialize` (`identity.credential`), `Json.Null`
        /// if nothing. Never trusted: the provider checks it.
        Presented: Json
        Connection: Connection
        Host: HostIdentity
    }

    // The presented credential may be a secret.
    override this.ToString() =
        "credential over " + string this.Connection

/// Why a provider did not vouch for a client (spec §11.2 `AuthError`): an expected failure.
[<RequireQualifiedAccess>]
type AuthError =
    /// Nothing about the connection or the credential lets the provider say who the client is, e.g.
    /// a remote connection for `local-os-user`.
    | NoProof of reason: string
    /// The credential is wrong: another account, a bad signature, an unknown key.
    | Rejected of reason: string
    /// The credential has expired: refresh it or log in again.
    | Expired
    /// The provider could not check it now, e.g. its server is unreachable.
    | Unavailable of reason: string

    override this.ToString() =
        match this with
        | NoProof reason -> "no proof: " + reason
        | Rejected reason -> "rejected: " + reason
        | Expired -> "expired"
        | Unavailable reason -> "unavailable: " + reason

/// How a client signs in, in `initialize` (`identity`).
[<RequireQualifiedAccess>]
type SignIn =
    /// Through an identity provider, with what it asks for (`Json.Null` if nothing).
    | Provider of provider: string * credential: Json
    /// With a session token the host issued before (a reconnect).
    | Token of token: string

    // Never shows the token or the credential.
    override this.ToString() =
        match this with
        | Provider(provider, _) -> "sign in with " + provider
        | Token _ -> "sign in with a session token"

/// Why the host refused a client's identity in `initialize`.
[<RequireQualifiedAccess>]
type IdentityFailure =
    /// The client did not say who it is, and the connection carries no session token.
    | Missing
    /// The `identity` member does not decode.
    | Invalid of Issue list
    /// No identity provider of that id is enabled on the host.
    | UnknownProvider of provider: string
    /// The provider did not vouch for the client.
    | Refused of provider: string * error: AuthError
    /// The provider vouched with a principal that breaks the rules (a defect in the provider).
    | InvalidPrincipal of provider: string * reason: string
    /// The principal has no role on this host.
    | NoRole of principal: string
    /// The session token is unknown, revoked or expired.
    | TokenRejected

    override this.ToString() =
        match this with
        | Missing -> "no identity"
        | Invalid issues -> "invalid identity:\n" + Issue.renderAtMost 10 issues
        | UnknownProvider provider -> "no identity provider " + provider
        | Refused(provider, error) -> provider + " refused: " + string error
        | InvalidPrincipal(provider, reason) -> provider + " gave an invalid principal: " + reason
        | NoRole principal -> principal + " has no role on this host"
        | TokenRejected -> "the session token is unknown, revoked or expired"

/// A client's session with the host, as the `initialize` result tells it (`session`): who the
/// client is, what it may do, and the token that resumes it.
type SessionInfo =
    {
        /// The session token's public id, e.g. to revoke it.
        Id: string
        /// The session token: only when the host issued it in this `initialize`. A secret.
        Token: string option
        Principal: Principal
        Roles: Role list
        /// The client, as events attribute its inputs (`Attribution.Via`), the same after reconnects.
        Client: string
        /// When the token expires: microseconds since the Unix epoch, UTC.
        Expires: int64
    }

    // Never shows the token.
    override this.ToString() =
        let roles = this.Roles |> List.map string |> String.concat ", "

        string this.Principal
        + " via "
        + this.Client
        + " ["
        + roles
        + "] until "
        + string this.Expires
