/// Command-line parsing and dispatch. Pure: `Program` performs the output and runs commands.
module Fx.Cli.Cli

open System
open Fx
open Fx.Auth
open Fx.Contracts
open Fx.Envd.Live
open Fx.Host
open Fx.Sqlite
open Fx.Tui.Live
open Fx.Variants

type Outcome =
    {
        ExitCode: int
        Stdout: string
        Stderr: string
    }

/// What `run` asks for.
type RunArgs =
    {
        Prompt: string
        /// `--json`: print the outcome as JSON on standard output.
        Json: bool
        /// `--session <id>`: prompt a session that exists.
        Session: string option
        Title: string option
        /// `--model`, `--surface` and `--variant`, in order: the explicit layer.
        Selections: (string * Json) list
    }

    override this.ToString() =
        let json = if this.Json then " --json" else ""
        $"run{json} ({this.Selections.Length} selections)"

/// What `trust` asks for.
[<RequireQualifiedAccess>]
type TrustArgs =
    /// Trust a directory (`None`: the current one).
    | Add of dir: string option
    | Remove of dir: string option
    | List

    override this.ToString() =
        match this with
        | Add dir -> "trust " + (dir |> Option.defaultValue ".")
        | Remove dir -> "untrust " + (dir |> Option.defaultValue ".")
        | List -> "trust --list"

/// What the command line asks for.
type Command =
    /// Print and exit.
    | Print of Outcome
    /// Serve the environment protocol on standard input and output (`envd --stdio`; hidden: hosts
    /// start it, users do not).
    | Envd of EnvdOptions
    /// Run one prompt headless in the current directory (embedded host).
    | Run of RunArgs
    | Trust of TrustArgs
    | Variants of VariantsView
    /// Log in to model providers, or show the credentials the host uses (`auth …`).
    | Auth of AuthCommand
    /// Run the host and serve the client protocol on its local listener; `Some port`: loopback TCP.
    | Serve of tcp: int option
    /// Replay a recorded session exactly and report the first difference (`replay <session>`).
    | Replay of Fx.Replay.ReplayArgs
    /// List, make or revoke grants as the person running the command (`grants …`).
    | Grants of GrantsCommand
    /// The TUI on a host in this process (`fx` with no command), with the command line's selections.
    | Tui of TuiArgs * selections: (string * Json) list
    /// The TUI of the host `fx serve` runs for this data directory.
    | Attach of TuiArgs

    override this.ToString() =
        match this with
        | Print outcome -> $"print (exit {outcome.ExitCode})"
        | Envd options -> string options
        | Run args -> string args
        | Trust args -> string args
        | Variants view -> "variants " + string view
        | Auth command -> "auth " + string command
        | Serve None -> "serve"
        | Serve(Some port) -> $"serve --tcp {port}"
        | Replay args -> string args
        | Grants command -> "grants " + string command
        | Tui(args, selections) -> $"{args} ({selections.Length} selections)"
        | Attach args -> "attach " + string args

let versionText = $"{Product.Name} {Product.Version}\n"

/// The version and the components built into the binary, for bug reports.
let buildInfo () =
    versionText + $"sqlite {Library.version ()} ({Library.sourceId ()})\n"

let private bin = Product.binaryName

let usage =
    $"{Product.Name} {Product.Version}: exploration stage.\n\n"
    + "Usage:\n"
    + $"  {bin} [--session <id> | --continue] [--title <text>] [--model <provider/model>]\n"
    + "      [--surface cells-only|hybrid] [--variant <slot>=<value>]...\n"
    + "      The interactive interface (TUI) on a host for the current directory, in this process.\n"
    + $"  {bin} attach [--session <id> | --continue]\n"
    + $"      The TUI of the host `{bin} serve` runs.\n"
    + $"  {bin} run -p <prompt> [--json] [--model <provider/model>] [--surface cells-only|hybrid]\n"
    + $"      [--variant <slot>=<value>]... [--session <id>] [--title <text>]\n"
    + "      Runs one prompt in the current directory, headless, and prints how its turn ended.\n"
    + $"  {bin} trust [<dir>] | --remove [<dir>] | --list\n"
    + "      Trusts a project: its config, extensions and setup may run.\n"
    + $"  {bin} variants [list|explain|schema]\n"
    + "      Lists the variant slots, explains their values here, or prints the config schema.\n"
    + $"  {bin} auth login <provider> [--method <kind>] | logout <provider> | list | status\n"
    + "      Stores a model provider's credential, removes it, lists them, or shows how each resolves.\n"
    + $"  {bin} serve [--tcp [<port>]]\n"
    + "      Runs the host for the current directory and serves clients on its local listener.\n"
    + $"  {bin} replay <session> [--json] [--stall <seconds>]\n"
    + "      Replays a recorded session exactly and reports the first difference from the recording.\n"
    + $"  {bin} grants list [--all] | allow|deny <action> <pattern> [--scope host|session:<id>] | revoke <id>\n"
    + "      Lists the grants, or grants or revokes one beforehand (e.g. allow component.install bun@1.4.5).\n"
    + $"  {bin} --help | --version | --build-info\n"

