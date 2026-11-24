/// The ids conformance suite: what every `Ids` interpreter must do. Also compiled into
/// Fx.Effects.Live.Tests.
module Fx.Effects.Tests.Conformance.IdsConformance

open System
open System.Threading.Tasks
open Expecto
open Fx.Effects

/// Tests that the interpreters made by `create` conform. `create` is called once per test.
let tests (name: string) (create: unit -> Ids) : Test =
    testList
        $"Ids conformance ({name})"
        [
            testCase "ids strictly increase, as values and as text" (fun () ->
                let ids = create ()
                let generated = List.init 5000 (fun _ -> ids.NewId())

                for a, b in List.pairwise generated do
                    Expect.isLessThan a b "values increase"
                    Expect.isLessThan (String.CompareOrdinal(string a, string b)) 0 "text increases")

            testCase "the text form round-trips" (fun () ->
                let ids = create ()

                for _ in 1..100 do
                    let id = ids.NewId()
                    Expect.equal (Id.tryParse (string id)) (Some id) "parsed")

            testCase "ids from many threads are unique" (fun () ->
                let ids = create ()

                let generated =
                    Array.init 8 (fun _ -> Task.Run(fun () -> Array.init 1000 (fun _ -> ids.NewId())))
                    |> Task.WhenAll
                    |> _.Result
                    |> Array.concat

                Expect.equal (Array.distinct generated).Length generated.Length "unique")

            testCase "random bytes have the requested length and differ" (fun () ->
                let ids = create ()
                Expect.equal (ids.RandomBytes 0).Length 0 "empty"
                let a = ids.RandomBytes 32
                let b = ids.RandomBytes 32
                Expect.equal a.Length 32 "length"
                Expect.notEqual a b "differ")
        ]
