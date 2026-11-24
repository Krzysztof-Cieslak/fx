// EFF008: calling native code (P/Invoke) outside Eff; only capability interpreters may.
open System.Runtime.InteropServices

[<DllImport("libc")>]
extern int getpid()

let pid () = getpid () // expect: EFF008
