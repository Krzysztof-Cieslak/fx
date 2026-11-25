/// advapi32 (Credential Manager), crypt32 (DPAPI) and kernel32 on Windows (x64 and arm64).
///
/// Every signature is blittable: pointers are `nativeint`, `BOOL` is `int`, `DWORD` is `uint32`.
/// Structures are written into zeroed native buffers at their 64-bit offsets; fx targets only
/// 64-bit Windows.
module internal Fx.Keyring.Native.Windows

#nowarn "9" // Pointers: this is the native interop layer.

open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<Literal>]
let private Advapi32 = "advapi32.dll"

[<Literal>]
let private Crypt32 = "crypt32.dll"

[<Literal>]
let private Kernel32 = "kernel32.dll"

let CredTypeGeneric = 1u

/// Persists for this user on this machine, across logon sessions; never roams with a profile.
let CredPersistLocalMachine = 2u

/// `CRED_MAX_CREDENTIAL_BLOB_SIZE`: 5 * 512 bytes.
let CredMaxBlobSize = 2560

let CryptProtectUiForbidden = 0x1u

let ErrorNotFound = 1168
let ErrorNoSuchLogonSession = 1312
let ErrorInvalidParameter = 87
let ErrorCancelled = 1223

/// `sizeof(CREDENTIALW)` and its offsets, on 64-bit Windows.
module Credential =
    let Size = 80
    let Flags = 0
    let Type = 4
    let TargetName = 8
    let Comment = 16
    let LastWritten = 24
    let CredentialBlobSize = 32
    let CredentialBlob = 40
    let Persist = 48
    let AttributeCount = 52
    let Attributes = 56
    let TargetAlias = 64
    let UserName = 72

/// `sizeof(DATA_BLOB)` and its offsets, on 64-bit Windows.
module DataBlob =
    let Size = 16
    let Length = 0
    let Data = 8

[<DllImport(Advapi32, EntryPoint = "CredWriteW", SetLastError = true)>]
extern int CredWrite(nativeint credential, uint32 flags)

[<DllImport(Advapi32, EntryPoint = "CredReadW", SetLastError = true)>]
extern int CredRead(nativeint targetName, uint32 credentialType, uint32 flags, nativeint credential)

[<DllImport(Advapi32, EntryPoint = "CredDeleteW", SetLastError = true)>]
extern int CredDelete(nativeint targetName, uint32 credentialType, uint32 flags)

[<DllImport(Advapi32, EntryPoint = "CredEnumerateW", SetLastError = true)>]
extern int CredEnumerate(nativeint filter, uint32 flags, nativeint count, nativeint credentials)

[<DllImport(Advapi32)>]
extern void CredFree(nativeint buffer)

[<DllImport(Crypt32, SetLastError = true)>]
extern int CryptProtectData(
    nativeint dataIn,
    nativeint description,
    nativeint entropy,
    nativeint reserved,
    nativeint prompt,
    uint32 flags,
    nativeint dataOut
)

[<DllImport(Crypt32, SetLastError = true)>]
extern int CryptUnprotectData(
    nativeint dataIn,
    nativeint description,
    nativeint entropy,
    nativeint reserved,
    nativeint prompt,
    uint32 flags,
    nativeint dataOut
)

[<DllImport(Kernel32)>]
extern nativeint LocalFree(nativeint memory)

/// Zeroed native memory, freed with `free`.
let alloc (size: int) : nativeint =
    NativePtr.toNativeInt (NativePtr.ofVoidPtr<byte>(NativeMemory.AllocZeroed(unativeint size)))

let free (pointer: nativeint) =
    if pointer <> 0n then
        NativeMemory.Free(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> pointer))

/// Zeroes native memory that held a secret.
let clear (pointer: nativeint) (length: int) =
    if pointer <> 0n && length > 0 then
        NativeMemory.Clear(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> pointer), unativeint length)

/// Copies bytes into fresh native memory (at least one byte, so the pointer is never null).
let copyIn (bytes: byte array) : nativeint =
    let p = alloc (max 1 bytes.Length)
    Marshal.Copy(bytes, 0, p, bytes.Length)
    p

/// A NUL-terminated UTF-16 copy of a string, freed with `free`.
let wide (text: string) : nativeint =
    let p = alloc ((text.Length + 1) * 2)

    for i in 0 .. text.Length - 1 do
        Marshal.WriteInt16(p, i * 2, int16 text[i])

    p

/// The last Win32 error of a P/Invoke with `SetLastError = true`.
let lastError () = Marshal.GetLastPInvokeError()

let describe (error: int) = Marshal.GetPInvokeErrorMessage error
