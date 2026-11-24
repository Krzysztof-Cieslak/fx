/// The hook algebra (spec §16.3) and its laws, as properties.
module Fx.Sdk.Tests.HookTests

open System
open System.Threading
open Expecto
open Fx.Effects
open Fx.Sdk
open Fx.Sdk.Tests.Support

/// A small domain, so that properties often meet equal reasons and ties.
let private decisionOf (kind: byte) (message: byte) : Decision =
    let message = if message % 2uy = 0uy then "x" else "y"

    match kind % 3uy with
    | 0uy -> Decision.Allow
    | 1uy -> Decision.Ask message
    | _ -> Decision.Deny message

let private byOf (b: byte) = [| "a"; "b"; "c" |][int (b % 3uy)]

let private verdictOf (inputs: (byte * byte * byte) list) : Verdict =
    inputs
    |> List.map (fun (by, kind, message) -> Verdict.ofDecision (byOf by) (decisionOf kind message))
    |> Verdict.combineAll

let private gateOf (kind: byte) (message: byte) : Gate<int> =
    {
        Check = fun _ -> Eff.ret (decisionOf kind message)
    }

let private failing: Gate<int> = { Check = fun _ -> invalidOp "broken" }

let private failingAsync: Gate<int> =
    {
        Check = fun _ -> Eff.ofTask (fun () -> Tasks.Task.FromException<Decision>(TimeoutException()))
    }

let private gates (inputs: (byte * byte * byte) list) : Contributed<Gate<int>> list =
    inputs
    |> List.mapi (fun i (by, kind, message) -> contributed (byOf by) (string i) (gateOf kind message))

[<Tests>]
let verdicts =
    testList
        "Gate"
        [
            property "combining is commutative" (fun (a: (byte * byte * byte) list) (b: (byte * byte * byte) list) ->
                let a, b = verdictOf a, verdictOf b
                Expect.equal (Verdict.combine a b) (Verdict.combine b a) "commutative")

            property
                "combining is associative"
                (fun (a: (byte * byte * byte) list) (b: (byte * byte * byte) list) (c: (byte * byte * byte) list) ->
                    let a, b, c = verdictOf a, verdictOf b, verdictOf c

                    Expect.equal
                        (Verdict.combine (Verdict.combine a b) c)
                        (Verdict.combine a (Verdict.combine b c))
                        "associative")

            property "Allow is the identity, and combining is idempotent" (fun (a: (byte * byte * byte) list) ->
                let a = verdictOf a
                Expect.equal (Verdict.combine Verdict.Allow a) a "left identity"
                Expect.equal (Verdict.combine a Verdict.Allow) a "right identity"
                Expect.equal (Verdict.combine a a) a "idempotent")

            property
                "the result is the maximum: Deny > Ask > Allow"
                (fun (a: (byte * byte * byte) list) (b: (byte * byte * byte) list) ->
                    let a, b = verdictOf a, verdictOf b

                    Expect.equal
                        (Verdict.severity (Verdict.combine a b))
                        (max (Verdict.severity a) (Verdict.severity b))
                        "max")

            test "reasons at the winning level are kept, sorted and without duplicates" {
                let ask = Verdict.ofDecision "b" (Decision.Ask "sure?")
                let deny = Verdict.ofDecision "c" (Decision.Deny "no")
                let deny2 = Verdict.ofDecision "a" (Decision.Deny "never")
                Expect.equal (Verdict.combine ask deny) deny "Deny drops the Ask reasons"

                Expect.equal
                    (Verdict.combineAll [ deny; ask; deny2; deny ])
                    (Verdict.Deny [ { By = "a"; Message = "never" }; { By = "c"; Message = "no" } ])
                    "sorted"

                Expect.equal (string (Verdict.combine deny deny2)) "deny (a: never; c: no)" "text"
            }

            property "deciding does not depend on the order of the gates" (fun (inputs: (byte * byte * byte) list) ->
                let forward = run (Hooks.decide (gates inputs) 0)
                let backward = run (Hooks.decide (List.rev (gates inputs)) 0)

                let sorted =
                    run (Hooks.decide (gates inputs |> List.sortBy (fun g -> (int g.Id * 7919) % 13)) 0)

                Expect.equal backward forward "reversed"
                Expect.equal sorted forward "shuffled")

            property "deciding combines every gate's decision" (fun (inputs: (byte * byte * byte) list) ->
                let expected =
                    gates inputs
                    |> List.map (fun g ->
                        let (_, kind, message) = inputs[int g.Id]
                        Verdict.ofDecision g.By (decisionOf kind message))
                    |> Verdict.combineAll

                Expect.equal (run (Hooks.decide (gates inputs) 0)) expected "combined")

            property
                "a gate that fails denies, whatever the others say"
                (fun (inputs: (byte * byte * byte) list) (asynchronously: bool) (at: byte) ->
                    let all = gates inputs
                    let position = if all.IsEmpty then 0 else int at % (all.Length + 1)

                    let broken =
                        contributed "z" "broken" (if asynchronously then failingAsync else failing)

                    let mixed = List.take position all @ [ broken ] @ List.skip position all

                    match run (Hooks.decide mixed 0) with
                    | Verdict.Deny reasons ->
                        Expect.contains
                            reasons
                            {
                                By = "z/broken"
                                Message = "the gate failed"
                            }
                            "the failure is a reason"
                    | other -> failtest $"expected deny, got {other}")

            test "no gates allow" {
                Expect.equal (run (Hooks.decide ([]: Contributed<Gate<int>> list) 0)) Verdict.Allow "allow"
            }

            test "a gate runs in its extension's environment with the caller's scope" {
                let seen = ResizeArray<string * bool>()

                let gate: Gate<int> =
                    {
                        Check =
                            fun _ ->
                                fun env ->
                                    seen.Add(env.Extension, env.Scope.Cancellation.CanBeCanceled)
                                    Tasks.Task.FromResult Decision.Allow
                    }

                use cancellation = new CancellationTokenSource()

                let verdict =
                    (Eff.run
                        {
                            Scope = Scope.ofCancellation cancellation.Token
                        }
                        (Hooks.decide [ contributed "acme" "g" gate ] 0))
                        .GetAwaiter()
                        .GetResult()

                Expect.equal verdict Verdict.Allow "verdict"
                Expect.equal (List.ofSeq seen) [ "acme", true ] "extension's environment, caller's scope"
            }

            test "cancelling the caller cancels the gates and fails with the cancellation" {
                let waiting: Gate<int> =
                    {
                        Check =
                            fun _ ->
                                fun env ->
                                    task {
                                        do! Tasks.Task.Delay(-1, env.Scope.Cancellation)
                                        return Decision.Allow
                                    }
                    }

                use cancellation = new CancellationTokenSource()

                let decided =
                    Eff.run
                        {
                            Scope = Scope.ofCancellation cancellation.Token
                        }
                        (Hooks.decide [ contributed "acme" "slow" waiting ] 0)

                cancellation.Cancel()

                Expect.isSome
                    (throws<OperationCanceledException>(fun () ->
                        decided.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult() |> ignore))
                    "cancelled"
            }
        ]

