/// Interpreters' assets: digests, extraction with checks, and repairs.
module Fx.Interpreters.Tests.AssetsTests

open System.Text
open Expecto
open Fx.Effects
open Fx.Interpreters
open Fx.Interpreters.Tests.Support
open Fx.Sdk

let provider (assets: RuntimeFile list) : InterpreterProvider =
    {
        Id = "test-ts"
        Language = "typescript"
        Description = "A test interpreter."
        Requires = [ { Id = "bun"; Version = "1.4.5" } ]
        Launch =
            {
                Runtime = "bun"
                Program = [ "${asset.main.ts}"; "--bindings=${bindings}" ]
            }
        Assets = assets
        Bindings =
            fun surface ->
                let paths =
                    "finish" :: (surface.Bindings |> List.map (fun b -> b.Path))
                    |> List.sort
                    |> List.map (fun p -> $"// {p}\n")

                let text = $"declare const {surface.Global}: any;\n" + String.concat "" paths

                {
                    Files =
                        [
                            {
                                Name = surface.Global + ".d.ts"
                                Content = text
                            }
                        ]
                    Declarations = text
                }
        PromptPack =
            {
                Fence = "ts"
                Instructions = "Write cells."
                Examples = []
            }
        Check = Some CheckSpec.Kernel
        Caps =
            {
                Snapshot = false
                AsyncHostCalls = AsyncHostCalls.Await
                Packages = None
                Deterministic = false
            }
    }

let files =
    [
        {
            Name = "main.ts"
            Content = "import './lib.ts'\n"
        }
        {
            Name = "lib.ts"
            Content = "export const x = 1 // é\n"
        }
    ]

let root = path [ "home"; "assets" ]

let private ensure (f: Fakes) (p: InterpreterProvider) (unique: string) =
    run f.Env (Assets.ensure os root unique p)

let private content (f: Fakes) (file: string) =
    match run f.Env (Fs.readText file) with
    | Ok text -> text
    | Error e -> failtest (string e)

[<Tests>]
let tests =
    testList
        "assets"
        [
            testCase "the digest depends on the files, not their order"
            <| fun () ->
                let digest = InterpreterProvider.assetsDigest (provider files)
                Expect.stringStarts digest "sha256:" "sha256"
                Expect.equal (InterpreterProvider.assetsDigest (provider (List.rev files))) digest "order"

                let changed =
                    provider
                        [
                            files[0]
                            { files[1] with
                                Content = "export const x = 2\n"
                            }
                        ]

                Expect.notEqual (InterpreterProvider.assetsDigest changed) digest "content"

            testCase "assets are extracted into a directory named by the interpreter and the digest"
            <| fun () ->
                let f = fakes (fun _ -> None)
                let p = provider files

                match ensure f p "launch-1" with
                | Ok dir ->
                    let digest = InterpreterProvider.assetsDigest p
                    Expect.stringEnds dir ("test-ts-" + digest.Substring(7, 16)) "named"

                    Expect.equal
                        (content f (dir + string System.IO.Path.DirectorySeparatorChar + "lib.ts"))
                        files[1].Content
                        "content"

                    match run f.Env (Fs.list root) with
                    | Ok entries ->
                        Expect.equal
                            (entries |> List.map (fun e -> e.Name))
                            [ System.IO.Path.GetFileName dir ]
                            "no staging left"
                    | Error e -> failtest (string e)
                | Error e -> failtest (string e)

            testCase "an intact set is used as it is"
            <| fun () ->
                let f = fakes (fun _ -> None)
                let p = provider files
                let first = ensure f p "launch-1"
                let second = ensure f p "launch-2"
                Expect.equal second first "the same directory"

                match run f.Env (Fs.list root) with
                | Ok entries -> Expect.equal entries.Length 1 "nothing staged"
                | Error e -> failtest (string e)

            testCase "a changed or missing file is extracted again"
            <| fun () ->
                let f = fakes (fun _ -> None)
                let p = provider files

                match ensure f p "launch-1" with
                | Ok dir ->
                    let main = dir + string System.IO.Path.DirectorySeparatorChar + "main.ts"
                    f.Fs.AddText(main, "process.exit(1)\n")

                    match ensure f p "launch-2" with
                    | Ok again ->
                        Expect.equal again dir "the same directory"
                        Expect.equal (content f main) files[0].Content "repaired"
                    | Error e -> failtest (string e)

                    let lib = dir + string System.IO.Path.DirectorySeparatorChar + "lib.ts"
                    run f.Env (Fs.delete lib) |> ignore

                    match ensure f p "launch-3" with
                    | Ok _ -> Expect.equal (content f lib) files[1].Content "restored"
                    | Error e -> failtest (string e)
                | Error e -> failtest (string e)

            testCase "versions live side by side"
            <| fun () ->
                let f = fakes (fun _ -> None)
                let v1 = ensure f (provider files) "a"

                let v2 =
                    ensure
                        f
                        (provider
                            [
                                {
                                    Name = "main.ts"
                                    Content = "// v2\n"
                                }
                            ])
                        "b"

                Expect.notEqual v1 v2 "two directories"
                Expect.isOk v1 "v1"
                Expect.isOk v2 "v2"

            testCase "assets keep their bytes (UTF-8, no byte order mark)"
            <| fun () ->
                let f = fakes (fun _ -> None)

                match ensure f (provider files) "x" with
                | Ok dir ->
                    match run f.Env (Fs.readFile (dir + string System.IO.Path.DirectorySeparatorChar + "lib.ts")) with
                    | Ok bytes -> Expect.equal bytes (UTF8Encoding(false).GetBytes files[1].Content) "bytes"
                    | Error e -> failtest (string e)
                | Error e -> failtest (string e)
        ]
