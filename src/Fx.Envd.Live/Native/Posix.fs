/// libc on Linux (glibc and musl, x64 and arm64) and macOS (x64 and arm64): the descriptor-relative
/// calls (*at) that resolve paths beneath a directory without following symbolic links (T113b).
///
/// Every signature is blittable (integers, and paths as pointers to UTF-8 from `withPath`), so no
/// marshalling code is generated at run time. Layouts that differ per OS and architecture
/// (`struct stat`, `struct dirent`) are read at fixed offsets from buffers larger than any of them.
/// Calls whose glibc symbol is missing on older systems (`fstatat` before glibc 2.33) or that libc does
/// not wrap (`openat2`) go through `syscall` on Linux.
module internal Fx.Envd.Live.Native.Posix

#nowarn "9" // Pointers: this is the native interop layer.

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<Literal>]
let private Libc = "libc"

let private macOS = OperatingSystem.IsMacOS()
let private arm64 = RuntimeInformation.ProcessArchitecture = Architecture.Arm64

/// Constants that differ between Linux (x64, arm64) and macOS.
type Constants =
    {
        ODirectory: int
        ONoFollow: int
        OCloExec: int
        OCreat: int
        OExcl: int
        OTrunc: int
        /// macOS 11+: no symbolic link anywhere in the path (0 on Linux, which has `openat2`).
        ONoFollowAny: int
        AtSymlinkNoFollow: int
        AtRemoveDir: int
        ELoop: int
        ENotEmpty: int
        ENoSys: int
    }

    override _.ToString() = "Posix.Constants"

let private linux =
    {
        // asm-generic (arm64) differs from x86 for these two.
        ODirectory = if arm64 then 0x4000 else 0x10000
        ONoFollow = if arm64 then 0x8000 else 0x20000
        OCloExec = 0x80000
        OCreat = 0x40
        OExcl = 0x80
        OTrunc = 0x200
        ONoFollowAny = 0
        AtSymlinkNoFollow = 0x100
        AtRemoveDir = 0x200
        ELoop = 40
        ENotEmpty = 39
        ENoSys = 38
    }

let private darwin =
    {
        ODirectory = 0x100000
        ONoFollow = 0x100
        OCloExec = 0x1000000
        OCreat = 0x200
        OExcl = 0x800
        OTrunc = 0x400
        ONoFollowAny = 0x20000000
        AtSymlinkNoFollow = 0x20
        AtRemoveDir = 0x80
        ELoop = 62
        ENotEmpty = 66
        ENoSys = 78
    }

let c = if macOS then darwin else linux

let ORdOnly = 0
let OWrOnly = 1
let EPerm = 1
let ENoEnt = 2
let EIntr = 4
let E2Big = 7
let EAcces = 13
let EExist = 17
let EXDev = 18
let ENotDir = 20
let EIsDir = 21
let EInval = 22

/// `openat2`'s `resolve` flags (Linux 5.6+).
let ResolveNoMagicLinks = 0x02UL
let ResolveNoSymlinks = 0x04UL
let ResolveBeneath = 0x08UL

let SIfMt = 0xF000u
let SIfDir = 0x4000u
let SIfReg = 0x8000u
let SIfLnk = 0xA000u

[<DllImport(Libc, EntryPoint = "openat", SetLastError = true)>]
extern int private openatRegisters(int dirfd, nativeint path, int flags, uint32 mode)

[<DllImport(Libc, EntryPoint = "openat", SetLastError = true)>]
extern int private openatStack(
    int dirfd,
    nativeint path,
    int flags,
    nativeint x3,
    nativeint x4,
    nativeint x5,
    nativeint x6,
    nativeint x7,
    nativeint mode
)

/// `openat(dirfd, path, flags, mode)`. `openat` is variadic (`mode` is read only with `O_CREAT`).
/// Linux (x64, arm64) and macOS x64 pass variadic arguments like fixed ones; Apple arm64 passes them
/// on the stack, so x3..x7 are padding and `mode` becomes the first stack slot (as `Fx.Pty`'s `ioctl`).
let openat (dirfd: int) (path: nativeint) (flags: int) (mode: uint32) : int =
    if macOS && arm64 then
        openatStack (dirfd, path, flags, 0n, 0n, 0n, 0n, 0n, nativeint mode)
    else
        openatRegisters (dirfd, path, flags, mode)

[<DllImport(Libc, SetLastError = true)>]
extern int close(int fd)

[<DllImport(Libc, SetLastError = true)>]
extern int mkdirat(int dirfd, nativeint path, uint32 mode)

[<DllImport(Libc, SetLastError = true)>]
extern int unlinkat(int dirfd, nativeint path, int flags)

[<DllImport(Libc, SetLastError = true)>]
extern int renameat(int fromDirfd, nativeint fromPath, int toDirfd, nativeint toPath)

[<DllImport(Libc, SetLastError = true)>]
extern int fchmod(int fd, uint32 mode)

/// Linux only: `syscall(number, a, b, c, d)`. Variadic, which Linux passes like fixed arguments.
[<DllImport(Libc, EntryPoint = "syscall", SetLastError = true)>]
extern int64 private syscall4(int64 number, int64 a, int64 b, int64 c, int64 d)

// macOS x64 keeps the 32-bit-inode `stat` and `dirent` under the plain names; the 64-bit ones (the
// only ones on arm64) are the `$INODE64` symbols.

[<DllImport(Libc, EntryPoint = "fstatat", SetLastError = true)>]
extern int private fstatatPlain(int dirfd, nativeint path, nativeint buffer, int flags)

