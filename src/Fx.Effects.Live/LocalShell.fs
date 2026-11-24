namespace Fx.Effects.Live

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Fx.Effects

/// Runs processes on this machine through pipes. The only file that starts processes.
module LocalShell =
    /// The default shell for `Script` programs here: PowerShell 7 on Windows, `/bin/sh` elsewhere.
    /// Which shell to use is a variant (spec §15); this is only the bootstrap default.
    let defaultShell () : ShellSpec =
        if OperatingSystem.IsWindows() then
            ShellSpec.pwsh
        else
            ShellSpec.posix

    let private startInfo (shell: ShellSpec) (defaultCwd: string) (spec: ProcSpec) : ProcessStartInfo =
        let file, args =
            match spec.Program with
            | Exec(file, args) -> file, args
            | Script script -> ShellSpec.invocation shell script

        let info =
            ProcessStartInfo(
                file,
                WorkingDirectory = Option.defaultValue defaultCwd spec.Cwd,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            )

        for arg in args do
            info.ArgumentList.Add arg

        match spec.Env with
        | Inherit changes ->
            for name, value in changes do
                match value with
                | Some value -> info.Environment[name] <- value
                | None -> info.Environment.Remove name |> ignore
        | Exactly vars ->
            info.Environment.Clear()

            for name, value in vars do
                info.Environment[name] <- value

        info

    let private start (info: ProcessStartInfo) : Result<Process, ShellError> =
        let notStarted (reason: string) =
            Error(NotStarted(info.FileName, reason))

        try
            match Process.Start info with
            | null -> notStarted "the process did not start"
            | proc -> Ok proc
        with
        | :? Win32Exception as e -> notStarted e.Message
        | :? IOException as e -> notStarted e.Message
        | :? InvalidOperationException as e -> notStarted e.Message

    /// Copies a stream into the channel until it ends.
    let private pump (stream: Stream) (chunk: byte array -> OutputChunk) (output: ChannelWriter<OutputChunk>) =
        task {
            let buffer = Array.zeroCreate<byte> 16384
            let mutable reading = true

            while reading do
                let! count = stream.ReadAsync(buffer, 0, buffer.Length)

                if count = 0 then
                    reading <- false
                else
                    do! output.WriteAsync(chunk (Array.sub buffer 0 count))
        }

    let private kill (proc: Process) =
        try
            proc.Kill(entireProcessTree = true)
        with
        | :? InvalidOperationException
        | :? Win32Exception
        | :? NotSupportedException -> ()

    let private ofProcess (proc: Process) : Proc =
        let pid = proc.Id
        let stdin = proc.StandardInput.BaseStream

        let output =
            Channel.CreateBounded<OutputChunk>(BoundedChannelOptions(64, SingleReader = true, SingleWriter = false))

        let pumps =
            task {
                try
                    let! _ =
                        Task.WhenAll(
                            pump proc.StandardOutput.BaseStream Stdout output.Writer,
                            pump proc.StandardError.BaseStream Stderr output.Writer
                        )

                    output.Writer.TryComplete() |> ignore
                with e ->
                    output.Writer.TryComplete e |> ignore
            }

        let exited =
            task {
                do! proc.WaitForExitAsync()
                return proc.ExitCode
            }

        // Release the process handle once it has exited and its output has been read.
        task {
            let! _ = exited
            do! pumps
            proc.Dispose()
        }
        |> ignore

        {
            Pid = pid
            Write =
                fun bytes cancellation ->
                    task {
                        try
                            do! stdin.WriteAsync(bytes, 0, bytes.Length, cancellation)
                            do! stdin.FlushAsync cancellation
                        with :? IOException ->
                            // The process stopped reading (e.g. it exited): drop the input, as shells do.
                            ()
                    }
            CloseStdin =
                fun () ->
                    try
                        stdin.Dispose()
                    with :? IOException ->
                        ()

                    Task.FromResult()
            Read =
                fun cancellation ->
                    task {
                        let mutable chunk = None
                        let mutable waiting = true

                        while waiting do
                            match output.Reader.TryRead() with
                            | true, next ->
                                chunk <- Some next
                                waiting <- false
                            | false, _ ->
                                let! more = output.Reader.WaitToReadAsync cancellation
                                waiting <- more

                        return chunk
                    }
            Wait = fun cancellation -> exited.WaitAsync cancellation
            Kill =
                fun () ->
                    if not exited.IsCompleted then
                        kill proc

                    Task.FromResult()
        }

    /// A shell that runs `Script` programs with `shell` and starts processes in `cwd` unless a
    /// command says otherwise.
    let create (shell: ShellSpec) (cwd: string) : Shell =
        {
            Run =
                fun command cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<Result<Output, ShellError>> cancellation
                    else
                        match start (startInfo shell cwd command.Spec) with
                        | Error e -> Task.FromResult(Error e)
                        | Ok proc ->
                            task {
                                let! output = Proc.collect (ofProcess proc) command.Stdin cancellation
                                return Ok output
                            }
            Spawn =
                fun spec cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<Result<Proc, ShellError>> cancellation
                    else
                        Task.FromResult(start (startInfo shell cwd spec) |> Result.map ofProcess)
        }
