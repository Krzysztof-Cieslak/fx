/// The transport-parity suite (spec §1 principle 9, §16.4): a function from a way to load the sample
/// extension into a host to tests. The sample is written once (extensions/Fx.Ext.Sample); every
/// runtime that can host it, in process or out, passes the same suite. T109 and T115 run it on their
/// samples: link this file and supply a loader.
module Fx.Extensions.Tests.ParitySuite

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Extensions
open Fx.Sdk
open Fx.Ext.Sample
open Fx.Aot.Scenarios.Extensions
open Fx.Aot.Scenarios.Extensions.SampleHost

/// What every runtime must produce for `SampleHost.transcript`.
let expectedTranscript: string list =
    let ext = Sample.id

    [
        $"report: {ext} activated"
        $"gates: {ext}/polite, {ext}/fragile"
        $"transforms: {ext}/trim, {ext}/shout, {ext}/bracket, {ext}/guard"
        "words: alpha, beta"
        $"observers: {ext}/recorder, {ext}/flaky"
        "decide \"Ada\" en: allow"
        $"decide \"\" en: deny ({ext}/polite: a greeting needs a name)"
        $"decide \"Ada\" fr: ask ({ext}/polite: greet in fr?)"
        $"decide \"crash\" en: deny ({ext}/fragile: the gate failed)"
        $"decide \"\" fr: deny ({ext}/polite: a greeting needs a name)"
        "transform \"  hi \": \"[HI!]\""
        "transform \"fine\": \"[FINE!]\""
        $"transform \"please explode\": {ext}/transform:sample.text/guard failed"
        "observe one: failures []"
        $"observe boom: failures [{ext}/flaky]"
        "observe two: failures []"
        "observed: one, boom, two"
        "echo: ok hello"
        "divide 7 2: ok 3"
        "divide 1 0: typed error division by zero"
        "fail: crashed"
        "divide without a divisor: invalid params $.divisor: missing required field"
        "unknown method: unknown method nope"
    ]

let private using (load: unit -> Task<Loaded>) (body: Loaded -> Task<unit>) : Task<unit> =
    task {
        let! loaded = within (load ())

        try
            do! body loaded
        finally
            (loaded.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
    }

let private run (loaded: Loaded) (effect: Eff<HostEnv, 'T>) : Task<'T> = within (Eff.run loaded.Env effect)

let tests (name: string) (load: unit -> Task<Loaded>) : Test =
    testList
        name
        [
            testTask "the transcript of every hook and method" {
                do!
                    using load (fun loaded ->
                        task {
                            let! lines = transcript loaded
                            Expect.sequenceEqual lines expectedTranscript "transcript"
                        })
            }

            testTask "the registry records the admission and the activation, and replays" {
                do!
                    using load (fun loaded ->
                        task {
                            let host = loaded.Host

                            Expect.equal
                                (host.Events |> List.map (fun e -> e.ToString().Split(' ')[0]))
                                [ "admitted"; "activated" ]
                                "events"

                            Expect.equal (Registry.replay host.Events) host.State "replay"
                            Expect.equal (Registry.active host.State) [ Sample.id ] "active"
                        })
            }

            testTask "contributions carry their extension, id and data" {
                do!
                    using load (fun loaded ->
                        task {
                            let words = ExtensionHost.contributions Sample.Points.words loaded.Host

                            Expect.equal
                                (words |> List.map (fun w -> w.Extension, w.Type, w.Id, w.Value))
                                [
                                    Sample.id, "sample.word", "alpha", "alpha"
                                    Sample.id, "sample.word", "beta", "beta"
                                ]
                                "words"

                            let transforms = ExtensionHost.contributions Sample.Points.text loaded.Host

                            Expect.equal
                                (transforms |> List.map (fun t -> t.Id, t.Value.Priority))
                                [ "trim", 10; "shout", 20; "bracket", 20; "guard", 0 ]
                                "priorities travel"

                            Expect.isEmpty
                                (ExtensionHost.contributions ExtensionRuntimes.contributionType loaded.Host)
                                "no runtimes"
                        })
            }

            testTask "each activation has its own state" {
                do!
                    using load (fun first ->
                        task {
                            let observers = ExtensionHost.contributions Sample.Points.notes first.Host
                            let! _ = run first (Hooks.observe observers ({ Text = "only here" }: Sample.Note))

                            do!
                                using load (fun second ->
                                    task {
                                        let! seen =
                                            run
                                                second
                                                (ExtensionHost.callTyped
                                                    second.Host
                                                    Sample.id
                                                    Sample.Methods.observed
                                                    ())

                                        Expect.equal seen (Ok []) "a fresh activation"
                                    })

                            let! seen =
                                run first (ExtensionHost.callTyped first.Host Sample.id Sample.Methods.observed ())

                            Expect.equal seen (Ok [ "only here" ]) "the first keeps its own"
                        })
            }

            testTask "a disabled extension contributes nothing and answers no calls" {
                do!
                    using load (fun loaded ->
                        task {
                            match! run loaded (ExtensionHost.disable loaded.Host Sample.id "test") with
                            | Error rejection -> failtest (string rejection)
                            | Ok host ->
                                Expect.isEmpty (ExtensionHost.contributions Sample.Points.greeting host) "no gates"

                                let! verdict =
                                    run
                                        loaded
                                        (Hooks.decide
                                            (ExtensionHost.contributions Sample.Points.greeting host)
                                            ({ Name = ""; Language = "en" }: Sample.Greeting))

                                Expect.equal verdict Verdict.Allow "nothing to deny"

                                let! call =
                                    run
                                        loaded
                                        (ExtensionHost.call
                                            host
                                            Sample.id
                                            "echo"
                                            (Json.Object [ "text", Json.String "x" ]))

                                match call with
                                | Error(MethodFailure.Unreachable _) -> ()
                                | other -> failtest $"expected unreachable, got {other}"
                        })
            }
        ]
