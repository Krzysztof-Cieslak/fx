namespace Fx.Keyring

open System
open System.Runtime.InteropServices
open Fx.Keyring.Native.Windows

/// Data that DPAPI could not protect or unprotect.
[<RequireQualifiedAccess>]
type DpapiError =
    /// Not Windows.
    | NotSupported
    /// The logon session has no DPAPI keys (an SSH key logon, a service, a batch logon).
    | NoLogonSession
    /// The data was not protected by this user with this entropy, or was changed.
    | Rejected of code: int * message: string
    | Failed of code: int * message: string

    override this.ToString() =
        match this with
        | NotSupported -> "DPAPI is Windows-only"
        | NoLogonSession -> "the logon session has no DPAPI keys"
        | Rejected(code, message) -> $"DPAPI rejected the data ({code}): {message}"
        | Failed(code, message) -> $"DPAPI failed ({code}): {message}"

/// DPAPI (`CryptProtectData`, `CryptUnprotectData`) in the current user's scope: what the `file`
/// credential store (T121) encrypts its file with on Windows. Only this user, on this machine (or
/// with a roaming profile), can unprotect it; an administrator resetting the password loses it.
/// The entropy is a second, application-chosen key part: another program of the same user must know
/// it too. Never shows UI (`CRYPTPROTECT_UI_FORBIDDEN`).
module Dpapi =
    // NTE_BAD_DATA, ERROR_INVALID_DATA, CRYPT_E_... : the data was not ours, or was changed.
    let private rejections = [ 13; 0x80090005; 0x8009000B ]

    let private withBlob (bytes: byte array) (body: nativeint -> 'T) : 'T =
        let blob = alloc DataBlob.Size
        let data = copyIn bytes

        try
            Marshal.WriteInt32(blob, DataBlob.Length, bytes.Length)
            Marshal.WriteIntPtr(blob, DataBlob.Data, data)
            body blob
        finally
            clear data bytes.Length
            free data
            free blob

    let private run
        (call: nativeint -> nativeint -> nativeint -> int)
        (entropy: byte array)
        (input: byte array)
        : Result<byte array, DpapiError> =
        if not (OperatingSystem.IsWindows()) then
            Error DpapiError.NotSupported
        else
            withBlob input (fun inBlob ->
                withBlob entropy (fun entropyBlob ->
                    let outBlob = alloc DataBlob.Size

                    try
                        if call inBlob entropyBlob outBlob = 0 then
                            let error = lastError ()

                            if error = ErrorNoSuchLogonSession then
                                Error DpapiError.NoLogonSession
                            elif List.contains error rejections then
                                Error(DpapiError.Rejected(error, describe error))
                            else
                                Error(DpapiError.Failed(error, describe error))
                        else
                            let length = Marshal.ReadInt32(outBlob, DataBlob.Length)
                            let data = Marshal.ReadIntPtr(outBlob, DataBlob.Data)
                            let bytes = Array.zeroCreate<byte> length

                            if length > 0 then
                                Marshal.Copy(data, bytes, 0, length)

                            clear data length
                            LocalFree data |> ignore
                            Ok bytes
                    finally
                        free outBlob))

    /// Encrypts data for the current user.
    let protect (entropy: byte array) (plaintext: byte array) : Result<byte array, DpapiError> =
        run
            (fun inBlob entropyBlob outBlob ->
                CryptProtectData(inBlob, 0n, entropyBlob, 0n, 0n, CryptProtectUiForbidden, outBlob))
            entropy
            plaintext

    /// Decrypts data that `protect` encrypted for this user with the same entropy.
    let unprotect (entropy: byte array) (protectedData: byte array) : Result<byte array, DpapiError> =
        run
            (fun inBlob entropyBlob outBlob ->
                CryptUnprotectData(inBlob, 0n, entropyBlob, 0n, 0n, CryptProtectUiForbidden, outBlob))
            entropy
            protectedData
