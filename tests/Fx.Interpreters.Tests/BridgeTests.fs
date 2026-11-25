/// The host bridge: bindings as data with codecs, their dispatch, `finish`, and the bridge's checks.
module Fx.Interpreters.Tests.BridgeTests

open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Interpreters
open Fx.Interpreters.Tests.Support
open Fx.Kernel
open Fx.Sdk
open Fx.Store

let private inline' (text: string) = Value.Inline(parse text)

let private add: HostBinding<Env> =
    HostBinding.create2
        "math.add"
        "Adds two integers."
        (Arg.required "a" "" Codec.int, Arg.withDefault "b" "" 1 Codec.int)
        Codec.int
        (fun (a, b) -> EffResult.ok (a + b))

let private echo: HostBinding<Env> =
    HostBinding.create1
        "echo"
        "Returns its argument."
        (Arg.optional "text" "" Codec.string)
        (Codec.option Codec.string)
        EffResult.ok

let private call (binding: string) (args: Value list) : HostCall =
    {
        CellId = Some "c1"
        Binding = binding
        Args = args
    }

let private invoke (bindings: HostBinding<Env> list) (c: HostCall) =
    let f = fakes (fun _ -> None)
    run f.Env (HostBinding.dispatch bindings c)

let private bridge (accepted: ResizeArray<Turn>) : Bridge<Env> =
    Bridge.create (
        Finishing.create turnCodec [ summaryEnsure ] (fun turn ->
            accepted.Add turn
            Eff.unit)
    )
    |> Bridge.add [ "math", "Arithmetic." ] [ add; echo ]

