/// The process entry point (the imperative shell): reads the process's variables and directory once,
/// writes to the console, handles Ctrl+C, and runs what `Cli.parse` asks for.
module Fx.Cli.Program

open System
open System.Collections
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Envd.Live
open Fx.Grants
open Fx.Host

let private console = obj ()

/// A line on standard error, whole, whoever writes it.
let private show (line: string) =
    lock console (fun () -> stderr.WriteLine line)

/// What the entry point reads from the process, once.
let private facts () : ProcessFacts =
    let variables =
        [
            for entry in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> do
                match entry.Key, entry.Value with
                | (:? string as name), (:? string as value) -> name, value
                | _ -> ()
        ]
        |> List.sortBy fst

    let processPath =
        Environment.ProcessPath
        |> Option.ofObj
        |> Option.defaultValue Product.binaryName

    // On the JIT the process is `dotnet` running this assembly: envd starts the same way.
    let jit =
        String.Equals(Path.GetFileNameWithoutExtension processPath, "dotnet", StringComparison.OrdinalIgnoreCase)

    {
        Variables = variables
        CurrentDirectory = Directory.GetCurrentDirectory()
        Binary = processPath
        BinaryArgs =
            if jit then
                [ Path.Combine(AppContext.BaseDirectory, Product.binaryName + ".dll") ]
            else
                []
    }

/// Asks the person at the terminal to answer a grant request; nobody can when input is redirected.
let private answer (request: GrantRequest) : Task<GrantAnswer option> =
    if Console.IsInputRedirected then
        show $"  ! {request.Summary}: needs a person's approval ({request.Action} {request.Resource}); denied once"
        Task.FromResult None
    else
        Task.Run(fun () ->
            lock console (fun () ->
                stderr.WriteLine $"? {request.Summary}"
                stderr.WriteLine $"  {request.Action} {request.Resource}"
                stderr.Write "  Allow? [y] once, [a] always on this host, [N] no: "
                stderr.Flush()

                let reply =
                    (stdin.ReadLine() |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant()

                let allow (scope: ScopeChoice) =
                    Some
                        {
                            Effect = GrantEffect.Allow
                            Scope = scope
                            Resource = None
                            Expires = None
                            Reason = None
                        }

                match reply with
                | "y"
                | "yes" -> allow ScopeChoice.Once
                | "a"
                | "always" -> allow ScopeChoice.Host
                | _ ->
                    Some
                        {
                            Effect = GrantEffect.Deny
                            Scope = ScopeChoice.Once
                            Resource = None
                            Expires = None
                            Reason = Some "denied at the terminal"
                        }))

/// Ctrl+C: the first press interrupts (the token), the second exits.
let private interrupts () : CancellationTokenSource =
    let source = new CancellationTokenSource()

    Console.CancelKeyPress.Add(fun args ->
        if source.IsCancellationRequested then
            show "interrupted twice: exiting"
            exit 130
        else
            args.Cancel <- true
            show "interrupting… (Ctrl+C again to exit)"
            source.Cancel())

    source

let private fail (lines: string list) : int =
    for line in lines do
        show $"{Product.Name}: {line}"

    1

let private hostOptions (facts: ProcessFacts) (selections: (string * Json) list) : Result<HostOptions, string> =
    Live.options facts facts.CurrentDirectory selections

let private run (args: Cli.RunArgs) : int =
    let facts = facts ()

    match hostOptions facts args.Selections with
    | Error e -> fail [ e ]
    | Ok options ->
        use interrupt = interrupts ()

        match (Host.start options).GetAwaiter().GetResult() with
        | Error errors -> fail errors
        | Ok host ->
            try
                for warning in host.Warnings do
                    show $"warning: {warning}"

                let io: RunIo =
                    {
                        Show = show
                        Answer = answer
                        Interrupt = interrupt.Token
                    }

                let request: RunRequest =
                    {
                        Prompt = args.Prompt
                        Session = args.Session
                        Title = args.Title
                    }

                match (Headless.run host request io CancellationToken.None).GetAwaiter().GetResult() with
                | Error e -> fail [ e ]
                | Ok outcome ->
                    if args.Json then
                        stdout.WriteLine(Json.serialize (Codec.encode Headless.outcomeCodec outcome))
                    else
                        for line in Activity.ending outcome.Questions outcome.Ending do
                            stdout.WriteLine line

                        show
                            $"session {outcome.Session}: continue with `{Product.binaryName} run --session {outcome.Session} -p …`"

                    Headless.exitCode outcome
            finally
                (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore

let private printed (result: Result<string, string>) : int =
    match result with
    | Ok text ->
        stdout.Write(
            if text.EndsWith '\n' || text = "" then
                text
            else
                text + "\n"
        )

        0
    | Error e -> fail [ e ]

let private trust (args: Cli.TrustArgs) : int =
    let facts = facts ()

    match hostOptions facts [] with
    | Error e -> fail [ e ]
    | Ok options ->
        let dir (given: string option) =
            Path.GetFullPath(given |> Option.defaultValue ".", facts.CurrentDirectory)

        let result =
            match args with
            | Cli.TrustArgs.Add given -> Commands.trust options (dir given)
            | Cli.TrustArgs.Remove given -> Commands.untrust options (dir given)
            | Cli.TrustArgs.List -> Commands.trusted options

        printed (result.GetAwaiter().GetResult())

let private variants (view: VariantsView) : int =
    let facts = facts ()

    match hostOptions facts [] with
    | Error e -> fail [ e ]
    | Ok options ->
        match (Commands.variants options view).GetAwaiter().GetResult() with
        | Ok text ->
            stdout.Write text
            0
        | Error errors -> fail errors

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
    | Cli.Run args -> run args
    | Cli.Trust args -> trust args
    | Cli.Variants view -> variants view
