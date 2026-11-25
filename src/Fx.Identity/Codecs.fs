namespace Fx.Identity

open Fx.Contracts

/// The JSON of principals and of the identity members of `initialize` (spec §17: `initialize`
/// carries identity and returns a session token), and the TypeScript types generated from them.
/// Principal ids live in event logs for ever: their form never changes.
[<RequireQualifiedAccess>]
module Codecs =
    let private text: Codec<string> = Codec.string |> Codec.nonEmpty

    let principalKind: Codec<PrincipalKind> =
        Codec.enum
            [
                PrincipalKind.User, "user"
                PrincipalKind.Device, "device"
                PrincipalKind.Service, "service"
            ]
        |> Codec.describe "Who acts: a person (user), an unattended device, or a program (service)."
        |> Codec.named "PrincipalKind"

    let principalId: Codec<PrincipalId> =
        Codec.string
        |> Codec.tryMap PrincipalId.parse (fun id -> id.Value)
        |> Codec.describe
            "A principal's stable id: `<kind>:<provider>:<subject>`, e.g. `user:local-os-user:7Q2M9X4KD1/1000`."

    let private claims: Codec<Map<string, string list>> =
        Codec.stringMap (Codec.list Codec.string)
        |> Codec.describe "What the provider knows about the principal, by name."

    let principal: Codec<Principal> =
        Codec.record {
            let! id = Codec.field "id" (fun (p: Principal) -> p.Id) principalId
            and! display = Codec.field "display" (fun p -> p.Display) text
            and! kind = Codec.field "kind" (fun p -> p.Kind) principalKind
            and! provider = Codec.field "provider" (fun p -> p.Provider) text
            and! claims = Codec.defaultField "claims" (fun p -> p.Claims) Map.empty claims

            return
                {
                    Id = id
                    Display = display
                    Kind = kind
                    Provider = provider
                    Claims = claims
                }
        }
        |> Codec.refine Principal.validate
        |> Codec.describe "Who is connecting (spec §11.2): a principal an identity provider vouches for."
        |> Codec.named "Principal"

    let role: Codec<Role> =
        Codec.enum
            [
                Role.Viewer, "viewer"
                Role.Collaborator, "collaborator"
                Role.Owner, "owner"
            ]
        |> Codec.describe
            "What a principal may do on the host: viewer (reads), collaborator (also drives sessions and runs), owner (also permissions and the host)."
        |> Codec.named "Role"

    let osAccount: Codec<OsAccount> =
        Codec.record {
            let! id = Codec.field "id" (fun (a: OsAccount) -> a.Id) text
            and! name = Codec.optionalField "name" (fun a -> a.Name) text
            return { Id = id; Name = name }
        }
        |> Codec.describe "An account of the host machine's operating system: a Unix user id or a Windows SID."
        |> Codec.named "OsAccount"

    let hostIdentity: Codec<HostIdentity> =
        Codec.record {
            let! machine =
                Codec.field
                    "machine"
                    (fun (h: HostIdentity) -> h.Machine)
                    (Codec.string
                     |> Codec.refine (fun m ->
                         if HostIdentity.isMachine m then
                             Ok()
                         else
                             Error "expected 1 to 64 letters, digits, '.', '_' or '-'"))

            and! account = Codec.field "account" (fun h -> h.Account) osAccount
            return { Machine = machine; Account = account }
        }
        |> Codec.describe "The host: a stable id of the installation and the OS account it runs as."
        |> Codec.named "HostIdentity"

    let connectionKind: Codec<ConnectionKind> =
        Codec.enum
            [
                ConnectionKind.InProcess, "in-process"
                ConnectionKind.UnixSocket, "unix-socket"
                ConnectionKind.NamedPipe, "named-pipe"
                ConnectionKind.Loopback, "loopback"
                ConnectionKind.Stdio, "stdio"
                ConnectionKind.Remote, "remote"
            ]
        |> Codec.describe "How a client is connected to the host."
        |> Codec.named "ConnectionKind"

    let connection: Codec<Connection> =
        Codec.record {
            let! kind = Codec.field "kind" (fun (c: Connection) -> c.Kind) connectionKind
            and! peer = Codec.optionalField "peerAccount" (fun c -> c.PeerAccount) text

            and! secret =
                Codec.defaultField "ownerSecret" (fun c -> c.OwnerSecret) false Codec.bool

            return
                {
                    Kind = kind
                    PeerAccount = peer
                    OwnerSecret = secret
                }
        }
        |> Codec.describe "What the host established about a connection before the client said anything."
        |> Codec.named "Connection"

    let authError: Codec<AuthError> =
        Codec.union "kind" {
            let! noProof =
                Codec.case1 "noProof" AuthError.NoProof (Field.required "reason" Codec.string)

            and! rejected =
                Codec.case1 "rejected" AuthError.Rejected (Field.required "reason" Codec.string)

            and! expired = Codec.case0 "expired" AuthError.Expired

            and! unavailable =
                Codec.case1 "unavailable" AuthError.Unavailable (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | AuthError.NoProof reason -> noProof reason
                    | AuthError.Rejected reason -> rejected reason
                    | AuthError.Expired -> expired
                    | AuthError.Unavailable reason -> unavailable reason
        }
        |> Codec.describe "Why an identity provider did not vouch for a client."
        |> Codec.named "AuthError"

    /// The `identity` member of `initialize`.
    let signIn: Codec<SignIn> =
        Codec.union "kind" {
            let! provider =
                Codec.case2
                    "provider"
                    (fun provider credential -> SignIn.Provider(provider, defaultArg credential Json.Null))
                    (Field.required "provider" text)
                    (Field.optional "credential" Codec.json)

            and! token = Codec.case1 "token" SignIn.Token (Field.required "token" text)

            return
                fun value ->
                    match value with
                    | SignIn.Provider(id, Json.Null) -> provider id None
                    | SignIn.Provider(id, credential) -> provider id (Some credential)
                    | SignIn.Token secret -> token secret
        }
        |> Codec.describe
            "How a client signs in: through an identity provider (with what it asks for), or with a session token from before."
        |> Codec.named "SignIn"

    let identityFailure: Codec<IdentityFailure> =
        Codec.union "kind" {
            let! missing = Codec.case0 "missing" IdentityFailure.Missing

            and! invalid =
                Codec.case1
                    "invalid"
                    IdentityFailure.Invalid
                    (Field.required "issues" (Codec.list Fx.Contracts.Codecs.issue))

            and! unknown =
                Codec.case1 "unknownProvider" IdentityFailure.UnknownProvider (Field.required "provider" text)

            and! refused =
                Codec.case2
                    "refused"
                    (fun provider error -> IdentityFailure.Refused(provider, error))
                    (Field.required "provider" text)
                    (Field.required "error" authError)

            and! invalidPrincipal =
                Codec.case2
                    "invalidPrincipal"
                    (fun provider reason -> IdentityFailure.InvalidPrincipal(provider, reason))
                    (Field.required "provider" text)
                    (Field.required "reason" Codec.string)

            and! noRole =
                Codec.case1 "noRole" IdentityFailure.NoRole (Field.required "principal" text)

            and! tokenRejected = Codec.case0 "tokenRejected" IdentityFailure.TokenRejected

            return
                fun value ->
                    match value with
                    | IdentityFailure.Missing -> missing
                    | IdentityFailure.Invalid issues -> invalid issues
                    | IdentityFailure.UnknownProvider provider -> unknown provider
                    | IdentityFailure.Refused(provider, error) -> refused provider error
                    | IdentityFailure.InvalidPrincipal(provider, reason) -> invalidPrincipal provider reason
                    | IdentityFailure.NoRole principal -> noRole principal
                    | IdentityFailure.TokenRejected -> tokenRejected
        }
        |> Codec.describe "Why the host refused a client's identity in initialize."
        |> Codec.named "IdentityFailure"

    /// The `session` member of the `initialize` result.
    let sessionInfo: Codec<SessionInfo> =
        Codec.record {
            let! id = Codec.field "id" (fun (s: SessionInfo) -> s.Id) text
            and! token = Codec.optionalField "token" (fun s -> s.Token) text
            and! principal = Codec.field "principal" (fun s -> s.Principal) principal
            and! roles = Codec.field "roles" (fun s -> s.Roles) (Codec.list role)
            and! client = Codec.field "client" (fun s -> s.Client) text

            and! expires =
                Codec.field
                    "expires"
                    (fun s -> s.Expires)
                    (Codec.int64 |> Codec.describe "Microseconds since the Unix epoch, UTC.")

            return
                {
                    Id = id
                    Token = token
                    Principal = principal
                    Roles = roles
                    Client = client
                    Expires = expires
                }
        }
        |> Codec.describe
            "A client's session with the host: who it is, what it may do, and the token that resumes it (only when just issued)."
        |> Codec.named "SessionInfo"

/// The members `initialize` carries for identity (spec §17), next to the protocol's own.
[<RequireQualifiedAccess>]
module Members =
    /// The client's sign-in, in the params (`Codecs.signIn`).
    [<Literal>]
    let Identity = "identity"

    /// The client's session, in the result (`Codecs.sessionInfo`).
    [<Literal>]
    let Session = "session"
