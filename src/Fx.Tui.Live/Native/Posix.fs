/// libc on Linux (glibc and musl) and macOS: the controlling terminal's modes, size and I/O.
///
/// Every signature is blittable (integers and pointers as `nativeint`). `struct termios` differs
/// between the systems, so it is an opaque buffer larger than either, filled by `tcgetattr` and
/// changed only by `cfmakeraw`. `ioctl` is variadic: see `ioctl`.
///
/// Runtime marshalling is disabled for the assembly, so a P/Invoke signature in either native module
/// that would need a marshalling stub fails on the JIT as well as under Native AOT.
module internal Fx.Tui.Live.Native.Posix

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<assembly: DisableRuntimeMarshalling>]
do ()

[<Literal>]
let private Libc = "libc"

/// Larger than `struct termios` anywhere (60 bytes on Linux, 72 on macOS).
let TermiosSize = 256

/// `TCSANOW` (0 on Linux and macOS): change the modes at once.
let TcsaNow = 0

/// `POLLIN` (1 on Linux and macOS).
let PollIn = 1s

let EIntr = 4

/// `EAGAIN`: 11 on Linux, 35 on macOS.
let EAgain = if OperatingSystem.IsMacOS() then 35 else 11

/// `TIOCGWINSZ`.
let GetWinSize: unativeint =
    if OperatingSystem.IsMacOS() then 0x40087468un else 0x5413un

[<DllImport(Libc)>]
extern int isatty(int fd)

[<DllImport(Libc)>]
extern int tcgetattr(int fd, nativeint termios)

[<DllImport(Libc)>]
extern int tcsetattr(int fd, int actions, nativeint termios)

[<DllImport(Libc)>]
extern void cfmakeraw(nativeint termios)

[<DllImport(Libc)>]
extern nativeint read(int fd, nativeint buffer, unativeint count)

[<DllImport(Libc)>]
extern nativeint write(int fd, nativeint buffer, unativeint count)

/// `poll(struct pollfd *fds, nfds_t n, int timeout)`; `nfds_t` is 64 bits on Linux and 32 on macOS,
/// passed in a register either way.
[<DllImport(Libc)>]
extern int poll(nativeint fds, unativeint count, int timeout)

[<DllImport(Libc, EntryPoint = "ioctl")>]
extern int private ioctlRegisters(int fd, unativeint request, nativeint arg)

[<DllImport(Libc, EntryPoint = "ioctl")>]
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

/// `ioctl(fd, request, arg)`. Apple arm64 passes variadic arguments on the stack: padding x2..x7
/// makes `arg` the first stack slot, where `va_arg` reads it (as `src/Fx.Pty` does).
let ioctl (fd: int) (request: unativeint) (arg: nativeint) : int =
    if
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture = Architecture.Arm64
    then
        ioctlStack (fd, request, 0n, 0n, 0n, 0n, 0n, 0n, arg)
    else
        ioctlRegisters (fd, request, arg)

/// Zeroed native memory.
let alloc (size: int) : nativeint =
    let pointer = Marshal.AllocHGlobal size

    for i in 0 .. size - 1 do
        Marshal.WriteByte(pointer, i, 0uy)

    pointer

let free (pointer: nativeint) = Marshal.FreeHGlobal pointer

/// The error number of the call that just failed.
let errno () = Marshal.GetLastSystemError()
