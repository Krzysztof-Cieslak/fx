/// The shell conformance suite: what every `Shell` interpreter must do on a real machine (Linux and
/// macOS with `/bin/sh`, Windows with PowerShell 7). `LocalShell` passes it here; envd's proxy (in
/// process and over a child's stdio) passes it in Fx.Envd.Live.Tests, which compiles the same file.
module Fx.Effects.Live.Tests.Conformance.ShellConformance

open System
open System.IO
open System.Text
open Expecto
open Fx.Effects
open Fx.Effects.Live

/// What the tests run in: the shell under test and the real clock.
type Env =
    {
        Clock: Clock
        Shell: Shell
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IShell with
        member env.Shell = env.Shell

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

let private windows = OperatingSystem.IsWindows()

/// A script for the default shell: POSIX sh, or PowerShell 7 on Windows.
let private script (posix: string) (pwsh: string) =
    Command.script (if windows then pwsh else posix)

/// Text with Windows line endings normalized, for comparing shell output.
let private lines (text: string) = text.Replace("\r\n", "\n")

let private runIn (env: Env) (effect: Eff<Env, 'T>) : 'T =
    (Eff.run env effect).WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

let private tempDirectory () =
    let dir =
        Path.Combine(Path.GetTempPath(), "shell-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private removeDirectory (dir: string) =
    try
        Directory.Delete(dir, true)
    with _ ->
        ()

/// Tests that the shells made by `create` conform. `create` gets an existing directory with one file,
/// `marker.txt`, which must be the shell's default working directory; it is called once per test.
/// `release` cleans up after a shell.
let tests (name: string) (create: string -> Shell) (release: Shell -> unit) : Test =
    let withEnv (body: Env -> unit) =
        let dir = tempDirectory ()
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "")
        let shell = create dir

        try
            body
                {
                    Clock = SystemClock.clock
                    Shell = shell
                    Scope = Scope.root
                }
        finally
            release shell
            removeDirectory dir

    let run (command: Command) =
        let mutable result = None

        withEnv (fun env ->
            match runIn env (Shell.run command) with
            | Ok output -> result <- Some output
            | Error e -> failtest (string e))

        result.Value

    testList
        $"Shell conformance ({name})"
        [
            test "runs a script and captures its output and exit code" {
                let output =
                    run (
                        script
                            "echo hello; echo oops 1>&2; exit 3"
                            "Write-Output hello; [Console]::Error.WriteLine('oops'); exit 3"
                    )

                Expect.equal (lines (Output.stdoutText output)) "hello\n" "stdout"
                Expect.equal (lines (Output.stderrText output)) "oops\n" "stderr"
                Expect.equal output.ExitCode 3 "exit code"
            }

            test "passes standard input and closes it" {
                let output =
                    run (
                        script "cat" "[Console]::Out.Write([Console]::In.ReadToEnd())"
                        |> Command.withStdinText "line 1\nline 2\n"
                    )

                Expect.equal (lines (Output.stdoutText output)) "line 1\nline 2\n" "echoed"

                let empty = run (script "cat" "[Console]::Out.Write([Console]::In.ReadToEnd())")
                Expect.equal (Output.stdoutText empty) "" "no input: stdin is closed, so nothing hangs"
            }

            test "runs in the working directory with the environment variables given" {
                let listing = run (script "ls" "Get-ChildItem -Name")
                Expect.equal (lines (Output.stdoutText listing)) "marker.txt\n" "default working directory"

                let printVar =
                    script "printf %s \"$EFFECTS_TEST_VAR\"" "[Console]::Out.Write($env:EFFECTS_TEST_VAR)"

                let set =
                    run (printVar |> Command.withEnv (Inherit [ "EFFECTS_TEST_VAR", Some "value 1" ]))

                Expect.equal (Output.stdoutText set) "value 1" "inherited and set"

                let path =
                    Environment.GetEnvironmentVariable "PATH"
                    |> Option.ofObj
                    |> Option.defaultValue ""

                let exact =
                    run (
                        printVar
                        |> Command.withEnv (
                            Exactly
                                [
                                    "PATH", path
                                    if windows then
                                        "SystemRoot", Environment.GetEnvironmentVariable "SystemRoot" |> string
                                ]
                        )
                    )

                Expect.equal (Output.stdoutText exact) "" "exactly: the variable is not inherited"
            }

            test "passes arguments without a shell, verbatim" {
                if not windows then
                    let output =
                        run (Command.exec "/bin/sh" [ "-c"; "printf '%s|' \"$@\""; "sh"; "a b"; "\"q\""; "$HOME"; "" ])

                    Expect.equal (Output.stdoutText output) "a b|\"q\"|$HOME||" "arguments"
            }

            test "a program that does not exist is an expected failure" {
                withEnv (fun env ->
                    match runIn env (Shell.exec "no-such-program-for-effects-tests" []) with
                    | Error(NotStarted("no-such-program-for-effects-tests", _)) -> ()
                    | other -> failtest $"{other}")
            }

            test "cancelling kills the process" {
                withEnv (fun env ->
                    let started = env.Clock.Monotonic()

                    let result =
                        runIn
                            env
                            (Eff.timeout
                                (TimeSpan.FromMilliseconds 500.0)
                                (Shell.run (script "sleep 60" "Start-Sleep -Seconds 60")))

                    Expect.isNone result "timed out"

                    Expect.isLessThan
                        (env.Clock.Monotonic() - started)
                        (TimeSpan.FromSeconds 30.0)
                        "did not wait for the process")
            }

            test "spawned processes stream output and take input while running" {
                withEnv (fun env ->
                    let spec =
                        {
                            Program =
                                Script(
                                    if windows then
                                        "Write-Output ready; $line = [Console]::In.ReadLine(); Write-Output \"got $line\""
                                    else
                                        "echo ready; read line; echo \"got $line\""
                                )
                            Cwd = None
                            Env = Inherit []
                        }

                    let program =
                        Shell.withProcess spec (fun proc ->
                            eff {
                                let output = StringBuilder()
                                let mutable ready = false

                                while not ready do
                                    match! Shell.read proc with
                                    | Some(Stdout bytes) ->
                                        output.Append(Encoding.UTF8.GetString bytes) |> ignore
                                        ready <- output.ToString().Contains "ready"
                                    | Some(Stderr _) -> ()
                                    | None -> ready <- true

                                do! Shell.write proc (Encoding.UTF8.GetBytes "x\n")
                                do! Shell.closeStdin proc
                                let mutable reading = true

                                while reading do
                                    match! Shell.read proc with
                                    | Some(Stdout bytes) -> output.Append(Encoding.UTF8.GetString bytes) |> ignore
                                    | Some(Stderr _) -> ()
                                    | None -> reading <- false

                                let! code = Shell.wait proc
                                return lines (output.ToString()), code
                            })

                    match runIn env program with
                    | Ok(output, code) ->
                        Expect.equal output "ready\ngot x\n" "output"
                        Expect.equal code 0 "exit code"
                    | Error e -> failtest (string e))
            }
        ]
