module Fx.Effects.Tests.ShellTests

open System.Text
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Tests.TestEnv

let private text (bytes: byte array) = Encoding.UTF8.GetString bytes

[<Tests>]
let tests =
    testList
        "Shell and ScriptedShell"
        [
            test "runs scripted commands and records them" {
                let fakes =
                    createWith (fun command ->
                        match command.Spec.Program with
                        | Exec("git", [ "status" ]) -> Some(ScriptedShell.ok "clean\n")
                        | Script "exit 3" -> Some(Ok(ScriptedShell.output 3 "" "failed\n"))
                        | Exec("nope", _) -> Some(Error(NotStarted("nope", "not found")))
                        | _ -> None)

                let program =
                    effResult {
                        let! status = Shell.exec "git" [ "status" ]
                        let! failed = Shell.run (Command.script "exit 3" |> Command.inDir (path [ "repo" ]))
                        return Output.stdoutText status, failed.ExitCode, Output.stderrText failed
                    }

                Expect.equal (runIn fakes.Env program) (Ok("clean\n", 3, "failed\n")) "outputs"

                Expect.equal
                    (fakes.Shell.Calls |> List.map (fun c -> string c.Spec.Program, c.Spec.Cwd))
                    [ "git status", None; "script: exit 3", Some(path [ "repo" ]) ]
                    "calls"

                Expect.equal
                    (runIn fakes.Env (Shell.exec "nope" []))
                    (Error(NotStarted("nope", "not found")))
                    "not started"
            }

            test "an unexpected command is a defect in the test" {
                let fakes = create ()

                Expect.throwsT<System.InvalidOperationException>
                    (fun () -> runIn fakes.Env (Shell.exec "rm" [ "-rf"; "/" ]) |> ignore)
                    "throws"
            }

            test "spawned processes get their input, and Proc.collect drains them" {
                let fakes =
                    createWith (fun command ->
                        match command.Spec.Program with
                        | Exec("cat", []) -> Some(Ok(ScriptedShell.output 0 (text command.Stdin) "warning\n"))
                        | _ -> None)

                let program =
                    Shell.withProcess
                        {
                            Program = Exec("cat", [])
                            Cwd = None
                            Env = Inherit []
                        }
                        (fun proc ->
                            eff {
                                do! Shell.write proc (Encoding.UTF8.GetBytes "hello ")
                                do! Shell.write proc (Encoding.UTF8.GetBytes "world")
                                do! Shell.closeStdin proc
                                let! first = Shell.read proc
                                let! second = Shell.read proc
                                let! eof = Shell.read proc
                                let! code = Shell.wait proc
                                return first, second, eof, code
                            })

                match runIn fakes.Env program with
                | Ok(Some(Stdout out), Some(Stderr err), None, 0) ->
                    Expect.equal (text out) "hello world" "stdout"
                    Expect.equal (text err) "warning\n" "stderr"
                | other -> failtest $"{other}"

                let collected =
                    eff {
                        match!
                            Shell.spawn
                                {
                                    Program = Exec("cat", [])
                                    Cwd = None
                                    Env = Inherit []
                                }
                        with
                        | Ok proc ->
                            return
                                Ok(
                                    Proc.collect
                                        proc
                                        (Encoding.UTF8.GetBytes "in")
                                        System.Threading.CancellationToken.None
                                )
                        | Error e -> return Error e
                    }

                match runIn fakes.Env collected with
                | Ok output -> Expect.equal (Output.stdoutText output.Result) "in" "collected"
                | Error e -> failtest $"{e}"
            }

            test "withProcess kills the process when the body fails" {
                let fakes = createWith (fun _ -> Some(ScriptedShell.ok ""))
                let mutable spawned = None

                let program =
                    Shell.withProcess
                        {
                            Program = Exec("server", [])
                            Cwd = None
                            Env = Inherit []
                        }
                        (fun proc ->
                            spawned <- Some proc

                            Eff.ofTask (fun () ->
                                System.Threading.Tasks.Task.FromException<int>(
                                    System.InvalidOperationException "boom"
                                )))

                Expect.throwsT<System.InvalidOperationException> (fun () -> runIn fakes.Env program |> ignore) "failed"
                let code = spawned.Value.Wait System.Threading.CancellationToken.None
                Expect.equal code.Result -1 "killed before it answered"
                Expect.isEmpty fakes.Shell.Calls "never ran"
            }

            test "commands and specs print without their input" {
                let command =
                    Command.exec "git" [ "commit"; "-m"; "x" ]
                    |> Command.withEnv (Inherit [ "GIT_AUTHOR_NAME", Some "agent"; "PAGER", None ])
                    |> Command.withStdinText "secret input"

                Expect.equal
                    (string command)
                    "git commit -m x (in <default>, inherit (changing: GIT_AUTHOR_NAME, PAGER)) with 12 bytes of input"
                    "text"
            }

            test "shell specs build the invocation" {
                Expect.equal (ShellSpec.invocation ShellSpec.posix "echo hi") ("/bin/sh", [ "-c"; "echo hi" ]) "posix"

                Expect.equal
                    (ShellSpec.invocation ShellSpec.pwsh "echo hi")
                    ("pwsh", [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; "echo hi" ])
                    "pwsh"
            }
        ]
