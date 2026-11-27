/// Starting child processes: the imperative shell of the diff.
module Fx.Aot.Diff.Processes

open System
open System.ComponentModel
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Fx.Aot.Diff.Diff

let private create (workdir: string) (file: string) (args: string list) : Process =
    let info =
        ProcessStartInfo(file, UseShellExecute = false, WorkingDirectory = workdir)

    for a in args do
        info.ArgumentList.Add a

    new Process(StartInfo = info)

/// Runs a command to completion and captures its output. `Error` when it cannot start or times out.
let capture (timeout: TimeSpan) (workdir: string) (file: string) (args: string list) : Result<Run, string> =
    use p = create workdir file args
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    p.StartInfo.StandardOutputEncoding <- UTF8Encoding(false)
    p.StartInfo.StandardErrorEncoding <- UTF8Encoding(false)

    try
        p.Start() |> ignore
        let stdout = p.StandardOutput.ReadToEndAsync()
        let stderr = p.StandardError.ReadToEndAsync()

        if p.WaitForExit timeout then
            // A process it started may still hold the pipes: their ends are bounded too.
            if Task.WaitAll([| stdout :> Task; stderr :> Task |], timeout) then
                Ok
                    {
                        ExitCode = p.ExitCode
                        Stdout = stdout.Result
                        Stderr = stderr.Result
                    }
            else
                Error $"its output was still open {timeout.TotalSeconds} s after it exited"
        else
            p.Kill true
            Error $"timed out after {timeout.TotalSeconds} s"
    with :? Win32Exception as e ->
        Error $"cannot start {file}: {e.Message}"

/// Runs a command with inherited stdout and stderr, and returns its exit code.
let passThrough (workdir: string) (file: string) (args: string list) : int =
    use p = create workdir file args
    p.Start() |> ignore
    p.WaitForExit()
    p.ExitCode
