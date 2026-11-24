/// Fx.Pty: processes on pseudo-terminals (PTY on Linux and macOS, ConPTY on Windows). The child
/// programs print `@@key=value` markers; the scenarios print only facts parsed from them, so output
/// does not depend on timing, echo, line endings or the VT sequences ConPTY adds.
///
/// Each fact is also checked against the value it must have on this platform: a terminal that
/// behaves wrongly in the same way on the JIT and natively would otherwise pass the diff. A wrong
/// value fails the scenario with the terminal's text.
module Fx.Aot.Scenarios.Pty

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
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

/// The output and exit code of one terminal session, for checking facts.
type private Session =
    {
        Transcript: Transcript
        ExitCode: int
    }

    /// The value of the last `@@key=value` marker.
    member s.Marker(key: string) : string =
        let matches = Regex.Matches(s.Transcript.Plain, "@@" + key + "=([^\\s]*)")

        if matches.Count = 0 then
            "<missing>"
        else
            matches[matches.Count - 1].Groups[1].Value

    member s.Has(key: string) = s.Transcript.Plain.Contains("@@" + key)

    /// `label = actual`, or a failure showing the terminal's text if it is not `expected`.
    member s.Fact (label: string) (expected: string) (actual: string) : string =
        if actual <> expected then
            let text = Show.escape s.Transcript.Plain
            failwith $"{label}: expected {expected}, got {actual}; terminal text: {text}"

        label + " = " + actual

    override s.ToString() = $"Session(exit {s.ExitCode})"

/// Runs a terminal process: `interact` drives it, then the output is read to its end, and the
/// terminal is closed.
let private run (spec: PtySpec) (interact: Pty -> Transcript -> CancellationToken -> Task<unit>) : Task<Session> =
    task {
        use limit = limit ()

        match! (terminal ()).Spawn spec limit.Token with
        | Error e -> return failwith $"spawn: {e}"
        | Ok pty ->
            try
                let transcript = Transcript pty
                do! interact pty transcript limit.Token
                do! transcript.ReadToEnd limit.Token
                let! code = pty.Wait limit.Token

                return
                    {
                        Transcript = transcript
                        ExitCode = code
                    }
            finally
                pty.Close().Wait()
    }

let private noInput (_: Pty) (_: Transcript) (_: CancellationToken) = Task.FromResult()

/// Waits for a marker; a missing one shows up as a wrong fact afterwards.
let private waitFor (transcript: Transcript) (key: string) (cancellation: CancellationToken) =
    task {
        let! _ = transcript.ReadUntil (fun text -> text.Contains("@@" + key)) cancellation
        return ()
    }

let private killedExitCode = if windows then "-1" else "137"

let basics =
    Scenario.task "pty-basics" (fun () ->
        task {
            let program =
                program
                    "echo \"@@size=$(stty size | tr ' ' x)\"; read line; echo \"@@got=$line\"; exit 3"
                    "Write-Host \"@@size=$([Console]::WindowHeight)x$([Console]::WindowWidth)\"; $line = [Console]::ReadLine(); Write-Host \"@@got=$line\"; exit 3"

            let! s =
                run (spec program { Cols = 100; Rows = 30 }) (fun pty transcript cancellation ->
                    task {
                        do! waitFor transcript "size" cancellation
                        do! pty.Write (utf8 "hello\r") cancellation
                    })

            return
                [
                    s.Fact "size" "30x100" (s.Marker "size")
                    s.Fact "input" "hello" (s.Marker "got")
                    s.Fact "exit code" "3" (string s.ExitCode)
                    s.Fact "output ended" "True" (string s.Transcript.Ended)
                ]
        })

let resize =
    Scenario.task "pty-resize" (fun () ->
        task {
            let program =
                program
                    "trap 'echo \"@@winch=$(stty size | tr \" \" x)\"' WINCH; echo @@ready; while :; do if read line; then [ \"$line\" = done ] && break; fi; done; echo \"@@final=$(stty size | tr ' ' x)\""
                    "Write-Host '@@ready'; $null = [Console]::ReadLine(); Write-Host \"@@final=$([Console]::WindowHeight)x$([Console]::WindowWidth)\""

            let! s =
                run (spec program PtySize.standard) (fun pty transcript cancellation ->
                    task {
                        do! waitFor transcript "ready" cancellation
                        do! pty.Resize { Cols = 132; Rows = 43 }
                        do! pty.Write (utf8 "done\r") cancellation
                    })

            return
                [
                    s.Fact "final size" "43x132" (s.Marker "final")
                    if not windows then
                        s.Fact "sigwinch size" "43x132" (s.Marker "winch")
                    s.Fact "exit code" "0" (string s.ExitCode)
                ]
        })

