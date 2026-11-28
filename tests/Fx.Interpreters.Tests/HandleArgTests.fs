/// Handles as binding arguments and results (T201b): `Arg.valueOrHandle` takes a value or a handle the
/// session's own table issued, of the declared type; `HostBinding.createHandle1` issues one.
module Fx.Interpreters.Tests.HandleArgTests

open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Interpreters
open Fx.Kernel

type private IdsEnv =
    {
        Ids: Ids
        Scope: Scope
    }

    interface IIds with
        member env.Ids = env.Ids

    interface IScope with
        member env.Scope = env.Scope

let private env =
    {
        Ids = FakeIds(1UL, FakeClock(Instant.unixEpoch).Clock).Ids
        Scope = Scope.root
    }

let private run (effect: Eff<IdsEnv, 'T>) : 'T = (effect env).Result

let private preview = { Text = "x"; Truncated = false }

[<Tests>]
let tests =
    testList
        "handles as arguments and results"
        [
            testCase "a value, or a handle of the declared type from the session's own table"
            <| fun () ->
                let own = HandleKind.create "Thing" "A thing." (HandleTable<string>())
                let other = HandleKind.create "Thing" "A thing." (HandleTable<string>())
                let otherType = HandleKind.create "Other" "Another." own.Table
                let arg = Arg.valueOrHandle "thing" "" Codec.string own

                Expect.equal (arg.Decode(Some(Value.Inline(Json.String "t1")))) (Ok(Choice1Of2 "t1")) "a value"

                let mine = run (HandleTable.issue own.Table own.Type preview "t1")
                Expect.equal (arg.Decode(Some mine)) (Ok(Choice2Of2 "t1")) "its own handle"

                let foreign = run (HandleTable.issue other.Table other.Type preview "t2")
                Expect.isError (arg.Decode(Some foreign)) "another session's handle"

                let wrong = run (HandleTable.issue otherType.Table otherType.Type preview "t3")
                Expect.isError (arg.Decode(Some wrong)) "a handle of another type"

            testCase "a binding that returns a handle issues it from the kind's table"
            <| fun () ->
                let kind = HandleKind.create "Thing" "A thing." (HandleTable<string>())

                let binding =
                    HostBinding.createHandle1 "things.open" "" (Arg.required "id" "" Codec.string) kind (fun id ->
                        EffResult.ok (id, preview))

                match run (binding.Invoke [ Value.Inline(Json.String "t1") ]) with
                | Ok(Value.Handle(id, typeRef, _)) ->
                    Expect.equal typeRef kind.Type "its type"
                    Expect.equal (HandleTable.resolveAs kind.Table kind.Type id) (Ok "t1") "resolves to its object"
                | other -> failtest $"{other}"
        ]
