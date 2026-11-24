module Fx.Pty.Tests.LocalTerminalTests

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx.Effects
open Fx.Pty

let private windows = OperatingSystem.IsWindows()

/// Every wait is bounded, so a hang fails the test instead of the job.
let private timeout = TimeSpan.FromSeconds 60.0

let private utf8 (text: string) = UTF8Encoding(false).GetBytes text

let private sync (task: Threading.Tasks.Task<'T>) =
    task.WaitAsync(timeout).GetAwaiter().GetResult()

type private Env =
    {
        Terminal: Terminal
        Scope: Scope
    }

    interface ITerminal with
        member env.Terminal = env.Terminal

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

let private withDirectory (body: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), "pty-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "")
        body dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private terminal (dir: string) =
    LocalTerminal.create (LocalTerminal.defaultShell ()) dir

/// PowerShell 7, with a script; on Windows tests use it instead of `/bin/sh`.
let private pwsh (script: string) =
    Exec("pwsh", [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; script ])

/// A program for the platform: a POSIX `sh` script, or PowerShell 7 on Windows.
let private program (posix: string) (powershell: string) =
    if windows then pwsh powershell else Script posix

let private spec (program: Program) (size: PtySize) =
    {
        Program = program
        Cwd = None
        Env = Inherit []
        Size = size
    }

let private spawn (dir: string) (spec: PtySpec) =
    match sync ((terminal dir).Spawn spec CancellationToken.None) with
    | Ok pty -> pty
    | Error e -> failtest (string e)

/// Runs `body` with a terminal process and its transcript, and closes the terminal afterwards.
let private session (spec: PtySpec) (body: Pty -> Transcript -> unit) =
    withDirectory (fun dir ->
        let pty = spawn dir spec

        try
            body pty (Transcript pty)
        finally
            sync (pty.Close()))

let private waitFor (transcript: Transcript) (marker: string) =
    let found =
        sync (transcript.ReadUntil (fun text -> text.Contains marker) CancellationToken.None)

    Expect.isTrue found $"saw {marker} in:\n{transcript.Plain}"

let private readToEnd (transcript: Transcript) =
    sync (transcript.ReadToEnd CancellationToken.None)

/// Enter, as a keyboard sends it (the terminal turns it into a line feed for the program).
let private enter = "\r"

[<Tests>]
let tests =
    testList
        "LocalTerminal"
        [
            test "starts with the given size, takes input and returns the exit code" {
                let program =
                    program
                        "echo \"@@size=$(stty size | tr ' ' x)\"; read line; echo \"@@got=$line\"; exit 3"
                        "Write-Host \"@@size=$([Console]::WindowHeight)x$([Console]::WindowWidth)\"; $line = [Console]::ReadLine(); Write-Host \"@@got=$line\"; exit 3"

                session (spec program { Cols = 100; Rows = 30 }) (fun pty transcript ->
                    waitFor transcript "@@size=30x100"
                    sync (pty.Write (utf8 ("hello" + enter)) CancellationToken.None)
                    readToEnd transcript
                    Expect.stringContains transcript.Plain "@@got=hello" "input reached the program"
                    Expect.equal (sync (pty.Wait CancellationToken.None)) 3 "exit code")
            }

            test "resizing changes the size the program sees" {
                let program =
                    program
                        "trap 'echo \"@@winch=$(stty size | tr \" \" x)\"' WINCH; echo @@ready; while :; do if read line; then [ \"$line\" = done ] && break; fi; done; echo \"@@final=$(stty size | tr ' ' x)\""
                        "Write-Host '@@ready'; $null = [Console]::ReadLine(); Write-Host \"@@final=$([Console]::WindowHeight)x$([Console]::WindowWidth)\""

                session (spec program PtySize.standard) (fun pty transcript ->
                    waitFor transcript "@@ready"
                    sync (pty.Resize { Cols = 120; Rows = 40 })
                    sync (pty.Write (utf8 ("done" + enter)) CancellationToken.None)
                    readToEnd transcript
                    Expect.stringContains transcript.Plain "@@final=40x120" "new size"

                    if not windows then
                        Expect.stringContains transcript.Plain "@@winch=40x120" "SIGWINCH reached the program"

                    Expect.equal (sync (pty.Wait CancellationToken.None)) 0 "exit code")
            }

            test "the terminal is the controlling terminal of a new session" {
                if not windows then
                    // Opening /dev/tty fails with ENXIO in a process without a controlling terminal.
                    session (spec (Script "echo @@ctty > /dev/tty") PtySize.standard) (fun pty transcript ->
                        readToEnd transcript
                        Expect.stringContains transcript.Plain "@@ctty" "/dev/tty opens"
                        Expect.equal (sync (pty.Wait CancellationToken.None)) 0 "exit code")
            }

            test "Ctrl+C interrupts the foreground program" {
                let program =
                    program
                        "echo @@ready; sleep 30; echo @@after"
                        "Write-Host '@@ready'; Start-Sleep -Seconds 30; Write-Host '@@after'"

                session (spec program PtySize.standard) (fun pty transcript ->
                    waitFor transcript "@@ready"
                    sync (pty.Write [| 3uy |] CancellationToken.None)
                    let code = sync (pty.Wait CancellationToken.None)
                    readToEnd transcript
                    Expect.isFalse (transcript.Plain.Contains "@@after") "interrupted"

                    if not windows then
                        Expect.equal code 130 "killed by SIGINT")
            }

            test "signals start at their default actions" {
                if not windows then
                    // The .NET runtime ignores SIGPIPE; a terminal session must not inherit that.
                    session (spec (Script "kill -PIPE $$; echo @@survived") PtySize.standard) (fun pty transcript ->
                        readToEnd transcript
                        Expect.isFalse (transcript.Plain.Contains "@@survived") "SIGPIPE killed the shell"
                        Expect.equal (sync (pty.Wait CancellationToken.None)) 141 "killed by SIGPIPE")
            }

            test "kill ends the program and its output" {
                let program =
                    program "echo @@ready; sleep 30" "Write-Host '@@ready'; Start-Sleep -Seconds 30"

                session (spec program PtySize.standard) (fun pty transcript ->
                    waitFor transcript "@@ready"
                    sync (pty.Kill())
                    let code = sync (pty.Wait CancellationToken.None)
                    readToEnd transcript
                    Expect.isTrue transcript.Ended "output ended"
                    Expect.equal code (if windows then -1 else 137) "killed")
            }

            test "runs in the working directory with the environment given" {
                let program =
                    program
                        "ls; echo \"@@var=$PTY_TEST_VAR\""
                        "Get-ChildItem -Name; Write-Host \"@@var=$env:PTY_TEST_VAR\""

                session
                    { spec program PtySize.standard with
                        Env = Inherit [ "PTY_TEST_VAR", Some "value 1" ]
                    }
                    (fun pty transcript ->
                        readToEnd transcript
                        Expect.stringContains transcript.Plain "marker.txt" "working directory"
                        Expect.stringContains transcript.Plain "@@var=value 1" "variable"
                        Expect.equal (sync (pty.Wait CancellationToken.None)) 0 "exit code")
            }

            test "a program that does not exist is an expected failure" {
                withDirectory (fun dir ->
                    let result =
                        sync (
                            (terminal dir).Spawn
                                (PtySpec.exec "no-such-program-for-pty-tests" [])
                                CancellationToken.None
                        )

                    match result with
                    | Error(NotStarted("no-such-program-for-pty-tests", _)) -> ()
                    | other -> failtest $"{other}")
            }

            test "an invalid size is an expected failure" {
                withDirectory (fun dir ->
                    let size = { Cols = 0; Rows = 24 }

                    match
                        sync (
                            (terminal dir).Spawn
                                { PtySpec.script "exit" with
                                    Size = size
                                }
                                CancellationToken.None
                        )
                    with
                    | Error(InvalidSize s) -> Expect.equal s size "size"
                    | other -> failtest $"{other}")
            }

            test "withPty closes the terminal when the body ends" {
                withDirectory (fun dir ->
                    let env =
                        {
                            Terminal = terminal dir
                            Scope = Scope.root
                        }

                    let program =
                        program "echo @@ready; sleep 30" "Write-Host '@@ready'; Start-Sleep -Seconds 30"

                    let mutable seen = None

                    let effect =
                        Terminal.withPty (spec program PtySize.standard) (fun pty ->
                            eff {
                                seen <- Some pty
                                let transcript = Transcript pty

                                let! found =
                                    fun (_: Env) ->
                                        transcript.ReadUntil (fun t -> t.Contains "@@ready") CancellationToken.None

                                return found
                            })

                    match sync (Eff.run env effect) with
                    | Ok found ->
                        Expect.isTrue found "ready"
                        let pty = Option.get seen
                        Expect.equal (sync (pty.Wait CancellationToken.None)) (if windows then -1 else 137) "killed"
                        Expect.equal (sync (pty.Read CancellationToken.None)) None "output discarded"
                    | Error e -> failtest (string e))
            }

            test "PowerShell 7 and Windows PowerShell start on ConPTY" {
                if windows then
                    for file, expected in [ "pwsh", "@@major=7"; "powershell", "@@major=5" ] do
                        let program =
                            Exec(
                                file,
                                [
                                    "-NoLogo"
                                    "-NoProfile"
                                    "-Command"
                                    "Write-Host \"@@major=$($PSVersionTable.PSVersion.Major)\""
                                ]
                            )

                        session (spec program PtySize.standard) (fun pty transcript ->
                            readToEnd transcript
                            Expect.stringContains transcript.Plain expected file
                            Expect.equal (sync (pty.Wait CancellationToken.None)) 0 "exit code")
            }
        ]
