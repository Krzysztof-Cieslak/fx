// EFF001: reading the clock or waiting outside Eff; time comes from `IClock`.
open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

let now = DateTime.Now // expect: EFF001
let utcNow = DateTime.UtcNow // expect: EFF001
let offsetNow = DateTimeOffset.UtcNow // expect: EFF001
let ticks = Environment.TickCount64 // expect: EFF001
let watch = Stopwatch.StartNew() // expect: EFF001
let sleep () = Thread.Sleep 10 // expect: EFF001
let delay () = Task.Delay 10 // expect: EFF001
let asyncSleep = Async.Sleep 10 // expect: EFF001
let timeProvider = TimeProvider.System // expect: EFF001
let timer = new PeriodicTimer(TimeSpan.FromSeconds 1.0) // expect: EFF001
