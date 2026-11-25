/// The interpreter provider conformance suite (docs/modules/interpreters.md, "Conformance"): a
/// function from a provider to tests that every interpreter passes, built-in or not, before it can
/// be selected. Its kernel must also pass the kernel conformance suite (tests/Fx.Kernel.Tests).
module Fx.Interpreters.Tests.InterpreterConformance

open Expecto
open Fx
open Fx.Contracts
open Fx.Sdk

let private surface (bindings: BindingDecl list) (groups: (string * string) list) : BindingSurface =
    {
        Global = Product.kernelGlobal
        Description = Some "The host bridge."
        Groups = groups
        Bindings = bindings
        Finish = Codec.string |> Codec.named "Result" |> fun c -> c.Schema
    }

let private binding (path: string) : BindingDecl =
    {
        Path = path
        Description = Some $"Calls {path}."
        Params =
            [
                {
                    Name = "input"
                    Description = None
                    Schema = Codec.string.Schema
                    Optional = false
                }
                {
                    Name = "options"
                    Description = None
                    Schema = (Codec.stringMap Codec.int).Schema
                    Optional = true
                }
            ]
        Returns = (Codec.list Codec.int).Schema
    }

let private sample =
    surface
        [ binding "shell.run"; binding "fs.read"; binding "fs.write"; binding "now" ]
        [ "shell", "Commands."; "fs", "Files." ]

let private targets: (Os * string * string) list =
    [
        Os.Linux, "/home/u/.fx/kernels/x", "/home/u/.fx/owned/k1/bindings"
        Os.MacOs, "/Users/u/.fx/kernels/x", "/Users/u/.fx/owned/k1/bindings"
        Os.Windows, @"C:\Users\u\AppData\Local\fx\kernels\x", @"C:\Users\u\AppData\Local\fx\owned\k1\bindings"
    ]

/// The suite for one provider.
let tests (name: string) (provider: InterpreterProvider) : Test =
    testList
        name
        [
            testCase "it is valid"
            <| fun () -> Expect.isEmpty (InterpreterProvider.validate provider |> List.map string) "issues"

            testCase "its id, language and runtime are set"
            <| fun () ->
                Expect.isNotEmpty provider.Language "language"
                Expect.isNotEmpty provider.Launch.Runtime "runtime"

                Expect.isTrue
                    (provider.Requires |> List.exists (fun r -> r.Id = provider.Launch.Runtime))
                    "it requires its runtime's component"

            testCase "its assets have a stable digest, whatever their order"
            <| fun () ->
                let digest = InterpreterProvider.assetsDigest provider

                Expect.equal
                    (InterpreterProvider.assetsDigest
                        { provider with
                            Assets = List.rev provider.Assets
                        })
                    digest
                    "order"

                Expect.isNonEmpty provider.Assets "a kernel has files"

            testCase "its program expands to absolute paths on every target"
            <| fun () ->
                for os, assets, bindings in targets do
                    match InterpreterProvider.program provider os assets bindings with
                    | Ok(first :: _ as program) ->
                        Expect.isTrue (RuntimeSpec.isAbsoluteOn os first) $"{os}: {first}"
                        Expect.isFalse (program |> List.exists (fun a -> a.Contains "${")) $"{os}: {program}"
                    | Ok [] -> failtest "no program"
                    | Error issues -> failtest (Issue.renderAtMost 5 issues)

            testCase "its binding files are deterministic, and independent of the bindings' order"
            <| fun () ->
                let files = provider.Bindings sample

                let reversed =
                    provider.Bindings
                        { sample with
                            Bindings = List.rev sample.Bindings
                            Groups = List.rev sample.Groups
                        }

                Expect.equal (string reversed) (string files) "files"
                Expect.equal reversed.Declarations files.Declarations "declarations"

                Expect.equal
                    (reversed.Files |> List.map (fun f -> f.Content))
                    (files.Files |> List.map (fun f -> f.Content))
                    "contents"

            testCase "its declarations name the global and every binding, and never another product's name"
            <| fun () ->
                let files = provider.Bindings sample

                for path in [ "run"; "read"; "write"; "now"; "finish" ] do
                    Expect.stringContains files.Declarations path $"declares {path}"

                Expect.stringContains files.Declarations Product.kernelGlobal "the global"

                let other = provider.Bindings { sample with Global = "elsewhere" }

                Expect.stringContains other.Declarations "elsewhere" "the global given"

                for file in files.Files do
                    Expect.isTrue (Component.isCommandName file.Name) $"a plain file name: {file.Name}"

            testCase "a surface that leaves groups out still renders"
            <| fun () ->
                let files =
                    provider.Bindings
                        { sample with
                            Bindings = [ binding "now" ]
                        }

                Expect.stringContains files.Declarations "now" "the remaining binding"

            testCase "its prompt has the instructions, the declarations and the examples"
            <| fun () ->
                let files = provider.Bindings sample
                let prompt = PromptPack.render provider.PromptPack files
                Expect.stringContains prompt (provider.PromptPack.Instructions.TrimEnd '\n') "instructions"
                Expect.stringContains prompt (files.Declarations.TrimEnd '\n') "declarations"

                for example in provider.PromptPack.Examples do
                    Expect.stringContains prompt example.Code example.Title

                Expect.equal (PromptPack.render provider.PromptPack files) prompt "deterministic"
                Expect.isFalse (prompt.Contains '\r') "\\n line ends"

            testCase "it is privileged code, contributed in process only"
            <| fun () ->
                let t = InterpreterProviders.contributionType
                Expect.isTrue t.Privileged "privileged"
                Expect.equal t.Kind ContributionKind.Code "code"
                let contribution = Contribution.create t provider.Id provider
                Expect.isNone contribution.Json "no JSON form"

                Expect.isSome (Contribution.tryValue t contribution) "the value back"

            testCase "its caps are what kernels announce"
            <| fun () ->
                let json = InterpreterCaps.codec.Encode provider.Caps

                match InterpreterCaps.codec.Decode json with
                | Ok caps -> Expect.equal caps provider.Caps "round trip"
                | Error issues -> failtest (Issue.renderAtMost 5 issues)
        ]
