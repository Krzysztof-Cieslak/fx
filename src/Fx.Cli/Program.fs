module Fx.Cli.Program

[<EntryPoint>]
let main argv =
    let outcome = Cli.run (List.ofArray argv)
    stdout.Write outcome.Stdout
    stderr.Write outcome.Stderr
    outcome.ExitCode
