# fx.identity.local-os-user

The bundled identity provider of embedded mode and of local clients (spec §3, §11.2, §16.4): a
client is the person the host runs for when it runs as the **same OS account as the host**. A
native extension built on the public SDK only (`src/Fx.Sdk`); the module spec is
[docs/modules/identity.md](../../docs/modules/identity.md).

| File | What |
|---|---|
| `LocalOsUser.fs` | The proof rules, the principal, the roles, the `IdentityProvider`, the manifest and the extension |

## How a client proves it

The provider takes no credential: a client sends `{"kind": "provider", "provider": "local-os-user"}`
as `identity` in `initialize`. What counts is what the host established about the connection
(`Credential.Connection`), never anything the client says:

| Connection | Proof | Established by |
|---|---|---|
| In process (embedded mode) | `in-process` | the host itself |
| Standard streams | `stdio` | the host started the client, or the client started the host: one account starts processes as itself |
| Unix socket | `peer-credentials` | `SO_PEERCRED` (Linux), `LOCAL_PEERCRED` (macOS), the client process's token (Windows) |
| Named pipe (Windows) | `peer-credentials` | `GetNamedPipeClientProcessId`, then the client process's token |
| Loopback TCP, or a socket that does not say | `owner-secret` | the client presented the listener's token, which lives in an owner-only file (`0600`, or an ACL with only the user) |

Peer credentials that name another account refuse the client whatever else it presented
(`rejected`); a connection with no proof (a remote one, loopback without the token) is `noProof`.
A browser client given the listener's token by the user counts as the user.

## The principal

`user:local-os-user:<machine>/<account>`: the host's machine id (a stable id of the installation,
so the same uid on two machines is two principals) and the account (the decimal uid on Unix, the
SID on Windows). Display: the account's name. Claims: `machine`, `account`, `name` (when known),
`proof`. Roles: `owner` (the host's own account owns the host).

## Testing

`tests/Fx.Ext.IdentityLocalOsUser.Tests` runs the identity-provider conformance suite
(`tests/Fx.Identity.Host.Tests/Conformance`), the rules above, loading through the extension host
(a project may not contribute an identity provider), and sign-in end to end: in process, over a
Unix socket (every OS), a named pipe (Windows) and loopback TCP with the token file, then a
reconnect with the session token as the listener's bearer and its revocation.
