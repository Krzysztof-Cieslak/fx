/// Fx.Pty: processes on pseudo-terminals (PTY on Linux and macOS, ConPTY on Windows). The child
/// programs print `@@key=value` markers; the scenarios print only facts parsed from them, so output
/// does not depend on timing, echo, line endings or the VT sequences ConPTY adds.
module Fx.Aot.Scenarios.Pty

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open Fx.Effects
open Fx.Pty

let private windows = OperatingSystem.IsWindows()

let private utf8 (text: string) = UTF8Encoding(false).GetBytes text

/// Every wait in a scenario is bounded by this.
let private limit () =
    new CancellationTokenSource(TimeSpan.FromSeconds 60.0)

let private terminal () =
    LocalTerminal.create (LocalTerminal.defaultShell ()) (Path.GetTempPath())

let private pwsh (script: string) =
    Exec("pwsh", [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; script ])

/// A POSIX `sh` script, or PowerShell 7 on Windows.
let private program (posix: string) (powershell: string) =
    if windows then pwsh powershell else Script posix

let private spec (program: Program) (size: PtySize) =
    {
        Program = program
        Cwd = None
        Env = Inherit []
        Size = size
    }

/// The value of the last `@@key=value` marker in the output.
let private marker (key: string) (transcript: Transcript) : string =
    let matches = Regex.Matches(transcript.Plain, "@@" + key + "=([^\\s]*)")

    if matches.Count = 0 then
        "<missing>"
    else
        matches[matches.Count - 1].Groups[1].Value

let private line (label: string) (value: string) = label + " = " + value

let private has (key: string) (transcript: Transcript) = transcript.Plain.Contains("@@" + key)

/// Runs `body` with a terminal process, closing it afterwards.
let private session
    (spec: PtySpec)
    (body: Pty -> Transcript -> CancellationToken -> Threading.Tasks.Task<string list>)
    =
    task {
        use limit = limit ()

        match! (terminal ()).Spawn spec limit.Token with
        | Error e -> return [ $"spawn = {e}" ]
        | Ok pty ->
            try
                let! lines = body pty (Transcript pty) limit.Token
                return "spawn = ok" :: lines
            finally
                pty.Close().Wait()
    }

let private waitFor (transcript: Transcript) (key: string) (cancellation: CancellationToken) =
    transcript.ReadUntil (fun text -> text.Contains("@@" + key)) cancellation

let basics =
    Scenario.task "pty-basics" (fun () ->
        let program =
            program
                "echo \"@@size=$(stty size | tr ' ' x)\"; read line; echo \"@@got=$line\"; exit 3"
                "Write-Host \"@@size=$([Console]::WindowHeight)x$([Console]::WindowWidth)\"; $line = [Console]::ReadLine(); Write-Host \"@@got=$line\"; exit 3"

        session (spec program { Cols = 100; Rows = 30 }) (fun pty transcript cancellation ->
            task {
                let! _ = waitFor transcript "size" cancellation
                do! pty.Write (utf8 "hello\r") cancellation
                do! transcript.ReadToEnd cancellation
                let! code = pty.Wait cancellation

                return
                    [
                        line "size" (marker "size" transcript)
                        line "input" (marker "got" transcript)
                        $"exit code = {code}"
                        $"output ended = {transcript.Ended}"
                    ]
            }))

let resize =
    Scenario.task "pty-resize" (fun () ->
        let program =
            program
                "trap 'echo \"@@winch=$(stty size | tr \" \" x)\"' WINCH; echo @@ready; while :; do if read line; then [ \"$line\" = done ] && break; fi; done; echo \"@@final=$(stty size | tr ' ' x)\""
                "Write-Host '@@ready'; $null = [Console]::ReadLine(); Write-Host \"@@final=$([Console]::WindowHeight)x$([Console]::WindowWidth)\""

        session (spec program PtySize.standard) (fun pty transcript cancellation ->
            task {
                let! _ = waitFor transcript "ready" cancellation
                do! pty.Resize { Cols = 132; Rows = 43 }
                do! pty.Write (utf8 "done\r") cancellation
                do! transcript.ReadToEnd cancellation
                let! code = pty.Wait cancellation

                return
                    [
                        line "final size" (marker "final" transcript)
                        if not windows then
                            line "sigwinch size" (marker "winch" transcript)
                        $"exit code = {code}"
                    ]
            }))

