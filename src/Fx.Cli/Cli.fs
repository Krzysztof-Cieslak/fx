/// Command-line parsing and dispatch. Pure: `Program` performs the output.
module Fx.Cli.Cli

open Fx
open Fx.Sqlite

type Outcome =
    {
        ExitCode: int
        Stdout: string
        Stderr: string
    }

let versionText = $"{Product.Name} {Product.Version}\n"

/// The version and the components built into the binary, for bug reports.
let buildInfo () =
    versionText + $"sqlite {Library.version ()} ({Library.sourceId ()})\n"

let usage =
    $"{Product.Name} {Product.Version}: exploration stage, nothing usable yet.\n\n"
    + $"Usage: {Product.binaryName} [--help | --version | --build-info]\n"

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
    | [ "--build-info" ] ->
        {
            ExitCode = 0
            Stdout = buildInfo ()
            Stderr = ""
        }
    | other ->
        let given = String.concat " " other

        {
            ExitCode = 2
            Stdout = ""
            Stderr = $"{Product.Name}: unknown arguments: {given}\n\n{usage}"
        }
