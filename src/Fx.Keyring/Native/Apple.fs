/// CoreFoundation and Security.framework on macOS: the keychain's generic passwords
/// (`SecItemAdd`, `SecItemCopyMatching`, `SecItemUpdate`, `SecItemDelete`) with queries built as
/// `CFDictionary`s.
///
/// Every signature is blittable: CF objects and pointers are `nativeint`, `CFIndex` is `nativeint`,
/// `OSStatus` is `int` and `Boolean` (an unsigned char) is `byte`, never `int`: only the low byte of
/// a returned `Boolean` is defined. Constants such as `kSecClass` are exported variables, read with
/// `NativeLibrary.GetExport`. The frameworks are loaded by absolute path, so nothing else can
/// satisfy the imports.
module internal Fx.Keyring.Native.Apple

#nowarn "9" // Pointers: this is the native interop layer.

open System
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop

[<Literal>]
let private CoreFoundation =
    "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"

[<Literal>]
let private Security = "/System/Library/Frameworks/Security.framework/Security"

[<Literal>]
let Utf8Encoding = 0x08000100u

// OSStatus values from SecBase.h.
let ErrSecSuccess = 0
let ErrSecItemNotFound = -25300
let ErrSecDuplicateItem = -25299
let ErrSecInteractionNotAllowed = -25308
let ErrSecAuthFailed = -25293
let ErrSecUserCanceled = -128
let ErrSecNotAvailable = -25291
let ErrSecNoSuchKeychain = -25294
let ErrSecNoDefaultKeychain = -25307
let ErrSecMissingEntitlement = -34018

/// `SecKeychainStatus` bits.
let UnlockStateStatus = 1u

[<DllImport(CoreFoundation)>]
extern void CFRelease(nativeint cf)

[<DllImport(CoreFoundation)>]
extern nativeint CFStringCreateWithBytes(
    nativeint allocator,
    nativeint bytes,
    nativeint length,
    uint32 encoding,
    byte isExternalRepresentation
)

[<DllImport(CoreFoundation)>]
extern nativeint CFStringGetLength(nativeint text)

[<DllImport(CoreFoundation)>]
extern nativeint CFStringGetMaximumSizeForEncoding(nativeint length, uint32 encoding)

[<DllImport(CoreFoundation)>]
extern byte CFStringGetCString(nativeint text, nativeint buffer, nativeint bufferSize, uint32 encoding)

[<DllImport(CoreFoundation)>]
extern nativeint CFDataCreate(nativeint allocator, nativeint bytes, nativeint length)

[<DllImport(CoreFoundation)>]
extern nativeint CFDataGetLength(nativeint data)

[<DllImport(CoreFoundation)>]
extern nativeint CFDataGetBytePtr(nativeint data)

[<DllImport(CoreFoundation)>]
extern nativeint CFDictionaryCreate(
    nativeint allocator,
    nativeint keys,
    nativeint values,
    nativeint count,
    nativeint keyCallBacks,
    nativeint valueCallBacks
)

[<DllImport(CoreFoundation)>]
extern nativeint CFDictionaryGetValue(nativeint dictionary, nativeint key)

[<DllImport(CoreFoundation)>]
extern nativeint CFArrayGetCount(nativeint array)

[<DllImport(CoreFoundation)>]
extern nativeint CFArrayGetValueAtIndex(nativeint array, nativeint index)

[<DllImport(CoreFoundation)>]
extern unativeint CFGetTypeID(nativeint cf)

[<DllImport(CoreFoundation)>]
extern unativeint CFStringGetTypeID()

[<DllImport(CoreFoundation)>]
extern unativeint CFDataGetTypeID()

[<DllImport(CoreFoundation)>]
extern unativeint CFArrayGetTypeID()

[<DllImport(CoreFoundation)>]
extern unativeint CFDictionaryGetTypeID()

[<DllImport(Security)>]
extern int SecItemAdd(nativeint attributes, nativeint result)

[<DllImport(Security)>]
extern int SecItemCopyMatching(nativeint query, nativeint result)

[<DllImport(Security)>]
extern int SecItemUpdate(nativeint query, nativeint attributesToUpdate)

[<DllImport(Security)>]
extern int SecItemDelete(nativeint query)

[<DllImport(Security)>]
extern nativeint SecCopyErrorMessageString(int status, nativeint reserved)

/// Deprecated with the rest of `SecKeychain`, but still the only way to read whether the default
/// keychain is locked without touching an item.
[<DllImport(Security)>]
extern int SecKeychainCopyDefault(nativeint keychain)

[<DllImport(Security)>]
extern int SecKeychainGetStatus(nativeint keychain, nativeint status)

let private library (path: string) = lazy (NativeLibrary.Load path)

let private coreFoundation = library CoreFoundation
let private security = library Security

/// The address of an exported variable.
let private address (lib: Lazy<nativeint>) (name: string) : nativeint =
    NativeLibrary.GetExport(lib.Value, name)

/// The value of an exported `CFTypeRef` variable (a constant owned by the framework: never released).
let private constant (lib: Lazy<nativeint>) (name: string) : nativeint = Marshal.ReadIntPtr(address lib name)

