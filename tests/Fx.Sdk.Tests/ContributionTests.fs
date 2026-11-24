module Fx.Sdk.Tests.ContributionTests

open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Sdk
open Fx.Sdk.Tests.Support

let private words = Hook.contribute "test.word" "Words." Codec.string
let private numbers = Hook.contribute "test.number" "Numbers." Codec.int
let private gate = Hook.gate "test.gate" "A gate." Codec.string
let private transform = Hook.transform "test.text" "A transform." Codec.string
let private observer = Hook.observe "test.event" "An observer." Codec.string

let private middleware: ContributionType<Middleware<int -> int>> =
    Hook.interpret "test.fn" "Middleware."

/// A remote whose peer answers every invocation with `answer` and records the inputs.
let private remote (answer: Json -> Result<Json, RemoteFailure>) (seen: ResizeArray<Address * Json>) : Remote =
    let unexpected () = invalidOp "unexpected"

    {
        Peer =
            {
                Activate = fun _ _ -> unexpected ()
                Invoke =
                    fun address input _ ->
                        seen.Add(address, input)
                        Task.FromResult(answer input)
                Call = fun _ _ _ _ -> unexpected ()
                Deactivate = fun _ _ -> unexpected ()
            }
        Address =
            {
                Extension = "acme"
                Type = "t"
                Id = "x"
            }
    }

