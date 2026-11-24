// EFF006: starting or inspecting processes outside Eff; commands run through `IShell`.
open System.Diagnostics
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

let started = Process.Start "git" // expect: EFF006
let current = Process.GetCurrentProcess() // expect: EFF006
let make () = new Process() // expect: EFF006
let handle = SafeProcessHandle.Start(ProcessStartInfo "git") // expect: EFF006
let custom = UnixProcessStartArguments.Start(ProcessStartInfo "git", fun _ -> 1) // expect: EFF006
