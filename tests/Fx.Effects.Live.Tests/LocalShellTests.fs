module Fx.Effects.Live.Tests.LocalShellTests

open System
open System.IO
open System.Text
open Expecto
open Fx.Effects
open Fx.Effects.Live.Tests.LiveEnv

let private windows = OperatingSystem.IsWindows()

/// A script for the default shell: POSIX sh, or PowerShell 7 on Windows.
let private script (posix: string) (pwsh: string) =
    Command.script (if windows then pwsh else posix)

/// Text with Windows line endings normalized, for comparing shell output.
let private lines (text: string) = text.Replace("\r\n", "\n")

let private run (command: Command) =
    let dir = tempDirectory ()

    try
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "")

        match runIn (create dir) (Shell.run command) with
        | Ok output -> output
        | Error e -> failtest (string e)
    finally
        removeDirectory dir

let private withEnv (body: Env -> unit) =
    let dir = tempDirectory ()

    try
        body (create dir)
    finally
        removeDirectory dir

[<Tests>]
let tests =
    testList
        "LocalShell"
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
