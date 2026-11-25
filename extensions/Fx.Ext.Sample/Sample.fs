/// The sample extension: one of each hook kind, data, and methods with typed errors, written once
/// against the public SDK. The transport-parity tests run it in the host (`native` runtime) and in a
/// child process (`process` runtime) and expect the same behaviour; it is the reference for writing
/// a built-in. It is not bundled.
module Fx.Ext.Sample.Sample

open System
open System.Collections.Concurrent
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// What the greeting gate decides about.
type Greeting =
    {
        Name: string
        Language: string
    }

    override this.ToString() = $"greet {this.Name} in {this.Language}"

type Note =
    {
        Text: string
    }

    override this.ToString() = this.Text

type Division =
    {
        Dividend: int
        Divisor: int
    }

    override this.ToString() = $"{this.Dividend} / {this.Divisor}"

/// The typed errors of `divide`.
type MathError =
    | DivideByZero

    override this.ToString() =
        match this with
        | DivideByZero -> "division by zero"

module Codecs =
    let greeting: Codec<Greeting> =
        Codec.record {
            let! name = Codec.field "name" (fun (g: Greeting) -> g.Name) Codec.string
            and! language = Codec.field "language" (fun g -> g.Language) Codec.string
            return { Name = name; Language = language }
        }
        |> Codec.named "Greeting"

    let note: Codec<Note> =
        Codec.record {
            let! text = Codec.field "text" (fun (n: Note) -> n.Text) Codec.string
            return { Text = text }
        }
        |> Codec.named "Note"

    let division: Codec<Division> =
        Codec.record {
            let! dividend = Codec.field "dividend" (fun (d: Division) -> d.Dividend) Codec.int
            and! divisor = Codec.field "divisor" (fun d -> d.Divisor) Codec.int

            return
                {
                    Dividend = dividend
                    Divisor = divisor
                }
        }
        |> Codec.named "Division"

    let mathError: Codec<MathError> =
        Codec.union "kind" {
            let! divideByZero = Codec.case0 "divideByZero" DivideByZero

            return
                fun value ->
                    match value with
                    | DivideByZero -> divideByZero
        }

/// The hook points the sample hooks into. They belong to whoever runs them: here, the tests, which
/// reference these values and add them to the host's catalog.
module Points =
    let greeting =
        Hook.gate "sample.greeting" "Decides whether a greeting may be sent." Codecs.greeting

    let text =
        Hook.transform "sample.text" "Rewrites a text before it is shown." Codec.string

    let words =
        Hook.contribute "sample.word" "Words for the sample's vocabulary." Codec.string

    let notes = Hook.observe "sample.note" "Sees every note that is posted." Codecs.note

    /// For the host's catalog.
    let all: ContributionTypeInfo list =
        [
            ContributionType.info greeting
            ContributionType.info text
            ContributionType.info words
            ContributionType.info notes
        ]

/// The sample's RPC contract: `ext/<id>/<name>`.
module Methods =
    let echo =
        ExtMethod.define "echo" Codecs.note Codecs.note
        |> ExtMethod.describe "Returns the note it is given."

    let divide =
        ExtMethod.define "divide" Codecs.division Codec.int
        |> ExtMethod.withErrors Codecs.mathError
        |> ExtMethod.describe "Integer division; dividing by zero is a typed error."

    let observed =
        ExtMethod.define "observed" Codec.unit (Codec.list Codec.string)
        |> ExtMethod.describe "The notes the recorder has seen, in order."

    let fail =
        ExtMethod.define "fail" Codec.unit Codec.unit
        |> ExtMethod.describe "Throws: a defect."

let id = Product.extensionId "sample"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "The sample extension: one of each hook kind, data and methods."
        Capabilities = [ "clock" ]
        Contributions =
            [
                Declare.contribution Points.greeting "polite"
                Declare.contribution Points.greeting "fragile"
                Declare.contribution Points.text "trim"
                Declare.contribution Points.text "shout"
                Declare.contribution Points.text "bracket"
                Declare.contribution Points.text "guard"
                Declare.contribution Points.words "alpha"
                Declare.contribution Points.words "beta"
                Declare.contribution Points.notes "recorder"
                Declare.contribution Points.notes "flaky"
            ]
        Rpc =
            {
                Methods =
                    [
                        Declare.method Methods.echo
                        Declare.method Methods.divide
                        Declare.method Methods.observed
                        Declare.method Methods.fail
                    ]
            }
    }

let private languages = set [ "en"; "pl" ]

/// Builds the contributions; the recorder's notes are the state of this activation.
let activate: Eff<IExtensionEnv, Activation> =
    eff {
        let notes = ConcurrentQueue<string>()

        let polite: Gate<Greeting> =
            {
                Check =
                    fun greeting ->
                        Eff.ret (
                            if String.IsNullOrWhiteSpace greeting.Name then
                                Decision.Deny "a greeting needs a name"
                            elif not (languages.Contains greeting.Language) then
                                Decision.Ask $"greet in {greeting.Language}?"
                            else
                                Decision.Allow
                        )
            }

        let fragile: Gate<Greeting> =
            {
                Check =
                    fun greeting ->
                        if greeting.Name = "crash" then
                            invalidOp "the fragile gate broke"
                        else
                            Eff.ret Decision.Allow
            }

        let guard: Transform<string> =
            {
                Priority = 0
                Apply =
                    fun text ->
                        if text.Contains "explode" then
                            invalidOp "the guard exploded"
                        else
                            Eff.ret text
            }

        let recorder: Observer<Note> =
            {
                Observe =
                    fun note ->
                        notes.Enqueue note.Text
                        Eff.unit
            }

        let flaky: Observer<Note> =
            {
                Observe =
                    fun note ->
                        if note.Text = "boom" then
                            invalidOp "the flaky observer failed"
                        else
                            Eff.unit
            }

        return
            Activation.create
                [
                    Contribution.create Points.greeting "polite" polite
                    Contribution.create Points.greeting "fragile" fragile
                    Contribution.create Points.text "trim" (Transform.ofFunction 10 (fun (s: string) -> s.Trim()))
                    Contribution.create
                        Points.text
                        "shout"
                        (Transform.ofFunction 20 (fun s -> s.ToUpperInvariant() + "!"))
                    Contribution.create Points.text "bracket" (Transform.ofFunction 20 (fun s -> "[" + s + "]"))
                    Contribution.create Points.text "guard" guard
                    Contribution.create Points.words "alpha" "alpha"
                    Contribution.create Points.words "beta" "beta"
                    Contribution.create Points.notes "recorder" recorder
                    Contribution.create Points.notes "flaky" flaky
                ]
                [
                    ExtMethod.handle Methods.echo (fun note -> EffResult.ok note)
                    ExtMethod.handle Methods.divide (fun d ->
                        if d.Divisor = 0 then
                            EffResult.error DivideByZero
                        else
                            EffResult.ok (d.Dividend / d.Divisor))
                    ExtMethod.handle Methods.observed (fun () -> EffResult.ok (List.ofSeq notes))
                    ExtMethod.handle Methods.fail (fun () -> invalidOp "the sample failed")
                ]
    }

/// The sample as a native extension.
let extension: Extension =
    {
        Manifest = manifest
        Activate = activate
    }
