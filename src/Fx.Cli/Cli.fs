/// Command-line parsing and dispatch. Pure: `Program` performs the output and runs commands.
module Fx.Cli.Cli

open System
open Fx
open Fx.Envd.Live
open Fx.Sqlite

type Outcome =
    {
        ExitCode: int
        Stdout: string
        Stderr: string
    }

/// What the command line asks for.
type Command =
    /// Print and exit.
    | Print of Outcome
    /// Serve the environment protocol on standard input and output (`envd --stdio`; hidden: hosts
    /// start it, users do not).
    | Envd of EnvdOptions

    override this.ToString() =
        match this with
        | Print outcome -> $"print (exit {outcome.ExitCode})"
        | Envd options -> string options

let versionText = $"{Product.Name} {Product.Version}\n"

/// The version and the components built into the binary, for bug reports.
let buildInfo () =
    versionText + $"sqlite {Library.version ()} ({Library.sourceId ()})\n"

let usage =
    $"{Product.Name} {Product.Version}: exploration stage, nothing usable yet.\n\n"
    + $"Usage: {Product.binaryName} [--help | --version | --build-info]\n"

let private envdUsage =
    $"Usage: {Product.binaryName} envd --stdio [--workspace <dir>] [--state <dir>] [--shell pwsh|powershell|sh] [--retain <bytes>]\n"

let private failure (message: string) (help: string) =
    Print
        {
            ExitCode = 2
            Stdout = ""
            Stderr = $"{Product.Name}: {message}\n\n{help}"
        }

/// `envd` arguments: `--stdio` (the only transport so far) and options.
let private parseEnvd (args: string list) : Command =
    let rec loop (options: EnvdOptions) (stdio: bool) (args: string list) =
        match args with
        | [] when stdio -> Envd options
        | [] -> failure "envd needs --stdio" envdUsage
        | "--stdio" :: rest -> loop options true rest
        | "--workspace" :: dir :: rest -> loop { options with Workspace = Some dir } stdio rest
        | "--state" :: dir :: rest -> loop { options with State = Some dir } stdio rest
        | "--shell" :: shell :: rest -> loop { options with Shell = Some shell } stdio rest
        | "--retain" :: bytes :: rest ->
            match
                Int32.TryParse(bytes, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
            with
            | true, n when n > 0 -> loop { options with Retain = n } stdio rest
            | _ -> failure $"invalid --retain: {bytes}" envdUsage
        | other :: _ -> failure $"unknown envd argument: {other}" envdUsage

    loop EnvdOptions.defaults false args

let parse (args: string list) : Command =
    match args with
    | []
    | [ "-h" ]
    | [ "--help" ] ->
        Print
            {
                ExitCode = 0
                Stdout = usage
                Stderr = ""
            }
    | [ "--version" ] ->
        Print
            {
                ExitCode = 0
                Stdout = versionText
                Stderr = ""
            }
    | [ "--build-info" ] ->
        Print
            {
                ExitCode = 0
                Stdout = buildInfo ()
                Stderr = ""
            }
    | "envd" :: rest -> parseEnvd rest
    | other ->
        let given = String.concat " " other
        failure $"unknown arguments: {given}" usage
