/// kernel32 on Windows 10 1809+ (x64 and arm64): pipes, ConPTY, process creation and job objects.
///
/// Every signature is blittable: handles and pointers are `nativeint`, `BOOL` is `int`, and `COORD`
/// (two 16-bit numbers passed by value) is packed into an `int`. Structures are written into zeroed
/// native buffers at their 64-bit offsets; fx targets only 64-bit Windows.
module internal Fx.Pty.Native.Windows

#nowarn "9" // Pointers: this is the native interop layer.

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<Literal>]
let private Kernel32 = "kernel32.dll"

let ErrorBrokenPipe = 109
let ErrorNoData = 232
let ErrorInsufficientBuffer = 122

let ExtendedStartupInfoPresent = 0x00080000u
let CreateUnicodeEnvironment = 0x00000400u
let StartfUseStdHandles = 0x00000100

let ProcThreadAttributePseudoConsole = 0x00020016n
let ProcThreadAttributeJobList = 0x0002000Dn

let JobObjectExtendedLimitInformation = 9
let JobObjectLimitKillOnJobClose = 0x00002000

/// `sizeof(STARTUPINFOEXW)` and the offsets used, on 64-bit Windows.
module StartupInfoEx =
    let Size = 112
    let Flags = 60
    let StdInput = 80
    let StdOutput = 88
    let StdError = 96
    let AttributeList = 104

/// `sizeof(PROCESS_INFORMATION)` and its offsets, on 64-bit Windows.
module ProcessInformation =
    let Size = 24
    let Process = 0
    let Thread = 8

/// `sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)` and the offset of `BasicLimitInformation.LimitFlags`.
module ExtendedLimitInformation =
    let Size = 144
    let LimitFlags = 16

/// The `COORD` for a size: columns in the low 16 bits, rows in the high 16 bits.
let coord (cols: int) (rows: int) : int = (cols &&& 0xffff) ||| (rows <<< 16)

[<DllImport(Kernel32, SetLastError = true)>]
extern int CreatePipe(nativeint readPipe, nativeint writePipe, nativeint attributes, uint32 size)

[<DllImport(Kernel32)>]
extern int CreatePseudoConsole(int size, nativeint input, nativeint output, uint32 flags, nativeint pseudoConsole)

[<DllImport(Kernel32)>]
extern int ResizePseudoConsole(nativeint pseudoConsole, int size)

[<DllImport(Kernel32)>]
extern void ClosePseudoConsole(nativeint pseudoConsole)

[<DllImport(Kernel32, SetLastError = true)>]
extern int InitializeProcThreadAttributeList(nativeint list, int count, int flags, nativeint size)

[<DllImport(Kernel32, SetLastError = true)>]
extern int UpdateProcThreadAttribute(
    nativeint list,
    uint32 flags,
    nativeint attribute,
    nativeint value,
    nativeint size,
    nativeint previousValue,
    nativeint returnSize
)

[<DllImport(Kernel32)>]
extern void DeleteProcThreadAttributeList(nativeint list)

[<DllImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true)>]
extern int CreateProcess(
    nativeint applicationName,
    nativeint commandLine,
    nativeint processAttributes,
    nativeint threadAttributes,
    int inheritHandles,
    uint32 creationFlags,
    nativeint environment,
    nativeint currentDirectory,
    nativeint startupInfo,
    nativeint processInformation
)

[<DllImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true)>]
extern nativeint CreateJobObject(nativeint attributes, nativeint name)

[<DllImport(Kernel32, SetLastError = true)>]
extern int SetInformationJobObject(nativeint job, int infoClass, nativeint info, uint32 length)

[<DllImport(Kernel32, SetLastError = true)>]
extern int TerminateJobObject(nativeint job, uint32 exitCode)

[<DllImport(Kernel32, SetLastError = true)>]
extern int CloseHandle(nativeint handle)

[<DllImport(Kernel32, SetLastError = true)>]
extern int ReadFile(nativeint file, nativeint buffer, uint32 count, nativeint read, nativeint overlapped)

[<DllImport(Kernel32, SetLastError = true)>]
extern int WriteFile(nativeint file, nativeint buffer, uint32 count, nativeint written, nativeint overlapped)

/// Zeroed native memory, freed with `free`.
let alloc (size: int) : nativeint =
    NativePtr.toNativeInt (NativePtr.ofVoidPtr<byte>(NativeMemory.AllocZeroed(unativeint size)))

let free (pointer: nativeint) =
    if pointer <> 0n then
        NativeMemory.Free(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> pointer))

/// The last Win32 error of a P/Invoke with `SetLastError = true`.
let lastError () = Marshal.GetLastPInvokeError()

let describe (error: int) = Marshal.GetPInvokeErrorMessage error