let private appending (priority: int) (tag: char) : Transform<string> =
    Transform.ofFunction priority (fun s -> s + string tag)

[<Tests>]
let transforms =
    testList
        "Transform"
        [
            property
                "transforms apply in ascending priority, ties in registration order"
                (fun (inputs: (byte * byte) list) ->
                    let tagOf (tag: byte) = char (int 'a' + int (tag % 26uy))

                    let items =
                        inputs
                        |> List.mapi (fun i (priority, tag) ->
                            contributed "x" (string i) (appending (int (priority % 4uy)) (tagOf tag)))

                    let expected =
                        inputs
                        |> List.indexed
                        |> List.sortBy (fun (i, (priority, _)) -> int (priority % 4uy), i)
                        |> List.map (fun (_, (_, tag)) -> string (tagOf tag))
                        |> String.Concat

                    Expect.equal (run (Hooks.transform items "")) expected "order")

            property
                "identity transforms change nothing, wherever they are"
                (fun (inputs: (byte * byte) list) (at: byte) ->
                    let items =
                        inputs
                        |> List.mapi (fun i (priority, tag) ->
                            contributed
                                "x"
                                (string i)
                                (appending (int (priority % 4uy)) (char (65 + int (tag % 26uy)))))

                    let position = int at % (items.Length + 1)

                    let withIdentity =
                        List.take position items
                        @ [ contributed "x" "identity" (Transform.identity (int at % 4)) ]
                        @ List.skip position items

                    Expect.equal (run (Hooks.transform withIdentity "")) (run (Hooks.transform items "")) "neutral")

            property
                "composition is associative: running two lists in turn is running their concatenation"
                (fun (a: (byte * byte) list) (b: (byte * byte) list) ->
                    // With one priority, order is registration order, so the lists compose in sequence.
                    let items (tags: (byte * byte) list) offset =
                        tags
                        |> List.mapi (fun i (_, tag) ->
                            contributed "x" (string (i + offset)) (appending 0 (char (65 + int (tag % 26uy)))))

                    let first, second = items a 0, items b a.Length
                    let inTurn = run (Hooks.transform second (run (Hooks.transform first "")))
                    Expect.equal (run (Hooks.transform (first @ second) "")) inTurn "associative")

            test "a failing transform fails the pipeline: nothing goes out untransformed" {
                let explode: Transform<string> =
                    {
                        Priority = 1
                        Apply = fun _ -> invalidOp "boom"
                    }

                let items =
                    [
                        contributed "x" "a" (appending 0 'a')
                        { contributed "acme" "explode" explode with
                            Type = "transform:t"
                        }
                        contributed "x" "c" (appending 2 'c')
                    ]

                match throws<ContributionFailedException>(fun () -> run (Hooks.transform items "") |> ignore) with
                | Some e ->
                    Expect.equal (string e.Address) "acme/transform:t/explode" "address"
                    Expect.equal e.Detail "InvalidOperationException" "detail"
                    Expect.equal e.Message "acme/transform:t/explode failed" "message"
                | None -> failtest "no failure"
            }
        ]

