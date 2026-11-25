/// The wire-conformance suite on `openai.responses`, over the case files (`Cases`), and the golden
/// check that keeps the files current.
module Fx.Ext.ModelOpenAI.Tests.ConformanceTests

open System.IO
open Expecto
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models.Tests.Conformance
open Fx.Models.Tests.Conformance.WireConformance

/// The cases as the files hold them: the hand-written ones, and any recorded ones.
let private cases () =
    if Directory.Exists Cases.directory then
        WireCase.loadDirectory Cases.directory
    else
        []

[<Tests>]
let tests =
    testList
        "openai.responses conformance"
        [
            testList
                "case files"
                [
                    for case in Cases.all do
                        test $"{case.Name} is current (golden)" { expectFile (Cases.path case) (WireCase.render case) }

                    test "every hand-written case has a file" {
                        let files =
                            Directory.GetFiles(Cases.directory, "*.json")
                            |> Array.map Path.GetFileNameWithoutExtension
                            |> Set.ofArray

                        for case in Cases.all do
                            Expect.isTrue (files.Contains case.Name) $"{case.Name}.json"
                    }
                ]

            WireConformance.tests OpenAI.wire (cases ())
        ]