[<DllImport(Libc, EntryPoint = "fstatat$INODE64", SetLastError = true)>]
extern int private fstatatInode64(int dirfd, nativeint path, nativeint buffer, int flags)

[<DllImport(Libc, EntryPoint = "fdopendir", SetLastError = true)>]
extern nativeint private fdopendirPlain(int fd)

[<DllImport(Libc, EntryPoint = "fdopendir$INODE64", SetLastError = true)>]
extern nativeint private fdopendirInode64(int fd)

[<DllImport(Libc, EntryPoint = "readdir", SetLastError = true)>]
extern nativeint private readdirPlain(nativeint dir)

[<DllImport(Libc, EntryPoint = "readdir$INODE64", SetLastError = true)>]
extern nativeint private readdirInode64(nativeint dir)

[<DllImport(Libc, SetLastError = true)>]
extern int closedir(nativeint dir)

let private inode64 = macOS && not arm64

/// The errno of the last call (P/Invokes with `SetLastError = true`).
let errno () = Marshal.GetLastPInvokeError()

/// The text for an error number.
let describe (error: int) = Marshal.GetPInvokeErrorMessage error

/// Runs `f` with a path as NUL-terminated UTF-8.
let withPath (path: string) (f: nativeint -> 'T) : 'T =
    let pointer = Marshal.StringToCoTaskMemUTF8 path

    try
        f pointer
    finally
        Marshal.FreeCoTaskMem pointer

/// Linux 5.6+: `openat2(dirfd, path, {flags, mode = 0, resolve})`; -1 with errno (`ENOSYS` where the
/// kernel, or a seccomp filter, does not have it).
let openat2 (dirfd: int) (path: nativeint) (flags: int) (resolve: uint64) : int =
    let how = NativePtr.stackalloc<uint64> 3
    NativePtr.set how 0 (uint64 (uint32 flags))
    NativePtr.set how 1 0UL
    NativePtr.set how 2 resolve
    // openat2 is 437 on every Linux architecture.
    int (syscall4 (437L, int64 dirfd, int64 path, int64 (NativePtr.toNativeInt how), 24L))

/// What `fstatat` reports: the file type and permission bits, the size and the modification time.
[<Struct>]
type Stat =
    {
        Mode: uint32
        Size: int64
        ModifiedSeconds: int64
        ModifiedNanoseconds: int64
    }

    member this.Type = this.Mode &&& SIfMt

    override this.ToString() = $"mode {this.Mode} size {this.Size}"

/// Room for any `struct stat` (144 bytes on Linux x64 and macOS, 128 on Linux arm64).
[<Literal>]
let private StatSize = 256

/// `fstatat(dirfd, name, flags)`: the entry, or the errno.
let fstatat (dirfd: int) (name: nativeint) (flags: int) : Result<Stat, int> =
    let buffer = NativePtr.stackalloc<byte> StatSize
    let address = NativePtr.toNativeInt buffer

    let status =
        if macOS then
            if inode64 then
                fstatatInode64 (dirfd, name, address, flags)
            else
                fstatatPlain (dirfd, name, address, flags)
        else
            // newfstatat: 262 on x64, 79 on arm64.
            let number = if arm64 then 79L else 262L
            int (syscall4 (number, int64 dirfd, int64 name, int64 address, int64 flags))

    if status <> 0 then
        Error(errno ())
    elif macOS then
        // dev 0, mode (16 bits) 4, nlink 6, ino 8, uid 16, gid 20, rdev 24, atime 32, mtime 48,
        // ctime 64, birthtime 80, size 96.
        Ok
            {
                Mode = uint32 (uint16 (Marshal.ReadInt16(address, 4)))
                Size = Marshal.ReadInt64(address, 96)
                ModifiedSeconds = Marshal.ReadInt64(address, 48)
                ModifiedNanoseconds = Marshal.ReadInt64(address, 56)
            }
    else
        // x64: dev 0, ino 8, nlink 16, mode 24; arm64: dev 0, ino 8, mode 16, nlink 20. Both: size 48,
        // atime 72, mtime 88.
        Ok
            {
                Mode = uint32 (Marshal.ReadInt32(address, (if arm64 then 16 else 24)))
                Size = Marshal.ReadInt64(address, 48)
                ModifiedSeconds = Marshal.ReadInt64(address, 88)
                ModifiedNanoseconds = Marshal.ReadInt64(address, 96)
            }

/// The names in a directory, without `.` and `..`. Takes `fd` over (`closedir` closes it).
let readNames (fd: int) : Result<string list, int> =
    let dir = if inode64 then fdopendirInode64 fd else fdopendirPlain fd

    if dir = 0n then
        let error = errno ()
        close fd |> ignore
        Error error
    else
        try
            // d_name follows d_ino, d_off, d_reclen and d_type on Linux (glibc and musl, 64-bit), and
            // d_ino, d_seekoff, d_reclen, d_namlen and d_type on macOS.
            let nameOffset = if macOS then 21 else 19
            let names = ResizeArray<string>()
            let mutable failure = 0
            let mutable reading = true

            while reading do
                Marshal.SetLastSystemError 0
                let entry = if inode64 then readdirInode64 dir else readdirPlain dir

                if entry = 0n then
                    failure <- errno ()
                    reading <- false
                else
                    match Marshal.PtrToStringUTF8(entry + nativeint nameOffset) with
                    | null
                    | "."
                    | ".." -> ()
                    | name -> names.Add name

            if failure <> 0 then Error failure else Ok(List.ofSeq names)
        finally
            closedir dir |> ignore
