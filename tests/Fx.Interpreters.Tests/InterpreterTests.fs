/// Interpreter providers (validation, programs, prompts) and preparing a kernel's launch over fakes.
module Fx.Interpreters.Tests.InterpreterTests

open System
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Ext.RuntimeBun
open Fx.Interpreters
open Fx.Interpreters.Tests.Support
open Fx.Interpreters.Tests.AssetsTests
open Fx.Kernel
open Fx.Sdk

let private test = provider files

let private target =
    match TargetTriple.ofRid (Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier) with
    | Some target -> target
    | None -> TargetTriple.create os Arch.X64 (if os = Os.Linux then Some Libc.Glibc else None)

let private bunDir = path [ "rt"; "bun" ]

let private installed: InstalledComponent =
    {
        Id = "bun"
        Version = RuntimeBun.bunComponent.Version
        Dir = bunDir
        Bin =
            [
                "bun", CoreBindings.resolve (CoreSettings.defaults bunDir os) ("bun" + TargetTriple.exeSuffix target)
            ]
        Env = []
    }

let private session (p: InterpreterProvider) (installed: InstalledComponent list) : InterpreterSession =
    {
        Provider = p
        Runtime = RuntimeBun.runtime
        Installed = installed
        AssetsRoot = root
        Launch =
            {
                Target = target
                Owned = OwnedDirs.under (path [ "owned"; "k1" ])
                Workspace = Some workspace
                ProjectTrusted = false
                HostEnvironment = [ "PATH", "/usr/bin"; "BUN_OPTIONS", "--preload=evil.ts"; "SECRET", "x" ]
            }
    }

let private surface: BindingSurface =
    {
        Global = Product.kernelGlobal
        Description = None
        Groups = []
        Bindings = []
        Finish = turnCodec.Schema
    }

let private issues (p: InterpreterProvider) =
    InterpreterProvider.validate p |> List.map string

