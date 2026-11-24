// EFF003: reading or changing the process environment outside Eff.
open System

let home = Environment.GetEnvironmentVariable "HOME" // expect: EFF003
let cwd = Environment.CurrentDirectory // expect: EFF003
let args = Environment.GetCommandLineArgs() // expect: EFF003
let cores = Environment.ProcessorCount // expect: EFF003
let quit () = Environment.Exit 1 // expect: EFF003
let baseDir = AppContext.BaseDirectory // expect: EFF003
let zone = TimeZoneInfo.Local // expect: EFF003
