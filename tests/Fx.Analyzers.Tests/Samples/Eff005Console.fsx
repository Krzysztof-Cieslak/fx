// EFF005: console I/O outside Eff; only the entry point writes to the console.
let hello () = printfn "hello" // expect: EFF005
let oops () = eprintfn "oops" // expect: EFF005
let out () = stdout.WriteLine "hello" // expect: EFF005
let err () = stderr.Write "oops" // expect: EFF005
let line () = stdin.ReadLine() // expect: EFF005
let console () = System.Console.WriteLine "hello" // expect: EFF005
let viaModule () = Printf.printf "x" // expect: EFF005