[<Tests>]
let tests =
    testList
        "interpreters"
        [
            InterpreterConformance.tests "conformance (test provider)" test

            testList
                "validation"
                [
                    testCase "ids, languages, runtimes and programs"
                    <| fun () ->
                        Expect.isNonEmpty (issues { test with Id = "Bun TS" }) "id"
                        Expect.isNonEmpty (issues { test with Language = " " }) "language"

                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Launch = { test.Launch with Runtime = "" }
                                })
                            "runtime"

                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Launch = { test.Launch with Program = [] }
                                })
                            "program"

                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Launch =
                                        { test.Launch with
                                            Program = [ "${asset.missing.ts}" ]
                                        }
                                })
                            "an unknown asset"

                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Launch =
                                        { test.Launch with
                                            Program = [ "${env.HOME}" ]
                                        }
                                })
                            "no other placeholders"

                    testCase "assets, requirements and bindings"
                    <| fun () ->
                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Assets = [ { Name = "../x.ts"; Content = "" }; { Name = "main.ts"; Content = "" } ]
                                })
                            "a path"

                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Assets = [ { Name = "main.ts"; Content = "" }; { Name = "MAIN.ts"; Content = "" } ]
                                })
                            "twice without case"

                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Requires = [ { Id = "bun"; Version = "latest" } ]
                                })
                            "a range"

                        Expect.isNonEmpty
                            (issues
                                { test with
                                    Bindings = fun _ -> invalidOp "broken"
                                })
                            "bindings that throw"
                ]

            testList
                "programs and prompts"
                [
                    testCase "the program on each target"
                    <| fun () ->
                        Expect.equal
                            (InterpreterProvider.program test Os.Linux "/a" "/b")
                            (Ok [ "/a/main.ts"; "--bindings=/b" ])
                            "Linux"

                        Expect.equal
                            (InterpreterProvider.program test Os.Windows @"C:\a" @"C:\b")
                            (Ok [ @"C:\a\main.ts"; @"--bindings=C:\b" ])
                            "Windows"

                    testCase "the prompt: instructions, declarations, examples"
                    <| fun () ->
                        let pack =
                            {
                                Fence = "ts"
                                Instructions = "Write cells.\n"
                                Examples =
                                    [
                                        {
                                            Title = "An example:"
                                            Code = "1 + 1\n"
                                        }
                                    ]
                            }

                        let files =
                            {
                                Files = []
                                Declarations = "declare const x: 1;\n"
                            }

                        Expect.equal
                            (PromptPack.render pack files)
                            "Write cells.\n\n```ts\ndeclare const x: 1;\n```\n\nAn example:\n```ts\n1 + 1\n```\n"
                            "the text"
                ]

            testList
                "preparing a launch"
                [
                    testCase "assets, owned directories and binding files are written, and the launch is hardened"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        match run f.Env (Interpreter.prepare (session test [ installed ]) surface) with
                        | Error e -> failtest (string e)
                        | Ok launch ->
                            let assets = Assets.directory os root test
                            let bindings = Interpreter.bindingsDir (session test [ installed ]).Launch
                            let sep = string IO.Path.DirectorySeparatorChar

                            match launch.Process.Program with
                            | Exec(exe, args) ->
                                Expect.equal exe (snd installed.Bin[0]) "the runtime's executable"
                                let program = args |> List.skipWhile (fun a -> a.StartsWith "--")

                                Expect.equal
                                    (List.truncate 2 (args |> List.rev) |> List.rev)
                                    [ assets + sep + "main.ts"; "--bindings=" + bindings ]
                                    "the program last"

                                Expect.equal program.Head (assets + sep + "main.ts") "after the runtime's flags"
                                Expect.contains args "--no-install" "hardened"

                                Expect.isTrue
                                    (args |> List.exists (fun a -> a.StartsWith "--tsconfig-override="))
                                    "untrusted overrides"
                            | other -> failtest $"{other}"

                            match launch.Process.Env with
                            | Exactly vars ->
                                let names = vars |> List.map fst
                                Expect.contains names "PATH" "allowlisted"
                                Expect.isFalse (List.contains "BUN_OPTIONS" names) "no vector"
                                Expect.isFalse (List.contains "SECRET" names) "nothing else of the launcher"
                            | other -> failtest $"{other}"

                            Expect.isEmpty (KernelLaunch.check launch) "passes the hygiene check"
                            Expect.equal launch.EnvDeny RuntimeBun.runtime.Launch.EnvDeny "the runtime's deny list"

                            match run f.Env (Fs.readText (bindings + sep + Product.kernelGlobal + ".d.ts")) with
                            | Ok text -> Expect.stringContains text Product.kernelGlobal "the binding file"
                            | Error e -> failtest (string e)

                            match run f.Env (Fs.readText (assets + sep + "lib.ts")) with
                            | Ok text -> Expect.equal text files[1].Content "the assets"
                            | Error e -> failtest (string e)

                            let owned = (session test [ installed ]).Launch.Owned

                            match run f.Env (Fs.readText (owned.Config + sep + "bunfig.toml")) with
                            | Ok text -> Expect.stringContains text "telemetry = false" "the runtime's files"
                            | Error e -> failtest (string e)

                    testCase "without the runtime's component, nothing is written"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        match run f.Env (Interpreter.prepare (session test []) surface) with
                        | Error(InterpreterError.NotInstalled "bun") -> ()
                        | other -> failtest $"{other}"

                        match run f.Env (Fs.stat root) with
                        | Error(NotFound _) -> ()
                        | other -> failtest $"{other}"

                    testCase "an interpreter on another runtime, or an invalid one, is refused"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        let other =
                            { test with
                                Launch = { test.Launch with Runtime = "node" }
                            }

                        match run f.Env (Interpreter.prepare (session other [ installed ]) surface) with
                        | Error(InterpreterError.Invalid [ issue ]) ->
                            Expect.stringContains (string issue) "node" "named"
                        | result -> failtest $"{result}"

                        match run f.Env (Interpreter.prepare (session { test with Id = "" } [ installed ]) surface) with
                        | Error(InterpreterError.Invalid _) -> ()
                        | result -> failtest $"{result}"
                ]
        ]
