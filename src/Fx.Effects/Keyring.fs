namespace Fx.Effects

open System
open System.Threading
open System.Threading.Tasks

/// A service: the namespace in a keyring that an application chooses, such as the product's name.
/// 1–128 characters from `A-Z a-z 0-9 . _ - /`, starting with a letter or digit. Never `:`, which
/// separates the service from the account in a Credential Manager target name.
type KeyringService =
    private
    | KeyringService of string

    member this.Name =
        let (KeyringService name) = this
        name

    override this.ToString() = this.Name

module KeyringService =
    let private isServiceChar (c: char) =
        Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-' || c = '/'

    let tryCreate (name: string) : Result<KeyringService, string> =
        if String.IsNullOrEmpty name || name.Length > 128 then
            Error "a keyring service has 1 to 128 characters"
        elif not (Char.IsAsciiLetterOrDigit name[0]) then
            Error "a keyring service starts with a letter or digit"
        elif not (Seq.forall isServiceChar name) then
            Error "a keyring service has only letters, digits and . _ - /"
        else
            Ok(KeyringService name)

/// Where a secret lives in a keyring: a service and an account within it (1–256 characters without
/// control characters). The pair identifies at most one secret.
type KeyringKey =
    private
    | KeyringKey of service: KeyringService * account: string

    member this.Service =
        let (KeyringKey(service, _)) = this
        service

    member this.Account =
        let (KeyringKey(_, account)) = this
        account

    override this.ToString() = $"{this.Service} ({this.Account})"

module KeyringKey =
    /// The key of an account within a service.
    let create (service: KeyringService) (account: string) : Result<KeyringKey, string> =
        if String.IsNullOrEmpty account || account.Length > 256 then
            Error "a keyring account has 1 to 256 characters"
        elif Seq.exists Char.IsControl account then
            Error "a keyring account has no control characters"
        else
            Ok(KeyringKey(service, account))

    /// The key of an account within a service, both given as text.
    let tryCreate (service: string) (account: string) : Result<KeyringKey, string> =
        KeyringService.tryCreate service
        |> Result.bind (fun service -> create service account)

/// Whether a keyring may ask the user (an unlock dialog, an access confirmation).
[<RequireQualifiedAccess>]
type Prompts =
    /// Never: a keyring that needs the user fails with `KeyringError.Locked` instead. For headless
    /// sessions (SSH, CI, containers), where a prompt would never be seen.
    | Never
    /// The keyring may show its prompts and wait for the user; the scope's cancellation bounds the wait.
    | Allow

    override this.ToString() =
        match this with
        | Never -> "never"
        | Allow -> "allow"

/// Why there is no usable keyring on this machine: an expected state of headless machines
/// (SSH sessions, containers, CI, WSL), not a defect.
[<RequireQualifiedAccess>]
type KeyringUnavailable =
    /// Linux: no D-Bus session bus (`DBUS_SESSION_BUS_ADDRESS` unset and no `$XDG_RUNTIME_DIR/bus`).
    | NoSessionBus
    /// Linux: the session bus address is set, but connecting or authenticating failed.
    | BusUnreachable of reason: string
    /// Linux: nothing provides `org.freedesktop.secrets` on the session bus, and it cannot be started.
    | NoSecretService
    /// Linux: the Secret Service has no default collection; creating one needs the user.
    | NoDefaultCollection
    /// macOS: the user has no default keychain.
    | NoKeychain
    /// Windows: the logon session has no credentials (an SSH key logon, a service, a batch logon).
    | NoLogonSession
    /// This operating system has no keyring backend.
    | NotSupported of os: string

    override this.ToString() =
        match this with
        | NoSessionBus -> "no-session-bus"
        | BusUnreachable reason -> $"bus-unreachable: {reason}"
        | NoSecretService -> "no-secret-service"
        | NoDefaultCollection -> "no-default-collection"
        | NoKeychain -> "no-keychain"
        | NoLogonSession -> "no-logon-session"
        | NotSupported os -> $"not-supported: {os}"

/// What a keyring's probe found.
[<RequireQualifiedAccess>]
type KeyringStatus =
    /// Reads and writes work without asking the user.
    | Ready
    /// The keyring exists but is locked: unlocking it needs the user (a prompt).
    | Locked
    | Unavailable of KeyringUnavailable
    /// The probe failed in a way this module does not classify; the code and message are for display.
    | Failed of code: string * message: string

    override this.ToString() =
        match this with
        | Ready -> "ready"
        | Locked -> "locked"
        | Unavailable reason -> $"unavailable: {reason}"
        | Failed(code, message) -> $"failed: {code} {message}"

