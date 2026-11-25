/// libc on Linux (glibc and musl): the one call the D-Bus client needs.
module internal Fx.Keyring.Native.Libc

open System.Runtime.InteropServices

[<Literal>]
let private Libc = "libc"

/// The effective user id, which D-Bus's `EXTERNAL` authentication names (the bus checks it against
/// the socket's peer credentials).
[<DllImport(Libc)>]
extern uint32 geteuid()