[<Tests>]
let tests =
    testList
        "Contributions"
        [
            test "a contribution keeps its type: the same type gets the value back, another gets nothing" {
                let c = Contribution.create words "hello" "hello"
                Expect.equal (Contribution.tryValue words c) (Some "hello") "same type"
                Expect.equal (Contribution.tryValue numbers c) None "other type"

                Expect.equal
                    c.Decl
                    {
                        Type = "test.word"
                        Id = "hello"
                        Value = Some(Json.String "hello")
                    }
                    "decl"

                Expect.equal (string c) "test.word/hello" "text"
            }

            test "two types with one name and different value types are a defect" {
                let impostor = Hook.contribute "test.word" "Not words." Codec.int
                let c = Contribution.create words "hello" "hello"

                Expect.throwsT<System.InvalidOperationException>
                    (fun () -> Contribution.tryValue impostor c |> ignore)
                    "defect"
            }

            test "hook point names carry their kind" {
                Expect.equal gate.Name "gate:test.gate" "gate"
                Expect.equal transform.Name "transform:test.text" "transform"
                Expect.equal observer.Name "observe:test.event" "observe"
                Expect.equal middleware.Name "interpret:test.fn" "interpret"
                Expect.equal words.Name "test.word" "data"

                Expect.equal
                    ([ gate.Kind; transform.Kind; observer.Kind; middleware.Kind; words.Kind ]
                     |> List.map string)
                    [ "gate"; "transform"; "observe"; "interpret"; "data" ]
                    "kinds"

                Expect.isFalse gate.Privileged "not privileged by default"
                Expect.isTrue (ContributionType.privileged gate).Privileged "privileged"
                Expect.isTrue ExtensionRuntimes.contributionType.Privileged "runtimes are privileged"
            }

            test "data loads from its JSON form, with issues" {
                let info = ContributionType.info numbers

                match info.Load "n" (Some(Json.int 3)) None with
                | Ok c -> Expect.equal (Contribution.tryValue numbers c) (Some 3) "value"
                | Error issues -> failtest (Issue.render issues)

                Expect.equal
                    (info.Load "n" (Some(Json.String "x")) None |> Result.mapError (List.map string))
                    (Error [ "$: expected integer, got string \"x\"" ])
                    "issue"

                Expect.equal
                    (info.Load "n" None None |> Result.mapError (List.map string))
                    (Error [ "$: missing value" ])
                    "missing"
            }

            test "code cannot come from a declarative manifest" {
                for info in
                    [
                        ContributionType.info gate
                        ContributionType.info transform
                        ContributionType.info observer
                    ] do
                    match info.Load "x" None None with
                    | Error [ issue ] -> Expect.stringContains issue.Message "is code" info.Name
                    | other -> failtest (string (Result.map string other))

                match
                    (ContributionType.info middleware).Load
                        "x"
                        None
                        (Some(remote (fun _ -> Ok Json.Null) (ResizeArray())))
                with
                | Error [ issue ] -> Expect.stringContains issue.Message "in the host's process only" "middleware"
                | other -> failtest (string (Result.map string other))
            }

            test "a remote gate sends the intent and decodes the decision" {
                let seen = ResizeArray()

                let answer _ =
                    Ok(Decision.codec.Encode(Decision.Ask "really?"))

                match gate.OfJson None (Some(remote answer seen)) with
                | Error issues -> failtest (Issue.render issues)
                | Ok g ->
                    let decision = run (ExtensionEnv.within (extEnv "acme") (g.Check "rm -rf"))
                    Expect.equal decision (Decision.Ask "really?") "decision"
                    Expect.equal (List.ofSeq seen |> List.map snd) [ Json.String "rm -rf" ] "intent"
            }

            test "a remote failure or an answer that does not decode is the contribution's failure" {
                for answer in [ Error(RemoteFailure.Failed "boom"); Ok(Json.String "maybe") ] do
                    match gate.OfJson None (Some(remote (fun _ -> answer) (ResizeArray()))) with
                    | Error issues -> failtest (Issue.render issues)
                    | Ok g ->
                        let failure =
                            throws<ContributionFailedException>(fun () ->
                                run (ExtensionEnv.within (extEnv "acme") (g.Check "x")) |> ignore)

                        match failure with
                        | Some e -> Expect.equal (string e.Address) "acme/t/x" "address"
                        | None -> failtest "no failure"
            }

            test "a remote transform takes its priority from its JSON form" {
                match
                    transform.OfJson (Some(Json.Object [ "priority", Json.int 7 ])) (Some(remote Ok (ResizeArray())))
                with
                | Ok t ->
                    Expect.equal t.Priority 7 "priority"
                    Expect.equal (run (ExtensionEnv.within (extEnv "acme") (t.Apply "same"))) "same" "echoed"
                | Error issues -> failtest (Issue.render issues)

                match transform.OfJson None (Some(remote Ok (ResizeArray()))) with
                | Ok t -> Expect.equal t.Priority 0 "default priority"
                | Error issues -> failtest (Issue.render issues)

                Expect.equal
                    (transform.ToJson(Transform.ofFunction 3 id))
                    (Some(Json.Object [ "priority", Json.int 3 ]))
                    "settings"
            }

            test "serving decodes the input with the hook's codec and encodes the output" {
                let g: Gate<string> =
                    {
                        Check = fun s -> Eff.ret (if s = "ok" then Decision.Allow else Decision.Deny s)
                    }

                let c = Contribution.create gate "g" g

                match c.Serve with
                | None -> failtest "a gate is servable"
                | Some serve ->
                    let env = extEnv "acme"
                    let served = (serve (Json.String "no") env).GetAwaiter().GetResult()

                    Expect.equal
                        served
                        (Ok(Json.Object [ "decision", Json.String "deny"; "reason", Json.String "no" ]))
                        "decision"

                    let invalid = (serve (Json.int 1) env).GetAwaiter().GetResult()

                    Expect.equal
                        (invalid |> Result.mapError (List.map string))
                        (Error [ "$: expected string, got number 1" ])
                        "issues"

                Expect.isNone (Contribution.create words "w" "w").Serve "data is not served"

                Expect.isNone
                    (Contribution.create middleware "m" { Priority = 0; Wrap = id }).Serve
                    "middleware is not served"
            }

            test "Declare builds manifest entries from typed values" {
                Expect.equal
                    (Declare.contribution gate "g")
                    {
                        Type = "gate:test.gate"
                        Id = "g"
                        Value = None
                    }
                    "code"

                Expect.equal
                    (Declare.value numbers "n" 4)
                    {
                        Type = "test.number"
                        Id = "n"
                        Value = Some(Json.int 4)
                    }
                    "data"
            }
        ]