/// A keyring operation that failed (an expected failure).
[<RequireQualifiedAccess>]
type KeyringError =
    | Unavailable of KeyringUnavailable
    /// The keyring is locked and prompts are off (`Prompts.Never`), or it refused to ask.
    | Locked
    /// The user dismissed the keyring's prompt.
    | Dismissed
    /// The value is larger than the keyring stores (`Keyring.MaxValueBytes`, in UTF-8 bytes).
    | TooLarge of limit: int
    /// The operating system or the service reported an error; the code and message are for display.
    | Failed of code: string * message: string

    override this.ToString() =
        match this with
        | Unavailable reason -> $"keyring unavailable: {reason}"
        | Locked -> "the keyring is locked"
        | Dismissed -> "the keyring prompt was dismissed"
        | TooLarge limit -> $"the value is larger than the keyring's limit of {limit} bytes"
        | Failed(code, message) -> $"keyring error {code}: {message}"

/// The operating system's keyring (spec §11.4 `keyring`): the macOS Keychain, the Windows Credential
/// Manager or the Secret Service on Linux. Secrets are text, stored under a `KeyringKey`.
///
/// Interpreters: `LocalKeyring.create` (this machine), `InMemoryKeyring` (tests). Every operation
/// is data in and data out, so calls can be recorded (references and outcomes only, never values)
/// and replayed. Host-only: environments never get a keyring (spec §18).
type Keyring =
    {
        /// The backend, for display: `keychain`, `credman`, `secret-service`, `memory`, `none`.
        Backend: string
        /// The largest value the keyring stores, in UTF-8 bytes. Credential Manager stores 2560.
        MaxValueBytes: int
        /// Whether the keyring can be used now, without side effects (no secret is read or written).
        Probe: CancellationToken -> Task<KeyringStatus>
        /// The secret under a key; `None` if there is none.
        Read: KeyringKey -> CancellationToken -> Task<Result<SecretText option, KeyringError>>
        /// Stores a secret under a key, replacing any secret there.
        Write: KeyringKey -> SecretText -> CancellationToken -> Task<Result<unit, KeyringError>>
        /// Deletes the secret under a key; `false` if there was none.
        Delete: KeyringKey -> CancellationToken -> Task<Result<bool, KeyringError>>
        /// The accounts with a secret under a service, in ordinal order.
        List: KeyringService -> CancellationToken -> Task<Result<string list, KeyringError>>
    }

    override this.ToString() = $"Keyring({this.Backend})"

/// An environment with the operating system's keyring.
type IKeyring =
    abstract Keyring: Keyring

module Keyring =
    let probe<'R when 'R :> IKeyring and 'R :> IScope> : Eff<'R, KeyringStatus> =
        fun env -> env.Keyring.Probe env.Scope.Cancellation

    let read<'R when 'R :> IKeyring and 'R :> IScope>
        (key: KeyringKey)
        : Eff<'R, Result<SecretText option, KeyringError>> =
        fun env -> env.Keyring.Read key env.Scope.Cancellation

    let write<'R when 'R :> IKeyring and 'R :> IScope>
        (key: KeyringKey)
        (value: SecretText)
        : Eff<'R, Result<unit, KeyringError>> =
        fun env -> env.Keyring.Write key value env.Scope.Cancellation

    let delete<'R when 'R :> IKeyring and 'R :> IScope> (key: KeyringKey) : Eff<'R, Result<bool, KeyringError>> =
        fun env -> env.Keyring.Delete key env.Scope.Cancellation

    let list<'R when 'R :> IKeyring and 'R :> IScope>
        (service: KeyringService)
        : Eff<'R, Result<string list, KeyringError>> =
        fun env -> env.Keyring.List service env.Scope.Cancellation

    /// The UTF-8 size of a value.
    let byteCount (value: SecretText) =
        Text.Encoding.UTF8.GetByteCount(value.Reveal())

    /// A keyring on which everything fails with the same reason (no backend on this machine).
    let unavailable (backend: string) (reason: KeyringUnavailable) : Keyring =
        let error () =
            Task.FromResult(Error(KeyringError.Unavailable reason))

        {
            Backend = backend
            MaxValueBytes = 0
            Probe = fun _ -> Task.FromResult(KeyringStatus.Unavailable reason)
            Read = fun _ _ -> error ()
            Write = fun _ _ _ -> error ()
            Delete = fun _ _ -> error ()
            List = fun _ _ -> error ()
        }
