/// kernel32 on Windows: opening files and directories without following reparse points (T113b).
/// Blittable signatures only (paths as pointers to UTF-16 from `withPath`).
module internal Fx.Envd.Live.Native.Windows

open System
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

[<Literal>]
let private Kernel32 = "kernel32.dll"

let GenericRead = 0x80000000u
let GenericWrite = 0x40000000u
let FileListDirectory = 0x0001u
let FileReadAttributes = 0x0080u
let Synchronize = 0x00100000u

let FileShareRead = 0x1u
let FileShareWrite = 0x2u
let FileShareDelete = 0x4u

let OpenExisting = 3u
let OpenAlways = 4u

let FileFlagBackupSemantics = 0x02000000u
let FileFlagOpenReparsePoint = 0x00200000u

let ErrorFileNotFound = 2
let ErrorPathNotFound = 3
let ErrorAccessDenied = 5
let ErrorSharingViolation = 32
let ErrorDirectory = 267

[<DllImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true)>]
extern nativeint private createFile(
    nativeint fileName,
    uint32 desiredAccess,
    uint32 shareMode,
    nativeint securityAttributes,
    uint32 creationDisposition,
    uint32 flagsAndAttributes,
    nativeint templateFile
)

/// A fully qualified, normalized path in its extended-length form (`\\?\`), which `CreateFileW`
/// takes beyond the old 260-character limit (.NET adds it the same way).
let extended (path: string) : string =
    if
        path.StartsWith(@"\\?\", StringComparison.Ordinal)
        || path.StartsWith(@"\\.\", StringComparison.Ordinal)
    then
        path
    elif path.StartsWith(@"\\", StringComparison.Ordinal) then
        @"\\?\UNC\" + path.Substring 2
    else
        @"\\?\" + path

/// `CreateFileW`: the handle, or the Win32 error. `path` is fully qualified and normalized.
let open'
    (path: string)
    (access: uint32)
    (share: uint32)
    (disposition: uint32)
    (flags: uint32)
    : Result<SafeFileHandle, int> =
    let name = Marshal.StringToHGlobalUni(extended path)

    try
        let handle = createFile (name, access, share, 0n, disposition, flags, 0n)

        if handle = -1n then
            Error(Marshal.GetLastPInvokeError())
        else
            Ok(new SafeFileHandle(handle, true))
    finally
        Marshal.FreeHGlobal name