[<Tests>]
let tests =
    testList
        "bridge"
        [
            testList
                "bindings"
                [
                    testCase "arguments decode into their declared types, by position"
                    <| fun () ->
                        let result = invoke [ add ] (call "math.add" [ inline' "2"; inline' "3" ])
                        Expect.equal result (Ok(inline' "5")) "2 + 3"

                    testCase "an optional argument may be left out, or null"
                    <| fun () ->
                        Expect.equal (invoke [ add ] (call "math.add" [ inline' "2" ])) (Ok(inline' "3")) "default"

                        Expect.equal
                            (invoke [ echo ] (call "echo" [ inline' "null" ]))
                            (Ok(inline' "null"))
                            "null is none"

                    testCase "arguments that do not decode are issues at their names, and nothing runs"
                    <| fun () ->
                        match invoke [ add ] (call "math.add" [ inline' "\"x\""; inline' "1.5" ]) with
                        | Error(HostCallError.InvalidArguments issues) ->
                            let paths = issues |> List.map (fun i -> Issue.render [ i ])
                            Expect.equal issues.Length 2 $"both: {paths}"
                            Expect.stringContains paths[0] "a" "the first"
                            Expect.stringContains paths[1] "b" "the second"
                        | other -> failtest $"{other}"

                    testCase "a missing argument and too many arguments are issues"
                    <| fun () ->
                        match invoke [ add ] (call "math.add" []) with
                        | Error(HostCallError.InvalidArguments [ issue ]) ->
                            Expect.stringContains (string issue) "is required" "missing"
                        | other -> failtest $"{other}"

                        match invoke [ add ] (call "math.add" [ inline' "1"; inline' "2"; inline' "3" ]) with
                        | Error(HostCallError.InvalidArguments [ issue ]) ->
                            Expect.stringContains (string issue) "at most 2 arguments" "too many"
                        | other -> failtest $"{other}"

                    testCase "references never decode into data"
                    <| fun () ->
                        let blob =
                            Value.Blob(
                                {
                                    Hash = BlobHash.ofBytes [| 1uy |]
                                    Size = 1L
                                },
                                Preview.full "one byte"
                            )

                        match invoke [ echo ] (call "echo" [ blob ]) with
                        | Error(HostCallError.InvalidArguments [ issue ]) ->
                            Expect.stringContains (string issue) "blob reference" "a blob is not a string"
                        | other -> failtest $"{other}"

                    testCase "an unknown binding"
                    <| fun () ->
                        Expect.equal
                            (invoke [ add ] (call "math.sub" []))
                            (Error(HostCallError.UnknownBinding "math.sub"))
                            "unknown"

                    testCase "a capability's typed failure keeps its kind as the code and its JSON as data"
                    <| fun () ->
                        match HostBinding.failed Fx.Envd.EnvCodecs.fsError (NotFound "/x") with
                        | HostCallError.Failed(code, message, Some data) ->
                            Expect.equal code "notFound" "code"
                            Expect.stringContains message "/x" "message"
                            Expect.equal (canonical data) """{"kind":"notFound","path":"/x"}""" "data"
                        | other -> failtest $"{other}"

                    testCase "declarations carry the parameters' and the result's schemas"
                    <| fun () ->
                        let decl = add.Decl
                        Expect.equal decl.Path "math.add" "path"

                        Expect.equal
                            (decl.Params |> List.map (fun p -> p.Name, p.Optional))
                            [ "a", false; "b", true ]
                            "params"

                        Expect.equal decl.Returns Codec.int.Schema "returns"
                ]

            testList
                "finish"
                [
                    testCase "an accepted result is handed on"
                    <| fun () ->
                        let accepted = ResizeArray()
                        let b = bridge accepted
                        let f = fakes (fun _ -> None)

                        let outcome =
                            run
                                f.Env
                                (b.Finish.Validate
                                    {
                                        CellId = Some "c1"
                                        Result = inline' """{"kind":"done","summary":"ok","verify":"make test"}"""
                                    })

                        Expect.equal outcome (Ok()) "accepted"
                        Expect.equal (List.ofSeq accepted) [ Turn.Done("ok", Some "make test") ] "handed on"

                    testCase "a result that does not decode is rejected with issues under result"
                    <| fun () ->
                        let accepted = ResizeArray()
                        let f = fakes (fun _ -> None)

                        match
                            run
                                f.Env
                                ((bridge accepted).Finish.Validate
                                    {
                                        CellId = None
                                        Result = inline' """{"kind":"dnoe"}"""
                                    })
                        with
                        | Error rejected ->
                            Expect.isNonEmpty rejected.Issues "issues"
                            Expect.stringContains (Issue.renderAtMost 5 rejected.Issues) "result" "under result"
                        | Ok() -> failtest "accepted"

                        Expect.isEmpty accepted "nothing handed on"

                    testCase "a result that fails a check is rejected"
                    <| fun () ->
                        let accepted = ResizeArray()
                        let f = fakes (fun _ -> None)

                        match
                            run
                                f.Env
                                ((bridge accepted).Finish.Validate
                                    {
                                        CellId = None
                                        Result = inline' """{"kind":"done","summary":" "}"""
                                    })
                        with
                        | Error rejected ->
                            Expect.stringContains
                                (Issue.renderAtMost 5 rejected.Issues)
                                "the summary must say what was done"
                                "the check's issue"
                        | Ok() -> failtest "accepted"
                ]

            testList
                "the bridge"
                [
                    testCase "surface, setup and host agree"
                    <| fun () ->
                        let b = bridge (ResizeArray())
                        let surface = Bridge.surface b
                        Expect.equal surface.Global Product.kernelGlobal "the product's global"
                        Expect.equal (surface.Bindings |> List.map (fun d -> d.Path)) [ "math.add"; "echo" ] "bindings"
                        Expect.equal surface.Finish turnCodec.Schema "finish's schema"

                        let setup = Bridge.setup b KernelProtocol.defaultLimits (Some workspace)

                        Expect.equal setup.Bindings [ "math.add"; "echo" ] "the kernel's bindings"
                        Expect.equal setup.Global Product.kernelGlobal "the kernel's global"
                        Expect.equal setup.ModuleBase (Some workspace) "module base"
                        Expect.isEmpty (Bridge.validate b) "valid"

                    testCase "invalid paths, reserved names, duplicates and group conflicts are issues"
                    <| fun () ->
                        let named (path: string) =
                            HostBinding.create0 path "" Codec.unit (fun () -> EffResult.ok ())

                        let b =
                            Bridge.create (Finishing.create turnCodec [] (fun _ -> Eff.unit))
                            |> Bridge.add
                                []
                                [
                                    named "finish"
                                    named "activity.report"
                                    named "a..b"
                                    named "my-tool"
                                    named "fs"
                                    named "fs.read"
                                    named "x.y"
                                    named "x.y"
                                ]

                        let issues = Bridge.validate b |> List.map string
                        Expect.equal issues.Length 6 $"issues: {issues}"
                ]

            testList
                "cells"
                [
                    testCase "the intent line"
                    <| fun () ->
                        Expect.equal
                            (Cell.intent "// intent: find the callers\nconst x = 1")
                            (Some "find the callers")
                            "first line"

                        Expect.equal
                            (Cell.intent "\r\n  //intent:  trim me  \r\nx")
                            (Some "trim me")
                            "after blank lines, CRLF"

                        Expect.equal (Cell.intent "const x = 1\n// intent: late") None "only the first line"
                        Expect.equal (Cell.intent "// intent:") None "empty"
                        Expect.equal (Cell.intent "") None "no code"
                ]
        ]
