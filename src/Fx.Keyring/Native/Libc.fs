/// libc on Linux (glibc and musl): the one call the D-Bus client needs.
///
/// Runtime marshalling is disabled for the assembly, so a P/Invoke signature in any of the native
/// modules that would need a marshalling stub fails on the JIT as well as under Native AOT.
module internal Fx.Keyring.Native.Libc

open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<assembly: DisableRuntimeMarshalling>]
do ()

[<Literal>]
let private Libc = "libc"

/// The effective user id, which D-Bus's `EXTERNAL` authentication names (the bus checks it against
/// the socket's peer credentials).
[<DllImport(Libc)>]
extern uint32 geteuid()
