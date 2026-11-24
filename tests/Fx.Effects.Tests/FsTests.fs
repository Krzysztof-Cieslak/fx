module Fx.Effects.Tests.FsTests

open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Tests.Conformance
open Fx.Effects.Tests.TestEnv

/// An environment whose filesystem is confined to a directory: the attenuation example.
type ConfinedEnv =
    {
        Read: FsRead
        Write: Fs
    }

    interface IFs with
        member env.FsRead = env.Read
        member env.Fs = env.Write

    interface IScope with
        member _.Scope = Scope.root

    override _.ToString() = "ConfinedEnv"

let private confined (env: Env) (root: string) : ConfinedEnv =
    {
        Read = Fs.confineRead root env.FsRead
        Write = Fs.confine root env.Fs
    }

[<Tests>]
let tests =
    testList
        "Fs and InMemoryFs"
        [
            FsConformance.tests "InMemoryFs" (fun () ->
                let fs = InMemoryFs(FakeClock.Create().Clock, false)
                let root = path [ "work" ]
                fs.AddDirectory root

                {
                    Read = fs.FsRead
                    Fs = fs.Fs
                    Root = root
                    Release = ignore
                })

            test "case-insensitive mode treats names that differ in case as one" {
                let fs = InMemoryFs(FakeClock.Create().Clock, true)
                fs.AddText(path [ "Dir"; "File.txt" ], "x")

                let env =
                    { (create ()).Env with
                        FsRead = fs.FsRead
                        Fs = fs.Fs
                    }

                Expect.equal (runIn env (Fs.readText (path [ "dir"; "FILE.TXT" ]))) (Ok "x") "found"

                Expect.equal
                    (runIn env (Fs.list (path [ "DIR" ])) |> Result.map (List.map _.Name))
                    (Ok [ "File.txt" ])
                    "keeps the original case"
            }

            test "modified times come from the clock" {
                let fakes = create ()
                fakes.Clock.Advance(System.TimeSpan.FromMinutes 5.0)
                let file = path [ "f.txt" ]
                Expect.equal (runIn fakes.Env (Fs.writeText file "x")) (Ok()) "written"

                Expect.equal (runIn fakes.Env (Fs.stat file) |> Result.map _.Modified) (Ok(fakes.Clock.Now)) "modified"
            }

            test "confine attenuates to a subtree, also against dot segments" {
                let fakes = create ()
                fakes.Fs.AddText(path [ "project"; "a.txt" ], "inside")
                fakes.Fs.AddText(path [ "secret.txt" ], "outside")
                let env = confined fakes.Env (path [ "project" ])

                Expect.equal (runIn env (Fs.readText (path [ "project"; "a.txt" ]))) (Ok "inside") "inside"
                Expect.isOk (runIn env (Fs.list (path [ "project" ]))) "the root itself"

                Expect.equal
                    (runIn env (Fs.readText (path [ "secret.txt" ])))
                    (Error(AccessDenied(path [ "secret.txt" ])))
                    "outside"

                let sneaky = path [ "project"; ".."; "secret.txt" ]
                Expect.equal (runIn env (Fs.readText sneaky)) (Error(AccessDenied sneaky)) "dot segments"

                let prefix = path [ "project2"; "x.txt" ]

                Expect.equal
                    (runIn env (Fs.writeText prefix "no"))
                    (Error(AccessDenied prefix))
                    "sibling with a common prefix"

                Expect.equal
                    (runIn env (Fs.move (path [ "project"; "a.txt" ]) (path [ "stolen.txt" ])))
                    (Error(AccessDenied(path [ "stolen.txt" ])))
                    "moving out"

                match runIn env (Fs.readText "relative.txt") with
                | Error(InvalidPath _) -> ()
                | other -> failtest $"relative: {other}"
            }
        ]