let interrupt =
    Scenario.task "pty-interrupt" (fun () ->
        task {
            let program =
                program
                    "echo @@ready; sleep 30; echo @@after"
                    "Write-Host '@@ready'; Start-Sleep -Seconds 30; Write-Host '@@after'"

            let! s =
                run (spec program PtySize.standard) (fun pty transcript cancellation ->
                    task {
                        do! waitFor transcript "ready" cancellation
                        do! pty.Write [| 3uy |] cancellation
                    })

            return
                [
                    s.Fact "interrupted" "True" (string (not (s.Has "after")))
                    // ConPTY turns Ctrl+C into CTRL_C_EVENT; the exit code is PowerShell's choice.
                    if not windows then
                        s.Fact "exit code" "130" (string s.ExitCode)
                ]
        })

let kill =
    Scenario.task "pty-kill" (fun () ->
        task {
            let program =
                program "echo @@ready; sleep 30" "Write-Host '@@ready'; Start-Sleep -Seconds 30"

            let! s =
                run (spec program PtySize.standard) (fun pty transcript cancellation ->
                    task {
                        do! waitFor transcript "ready" cancellation
                        do! pty.Kill()
                    })

            return
                [
                    s.Fact "exit code" killedExitCode (string s.ExitCode)
                    s.Fact "output ended" "True" (string s.Transcript.Ended)
                ]
        })

/// Linux and macOS: what makes a terminal session real (controlling terminal, clean signal state).
let unixSession =
    Scenario.task "pty-unix-session" (fun () ->
        task {
            // /dev/tty opens only in a process that has a controlling terminal.
            let! ctty = run (spec (Script "echo @@ctty=yes > /dev/tty") PtySize.standard) noInput

            // The runtime ignores SIGPIPE; a terminal session starts with every signal at its default.
            let! sigpipe =
                run (spec (Script "kill -PIPE $$; echo @@survived") PtySize.standard) noInput

            let! env =
                run
                    { spec (Script "echo \"@@var=$PTY_VAR\"; echo \"@@home=${HOME-unset}\"") PtySize.standard with
                        Env = Exactly [ "PATH", "/usr/bin:/bin"; "PTY_VAR", "a-b" ]
                    }
                    noInput

            return
                [
                    ctty.Fact "controlling terminal" "yes" (ctty.Marker "ctty")
                    ctty.Fact "exit code" "0" (string ctty.ExitCode)
                    sigpipe.Fact "survived SIGPIPE" "False" (string (sigpipe.Has "survived"))
                    sigpipe.Fact "exit code" "141" (string sigpipe.ExitCode)
                    env.Fact "var" "a-b" (env.Marker "var")
                    env.Fact "home" "unset" (env.Marker "home")
                ]
        })
    |> Scenario.except [ Os.Windows ]

/// Windows: PowerShell 7, the Windows PowerShell fallback and cmd on ConPTY.
let windowsShells =
    Scenario.task "pty-windows-shells" (fun () ->
        task {
            let version (file: string) =
                Exec(
                    file,
                    [
                        "-NoLogo"
                        "-NoProfile"
                        "-Command"
                        "Write-Host \"@@v=$($PSVersionTable.PSVersion.Major)\""
                    ]
                )

            let mutable lines = []

            for name, program, expected in
                [
                    "pwsh", version "pwsh", "7"
                    "powershell", version "powershell", "5"
                    "cmd", Exec("cmd", [ "/d"; "/c"; "echo @@v=cmd" ]), "cmd"
                ] do
                let! s = run (spec program PtySize.standard) noInput

                lines <-
                    lines
                    @ [
                        s.Fact $"{name} version" expected (s.Marker "v")
                        s.Fact $"{name} exit code" "0" (string s.ExitCode)
                    ]

            return lines
        })
    |> Scenario.onlyOn [ Os.Windows ]

let notFound =
    Scenario.task "pty-not-found" (fun () ->
        task {
            use limit = limit ()

            match! (terminal ()).Spawn (PtySpec.exec "no-such-program-for-pty-scenarios" []) limit.Token with
            | Error(NotStarted(program, _)) -> return [ $"not started = {program}" ]
            | Error e -> return failwith $"unexpected error: {e}"
            | Ok pty ->
                do! pty.Close()
                return failwith "started a program that does not exist"
        })

let scenarios =
    [ basics; resize; interrupt; kill; unixSession; windowsShells; notFound ]
