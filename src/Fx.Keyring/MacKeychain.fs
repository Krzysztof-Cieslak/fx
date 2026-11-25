namespace Fx.Keyring

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Keyring.Native.Apple

/// The macOS keychain: generic passwords (`kSecClassGenericPassword`) in the user's default keychain
/// (the login keychain), with `kSecAttrService` and `kSecAttrAccount` from the key.
///
/// This is the file-based keychain, not the data protection keychain: that one needs a signed
/// binary with a keychain-access-groups entitlement, which a command-line tool does not have
/// (`errSecMissingEntitlement`). An item's access list trusts the binary that created it; a
/// different binary (an unsigned or ad-hoc-signed build after an update) gets a confirmation
/// dialog, or `errSecInteractionNotAllowed` without one (see docs/spikes/keychains.md).
///
/// Calls are synchronous: with `Prompts.Allow` a call blocks its thread while macOS asks the user.
module MacKeychain =
    /// No size limit is documented; this one keeps items small. Tokens fit with room to spare.
    [<Literal>]
    let MaxValueBytes = 65536

    let private utf8 = UTF8Encoding(false, true)

    let private toError (status: int) : KeyringError =
        if status = ErrSecInteractionNotAllowed then
            KeyringError.Locked
        elif status = ErrSecUserCanceled || status = ErrSecAuthFailed then
            KeyringError.Dismissed
        elif
            status = ErrSecNoDefaultKeychain
            || status = ErrSecNoSuchKeychain
            || status = ErrSecNotAvailable
        then
            KeyringError.Unavailable Unavailable.NoKeychain
        else
            KeyringError.Failed(string status, describe status)

    /// Whether the default keychain exists and is unlocked.
    let private keychainStatus () : KeyringStatus =
        let slot = outSlot ()

        try
            let status = SecKeychainCopyDefault slot

            if status = ErrSecNoDefaultKeychain || status = ErrSecNoSuchKeychain then
                KeyringStatus.Unavailable Unavailable.NoKeychain
            elif status <> ErrSecSuccess then
                KeyringStatus.Failed(string status, describe status)
            else
                let keychain = Marshal.ReadIntPtr slot
                let bits = alloc 4

                try
                    let status = SecKeychainGetStatus(keychain, bits)

                    if status <> ErrSecSuccess then
                        KeyringStatus.Failed(string status, describe status)
                    elif uint32 (Marshal.ReadInt32 bits) &&& UnlockStateStatus = 0u then
                        KeyringStatus.Locked
                    else
                        KeyringStatus.Ready
                finally
                    free bits
                    CFRelease keychain
        finally
            free slot

    /// Runs a keychain call; with `Prompts.Never`, a locked keychain fails before any call that
    /// could show the unlock dialog.
    let private guarded
        (prompts: Prompts)
        (cancellation: CancellationToken)
        (body: Owned -> Result<'T, KeyringError>)
        : Task<Result<'T, KeyringError>> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<'T, KeyringError>> cancellation
        else
            let precondition =
                match prompts with
                | Prompts.Allow -> Ok()
                | Prompts.Never ->
                    match keychainStatus () with
                    | KeyringStatus.Ready -> Ok()
                    | KeyringStatus.Locked -> Error KeyringError.Locked
                    | KeyringStatus.Unavailable reason -> Error(KeyringError.Unavailable reason)
                    | KeyringStatus.Failed(code, message) -> Error(KeyringError.Failed(code, message))

            match precondition with
            | Error e -> Task.FromResult(Error e)
            | Ok() ->
                use owned = new Owned()
                Task.FromResult(body owned)

    /// The pairs that select an item, plus "fail instead of showing UI" without prompts.
    let private query (owned: Owned) (prompts: Prompts) (service: string) (account: string option) =
        let c = constants.Value

        [
            c.Class, c.ClassGenericPassword
            c.AttrService, createString owned service
            match account with
            | Some account -> c.AttrAccount, createString owned account
            | None -> ()
            match prompts, c.UseAuthenticationUI with
            | Prompts.Never, Some(key, fail) -> key, fail
            | _ -> ()
        ]

    let private read (prompts: Prompts) (key: KeyringKey) (cancellation: CancellationToken) =
        guarded prompts cancellation (fun owned ->
            let c = constants.Value

            let request =
                createDictionary
                    owned
                    (query owned prompts key.Service.Name (Some key.Account)
                     @ [ c.ReturnData, c.True; c.MatchLimit, c.MatchLimitOne ])

            let slot = outSlot ()

            try
                let status = SecItemCopyMatching(request, slot)

                if status = ErrSecItemNotFound then
                    Ok None
                elif status <> ErrSecSuccess then
                    Error(toError status)
                else
                    let result = Marshal.ReadIntPtr slot

                    try
                        match readData result with
                        | None -> Error(KeyringError.Failed("type", "the keychain returned no data"))
                        | Some bytes ->
                            try
                                Ok(Some(SecretText(utf8.GetString bytes)))
                            with :? DecoderFallbackException ->
                                Error(KeyringError.Failed("encoding", "the secret is not UTF-8 text"))
                    finally
                        CFRelease result
            finally
                free slot)

    let private write (prompts: Prompts) (key: KeyringKey) (value: SecretText) (cancellation: CancellationToken) =
        if Keyring.byteCount value > MaxValueBytes then
            Task.FromResult(Error(KeyringError.TooLarge MaxValueBytes))
        else
            guarded prompts cancellation (fun owned ->
                let c = constants.Value
                let selector = query owned prompts key.Service.Name (Some key.Account)
                let secret = createData owned (utf8.GetBytes(value.Reveal()))
                let label = createString owned $"{key.Service}: {key.Account}"

                let added =
                    SecItemAdd(createDictionary owned (selector @ [ c.ValueData, secret; c.AttrLabel, label ]), 0n)

                let status =
                    if added = ErrSecDuplicateItem then
                        SecItemUpdate(
                            createDictionary owned selector,
                            createDictionary owned [ c.ValueData, secret; c.AttrLabel, label ]
                        )
                    else
                        added

                if status = ErrSecSuccess then
                    Ok()
                else
                    Error(toError status))

    let private delete (prompts: Prompts) (key: KeyringKey) (cancellation: CancellationToken) =
        guarded prompts cancellation (fun owned ->
            let status =
                SecItemDelete(createDictionary owned (query owned prompts key.Service.Name (Some key.Account)))

            if status = ErrSecSuccess then Ok true
            elif status = ErrSecItemNotFound then Ok false
            else Error(toError status))

    let private list (prompts: Prompts) (service: KeyringService) (cancellation: CancellationToken) =
        guarded prompts cancellation (fun owned ->
            let c = constants.Value

            let request =
                createDictionary
                    owned
                    (query owned prompts service.Name None
                     @ [ c.ReturnAttributes, c.True; c.MatchLimit, c.MatchLimitAll ])

            let slot = outSlot ()

            try
                let status = SecItemCopyMatching(request, slot)

                if status = ErrSecItemNotFound then
                    Ok []
                elif status <> ErrSecSuccess then
                    Error(toError status)
                else
                    let result = Marshal.ReadIntPtr slot

                    try
                        let items =
                            if isType (CFArrayGetTypeID()) result then
                                let count = int (CFArrayGetCount result)
                                [ for i in 0 .. count - 1 -> CFArrayGetValueAtIndex(result, nativeint i) ]
                            else
                                [ result ]

                        items
                        |> List.choose (fun item ->
                            if isType (CFDictionaryGetTypeID()) item then
                                readString (CFDictionaryGetValue(item, c.AttrAccount))
                            else
                                None)
                        |> List.distinct
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                        |> Ok
                    finally
                        CFRelease result
            finally
                free slot)

    let private probe (cancellation: CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<KeyringStatus> cancellation
        else
            Task.FromResult(keychainStatus ())

    let create (prompts: Prompts) : Keyring =
        {
            Backend = "keychain"
            MaxValueBytes = MaxValueBytes
            Probe = probe
            Read = read prompts
            Write = write prompts
            Delete = delete prompts
            List = list prompts
        }