let private envdUsage =
    $"Usage: {bin} envd --stdio [--workspace <dir>] [--state <dir>] [--shell pwsh|powershell|sh] [--retain <bytes>]\n"

let private runUsage =
    $"Usage: {bin} run -p <prompt> [--json] [--model <provider/model>] [--surface cells-only|hybrid] [--variant <slot>=<value>]... [--session <id>] [--title <text>]\n"

let private authUsage =
    "Usage:\n"
    + (AuthCommand.usage
       |> List.map (fun line -> "  " + line + "\n")
       |> String.concat "")

let private grantsUsage =
    "Usage:\n"
    + (GrantsCommand.usage
       |> List.map (fun line -> $"  {bin} {line}\n")
       |> String.concat "")

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

let private tuiUsage =
    $"Usage: {bin} [--session <id> | --continue] [--title <text>] [--model <provider/model>] [--surface cells-only|hybrid] [--variant <slot>=<value>]...\n       {bin} attach [--session <id> | --continue]\n"

/// A selection of the explicit layer from the command line (`--model`, `--surface`, `--variant`).
let private selection (flag: string) (value: string) : Result<string * Json, string> =
    match flag with
    | "--model" ->
        if value.Contains '/' && not (value.StartsWith '/') && not (value.EndsWith '/') then
            Ok((Slots.modelRole "main").Id.Value, Json.String value)
        else
            Error $"--model takes <provider>/<model>, e.g. anthropic/claude-sonnet-5-5; got {value}"
    | "--surface" ->
        match value with
        | "cells-only"
        | "hybrid" -> Ok(Slots.actionSurface.Id.Value, Json.String value)
        | other -> Error $"--surface is cells-only or hybrid, not {other}"
    | _ -> Selections.parseArgument value |> Result.mapError (fun e -> $"--variant: {e}")

/// The TUI's arguments (`fx [options]`), and with `attach` those of `fx attach`.
let private parseTui (attach: bool) (args: string list) : Command =
    let rec loop (tui: TuiArgs) (selections: (string * Json) list) (args: string list) =
        match args with
        | [] when tui.Session.IsSome && tui.Continue -> failure "--session and --continue are one or the other" tuiUsage
        | [] when attach -> Attach tui
        | [] -> Tui(tui, selections)
        | "--session" :: id :: rest when id <> "" && not (id.StartsWith '-') ->
            loop { tui with Session = Some id } selections rest
        | ("-c" | "--continue") :: rest -> loop { tui with Continue = true } selections rest
        | "--title" :: title :: rest when not attach -> loop { tui with Title = Some title } selections rest
        | ("--model" | "--surface" | "--variant" as flag) :: value :: rest when not attach ->
            match selection flag value with
            | Ok chosen -> loop tui (selections @ [ chosen ]) rest
            | Error e -> failure e tuiUsage
        | _ ->
            let given = String.concat " " args
            failure $"unknown arguments: {given}" usage

    loop TuiArgs.defaults [] args

/// `run` arguments.
let private parseRun (args: string list) : Command =
    let rec loop (run: RunArgs) (prompt: string option) (args: string list) =
        match args with
        | [] ->
            match prompt with
            | Some prompt when prompt.Trim() <> "" -> Run { run with Prompt = prompt }
            | _ -> failure "run needs a prompt (-p <prompt>)" runUsage
        | ("-p" | "--prompt") :: text :: rest -> loop run (Some text) rest
        | "--json" :: rest -> loop { run with Json = true } prompt rest
        | "--session" :: id :: rest -> loop { run with Session = Some id } prompt rest
        | "--title" :: title :: rest -> loop { run with Title = Some title } prompt rest
        | "--model" :: model :: rest ->
            if model.Contains '/' && not (model.StartsWith '/') && not (model.EndsWith '/') then
                loop
                    { run with
                        Selections = run.Selections @ [ (Slots.modelRole "main").Id.Value, Json.String model ]
                    }
                    prompt
                    rest
            else
                failure $"--model takes <provider>/<model>, e.g. anthropic/claude-sonnet-5-5; got {model}" runUsage
        | "--surface" :: surface :: rest ->
            match surface with
            | "cells-only"
            | "hybrid" ->
                loop
                    { run with
                        Selections = run.Selections @ [ Slots.actionSurface.Id.Value, Json.String surface ]
                    }
                    prompt
                    rest
            | other -> failure $"--surface is cells-only or hybrid, not {other}" runUsage
        | "--variant" :: selection :: rest ->
            match Selections.parseArgument selection with
            | Ok selection ->
                loop
                    { run with
                        Selections = run.Selections @ [ selection ]
                    }
                    prompt
                    rest
            | Error e -> failure $"--variant: {e}" runUsage
        | other :: _ -> failure $"unknown run argument: {other}" runUsage

    loop
        {
            Prompt = ""
            Json = false
            Session = None
            Title = None
            Selections = []
        }
        None
        args