/// The constants the keychain queries use, read once.
type Constants =
    {
        KeyCallBacks: nativeint
        ValueCallBacks: nativeint
        True: nativeint
        Class: nativeint
        ClassGenericPassword: nativeint
        AttrService: nativeint
        AttrAccount: nativeint
        AttrLabel: nativeint
        ValueData: nativeint
        ReturnData: nativeint
        ReturnAttributes: nativeint
        MatchLimit: nativeint
        MatchLimitOne: nativeint
        MatchLimitAll: nativeint
        /// `kSecUseAuthenticationUI` and `kSecUseAuthenticationUIFail`: fail instead of showing UI.
        /// Deprecated in macOS 11 for an `LAContext`, which needs Objective-C; `None` if a future
        /// macOS stops exporting them.
        UseAuthenticationUI: (nativeint * nativeint) option
    }

    override _.ToString() = "Apple.Constants"

let constants =
    lazy
        (let optional (name: string) =
            match NativeLibrary.TryGetExport(security.Value, name) with
            | true, a -> Some(Marshal.ReadIntPtr a)
            | _ -> None

         {
             KeyCallBacks = address coreFoundation "kCFTypeDictionaryKeyCallBacks"
             ValueCallBacks = address coreFoundation "kCFTypeDictionaryValueCallBacks"
             True = constant coreFoundation "kCFBooleanTrue"
             Class = constant security "kSecClass"
             ClassGenericPassword = constant security "kSecClassGenericPassword"
             AttrService = constant security "kSecAttrService"
             AttrAccount = constant security "kSecAttrAccount"
             AttrLabel = constant security "kSecAttrLabel"
             ValueData = constant security "kSecValueData"
             ReturnData = constant security "kSecReturnData"
             ReturnAttributes = constant security "kSecReturnAttributes"
             MatchLimit = constant security "kSecMatchLimit"
             MatchLimitOne = constant security "kSecMatchLimitOne"
             MatchLimitAll = constant security "kSecMatchLimitAll"
             UseAuthenticationUI =
                 match optional "kSecUseAuthenticationUI", optional "kSecUseAuthenticationUIFail" with
                 | Some key, Some value -> Some(key, value)
                 | _ -> None
         })

/// Zeroed native memory, freed with `free`.
let alloc (size: int) : nativeint =
    NativePtr.toNativeInt (NativePtr.ofVoidPtr<byte>(NativeMemory.AllocZeroed(unativeint size)))

let free (pointer: nativeint) =
    if pointer <> 0n then
        NativeMemory.Free(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> pointer))

/// Copies bytes into fresh native memory (at least one byte, so the pointer is never null).
let private copyIn (bytes: byte array) : nativeint =
    let p = alloc (max 1 bytes.Length)
    Marshal.Copy(bytes, 0, p, bytes.Length)
    p

/// CF objects created for one call, released together.
type Owned() =
    let objects = ResizeArray<nativeint>()

    member _.Add(cf: nativeint) : nativeint =
        if cf = 0n then
            raise (OutOfMemoryException "CoreFoundation returned NULL")

        objects.Add cf
        cf

    interface IDisposable with
        member _.Dispose() =
            for cf in objects do
                CFRelease cf

            objects.Clear()

let createString (owned: Owned) (text: string) : nativeint =
    let bytes = Encoding.UTF8.GetBytes text
    let p = copyIn bytes

    try
        owned.Add(CFStringCreateWithBytes(0n, p, nativeint bytes.Length, Utf8Encoding, 0uy))
    finally
        free p

/// A `CFData` with a copy of the bytes; the native copy is zeroed before it is freed.
let createData (owned: Owned) (bytes: byte array) : nativeint =
    let p = copyIn bytes

    try
        owned.Add(CFDataCreate(0n, p, nativeint bytes.Length))
    finally
        NativeMemory.Clear(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> p), unativeint bytes.Length)
        free p

let createDictionary (owned: Owned) (pairs: (nativeint * nativeint) list) : nativeint =
    let c = constants.Value
    let count = pairs.Length
    let keys = alloc (max 1 count * IntPtr.Size)
    let values = alloc (max 1 count * IntPtr.Size)

    try
        pairs
        |> List.iteri (fun i (k, v) ->
            Marshal.WriteIntPtr(keys, i * IntPtr.Size, k)
            Marshal.WriteIntPtr(values, i * IntPtr.Size, v))

        owned.Add(CFDictionaryCreate(0n, keys, values, nativeint count, c.KeyCallBacks, c.ValueCallBacks))
    finally
        free keys
        free values

let isType (typeId: unativeint) (cf: nativeint) = cf <> 0n && CFGetTypeID cf = typeId

/// The text of a `CFString`.
let readString (cf: nativeint) : string option =
    if not (isType (CFStringGetTypeID()) cf) then
        None
    else
        let size =
            CFStringGetMaximumSizeForEncoding(CFStringGetLength cf, Utf8Encoding) + 1n

        let buffer = alloc (int size)

        try
            if CFStringGetCString(cf, buffer, size, Utf8Encoding) = 0uy then
                None
            else
                match Marshal.PtrToStringUTF8 buffer with
                | null -> None
                | text -> Some text
        finally
            free buffer

/// The bytes of a `CFData`.
let readData (cf: nativeint) : byte array option =
    if not (isType (CFDataGetTypeID()) cf) then
        None
    else
        let length = int (CFDataGetLength cf)
        let bytes = Array.zeroCreate<byte> length

        if length > 0 then
            Marshal.Copy(CFDataGetBytePtr cf, bytes, 0, length)

        Some bytes

/// The OS's description of a status code.
let describe (status: int) : string =
    let cf = SecCopyErrorMessageString(status, 0n)

    if cf = 0n then
        $"OSStatus {status}"
    else
        try
            readString cf |> Option.defaultValue $"OSStatus {status}"
        finally
            CFRelease cf

/// Writes a pointer-sized out-parameter slot; read it with `Marshal.ReadIntPtr`.
let outSlot () = alloc IntPtr.Size
