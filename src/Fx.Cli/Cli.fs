/// Command-line parsing and dispatch. Pure: `Program` performs the output.
module Fx.Cli.Cli

open Fx

type Outcome =
    {
        ExitCode: int
        Stdout: string
        Stderr: string
    }

let versionText = $"{Product.Name} {Product.Version}\n"

let usage =
    $"{Product.Name} {Product.Version}: exploration stage, nothing usable yet.\n\n"
    + $"Usage: {Product.binaryName} [--help | --version]\n"

let run (args: string list) : Outcome =
    match args with
    | []
    | [ "-h" ]
    | [ "--help" ] ->
        {
            ExitCode = 0
            Stdout = usage
            Stderr = ""
        }
    | [ "--version" ] ->
        {
            ExitCode = 0
            Stdout = versionText
            Stderr = ""
        }
    | other ->
        let given = String.concat " " other

        {
            ExitCode = 2
            Stdout = ""
            Stderr = $"{Product.Name}: unknown arguments: {given}\n\n{usage}"
        }
