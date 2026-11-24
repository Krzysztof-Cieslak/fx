/// libc on Linux (glibc and musl) and macOS: pseudo-terminals, posix_spawn and signals.
///
/// Every signature is blittable (integers and pointers as `nativeint`), so no marshalling code is
/// generated at run time. Values whose layout differs per OS (`posix_spawnattr_t`,
/// `posix_spawn_file_actions_t`, `sigset_t`) are opaque buffers that are larger than any of them.
/// The only variadic function used with a variadic argument is `ioctl`; see `ioctl`.
module internal Fx.Pty.Native.Posix

#nowarn "9" // Pointers: this is the native interop layer.

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<Literal>]
let private Libc = "libc"

/// Constants that differ between Linux (the same on x64 and arm64, glibc and musl) and macOS.
type Constants =
    {
        NoCtty: int
        CloExec: int
        SpawnSetSid: int16
        /// macOS only: close every inherited descriptor that file actions do not mention.
        SpawnCloExecDefault: int16
        SetWinSize: unativeint
        GetWinSize: unativeint
        SigStop: int
    }

    override this.ToString() = "Posix.Constants"

let linux =
    {
        NoCtty = 0x100 // 0400
        CloExec = 0x80000 // 02000000
        SpawnSetSid = 0x80s
        SpawnCloExecDefault = 0s
        SetWinSize = 0x5414un
        GetWinSize = 0x5413un
        SigStop = 19
    }

let macOS =
    {
        NoCtty = 0x20000
        CloExec = 0x1000000
        SpawnSetSid = 0x400s
        SpawnCloExecDefault = 0x4000s
        SetWinSize = 0x80087467un // _IOW('t', 103, struct winsize)
        GetWinSize = 0x40087468un // _IOR('t', 104, struct winsize)
        SigStop = 17
    }

let constants = if OperatingSystem.IsMacOS() then macOS else linux

let ORdWr = 2
let SpawnSetSigDef = 0x04s
let SpawnSetSigMask = 0x08s
let SigKill = 9
let SigHup = 1
let EIntr = 4
let EIo = 5

/// `struct winsize`, identical on Linux and macOS.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type WinSize =
    {
        Rows: uint16
        Cols: uint16
        XPixel: uint16
        YPixel: uint16
    }

[<DllImport(Libc, EntryPoint = "open", SetLastError = true)>]
extern int openNoMode(nativeint path, int flags)

[<DllImport(Libc, SetLastError = true)>]
extern int close(int fd)

[<DllImport(Libc, SetLastError = true)>]
extern nativeint read(int fd, nativeint buffer, unativeint count)

[<DllImport(Libc, SetLastError = true)>]
extern nativeint write(int fd, nativeint buffer, unativeint count)

[<DllImport(Libc, SetLastError = true)>]
extern int grantpt(int fd)

[<DllImport(Libc, SetLastError = true)>]
extern int unlockpt(int fd)

/// Returns 0, or an error number (glibc, musl) or -1 with errno set (macOS).
[<DllImport(Libc, SetLastError = true)>]
extern int ptsname_r(int fd, nativeint buffer, unativeint length)

[<DllImport(Libc, SetLastError = true)>]
extern int kill(int pid, int signal)

[<DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)>]
extern int private ioctlRegisters(int fd, unativeint request, nativeint arg)

[<DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)>]
extern int private ioctlStack(
    int fd,
    unativeint request,
    nativeint x2,
    nativeint x3,
    nativeint x4,
    nativeint x5,
    nativeint x6,
    nativeint x7,
    nativeint arg
)

/// `ioctl(fd, request, arg)`. `ioctl` is variadic. Linux (x64, arm64) and macOS x64 pass variadic
/// arguments like fixed ones, in registers. Apple arm64 passes them on the stack, so a P/Invoke that
/// declares `arg` as the third parameter would put it in x2 and the kernel would read garbage: fill
/// x2..x7 with padding so that `arg` becomes the first stack slot, where `va_arg` looks for it.
let ioctl (fd: int) (request: unativeint) (arg: nativeint) : int =
    if
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture = Architecture.Arm64
    then
        ioctlStack (fd, request, 0n, 0n, 0n, 0n, 0n, 0n, arg)
    else
        ioctlRegisters (fd, request, arg)

[<DllImport(Libc)>]
extern int posix_spawn(
    nativeint pid,
    nativeint path,
    nativeint fileActions,
    nativeint attr,
    nativeint argv,
    nativeint envp
)

[<DllImport(Libc)>]
extern int posix_spawn_file_actions_init(nativeint fileActions)

[<DllImport(Libc)>]
extern int posix_spawn_file_actions_destroy(nativeint fileActions)

[<DllImport(Libc)>]
extern int posix_spawn_file_actions_addopen(nativeint fileActions, int fd, nativeint path, int flags, uint32 mode)

[<DllImport(Libc)>]
extern int posix_spawn_file_actions_adddup2(nativeint fileActions, int fd, int newFd)

/// glibc 2.29+, musl 1.1.24+, macOS 10.15+.
[<DllImport(Libc)>]
extern int posix_spawn_file_actions_addchdir_np(nativeint fileActions, nativeint path)

[<DllImport(Libc)>]
extern int posix_spawnattr_init(nativeint attr)

[<DllImport(Libc)>]
extern int posix_spawnattr_destroy(nativeint attr)

[<DllImport(Libc)>]
extern int posix_spawnattr_setflags(nativeint attr, int16 flags)

[<DllImport(Libc)>]
extern int posix_spawnattr_setsigdefault(nativeint attr, nativeint signals)

[<DllImport(Libc)>]
extern int posix_spawnattr_setsigmask(nativeint attr, nativeint signals)

[<DllImport(Libc)>]
extern int sigemptyset(nativeint set)

[<DllImport(Libc)>]
extern int sigfillset(nativeint set)

[<DllImport(Libc)>]
extern int sigdelset(nativeint set, int signal)

/// Room for any OS's `posix_spawnattr_t` (336 bytes on glibc and musl, a pointer on macOS) or
/// `posix_spawn_file_actions_t` (80 bytes, or a pointer).
let OpaqueSize = 1024un

/// Room for any OS's `sigset_t` (128 bytes on glibc and musl, 4 on macOS).
let SigSetSize = 256un

/// Zeroed native memory, freed with `free`.
let alloc (size: unativeint) : nativeint =
    NativePtr.toNativeInt (NativePtr.ofVoidPtr<byte>(NativeMemory.AllocZeroed size))

let free (pointer: nativeint) =
    if pointer <> 0n then
        NativeMemory.Free(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> pointer))

/// The errno of the last call (P/Invokes with `SetLastError = true`).
let errno () = Marshal.GetLastPInvokeError()

/// The text for an error number.
let describe (error: int) = Marshal.GetPInvokeErrorMessage error
