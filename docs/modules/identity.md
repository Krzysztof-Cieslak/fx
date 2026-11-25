# Module spec: identity (`Fx.Identity`, `Fx.Identity.Host`, `Fx.Sdk/Identity.fs`, `fx.identity.local-os-user`)

Spec: §11.1–11.2 (principal identity, `Principal`, `IdentityProvider`), §3 (principal auth via
identity-provider extensions, then a host-issued session token; embedded mode uses
`fx.identity.local-os-user`; principals and session tokens are core), §11.5 (privileged identity
extensions only from host config or policy), §12–13 (ledger and grant events carry the principal;
only people grant), §17 (`initialize` carries identity and capability negotiation, then a session
token; multi-client attribution), §18 (local listeners require a token and an `Origin` check;
credentials stay on the host). Task: T122.
Code: [`src/Fx.Identity`](../../src/Fx.Identity) (principals and the `initialize` members: pure, shared
with clients), [`src/Fx.Sdk/Identity.fs`](../../src/Fx.Sdk/Identity.fs) (the `IdentityProvider` contract
and its contribution type, public), [`src/Fx.Identity.Host`](../../src/Fx.Identity.Host) (session tokens,
the authenticator, `initialize`), [`src/Fx.Transport.Http`](../../src/Fx.Transport.Http) (`PeerCredentials`,
`TokenFile`, session tokens as the listener's bearer), and
[`extensions/Fx.Ext.IdentityLocalOsUser`](../../extensions/Fx.Ext.IdentityLocalOsUser) (the built-in provider).
Tests: `tests/Fx.Identity.Tests` (ids, principals, codecs, the generated TypeScript, the Fable
architecture test), `tests/Fx.Identity.Host.Tests` (tokens, sign-in, `initialize` over JSON-RPC, and the
identity-provider conformance suite in `Conformance/`), `tests/Fx.Ext.IdentityLocalOsUser.Tests` (the
suite on the built-in, loading it, and sign-in end to end over every local transport),
`tests/Fx.Transport.Http.Tests/PeerCredentialsTests.fs`, and the `identity-*` scenarios of the
JIT-vs-native diff.

Two kinds of identity (spec §11.1): **principal identity** (who is connecting: this module) and
**service credentials** (how the host authenticates to providers: T121). A client signs in once, in
`initialize`, through an identity provider an extension contributes; the host then issues a session
token that resumes the session on reconnects. Every input is attributed to a principal
(`Attribution.Principal`), and only people answer, grant and revoke permissions.

| Piece | Where |
|---|---|
| Principal ids, principals, roles, accounts, connections, credentials, errors, sign-ins, sessions | `Fx.Identity/Model.fs` |
| Their JSON and the `initialize` member names | `Fx.Identity/Codecs.fs` (`Codecs`, `Members`) |
| The provider contract and its contribution type | `Fx.Sdk/Identity.fs` (`IdentityProvider`, `IdentityProviders`, `AuthFlow`) |
| Session tokens and the identity's slot | `Fx.Identity.Host/SessionTokens.fs` (`ClientSession`, `TokenTable`, `SessionTokens`, `IdentitySlots`) |
| Signing in | `Fx.Identity.Host/Authenticator.fs` (`ConnectionFacts`, `Authenticator`) |
| `initialize` | `Fx.Identity.Host/IdentityServer.fs` (`ClientConnection`, `IdentityServer`, `IdentityClient`) |
| Peer accounts, the token file, bearers | `Fx.Transport.Http` (`PeerCredentials`, `TokenFile`, `LocalAccess.Bearer`) |

## Principals

```fsharp
type PrincipalKind = User | Device | Service
type Principal = { Id: PrincipalId; Display: string; Kind: PrincipalKind; Provider: string
                   Claims: Map<string, string list> }
```

**Kind** says who acts: a person (`User`), an unattended device (`Device`), a program (`Service`). A
person who signs in through a device (a paired phone, T505) is a `User`; the device is how they proved
it. The host's own inputs use the reserved id `system`, which is no principal.

**Ids** are `<kind>:<provider>:<subject>`:

| Part | Form | Example |
|---|---|---|
| kind | `user`, `device` or `service` | `user` |
| provider | the identity provider's id: lowercase letters and digits in segments joined by `.` or `-`, at most 64 characters | `local-os-user`, `com.example.sso` |
| subject | the provider's stable id of the principal: printable ASCII without spaces (`!` to `~`), at most 256 characters; may contain `:` (the id splits at its first two colons). Providers encode anything else | `7Q2M9X4KD1/1000` |

- Ids are recorded for ever (`Attribution.Principal` in every event, a grant's `By`), so a principal's id
  never changes and the form never changes. Since the kind is in the id, a log says what kind of
  principal acted without looking anything up, and clients that fold logs (Fable) can tell people apart.
- `PrincipalId.create`, `parse` (canonical form only), `tryParts`. `PrincipalId.kindOf` reads only what
  comes before the first colon, so ids written before principals existed (`user:alice` in the golden
  logs of T104, T125, T123) keep their kind; it is how the grants machine decides (`isPerson`).
- `Attribution.Principal`, `Requester.Principal` and the grants' `By` stay strings: their JSON is
  unchanged and no committed log needs an upcaster. Principal ids are what the host puts there.
- `Principal.validate`: the id is canonical and names the principal's kind and provider; the display
  text is 1–200 characters; at most 32 claims of at most 16 values, names and values at most 512
  characters. The authenticator refuses a provider's principal that fails it.

### Roles

What a principal may do on a host (spec §11.2 `Roles`), ordered: each role may do what the ones before it
may.

| Role | May |
|---|---|
| `viewer` | read sessions, runs, logs, the grants view |
| `collaborator` | also drive sessions and runs: prompt, steer, queue, interrupt, answer questions and decision cards |
| `owner` | also answer, grant and revoke permissions, and manage the host: config, extensions, other clients' tokens |

`Role.allows least roles` checks a method's least role; `IdentityServer.caller auth least connection`
gives a handler the attribution of a connection whose client has it. The host server (T129) assigns
each method group its least role; multi-client sharing and takeover (T507) refine the model. Roles come
from the provider when the token is issued and are bound to it.

## Identity providers

```fsharp
type IdentityProvider =
  { Id: string; Display: string
    Login  : (FormValues -> Eff<IExtensionEnv, Result<AuthFlow, AuthError>>) option
    Verify : Credential -> Eff<IExtensionEnv, Result<Principal, AuthError>>
    Refresh: (Json -> Eff<IExtensionEnv, Result<Json, AuthError>>) option
    Roles  : Principal -> Eff<IExtensionEnv, Role list> }

type Credential = { Presented: Json; Connection: Connection; Host: HostIdentity }
type Connection = { Kind: ConnectionKind; PeerAccount: string option; OwnerSecret: bool }
type HostIdentity = { Machine: string; Account: OsAccount }
```

- Spec §11.2's record with effects for tasks (spec §4.2): every function runs in the provider's
  extension environment with the caller's scope. A provider never mints tokens; the host does.
- **`Credential`** is what the provider verifies: what the client presented for it in `initialize`
  (`Json.Null` if nothing; never trusted), what the host established about the connection before the
  client said anything (never from the client), and the host itself (a stable machine id and the OS
  account it runs as).
- **`AuthError`**: `noProof` (nothing shows who the client is), `rejected` (the credential is wrong),
  `expired`, `unavailable`. Expected failures, never exceptions.
- **`Login`** starts an interactive flow (`AuthFlow`: `Browser`, `DeviceCode`, `Paste`, spec §11.3,
  shared with T121's auth methods) for device pairing and OAuth (T505, T506); **`Refresh`** renews a
  credential the client holds. `local-os-user` has neither.
- **Contribution:** `IdentityProviders.contributionType` (`identity.provider`): code, so native
  extensions only, and **privileged** (spec §11.5, §16.5): admission refuses a project extension that
  contributes one (`trust.privileged`). The contribution id is the provider's id. The host adds
  `IdentityProviders.contributionTypes` to its catalog.
- **Conformance:** `IdentityProviderConformance.tests name provider cases`
  (`tests/Fx.Identity.Host.Tests/Conformance`; link the file into a provider's tests). Given credentials
  it must accept (with the expected principal id) and refuse, it checks: a valid declaration; valid
  principals of its own, the same each time; refusals as errors; no exception whatever a client
  presents; roles for its principals and none for another provider's; and sign-in and resumption through
  the host's authenticator.

## What the host establishes about a connection

The transport, never the client, fills in `Connection`; the composition root builds it per connection.

| Transport | `Kind` | `PeerAccount` | `OwnerSecret` |
|---|---|---|---|
| Embedded mode | `in-process` | — | — |
| A process's standard streams | `stdio` | — | — |
| Unix socket (Linux) | `unix-socket` | `SO_PEERCRED` (`getsockopt`, through `Socket.GetRawSocketOption`) | the listener's token |
| Unix socket (macOS) | `unix-socket` | `LOCAL_PEERCRED` (`xucred`), `LOCAL_PEERPID` | the listener's token |
| Unix socket (Windows) | `unix-socket` | `SIO_AF_UNIX_GETPEERPID`, then the process's token user (SID) | the listener's token |
| Named pipe (Windows) | `named-pipe` | `GetNamedPipeClientProcessId`, then the process's token user (SID) | the listener's token |
| Loopback TCP | `loopback` | — | the listener's token |
| A relay, another machine | `remote` | — | — |

- `PeerCredentials.ofContext` reads the peer of a Kestrel connection (`IConnectionSocketFeature`,
  `IConnectionNamedPipeFeature`); `LocalServer.Request.Peer` carries it. Accounts are the decimal
  effective uid on Unix and the SID on Windows, the same form as `PeerCredentials.currentAccount ()`
  (`geteuid`, `WindowsIdentity`), so they compare as strings.
- **The listener's token** gates every connection (spec §18, [kestrel.md](../spikes/kestrel.md)). The
  host writes it with `TokenFile.write` into its per-user data directory: created `0600` on Unix (never
  widened later), with an ACL of only the user and no inheritance on Windows, replaced whole. A client
  that presents it could read the file, so it runs as the user (or the user handed it the token, e.g. a
  browser client): that is `OwnerSecret`, `LocalAccess.Bearer.Listener`.
- The composition root (T129) maps a request:

```fsharp
let facts (kind: ConnectionKind) (request: LocalServer.Request) : ConnectionFacts =
    { Connection = { Kind = kind
                     PeerAccount = request.Peer |> Option.map _.Account
                     OwnerSecret = (request.Access.Bearer = LocalAccess.Bearer.Listener) }
      Session = match request.Access.Bearer with
                | LocalAccess.Bearer.Session id -> Some id
                | LocalAccess.Bearer.Listener -> None }
```

## Session tokens

- **Issued** by the authenticator once a provider vouched for a client and gave it a role
  (`SessionTokens.issue`): 256 random bits from `IIds` in base64url (43 characters: a bearer header or a
  WebSocket subprotocol carries it), with a public id (`IIds.NewId`) that names it in lists and
  revocations.
- **Kept hashed:** the host keeps the token's SHA-256 (`TokenTable`, keyed by hash, with the hash by id)
  and the `ClientSession` it stands for: the principal, the roles, the client (`Attribution.Via`,
  `<client name>/<id>`, the same after reconnects), when it was issued and when it expires. Tokens are
  never logged or shown: the `ToString` of sessions, sign-ins, credentials and access policies hides
  them. Randomness from `IIds` is journaled like every capability output (spec §15), so a journal of the
  host's identity environment must be protected like the tokens; tokens never go into the event log.
- **Expiry:** `identity.sessionTokenTtl` (minutes, 1–43200, default 720), a privileged slot read when a
  token is issued; the host registers `IdentitySlots.all` with `Slots.all`. Expired tokens are refused
  on the clock (`TokenError.Expired`) and forgotten when the next is issued. Expiry stops new connections
  only: a live connection goes on.
- **Revocation:** `SessionTokens.revoke id` or `revokePrincipal` ("sign out everywhere"). A revoked token
  is refused as a bearer and in `initialize`, `Authenticator.current` (and so `IdentityServer.caller`)
  refuses the connections it opened, and `SessionTokens.Revocation id` is cancelled so the host server can
  end them.
- **Presented** in `initialize` (`{"kind": "token", "token": …}`) or as the local listener's bearer
  (`Authorization: Bearer`, or the `<product>.bearer.<token>` subprotocol): `AccessPolicy.Sessions` is
  `SessionTokens.lookup tokens clock`, `Granted.Bearer` is `Session id`, and the connection's facts carry
  the id, so `initialize` needs no `identity`. HTTP and SSE requests (the offset-read mirror, spec §17)
  carry it the same way. A request offering two different tokens is refused.
- Tokens live as long as the host process: a restarted host has none, and clients sign in again (with
  `local-os-user` that needs no person).

## `initialize`

```json
→ {"protocolVersions": ["1"], "peer": {"name": "tui", …}, "capabilities": {…},
   "identity": {"kind": "provider", "provider": "local-os-user"}}
← {"protocolVersion": "1", "peer": {…}, "capabilities": {…},
   "session": {"id": "01JDB…", "token": "…", "principal": {"id": "user:local-os-user:7Q2M9X4KD1/1000",
               "display": "alice", "kind": "user", "provider": "local-os-user", "claims": {…}},
               "roles": ["owner"], "client": "tui/01JDB…", "expires": 1764115200000000}}
```

- `identity` is a `SignIn`: `{"kind": "provider", "provider", "credential"?}` or `{"kind": "token",
  "token"}`; absent when the listener verified a session token. The result's `session` is a
  `SessionInfo`; `token` only when just issued.
- A refusal is `ApplicationError` (-32000) with an `IdentityFailure` as `data`: `missing`, `invalid`
  (issues under `identity`), `unknownProvider`, `refused` (with the provider's `AuthError`),
  `invalidPrincipal`, `noRole`, `tokenRejected` (unknown, revoked or expired, without saying which). The
  server stays in the handshake, so a client may try again until the handshake times out.
- **Server:** one `ClientConnection` per connection, `IdentityServer.withIdentity auth facts connection`
  on the connection's service, handlers ask `IdentityServer.caller auth least connection` for the
  attribution of what they do (the host server's `GrantsProtocol.Caller` maps a failure to
  `NotAPerson`, T129).
- **Client:** `IdentityClient.members signIn` for `Rpc.connect`, `IdentityClient.session peer`, and
  `IdentityClient.failure error` for a refused connection.
- The TypeScript types are generated from the codecs (`tests/Fx.Identity.Tests/golden/identity.d.ts`,
  type-checked by `bun scripts/declarations.ts`).

## Grants and attribution

- The grants machine refuses answers, grants and revocations from anything but a `user:` principal
  (`GrantLog.isPerson` is `PrincipalId.isPerson`): devices, services, `system` and ids without a kind
  get `grants.not-a-person` ([grants.md](grants.md)). Since `Fx.Grants` reads the kind from the id, it
  references `Fx.Identity` (also Fable-compatible).
- Sessions attribute every input to `ClientSession.attribution` (principal id, client id); the ledger
  (T301) records the same principal ids.

## `fx.identity.local-os-user`

The built-in provider of embedded mode and local clients: a client is the person the host runs for when
the connection shows it runs as the host's OS account. In process and over stdio by construction; over
Unix sockets and named pipes by the peer's account; otherwise by the listener's token. Peer credentials
that name another account refuse the client whatever else it presented. Its principal is
`user:local-os-user:<machine>/<account>` (the same uid on two machines is two principals), with claims
`machine`, `account`, `name` and `proof`, and it owns the host. Details:
[its README](../../extensions/Fx.Ext.IdentityLocalOsUser/README.md).

## Native AOT

- No reflection; P/Invoke with blittable signatures only (`DisableRuntimeMarshalling` on
  `Fx.Transport.Http`): `geteuid`, and on Windows `OpenProcess`, `OpenProcessToken`,
  `GetTokenInformation`, `GetNamedPipeClientProcessId`, `CloseHandle`. Unix peer credentials are socket
  options, not P/Invoke. `PeerCredentials.fs` and `TokenFile.fs` have their own entries in
  analyzers.json.
- Scenarios: `identity-principals`, `identity-tokens`, `identity-initialize`,
  `identity-peer-credentials` (every OS) and `identity-named-pipe` (Windows).

## Left to later tasks

| What | Task |
|---|---|
| Composition: the host's machine id (created once in the per-user data directory), its account (`PeerCredentials.currentAccount`, the user name), `IdentitySlots.all` in the slot registry, `IdentityProviders.contributionTypes` in the catalog, the listener's token file, the facts of each connection, ending connections on `Revocation`, each method group's least role, `session/*` over WebSocket | T129 |
| Methods to list sessions, sign out and revoke other clients' tokens (`identity/*`) | T129, T507 |
| Device pairing (`Login` with a one-time code, per-device keys) | T505 |
| GitHub OAuth and OIDC providers (`Login`, `Refresh`) | T506 |
| `AuthFlow`'s callback and polling details, shared with auth methods | T121 |
| Roles beyond the three, sharing, epoch-fenced takeover | T507 |
| An `ssh-key` provider (spec §11.2's third built-in) | not yet planned |
