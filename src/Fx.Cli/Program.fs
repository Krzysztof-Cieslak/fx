module Fx.Cli.Program

open System
open Fx.Envd.Live

[<EntryPoint>]
let main argv =
    match Cli.parse (List.ofArray argv) with
    | Cli.Print outcome ->
        stdout.Write outcome.Stdout
        stderr.Write outcome.Stderr
        outcome.ExitCode
    | Cli.Envd options ->
        // Standard output carries the protocol: messages go to standard error only.
        EnvdMain.runStdio options (Console.OpenStandardInput()) (Console.OpenStandardOutput()) (fun line ->
            stderr.WriteLine line)
