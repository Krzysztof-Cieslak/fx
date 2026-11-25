/// The process entry point (the imperative shell): reads the process's variables and directory once,
/// writes to the console, handles Ctrl+C, and runs what `Cli.parse` asks for.
module Fx.Cli.Program

open System
open System.Collections
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Auth
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

/// Asks the person at the terminal to answer a grant request (what it asks, its evidence; once, for the
/// session, or no); nobody can when input is redirected, and the run denies it once, saying so.
let private answer (request: GrantRequest) : Task<GrantAnswer option> =
    if Console.IsInputRedirected then
        Task.FromResult None
    else
        Task.Run(fun () ->
            lock console (fun () ->
                for line in GrantPrompts.lines request do
                    stderr.WriteLine line

                stderr.Write(GrantPrompts.question request)
                stderr.Flush()

                let reply = stdin.ReadLine() |> Option.ofObj |> Option.defaultValue ""
                Some(GrantPrompts.parse request reply)))

/// Asks the person at the terminal what happened to a call that was running when the host stopped;
/// nobody can when input is redirected.
let private decide (card: Fx.CodeAct.OutcomeUnknown) : Task<Fx.CodeAct.OutcomeAnswer option> =
    if Console.IsInputRedirected then
        show $"  ! {card.Question} (needs a person's decision)"
        Task.FromResult None
    else
        Task.Run(fun () ->
            lock console (fun () ->
                stderr.WriteLine $"? {card.Question}"

                let action = card.Call.Action.Split('\n') |> Array.truncate 8

                for line in action do
                    stderr.WriteLine $"    {line}"

                if card.Evidence.Output <> "" then
                    stderr.WriteLine "  It printed:"

                    for line in card.Evidence.Output.Split('\n') |> Array.rev |> Array.truncate 8 |> Array.rev do
                        stderr.WriteLine $"    {line}"

                for option in card.Options do
                    stderr.WriteLine $"  [{option.Id}] {option.Label}: {option.Detail}"

                let mutable chosen = None
                let mutable asking = true

                while asking do
                    stderr.Write "  Choose one (empty to leave it open): "
                    stderr.Flush()
                    let reply = (stdin.ReadLine() |> Option.ofObj |> Option.defaultValue "").Trim()

                    if reply = "" then
                        asking <- false
                    else
                        match card.Options |> List.tryFind (fun o -> o.Id = reply) with
                        | Some option ->
                            let answer: Fx.CodeAct.OutcomeAnswer = { Option = option.Id; Note = None }
                            chosen <- Some answer
                            asking <- false
                        | None -> ()

                chosen))

/// A line typed at the terminal without echo (a pasted key), or a line of standard input when it is
/// redirected (`… | fx auth login <provider>`).
let private readSecret () : string =
    if Console.IsInputRedirected then
        stdin.ReadLine() |> Option.ofObj |> Option.defaultValue ""
    else
        let text = Text.StringBuilder()
        let mutable reading = true

        while reading do
            let key = Console.ReadKey(true)

            match key.Key with
            | ConsoleKey.Enter -> reading <- false
            | ConsoleKey.Backspace ->
                if text.Length > 0 then
                    text.Length <- text.Length - 1
            | _ ->
                if not (Char.IsControl key.KeyChar) then
                    text.Append key.KeyChar |> ignore

        stderr.WriteLine()
        text.ToString()

/// Runs a login's flow at the terminal (`auth login`): a paste prompt that does not echo, or a device
/// code to enter elsewhere. Browser logins need a loopback listener the command line does not have.
let private runFlow (flow: Fx.Sdk.AuthFlow) (_: CancellationToken) : Task<Result<Fx.Sdk.FlowResult, string>> =
    match flow with
    | Fx.Sdk.AuthFlow.Paste instructions ->
        lock console (fun () ->
            stderr.WriteLine instructions
            stderr.Write "Paste it here (it is not shown): "
            stderr.Flush()

            match (readSecret ()).Trim() with
            | "" -> Task.FromResult(Error "nothing was pasted")
            | text -> Task.FromResult(Ok(Fx.Sdk.FlowResult.Pasted(Fx.Effects.SecretText text))))
    | Fx.Sdk.AuthFlow.DeviceCode(code, uri, _) ->
        show $"Open {uri} and enter the code {code}; waiting for the provider…"
        Task.FromResult(Ok Fx.Sdk.FlowResult.Device)
    | Fx.Sdk.AuthFlow.Browser _ -> Task.FromResult(Error "browser logins are not supported on the command line yet")

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
                        Decide = decide
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

let private auth (command: AuthCommand) : int =
    let facts = facts ()

    match hostOptions facts [] with
    | Error e -> fail [ e ]
    | Ok options ->
        match (Commands.auth options runFlow command).GetAwaiter().GetResult() with
        | Error errors -> fail errors
        | Ok(code, lines) ->
            let out = if code = 0 then stdout else stderr

            for line in lines do
                out.WriteLine line

            code

/// `serve`: the host for the current directory, serving clients until Ctrl+C.
let private serve (tcp: int option) : int =
    let facts = facts ()

    match hostOptions facts [] with
    | Error e -> fail [ e ]
    | Ok options ->
        use interrupt = interrupts ()

        match (Host.start options).GetAwaiter().GetResult() with
        | Error errors -> fail errors
        | Ok host ->
            try
                for warning in host.Warnings do
                    show $"warning: {warning}"

                let listener =
                    match tcp with
                    | Some port -> Fx.Transport.Http.LocalServer.Loopback port
                    | None -> Listening.preferred options.Paths host.Authenticator.Host.Machine

                match (Serve.start host listener).GetAwaiter().GetResult() with
                | Error e -> fail [ e ]
                | Ok served ->
                    show $"serving {options.Workspace} on {served.Listener} (Ctrl+C stops)"

                    try
                        interrupt.Token.WaitHandle.WaitOne() |> ignore
                        0
                    finally
                        (Serve.stop served).Wait(TimeSpan.FromSeconds 30.0) |> ignore
            finally
                (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore

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
    | Cli.Auth command -> auth command
    | Cli.Serve tcp -> serve tcp
