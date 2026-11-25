# Spike: OS keychains under Native AOT (T007)

Spec: §11.3–11.5 (credential stores `keyring`, `file`, `ephemeral`; `secret:` references), §18
(credentials live on the host; secrets are always referenced), §20 (OS keychains). Feeds T121 (auth
methods, the credential stores and `secret:` references).

## Verdict

**The three OS keyrings read, write, delete and list secrets under Native AOT, from F#, with no
third-party package and no native helper library:**

- **macOS:** the Keychain through Security.framework (`SecItemAdd`, `SecItemCopyMatching`,
  `SecItemUpdate`, `SecItemDelete`), with queries built as `CFDictionary`s through blittable
  CoreFoundation imports;
- **Windows:** the Credential Manager (`CredWriteW`, `CredReadW`, `CredDeleteW`, `CredEnumerateW`),
  and DPAPI (`CryptProtectData`, `CryptUnprotectData`, current user) for the `file` store's encryption
  at rest;
- **Linux:** the Secret Service over **our own minimal D-Bus client** (pure wire format, `EXTERNAL`
  authentication, method calls and signals over .NET's Unix domain sockets), with both session
  algorithms, `plain` and `dh-ietf1024-sha256-aes128-cbc-pkcs7`.

**Headless machines are told apart, cheaply and without side effects**: no session bus, a bus without a
Secret Service, a Secret Service without a default keyring, a locked keyring, a Windows logon session
without credentials. Every backend fails with `Locked` instead of waiting for a prompt nobody can
answer. What T121 should do in each case is [below](#headless-behaviour-recommendations-for-t121).

Evidence, all in CI:

- the keyring conformance suite (`tests/Fx.Keyring.Tests`) passes against the in-memory fake
  everywhere, the Credential Manager on Windows (the `os` job's tests), a temporary keychain on macOS arm64
  and x64, and gnome-keyring on Linux glibc and musl (keyring steps), where it runs once per session
  algorithm and adds interoperability with libsecret's `secret-tool` and a lock/unlock test;
- the `keyring-*` scenarios of the JIT-vs-native diff: the D-Bus wire format, the session crypto and
  the headless decisions (pure) and the probe on all eight targets; a full round trip through the real
  keyring on `win-x64` and `win-arm64` (the JIT-vs-native diff) and on `linux-x64`, `linux-musl-x64`,
  `osx-arm64` and `osx-x64` (keyring steps); DPAPI on Windows. Each fact is checked against the value it must have,
  so a keyring that misbehaves identically on the JIT and natively still fails;
- the keyring steps run the native probe in each headless state it can set up and checks the answer
  (`KEYRING_EXPECT`): no session bus, no Secret Service, no default collection, ready, locked (Linux),
  ready and locked (macOS).

Changes to the spec: §20's OS integration row names how the keyrings are reached (`Fx.Keyring`, our
D-Bus client). No design change.

## What was built

| Path | What |
|---|---|
| `src/Fx.Effects/Keyring.fs` (`src/Fx.Keyring/Keyring.fs` until T121) | The capability: `KeyringService`, `KeyringKey`, `Prompts`, `KeyringUnavailable` (`Unavailable` until T121), `KeyringStatus`, `KeyringError`, the `Keyring` record, `IKeyring` and the `Keyring.*` effects; `SecretText` (never prints its value) is in `Secrets.fs` |
| `src/Fx.Effects/Fakes/InMemoryKeyring.fs` | The deterministic fake; it can play a locked or unavailable keyring |
| `src/Fx.Keyring/Headless.fs` | Pure decisions from `HostFacts`: the kind of session (SSH, display, WSL, container, CI), the session bus, whether prompts can reach anyone |
| `src/Fx.Keyring/LocalHostFacts.fs` | Reads the facts once (environment variables, marker files) |
| `src/Fx.Keyring/DBus/` | The D-Bus client: `Message.fs` (values, messages), `Wire.fs` (marshalling, pure), `Address.fs` (bus addresses, pure), `Connection.fs` (socket, authentication, calls, signals) |
| `src/Fx.Keyring/SecretService/` | `SessionCrypto.fs` (the DH session, pure) and `SecretServiceKeyring.fs` (the Linux interpreter) |
| `src/Fx.Keyring/MacKeychain.fs`, `Native/Apple.fs` | The macOS interpreter and its CoreFoundation/Security imports |
| `src/Fx.Keyring/WindowsCredentials.fs`, `Dpapi.fs`, `Native/Windows.fs` | The Windows interpreter, DPAPI and their imports |
| `src/Fx.Keyring/LocalKeyring.fs` | `LocalKeyring.settings facts` and `LocalKeyring.create os settings`: this machine's keyring |
| `tests/Fx.Keyring.Tests` | `KeyringConformance` (a function from a `Keyring` to a `Test`), the fake, the wire format against bytes worked out from the specification, the crypto against known answers from Python and openssl, the headless decisions, and the live keyrings |
| `tests/aot/Fx.Aot.Scenarios/Scenarios/Keyring.fs` | The `keyring-*` scenarios (below) |
| `tests/aot/Fx.Aot.Diff` | `--only <prefix>` runs only some scenarios (the keyring steps' diff) |
| `.github/workflows/ci.yml`, `.github/scripts/keyring-*.sh` | The keyring steps (the `keyring` job until CI went to one job per OS) |

```fsharp
let facts = LocalHostFacts.read ()                       // once, in the composition root
let keyring = LocalKeyring.create facts.Os (LocalKeyring.settings facts)
match! keyring.Probe ct with
| KeyringStatus.Ready -> ...                             // use the keyring store
| KeyringStatus.Locked -> ...                            // prompts allowed? the first call asks the user
| KeyringStatus.Unavailable reason -> ...               // e.g. NoSessionBus: fall back (below)
| KeyringStatus.Failed(code, message) -> ...
let key = KeyringKey.tryCreate Product.Name "anthropic" |> Result.get
let! written = keyring.Write key (SecretText token) ct  // Result<unit, KeyringError>
let! value = keyring.Read key ct                        // Result<SecretText option, KeyringError>
let! existed = keyring.Delete key ct                    // Result<bool, KeyringError>
let! accounts = keyring.List key.Service ct             // Result<string list, KeyringError>, ordinal order
```

Keys are a service (1–128 characters of `A-Z a-z 0-9 . _ - /`, never `:`) and an account (1–256
characters, no control characters). Values are text, stored as UTF-8; `MaxValueBytes` is 2560 on
Windows and 65536 (our cap) elsewhere. Expected failures are `KeyringError`: `Unavailable` (with the
reason), `Locked`, `Dismissed`, `TooLarge`, `Failed` (the OS's code and message, for display).

| Scenario | Proves |
|---|---|
| `keyring-dbus-wire` | `Hello` laid out as the specification says; every D-Bus type round-trips; a big-endian reply decodes; a truncated message is an error; bus addresses |
| `keyring-session-crypto` | The DH key pair, the derived key and the AES ciphertext equal the known answers; a degenerate peer key is refused |
| `keyring-headless` | The decisions for a container, SSH with X forwarding, a desktop, the systemd user bus, WSL, macOS over SSH and Windows CI |
| `keyring-detect` | The probe of this machine (no D-Bus activation); checks `KEYRING_EXPECT` when set |
| `keyring-roundtrip` | Write, read, replace, Unicode, list, the size limit, delete; on Linux once per session algorithm. Windows always, elsewhere with `KEYRING_TESTS=1` |
| `keyring-dpapi` | DPAPI round trip; other entropy and tampering are rejected (Windows) |

## Linux: the Secret Service over our own D-Bus client

The Secret Service (`org.freedesktop.secrets` on the session bus) is implemented by gnome-keyring,
KWallet (5.97 and later), KeePassXC and others. Three ways to reach it from a Native AOT binary:

| Option | Verdict |
|---|---|
| **libsecret through P/Invoke** | Rejected. It is a native dependency the binary cannot carry (GLib, LGPL, not on servers, containers or Alpine), so it would be `dlopen`ed lazily and could be missing. Its synchronous API runs a GLib main loop inside the call, its convenience functions are variadic (only the `*v` variants with a `GHashTable` could be called), and errors come back as `GError`s to free. |
| **Tmds.DBus.Protocol** (NuGet, 0.95) | Viable: managed, marked AOT-compatible, used by Avalonia. Rejected because what we need of D-Bus is small and frozen, while the package is pre-1.0 with breaking changes between minor versions and brings `System.IO.Pipelines`; the same reasoning kept third-party code out of `Fx.Pty`. Worth revisiting if fx ever needs much more of D-Bus (portals, notifications). |
| **Our own minimal D-Bus client** | **Chosen.** About 1,100 lines (half of them the pure wire format and its doc comments), Native AOT-safe by construction (no reflection, no native code but `geteuid`), testable without a bus, and the same on glibc and musl. |

**The client** (`src/Fx.Keyring/DBus`):

- **Wire format:** every D-Bus type (`ybnqiuxtdsogh`, arrays, structs, dict entries, variants),
  alignment relative to the message start, header fields, both byte orders read (the bus forwards
  messages in their sender's order), little-endian written. Signatures are validated (length 255, depth
  64), strings must be UTF-8 without NUL, padding must be zero. Messages are capped at 16 MiB (the
  specification allows 128 MiB). Malformed input is an `Error`, never an exception out of `decode`.
- **Addresses:** `DBUS_SESSION_BUS_ADDRESS` with `unix:path=` and `unix:abstract=` (Linux abstract
  sockets: a leading NUL in `UnixDomainSocketEndPoint`), `%`-escapes, several addresses separated by
  `;`; other transports are skipped. Without the variable, `$XDG_RUNTIME_DIR/bus` if it exists (what
  libdbus and sd-bus do). X11 autolaunch is never used.
- **Authentication:** a NUL byte, then `AUTH EXTERNAL <hex of the decimal euid>`; the bus checks it
  against the socket's peer credentials. Then `BEGIN` and `Hello`. No Unix FD passing.
- **One connection per operation:** connect, authenticate, open a session, do the operation, close.
  Keyring operations are rare (startup, sign-in), and nothing has to be reconnected after the service
  restarts. A read took 4 ms with `plain` and 15 ms with the DH session on a 2-core VM (two 1024-bit
  modular exponentiations per side), while another build ran.
- **Calls and signals:** a call waits for the reply with its serial; signals that arrive meanwhile are
  kept for `WaitForSignal`. The bus delivers signals only for match rules, so a prompt adds its rule
  (`AddMatch`) *before* calling `Prompt`.

**The Secret Service mapping:**

- Items live in the **default collection** (`ReadAlias("default")`), as generic secrets with the
  attributes `service` and `account` (and `xdg:schema = org.freedesktop.Secret.Generic`), so
  `secret-tool lookup service <s> account <a>` finds ours and we find what `secret-tool store` wrote
  (tested). Labels are `<service>: <account>`; the content type is `text/plain`.
- Write is `CreateItem(properties, secret, replace = true)`; read is `SearchItems` on the collection,
  then `GetSecret` on the first item in path order; delete deletes every match; list reads each match's
  `Attributes` property.
- **Sessions:** `OpenSession` offers the algorithms in order, `dh-ietf1024-sha256-aes128-cbc-pkcs7`
  first and `plain` when the service answers `NotSupported`. The DH session: 128 random bytes as the
  private exponent in the RFC 2409 1024-bit group (generator 2), the peer's key checked to be in
  2..p-2, the shared secret left-padded to 128 bytes, HKDF-SHA256 with no salt and no info to a 16-byte
  key, AES-128-CBC with PKCS#7 and a fresh IV per secret. This matches libsecret, gnome-keyring and
  Python's SecretStorage; the known-answer test was computed with Python and `openssl enc`. It hides
  secrets from bus monitors only: any process of the same user can read this process's memory anyway.
- **Prompts:** with `Prompts.Never`, an operation that would need one (unlocking, creating the default
  collection, a service that confirms access) dismisses the prompt object and fails with `Locked`. With
  `Prompts.Allow` it calls `Prompt("")` and waits for `Completed`; a dismissed prompt is `Dismissed`.
  gnome-keyring without a display does exactly that: its `gcr-prompter` cannot open a display, and the
  prompt completes as dismissed.
- **The probe** connects, reads the default alias and the collection's `Locked` property. It never
  creates anything and, with `AutoStart = false`, sends every call with `NO_AUTO_START`, so it never
  starts a service either. Operations use activation (`AutoStart = true`), as libsecret does.

## macOS: the Keychain

- **Generic passwords** (`kSecClassGenericPassword`) in the user's default keychain, with
  `kSecAttrService` and `kSecAttrAccount`, the label `<service>: <account>` and the value as UTF-8
  `kSecValueData`. Write is `SecItemAdd`, then `SecItemUpdate` on `errSecDuplicateItem`.
- **The file-based keychain, not the data protection keychain:** the latter needs a signed binary with a
  `keychain-access-groups` entitlement and a provisioning profile, which a command-line tool does not
  have (`errSecMissingEntitlement`).
- **Interop:** frameworks are loaded by absolute path
  (`/System/Library/Frameworks/Security.framework/Security`), so nothing else can satisfy the imports.
  Constants (`kSecClass`, `kCFBooleanTrue`, the dictionary callbacks) are exported variables, read with
  `NativeLibrary.GetExport`. Every CF object created for a call is collected and released at its end
  (`Owned`); the framework's constants are never released. `Boolean` is an unsigned char, so it is
  declared as `byte`: only the low byte of the return register is defined. `CFStringGetCString` is used
  rather than `CFStringGetBytes`, which takes a `CFRange` by value.
- **No UI without prompts:** with `Prompts.Never`, every query carries `kSecUseAuthenticationUI =
  kSecUseAuthenticationUIFail` (deprecated since macOS 11 for an `LAContext`, which needs
  Objective-C; still exported, and read as optional), and the default keychain's lock state is checked
  first (`SecKeychainCopyDefault`, `SecKeychainGetStatus`, deprecated but still the only way to read
  it), so a locked keychain fails with `Locked` before anything could show the unlock dialog.
  `errSecInteractionNotAllowed` is `Locked`, `errSecUserCanceled` and `errSecAuthFailed` are
  `Dismissed`, and a missing keychain is `Unavailable NoKeychain`.
- **Access lists:** an item trusts the binary that created it. Another binary, including the next build
  of an ad-hoc-signed or unsigned `fx`, gets the "fx wants to use your confidential information"
  dialog, or `errSecInteractionNotAllowed` without UI. See [risks](#risks).
- **Calls are synchronous:** with `Prompts.Allow` a call blocks its thread while macOS asks the user.

## Windows: the Credential Manager and DPAPI

- **Generic credentials** (`CRED_TYPE_GENERIC`) named `<service>:<account>` (services never contain
  `:`), with the account as the user name, persisted with `CRED_PERSIST_LOCAL_MACHINE`: for this user on
  this machine, across logon sessions, never roaming with a profile (`CRED_PERSIST_ENTERPRISE` would).
  Windows encrypts them with the user's DPAPI keys. `CREDENTIALW` is written into zeroed native memory
  at its 64-bit offsets, as `Fx.Pty` does; secret buffers are zeroed before they are freed.
- **2560 bytes per credential** (`CRED_MAX_CREDENTIAL_BLOB_SIZE`): larger values fail with
  `TooLarge` before Windows sees them. API keys fit; some OAuth token sets (JWT access tokens with
  refresh and ID tokens) do not.
- **Target names are case-insensitive:** keys that differ only in case are the same credential on
  Windows (and `CredEnumerateW`'s filter `<service>:*` matches case-insensitively).
- **Logon sessions without credentials** (an OpenSSH logon with a key, a service, a scheduled task
  that runs whether the user is logged on or not) have no credential set: `CredReadW` fails with
  `ERROR_NO_SUCH_LOGON_SESSION`, which is `Unavailable NoLogonSession`. The probe reads a credential
  that never exists to tell this from a working store. DPAPI fails the same way there. CI cannot set
  such a session up, so this mapping is untested.
- **DPAPI** (`Dpapi.protect`/`unprotect`): current-user scope, an application-chosen entropy (a second
  key part: another program of the same user must know it too), never UI
  (`CRYPTPROTECT_UI_FORBIDDEN`). Data protected with other entropy, or changed, is `Rejected`. An
  administrator resetting the user's password makes it unreadable (the user changing it does not).

## Native AOT notes

- Every P/Invoke is blittable (`nativeint`, `int`, `uint32`, `byte`), with
  `[<assembly: DisableRuntimeMarshalling>]`, so a signature that needs marshalling fails on the JIT too.
  No variadic function is called.
- `SetLastError = true` is marshalling: with runtime marshalling disabled, every call to such an import
  throws ("Setting SetLastError to 'true' is not supported when runtime marshalling is disabled"; found
  by the Windows tests). The Windows imports leave it out and read `Marshal.GetLastSystemError()` right
  after a call that failed, which is what `LibraryImport`'s generated stubs do.
- The D-Bus client uses only .NET's sockets, `BinaryPrimitives` and `UTF8Encoding`. The session crypto
  uses `BigInteger.ModPow`, `HKDF` and `Aes.EncryptCbc`, which on Linux call OpenSSL: like TLS, the DH
  session needs `libssl` on the machine (every desktop with a Secret Service has it).
- The AOT publish of `Fx.Aot.Scenarios`, which references `Fx.Keyring`, has no trim or AOT warning.

## Headless behaviour: recommendations for T121

What the probe reports, and what the host should do. "Prompts" is `Headless.prompts`: never in CI,
never over SSH, and on Linux only with a display.

| Machine | Probe | Recommended store |
|---|---|---|
| macOS console session, Windows desktop, Linux desktop with an unlocked keyring | `ready` | `keyring` |
| A desktop whose keyring is locked, prompts possible | `locked` | `keyring`: the first use asks to unlock |
| Locked keyring over SSH (macOS login keychain, a gnome-keyring not unlocked by PAM) | `locked`, prompts never | `keyring` if the user unlocks it (`security unlock-keychain`); otherwise refuse with the reason, or use `file` if the user chose it |
| Containers, CI, most Linux servers, WSL without systemd | `unavailable: no-session-bus` | `file` (or `ephemeral` with secrets from the environment in CI) |
| A session bus without a Secret Service (minimal desktops, servers with systemd, WSL with systemd) | `unavailable: no-secret-service` | `file` |
| gnome-keyring started by D-Bus activation, no login keyring | `unavailable: no-default-collection` | `file`; with a display, creating the default keyring prompts for a password |
| Windows OpenSSH key logon, services, batch logons | `unavailable: no-logon-session` | `file` without DPAPI (it fails there too): permissions only, with a warning |

Recommendations:

1. **Probe once at host startup** and pick the store from the table: `credentials.store = auto |
   keyring | file | ephemeral`, a variant slot (privileged, so policy can force it). `auto` is the
   table; an explicit `keyring` that is unavailable is an error naming the reason and the fix (e.g.
   "no Secret Service on the session bus: install gnome-keyring or KeePassXC, or set
   credentials.store = file"), never a silent fallback.
2. **Never downgrade silently:** when `auto` picks `file`, say so once (`fx auth status`, and a notice
   on the first write), with the reason from the probe.
3. **The `file` store:** one file under the per-user directory (`~/.fx`, from `Product`), created with
   mode 0600 in a 0700 directory and written atomically. On Windows encrypt it with DPAPI (entropy from
   `Product`, so it is fx's); on Linux and macOS its protection is the file's permissions, as with `gh`,
   `aws` and `gcloud`. A passphrase (PBKDF2 or scrypt, AES-GCM) is possible but needs a prompt at every
   start, so it is opt-in.
4. **CI:** prefer the `env` secret source with the `ephemeral` store: nothing written to disk.
5. **Prompts** follow the attached client: with a local, interactive client allow them; for remote
   clients or a detached host use `Prompts.Never` and report `Locked` to the client, which can tell the
   user how to unlock.
6. **Large credentials on Windows:** store values over 2560 bytes in the `file` store (DPAPI), or keep
   in the Credential Manager only a key that encrypts them there.

## CI

- **Windows:** nothing to prepare: the runners' sessions have a credential set. The `os` job's tests
  run the live tests (the Credential Manager and DPAPI) on `windows-2025`; the JIT-vs-native diff runs
  `keyring-roundtrip` and `keyring-dpapi` on `win-x64` (`windows-2025`) and, in the full tier,
  `win-arm64` (`windows-11-arm`).
- **The keyring steps** (`.github/scripts/keyring-*.sh`), on `linux-x64` and `osx-arm64` (the `os`
  jobs, when keyring or credential code changes and in the full tier) and on `linux-musl-x64` and
  `osx-x64` (the `targets` job, full tier), run once the scenarios are published natively:
  - **Linux:** installs `dbus`, `gnome-keyring` and `libsecret-tools` (Ubuntu) or `dbus` and
    `gnome-keyring` (Alpine, which also needs a machine id and a password-database entry for the
    container's user, or `dbus-daemon` refuses to start). The native probe must report each state in
    turn: no session bus (`env -u DBUS_SESSION_BUS_ADDRESS -u XDG_RUNTIME_DIR`), no Secret Service
    (`dbus-run-session` alone), no default collection (`gnome-keyring-daemon --start`), ready
    (`--unlock` with a throwaway password on stdin) and locked (`Service.Lock` through `dbus-send`).
    Then, under one `dbus-run-session` with an unlocked gnome-keyring, the live tests and the diff
    (`--only keyring-`) with `KEYRING_TESTS=1`. The lock test unlocks again through gnome-keyring's
    `UnlockWithMasterPassword` (test-only).
  - **macOS:** creates a temporary keychain with a throwaway password, removes its auto-lock, puts it
    first in the search list and makes it the default; runs the live tests and the diff; the probe must
    say `ready`, then `locked` after `security lock-keychain`; finally restores the login keychain as
    the default and deletes the temporary one. (The runners' own login keychain is unlocked: the probe
    says `ready` before the temporary keychain exists. CI still never writes to it.)
- **musl:** the D-Bus client is managed code, so musl needs nothing special; Alpine's gnome-keyring
  works under `dbus-run-session` once the user is in `/etc/passwd`.

## Risks

- **macOS signing.** Ad-hoc-signed or unsigned builds change identity with every build, so after an
  update the keychain asks the user to allow access to each existing item (and fails with `Locked`
  without UI). Release binaries must be signed with a Developer ID under a stable identifier, so the
  designated requirement survives updates. Until then, developers will see the dialog after rebuilding.
- **Deprecated macOS APIs:** `SecKeychainGetStatus`, `SecKeychainCopyDefault` and
  `kSecUseAuthenticationUI` still work but may go; the code reads the last one as optional.
- **Credential Manager limits:** 2560 bytes per value; case-insensitive names.
- **Untested states:** Windows logon sessions without credentials; KWallet and KeePassXC (KeePassXC
  can confirm every access, which `Prompts.Never` turns into `Locked`; its database must be open).
- **The D-Bus client is minimal:** no TCP, no authentication but `EXTERNAL`, no Unix FD passing, no
  big-endian writes. That is all a session bus on Linux needs.
- **gnome-keyring's private unlock interface** is used by the tests only, never by the keyring.

## Recommendations for T121

T121 followed them ([docs/modules/auth.md](../modules/auth.md)): the capability moved to `Fx.Effects`
(the SDK exposes it; the interpreters stay here) and `fx.store.keyring` is built on it; `secret:<name>`
is the item (`Product.Name`, `<name>` in lowercase); the store is chosen once at startup from
`credentials.store` as the table above says, never falling back silently; `KeyringConformance`
generalised to `CredentialStoreConformance`, run on every store; the `file` store is
`LocalPrivateFiles` (`0600` in `0700`, DPAPI with an owner-only ACL on Windows).

- Build `fx.store.keyring` on this `Keyring` record. If the store becomes an extension, move the record,
  `IKeyring`, the error types and `KeyringConformance` into the SDK's capabilities (with `Fx.Effects`)
  and keep the interpreters host-side; extensions never get the interpreter itself.
- Map `secret:<name>` to the key (`Product.Name`, `<name>`) — one service for fx, the reference name as
  the account — so users find fx's secrets under one name in Keychain Access, Seahorse and the
  Credential Manager.
- Journal references and outcomes, never values (`SecretText` never prints its value).
- Run `KeyringConformance` against every store (`file` and `ephemeral` too, generalised to a
  credential-store suite), on all three OSes.
- Probe once at startup and choose the store as above; expose the probe's result in `fx auth status`.