/// `grants` arguments.
let private parseGrants (args: string list) : Command =
    let granting (effect: Fx.Grants.GrantEffect) (action: string) (pattern: string) (rest: string list) =
        let rec loop (scope: GrantsScope) (reason: string option) (args: string list) =
            match args with
            | [] -> Grants(GrantsCommand.Grant(effect, action, pattern, scope, reason))
            | "--scope" :: "host" :: rest -> loop GrantsScope.Host reason rest
            | "--scope" :: given :: rest when
                given.StartsWith("session:", StringComparison.Ordinal)
                && given.Length > "session:".Length
                ->
                loop (GrantsScope.Session(given.Substring "session:".Length)) reason rest
            | "--scope" :: given :: _ -> failure $"--scope is host or session:<id>, not {given}" grantsUsage
            | "--reason" :: text :: rest when text.Trim() <> "" -> loop scope (Some text) rest
            | other :: _ -> failure $"unknown grants argument: {other}" grantsUsage

        if action.StartsWith '-' || pattern.StartsWith '-' then
            failure "grants allow and deny take an action and a pattern" grantsUsage
        else
            loop GrantsScope.Host None rest

    match args with
    | []
    | [ "list" ] -> Grants(GrantsCommand.List false)
    | [ "list"; "--all" ] -> Grants(GrantsCommand.List true)
    | "allow" :: action :: pattern :: rest -> granting Fx.Grants.GrantEffect.Allow action pattern rest
    | "deny" :: action :: pattern :: rest -> granting Fx.Grants.GrantEffect.Deny action pattern rest
    | [ "revoke"; id ] when not (id.StartsWith '-') -> Grants(GrantsCommand.Revoke(id, None))
    | [ "revoke"; id; "--reason"; reason ] when not (id.StartsWith '-') && reason.Trim() <> "" ->
        Grants(GrantsCommand.Revoke(id, Some reason))
    | [ "-h" ]
    | [ "--help" ] ->
        Print
            {
                ExitCode = 0
                Stdout = grantsUsage
                Stderr = ""
            }
    | _ ->
        let given = String.concat " " args
        failure $"grants takes list, allow, deny or revoke; got: {given}" grantsUsage

let parse (args: string list) : Command =
    match args with
    | [] -> Tui(TuiArgs.defaults, [])
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
    | "run" :: rest -> parseRun rest
    | [ "trust" ] -> Trust(TrustArgs.Add None)
    | [ "trust"; "--list" ] -> Trust TrustArgs.List
    | [ "trust"; "--remove" ] -> Trust(TrustArgs.Remove None)
    | [ "trust"; "--remove"; dir ] -> Trust(TrustArgs.Remove(Some dir))
    | [ "trust"; dir ] when not (dir.StartsWith '-') -> Trust(TrustArgs.Add(Some dir))
    | "trust" :: _ -> failure "trust takes a directory, --remove [<dir>] or --list" usage
    | [ "variants" ]
    | [ "variants"; "list" ] -> Variants VariantsView.List
    | [ "variants"; "explain" ] -> Variants VariantsView.Explain
    | [ "variants"; "schema" ] -> Variants VariantsView.Schema
    | "variants" :: _ -> failure "variants takes list, explain or schema" usage
    | "auth" :: rest ->
        match AuthCommand.parse rest with
        | Ok command -> Auth command
        | Error e -> failure e authUsage
    | [ "serve" ] -> Serve None
    | [ "serve"; "--tcp" ] -> Serve(Some 0)
    | [ "serve"; "--tcp"; port ] ->
        match Int32.TryParse(port, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
        | true, n when n > 0 && n < 65536 -> Serve(Some n)
        | _ -> failure $"invalid port: {port}" usage
    | "serve" :: _ -> failure "serve takes --tcp [<port>]" usage
    | "replay" :: rest ->
        match Fx.Replay.ReplayCommand.parse rest with
        | Ok args -> Replay args
        | Error e -> failure e Fx.Replay.ReplayCommand.usage
    | "grants" :: rest -> parseGrants rest
    | "attach" :: rest -> parseTui true rest
    | first :: _ when first.StartsWith '-' -> parseTui false args
    | other ->
        let given = String.concat " " other
        failure $"unknown arguments: {given}" usage