let interrupt =
    Scenario.task "pty-interrupt" (fun () ->
        let program =
            program
                "echo @@ready; sleep 30; echo @@after"
                "Write-Host '@@ready'; Start-Sleep -Seconds 30; Write-Host '@@after'"

        session (spec program PtySize.standard) (fun pty transcript cancellation ->
            task {
                let! _ = waitFor transcript "ready" cancellation
                do! pty.Write [| 3uy |] cancellation
                let! code = pty.Wait cancellation
                do! transcript.ReadToEnd cancellation

                return
                    [
                        line "interrupted" (string (not (has "after" transcript)))
                        // ConPTY turns Ctrl+C into CTRL_C_EVENT; the exit code is PowerShell's.
                        if not windows then
                            $"exit code = {code}"
                    ]
            }))

let kill =
    Scenario.task "pty-kill" (fun () ->
        let program =
            program "echo @@ready; sleep 30" "Write-Host '@@ready'; Start-Sleep -Seconds 30"

        session (spec program PtySize.standard) (fun pty transcript cancellation ->
            task {
                let! _ = waitFor transcript "ready" cancellation
                do! pty.Kill()
                let! code = pty.Wait cancellation
                do! transcript.ReadToEnd cancellation
                return [ $"exit code = {code}"; $"output ended = {transcript.Ended}" ]
            }))

/// Unix only: what makes a terminal session real (controlling terminal, clean signal state).
let unixSession =
    Scenario.task "pty-unix-session" (fun () ->
        task {
            // /dev/tty opens only in a process that has a controlling terminal.
            let! ctty =
                session
                    (spec (Script "echo @@ctty=yes > /dev/tty") PtySize.standard)
                    (fun pty transcript cancellation ->
                        task {
                            do! transcript.ReadToEnd cancellation
                            let! code = pty.Wait cancellation

                            return
                                [
                                    line "controlling terminal" (marker "ctty" transcript)
                                    $"exit code = {code}"
                                ]
                        })

            // The runtime ignores SIGPIPE; a terminal session starts with every signal at its default.
            let! sigpipe =
                session
                    (spec (Script "kill -PIPE $$; echo @@survived=yes") PtySize.standard)
                    (fun pty transcript cancellation ->
                        task {
                            do! transcript.ReadToEnd cancellation
                            let! code = pty.Wait cancellation

                            return
                                [
                                    line "survived SIGPIPE" (string (has "survived" transcript))
                                    $"exit code = {code}"
                                ]
                        })

            let! env =
                session
                    { spec (Script "echo \"@@var=$PTY_VAR\"; echo \"@@home=${HOME-unset}\"") PtySize.standard with
                        Env = Exactly [ "PATH", "/usr/bin:/bin"; "PTY_VAR", "a-b" ]
                    }
                    (fun _ transcript cancellation ->
                        task {
                            do! transcript.ReadToEnd cancellation
                            return [ line "var" (marker "var" transcript); line "home" (marker "home" transcript) ]
                        })

            return ctty @ sigpipe @ env
        })
    |> Scenario.except [ Os.Windows ]

/// Windows only: PowerShell 7, the Windows PowerShell fallback and cmd on ConPTY.
let shells =
    Scenario.task "pty-windows-shells" (fun () ->
        task {
            let mutable lines = []

            for name, program in
                [
                    "pwsh",
                    Exec(
                        "pwsh",
                        [
                            "-NoLogo"
                            "-NoProfile"
                            "-Command"
                            "Write-Host \"@@v=$($PSVersionTable.PSVersion.Major)\""
                        ]
                    )
                    "powershell",
                    Exec(
                        "powershell",
                        [
                            "-NoLogo"
                            "-NoProfile"
                            "-Command"
                            "Write-Host \"@@v=$($PSVersionTable.PSVersion.Major)\""
                        ]
                    )
                    "cmd", Exec("cmd", [ "/d"; "/c"; "echo @@v=cmd" ])
                ] do
                let! result =
                    session (spec program PtySize.standard) (fun pty transcript cancellation ->
                        task {
                            do! transcript.ReadToEnd cancellation
                            let! code = pty.Wait cancellation
                            return [ line name (marker "v" transcript + $", exit code {code}") ]
                        })

                lines <- lines @ result

            return lines
        })
    |> Scenario.onlyOn [ Os.Windows ]

let notFound =
    Scenario.task "pty-not-found" (fun () ->
        task {
            use limit = limit ()

            match! (terminal ()).Spawn (PtySpec.exec "no-such-program-for-pty-scenarios" []) limit.Token with
            | Error(NotStarted(program, _)) -> return [ $"not started = {program}" ]
            | Error e -> return [ $"unexpected = {e}" ]
            | Ok pty ->
                do! pty.Close()
                return [ "started" ]
        })

let scenarios = [ basics; resize; interrupt; kill; unixSession; shells; notFound ]
