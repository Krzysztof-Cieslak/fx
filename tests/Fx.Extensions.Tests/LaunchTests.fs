/// Launch specs (data) to processes: templates, the environment's allowlist and deny lists.
module Fx.Extensions.Tests.LaunchTests

open System.IO
open Expecto
open Fx.Effects
open Fx.Sdk
open Fx.Extensions

let private root = Path.Combine(Path.GetTempPath(), "pkg")

let private context (windows: bool) : LaunchContext =
    {
        Root = root
        Workspace = Some(Path.Combine(Path.GetTempPath(), "work"))
        HostEnvironment =
            [
                "PATH", "/bin"
                "HOME", "/home/me"
                "LANG", "C"
                "LC_ALL", "C"
                "NODE_OPTIONS", "--require=evil"
                "SECRET_TOKEN", "s3cret"
                "Path", "C:\\bin"
            ]
        IsWindows = windows
        ProjectTrusted = false
        Bin = []
    }

let private spec: LaunchSpec =
    {
        Exe = "${root}/bin/tool${exeSuffix}"
        Args = [ "--root"; "${root}" ]
        EnvAllowlist = [ "PATH"; "HOME"; "LC_*"; "NODE_OPTIONS"; "SECRET_*" ]
        EnvSet = [ "HOME", "${root}/home"; "MODE", "fast" ]
        EnvDeny = [ "SECRET_*" ]
        Cwd = LaunchCwd.Owned
        UntrustedOverrides = [ "--no-project-config=${root}/empty" ]
    }

[<Tests>]
let tests =
    testList
        "Launch"
        [
            test "templates expand in the program and arguments; overrides come while the project is untrusted" {
                match
                    Launch.procSpec
                        { context false with
                            ProjectTrusted = true
                        }
                        spec
                with
                | Ok proc ->
                    Expect.equal proc.Program (Exec(root + "/bin/tool", [ "--root"; root ])) "program"
                    Expect.equal proc.Cwd (Some root) "owned cwd"
                | Error reason -> failtest reason

                match
                    Launch.procSpec
                        (context false)
                        { spec with
                            Args = [ "--quiet"; "main.js"; "--stdio" ]
                        }
                with
                | Ok proc ->
                    Expect.equal
                        proc.Program
                        (Exec(
                            root + "/bin/tool",
                            [ "--quiet"; "--no-project-config=" + root + "/empty"; "main.js"; "--stdio" ]
                        ))
                        "untrusted: before the program"
                | Error reason -> failtest reason

                match
                    Launch.procSpec
                        { context false with
                            ProjectTrusted = true
                        }
                        { spec with
                            Args = [ "--quiet"; "main.js" ]
                        }
                with
                | Ok proc ->
                    Expect.equal proc.Program (Exec(root + "/bin/tool", [ "--quiet"; "main.js" ])) "trusted: none"
                | Error reason -> failtest reason

                match Launch.procSpec (context true) spec with
                | Ok { Program = Exec(exe, _) } -> Expect.stringEnds exe ".exe" "suffix on Windows"
                | other -> failtest (string other)
            }

            test "the environment is the allowlist and EnvSet, minus injection vectors and denied names" {
                Expect.equal
                    (Launch.environment (context false) spec)
                    [ "PATH", "/bin"; "LC_ALL", "C"; "HOME", root + "/home"; "MODE", "fast" ]
                    "unix"

                // Windows compares names without case: `Path` is allowed by `PATH`.
                Expect.equal
                    (Launch.environment (context true) spec |> List.map fst)
                    [ "PATH"; "LC_ALL"; "Path"; "HOME"; "MODE" ]
                    "windows"
            }

            test "the deny list wins over EnvSet too" {
                let sneaky =
                    { spec with
                        EnvSet = [ "NODE_OPTIONS", "--inspect"; "dotnet_startup_hooks", "x" ]
                    }

                Expect.equal
                    (Launch.environment (context true) sneaky |> List.map fst)
                    [ "PATH"; "HOME"; "LC_ALL"; "Path" ]
                    "removed"
            }

            test "bare program names stay for PATH lookup; relative paths are inside the package" {
                let exe (name: string) =
                    match Launch.procSpec (context false) { spec with Exe = name } with
                    | Ok { Program = Exec(exe, _) } -> exe
                    | other -> failtest (string other)

                Expect.equal (exe "bun") "bun" "bare"
                Expect.equal (exe "bin/tool") (Path.Combine(root, "bin/tool")) "relative"
                Expect.equal (exe "/usr/bin/tool") "/usr/bin/tool" "absolute"
            }

            test "${bin.<name>} is a command of a component the extension requires; one that none exports is refused" {
                let bun = Path.Combine(Path.GetTempPath(), "runtimes", "bun", "bin", "bun")

                let installed =
                    { context false with
                        Bin = [ "bun", bun ]
                    }

                match
                    Launch.procSpec
                        installed
                        { spec with
                            Exe = "${bin.bun}"
                            Args = [ "${root}/main.ts" ]
                        }
                with
                | Ok proc ->
                    Expect.equal
                        proc.Program
                        (Exec(bun, [ "--no-project-config=" + root + "/empty"; root + "/main.ts" ]))
                        "the installed path"
                | Error reason -> failtest reason

                match Launch.procSpec (context false) { spec with Exe = "${bin.bun}" } with
                | Error reason -> Expect.stringContains reason "${bin.bun}" "names the command"
                | Ok proc -> failtest (string proc)

                Expect.isError (Launch.procSpec installed { spec with Args = [ "${bin.node}" ] }) "in an argument too"
            }

            test "the working directory: owned, or the workspace if there is one" {
                match Launch.procSpec (context false) { spec with Cwd = LaunchCwd.Workspace } with
                | Ok proc -> Expect.equal proc.Cwd (context false).Workspace "workspace"
                | Error reason -> failtest reason

                Expect.isError
                    (Launch.procSpec { context false with Workspace = None } { spec with Cwd = LaunchCwd.Workspace })
                    "no workspace"
            }
        ]
