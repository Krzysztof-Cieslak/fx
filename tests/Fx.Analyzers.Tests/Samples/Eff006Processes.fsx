// EFF006: starting or inspecting processes outside Eff; commands run through `IShell`.
open System.Diagnostics

let started = Process.Start "git" // expect: EFF006
let current = Process.GetCurrentProcess() // expect: EFF006
let make () = new Process() // expect: EFF006
