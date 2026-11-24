// analyzers.json allows EFF005 (console) in this file only: console use passes, other rules still apply.
let greet () = stdout.WriteLine "hello"
let shout () = System.Console.Error.WriteLine "HELLO"
let stamped () = System.DateTime.UtcNow // expect: EFF001
let formatted = sprintf "%d" 1 // expect: AOT002
