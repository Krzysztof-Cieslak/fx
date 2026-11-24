module Fx.Effects.Live.Tests.LocalFsTests

open System.IO
open Expecto
open Fx.Effects
open Fx.Effects.Live
open Fx.Effects.Live.Tests.LiveEnv
open Fx.Effects.Tests.Conformance

[<Tests>]
let tests =
    testList
        "LocalFs"
        [
            FsConformance.tests "LocalFs" (fun () ->
                let root = tempDirectory ()

                {
                    Read = LocalFs.read
                    Fs = LocalFs.write
                    Root = root
                    Release = fun () -> removeDirectory root
                })

            test "modified times come from the filesystem" {
                let root = tempDirectory ()

                try
                    let env = create root
                    let file = Path.Combine(root, "f.txt")
                    Expect.equal (runIn env (Fs.writeText file "x")) (Ok()) "written"

                    match runIn env (Fs.stat file) with
                    | Ok entry ->
                        Expect.equal
                            entry.Modified
                            (Instant.ofDateTimeOffset (System.DateTimeOffset(File.GetLastWriteTimeUtc file)))
                            "modified"
                    | Error e -> failtest (string e)
                finally
                    removeDirectory root
            }
        ]
