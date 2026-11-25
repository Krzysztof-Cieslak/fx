namespace Fx.Keyring

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Keyring.Native.Windows

/// The Windows Credential Manager: generic credentials (`CRED_TYPE_GENERIC`) persisted for this user
/// on this machine (`CRED_PERSIST_LOCAL_MACHINE`, never roaming), named `<service>:<account>`
/// (services never contain `:`), with the account as the user name. Windows encrypts them with the
/// user's DPAPI keys.
///
/// A credential holds at most 2560 bytes (`CRED_MAX_CREDENTIAL_BLOB_SIZE`); larger values fail with
/// `TooLarge`. Logon sessions without credentials (an SSH key logon, a service, a batch logon) have
/// no credential set: `NoLogonSession`.
module WindowsCredentials =
    let MaxValueBytes = CredMaxBlobSize

    let private utf8 = UTF8Encoding(false, true)

    let private target (service: string) (account: string) = service + ":" + account

    let private toError (error: int) : KeyringError =
        if error = ErrorNoSuchLogonSession then
            KeyringError.Unavailable KeyringUnavailable.NoLogonSession
        elif error = ErrorCancelled then
            KeyringError.Dismissed
        else
            KeyringError.Failed(string error, describe error)

    let private guarded (cancellation: CancellationToken) (body: unit -> Result<'T, KeyringError>) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<'T, KeyringError>> cancellation
        else
            Task.FromResult(body ())

    /// Reads a credential: `Ok None` if there is none.
    let private readTarget (name: string) : Result<byte array option, int> =
        let targetName = wide name
        let slot = alloc IntPtr.Size

        try
            if CredRead(targetName, CredTypeGeneric, 0u, slot) = 0 then
                let error = lastError ()
                if error = ErrorNotFound then Ok None else Error error
            else
                let credential = Marshal.ReadIntPtr slot

                try
                    let size = Marshal.ReadInt32(credential, Credential.CredentialBlobSize)
                    let blob = Marshal.ReadIntPtr(credential, Credential.CredentialBlob)
                    let bytes = Array.zeroCreate<byte> size

                    if size > 0 then
                        Marshal.Copy(blob, bytes, 0, size)

                    clear blob size
                    Ok(Some bytes)
                finally
                    CredFree credential
        finally
            free slot
            free targetName

    let private read (key: KeyringKey) (cancellation: CancellationToken) =
        guarded cancellation (fun () ->
            match readTarget (target key.Service.Name key.Account) with
            | Error error -> Error(toError error)
            | Ok None -> Ok None
            | Ok(Some bytes) ->
                try
                    Ok(Some(SecretText(utf8.GetString bytes)))
                with :? DecoderFallbackException ->
                    Error(KeyringError.Failed("encoding", "the secret is not UTF-8 text")))

    let private write (key: KeyringKey) (value: SecretText) (cancellation: CancellationToken) =
        guarded cancellation (fun () ->
            let bytes = utf8.GetBytes(value.Reveal())

            if bytes.Length > MaxValueBytes then
                Error(KeyringError.TooLarge MaxValueBytes)
            else
                let credential = alloc Credential.Size
                let targetName = wide (target key.Service.Name key.Account)
                let userName = wide key.Account
                let blob = copyIn bytes

                try
                    Marshal.WriteInt32(credential, Credential.Type, int CredTypeGeneric)
                    Marshal.WriteIntPtr(credential, Credential.TargetName, targetName)
                    Marshal.WriteInt32(credential, Credential.CredentialBlobSize, bytes.Length)
                    Marshal.WriteIntPtr(credential, Credential.CredentialBlob, blob)
                    Marshal.WriteInt32(credential, Credential.Persist, int CredPersistLocalMachine)
                    Marshal.WriteIntPtr(credential, Credential.UserName, userName)

                    if CredWrite(credential, 0u) = 0 then
                        Error(toError (lastError ()))
                    else
                        Ok()
                finally
                    clear blob bytes.Length
                    free blob
                    free userName
                    free targetName
                    free credential)

    let private delete (key: KeyringKey) (cancellation: CancellationToken) =
        guarded cancellation (fun () ->
            let targetName = wide (target key.Service.Name key.Account)

            try
                if CredDelete(targetName, CredTypeGeneric, 0u) <> 0 then
                    Ok true
                else
                    let error = lastError ()

                    if error = ErrorNotFound then
                        Ok false
                    else
                        Error(toError error)
            finally
                free targetName)

    let private list (service: KeyringService) (cancellation: CancellationToken) =
        guarded cancellation (fun () ->
            let prefix = service.Name + ":"
            let filter = wide (prefix + "*")
            let count = alloc 4
            let slot = alloc IntPtr.Size

            try
                if CredEnumerate(filter, 0u, count, slot) = 0 then
                    let error = lastError ()

                    if error = ErrorNotFound then
                        Ok []
                    else
                        Error(toError error)
                else
                    let credentials = Marshal.ReadIntPtr slot

                    try
                        [
                            for i in 0 .. Marshal.ReadInt32 count - 1 do
                                let credential = Marshal.ReadIntPtr(credentials, i * IntPtr.Size)

                                if Marshal.ReadInt32(credential, Credential.Type) = int CredTypeGeneric then
                                    match
                                        Marshal.PtrToStringUni(Marshal.ReadIntPtr(credential, Credential.TargetName))
                                    with
                                    | null -> ()
                                    | name when name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ->
                                        name.Substring prefix.Length
                                    | _ -> ()
                        ]
                        |> List.distinct
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                        |> Ok
                    finally
                        CredFree credentials
            finally
                free slot
                free count
                free filter)

    /// Reads a credential that never exists: `ERROR_NOT_FOUND` means the credential set is there.
    let private probe (cancellation: CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<KeyringStatus> cancellation
        else
            match readTarget "keyring-probe:\u0001" with
            | Ok _ -> Task.FromResult KeyringStatus.Ready
            | Error error when error = ErrorNoSuchLogonSession ->
                Task.FromResult(KeyringStatus.Unavailable KeyringUnavailable.NoLogonSession)
            | Error error -> Task.FromResult(KeyringStatus.Failed(string error, describe error))

    let create () : Keyring =
        {
            Backend = "credman"
            MaxValueBytes = MaxValueBytes
            Probe = probe
            Read = read
            Write = write
            Delete = delete
            List = list
        }
