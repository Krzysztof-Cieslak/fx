namespace Fx.Effects.Fakes

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// A shell that answers from a script instead of running anything. `respond` gets every command
/// (for `Spawn`, with the standard input written before it was closed) and returns its outcome, or
/// `None` for a command the script does not expect: that is a defect in the test, and throws.
/// `Calls` records every command in order.
[<Sealed>]
type ScriptedShell(respond: Command -> Result<Output, ShellError> option) =
    let gate = obj ()
    let calls = ResizeArray<Command>()
    let mutable pid = 1000

    let answer (command: Command) =
        lock gate (fun () -> calls.Add command)

        match respond command with
        | Some outcome -> outcome
        | None -> invalidOp $"ScriptedShell: no response scripted for {command.Spec}"

    let run (command: Command) (cancellation: CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<Output, ShellError>> cancellation
        else
            Task.FromResult(answer command)

    let spawn (spec: ProcSpec) (cancellation: CancellationToken) : Task<Result<Proc, ShellError>> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<Proc, ShellError>> cancellation
        else
            let stdin = new MemoryStream()
            let mutable outcome: Result<Output, ShellError> option = None
            let mutable chunks: OutputChunk list = []
            let mutable killed = false

            // The script answers once standard input is complete.
            let settle () =
                lock stdin (fun () ->
                    match outcome with
                    | Some outcome -> outcome
                    | None ->
                        let result = answer { Spec = spec; Stdin = stdin.ToArray() }

                        chunks <-
                            match result with
                            | Ok output ->
                                [
                                    if output.Stdout.Length > 0 then
                                        Stdout output.Stdout
                                    if output.Stderr.Length > 0 then
                                        Stderr output.Stderr
                                ]
                            | Error _ -> []

                        outcome <- Some result
                        result)

            let proc =
                {
                    Pid =
                        lock gate (fun () ->
                            pid <- pid + 1
                            pid)
                    Write =
                        fun bytes _ ->
                            lock stdin (fun () ->
                                if outcome.IsNone then
                                    stdin.Write(bytes, 0, bytes.Length))

                            Task.FromResult()
                    CloseStdin =
                        fun () ->
                            settle () |> ignore
                            Task.FromResult()
                    Read =
                        fun _ ->
                            settle () |> ignore

                            lock stdin (fun () ->
                                match chunks with
                                | chunk :: rest ->
                                    chunks <- rest
                                    Task.FromResult(Some chunk)
                                | [] -> Task.FromResult None)
                    Wait =
                        fun _ ->
                            if lock stdin (fun () -> killed) then
                                Task.FromResult -1
                            else
                                match settle () with
                                | Ok output -> Task.FromResult output.ExitCode
                                | Error _ -> Task.FromResult -1
                    Kill =
                        fun () ->
                            lock stdin (fun () ->
                                if outcome.IsNone then
                                    killed <- true

                                    outcome <-
                                        Some(
                                            Ok
                                                {
                                                    ExitCode = -1
                                                    Stdout = [||]
                                                    Stderr = [||]
                                                }
                                        ))

                            Task.FromResult()
                }

            Task.FromResult(Ok proc)

    member val Shell: Shell = { Run = run; Spawn = spawn }

    /// Every command so far, in order.
    member _.Calls: Command list = lock gate (fun () -> List.ofSeq calls)

module ScriptedShell =
    let private utf8 = UTF8Encoding(false)

    /// An output with UTF-8 text.
    let output (exitCode: int) (stdout: string) (stderr: string) : Output =
        {
            ExitCode = exitCode
            Stdout = utf8.GetBytes stdout
            Stderr = utf8.GetBytes stderr
        }

    /// Exit code 0 with the text on standard output.
    let ok (stdout: string) : Result<Output, ShellError> = Ok(output 0 stdout "")

    /// A shell that answers commands by their program (`Exec` or `Script`), ignoring the rest.
    let ofPrograms (responses: (Program * Result<Output, ShellError>) list) : ScriptedShell =
        ScriptedShell(fun command ->
            responses
            |> List.tryFind (fun (program, _) -> program = command.Spec.Program)
            |> Option.map snd)
