/// `fx.store.keyring` (spec §11.4, §16.4): credentials in the OS keyring, through the host's `IKeyring`
/// (the macOS Keychain, the Windows Credential Manager, the Secret Service on Linux; T007,
/// docs/spikes/keychains.md). Each credential is one item: the service is the product's name, the
/// account the credential's id, so people find them under one name in Keychain Access, Seahorse and
/// the Credential Manager. A native extension built only on the public SDK. See README.md and
/// docs/modules/auth.md.
module Fx.Ext.StoreKeyring.StoreKeyring

open System.Threading.Tasks
open Fx
open Fx.Effects
open Fx.Sdk

/// What `credentials.store` selects.
[<Literal>]
let Kind = "keyring"

/// The keyring service every credential is under: the product's name.
let service: KeyringService =
    match KeyringService.tryCreate Product.Name with
    | Ok service -> service
    | Error e -> invalidOp ("The product's name is not a keyring service: " + e)

/// The keyring item of a credential. Credential ids are always valid accounts.
let keyOf (id: CredentialId) : KeyringKey =
    match KeyringKey.create service id.Name with
    | Ok key -> key
    | Error e -> invalidOp ("A credential id is not a keyring account: " + e)

/// Why the keyring cannot be used, and what to do instead.
let reason (unavailable: KeyringUnavailable) : string =
    match unavailable with
    | KeyringUnavailable.NoSessionBus ->
        "no D-Bus session bus (a container, CI or an SSH session): set credentials.store to file or ephemeral"
    | KeyringUnavailable.BusUnreachable why ->
        "the D-Bus session bus is unreachable ("
        + why
        + "): set credentials.store to file"
    | KeyringUnavailable.NoSecretService ->
        "no Secret Service on the session bus: install gnome-keyring or KeePassXC, or set credentials.store to file"
    | KeyringUnavailable.NoDefaultCollection ->
        "the Secret Service has no default keyring: create one (e.g. in Seahorse), or set credentials.store to file"
    | KeyringUnavailable.NoKeychain ->
        "no default keychain: create one in Keychain Access, or set credentials.store to file"
    | KeyringUnavailable.NoLogonSession ->
        "this Windows logon session has no credentials (an SSH key logon, a service): set credentials.store to file"
    | KeyringUnavailable.NotSupported os -> "no keyring on " + os + ": set credentials.store to file"

/// How to unlock a locked keyring.
let unlockHint =
    "unlock the keyring (log in to the desktop, or `security unlock-keychain` on macOS), or set credentials.store to file"

let private storeError (error: KeyringError) : StoreError =
    match error with
    | KeyringError.Unavailable unavailable -> StoreError.Unavailable(reason unavailable)
    | KeyringError.Locked -> StoreError.Locked unlockHint
    | KeyringError.Dismissed -> StoreError.Failed "the keyring's prompt was dismissed"
    | KeyringError.TooLarge limit -> StoreError.TooLarge limit
    | KeyringError.Failed(code, message) -> StoreError.Failed(code + ": " + message)

/// What the keyring's probe means for the store.
let status (status: KeyringStatus) : StoreStatus =
    match status with
    | KeyringStatus.Ready -> StoreStatus.Ready
    | KeyringStatus.Locked -> StoreStatus.Locked unlockHint
    | KeyringStatus.Unavailable unavailable -> StoreStatus.Unavailable(reason unavailable)
    | KeyringStatus.Failed(code, message) ->
        StoreStatus.Unavailable(
            "the keyring failed ("
            + code
            + ": "
            + message
            + "): set credentials.store to file"
        )

/// What an item's value means: a stored credential's JSON, or else a plain secret (one stored with
/// `secret-tool`, Keychain Access or `cmdkey` under the product's service).
let credentialOf (value: SecretText) : StoredCredential =
    match StoredCredential.tryOfText (value.Reveal()) with
    | Some credential -> credential
    | None -> StoredCredential.ofSecret value

/// A store over a keyring.
let create (keyring: Keyring) : CredentialStore =
    {
        Kind = Kind
        Protection = keyring.Backend
        Probe =
            fun cancellation ->
                task {
                    let! probed = keyring.Probe cancellation
                    return status probed
                }
        Read =
            fun id cancellation ->
                task {
                    match! keyring.Read (keyOf id) cancellation with
                    | Ok value -> return Ok(value |> Option.map credentialOf)
                    | Error e -> return Error(storeError e)
                }
        Write =
            fun id credential cancellation ->
                task {
                    let text = SecretText(StoredCredential.toText credential)

                    match! keyring.Write (keyOf id) text cancellation with
                    | Ok() -> return Ok()
                    | Error e -> return Error(storeError e)
                }
        Delete =
            fun id cancellation ->
                task {
                    match! keyring.Delete (keyOf id) cancellation with
                    | Ok existed -> return Ok existed
                    | Error e -> return Error(storeError e)
                }
        List =
            fun cancellation ->
                task {
                    match! keyring.List service cancellation with
                    | Ok accounts ->
                        return
                            accounts
                            |> List.choose (fun account -> CredentialId.tryCreate account |> Result.toOption)
                            |> Ok
                    | Error e -> return Error(storeError e)
                }
    }

let def: CredentialStoreDef =
    {
        Kind = Kind
        Description =
            "Keeps credentials in the OS keyring: the macOS Keychain, the Windows Credential Manager or the Secret Service."
        Open = fun env -> Task.FromResult(create env.Keyring)
    }

/// The extension's id: `fx.store.keyring`.
let id = Product.extensionId "store.keyring"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "A credential store in the OS keyring."
        Contributions = [ Declare.contribution CredentialStores.contributionType Kind ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate = Eff.ret (Activation.create [ Contribution.create CredentialStores.contributionType Kind def ] [])
    }