[<Tests>]
let contributeObserveInterpret =
    testList
        "Contribute, Observe, Interpret"
        [
            property "collected values are the concatenation, in order" (fun (a: int list) (b: int list) ->
                let items (values: int list) =
                    values |> List.mapi (fun i v -> contributed "x" (string i) v)

                Expect.equal (Hooks.collect (items a @ items b)) (a @ b) "concatenation"
                Expect.equal (Hooks.collect (items [])) [] "identity")

            property
                "every observer sees the event; failures are isolated and reported in order"
                (fun (failingAt: bool list) ->
                    let seen = Collections.Concurrent.ConcurrentBag<string>()

                    let observers =
                        failingAt
                        |> List.mapi (fun i fails ->
                            contributed
                                "x"
                                (string i)
                                {
                                    Observe =
                                        fun event ->
                                            seen.Add(string i + ":" + event)

                                            if fails then invalidOp "observer failed" else Eff.unit
                                })

                    let failures = run (Hooks.observe observers "e")
                    Expect.equal seen.Count failingAt.Length "everyone saw it"

                    Expect.equal
                        (failures |> List.map (fun f -> f.By))
                        (failingAt
                         |> List.indexed
                         |> List.filter snd
                         |> List.map (fun (i, _) -> "x/" + string i))
                        "failures in order"

                    Expect.isTrue (failures |> List.forall (fun f -> f.Detail = "InvalidOperationException")) "details")

            test "observers run concurrently" {
                let started = new CountdownEvent(2)

                let waitForBoth: Observer<string> =
                    {
                        Observe =
                            fun _ ->
                                Eff.ofTask (fun () ->
                                    Tasks.Task.Run<unit>(
                                        Func<unit>(fun () ->
                                            started.Signal() |> ignore
                                            started.Wait(TimeSpan.FromSeconds 10.0) |> ignore)
                                    ))
                    }

                let failures =
                    run (Hooks.observe [ contributed "x" "a" waitForBoth; contributed "x" "b" waitForBoth ] "e")

                Expect.equal failures [] "both finished"
                Expect.isTrue started.IsSet "both ran at once"
            }

            property
                "middleware: the lowest priority is outermost; identity middleware changes nothing"
                (fun (priorities: byte list) ->
                    let log = ResizeArray<string>()

                    let layer (name: string) (priority: int) : Middleware<string -> string> =
                        {
                            Priority = priority
                            Wrap =
                                fun inner input ->
                                    log.Add name
                                    inner (input + name)
                        }

                    let items =
                        priorities
                        |> List.mapi (fun i p -> contributed "x" (string i) (layer (string i) (int (p % 5uy))))

                    let order =
                        items |> List.sortBy (fun m -> m.Value.Priority) |> List.map (fun m -> m.Id)

                    let result = (Hooks.interpret items id) ""
                    Expect.equal (List.ofSeq log) order "calls go outermost first"
                    Expect.equal result (String.Concat order) "and reach the inner interpreter"

                    let identity = contributed "x" "identity" { Priority = 2; Wrap = id }
                    log.Clear()
                    Expect.equal ((Hooks.interpret (identity :: items) id) "") result "identity")
        ]
