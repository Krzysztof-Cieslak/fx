/// The wire-conformance suite (spec §10, §20 "wire-adapter conformance"): what every chat wire
/// adapter must do, checked over recorded exchanges (cassettes). A function from a wire and its cases
/// to tests; adapters' test projects link this file and supply their cases (`cassettes/<family>/`).
///
/// Every case runs through `ModelAccess` (the only way to invoke a wire) on a `CassetteHttp`, which
/// fails the test if the adapter sends anything but the recorded request. Per case it checks:
/// the request shape and the events' fold against the expectation (response or typed error, stop
/// reason, usage); the stream rules; the same events however the body is cut into pieces; usage that
/// never decreases and ends as reported; provider state that survives its JSON byte for byte and is
/// replayed verbatim in a follow-up request; a connection that breaks off mid-stream, a stalled
/// stream and a failed connection as typed transport failures (events, not exceptions); and
/// cancelling a stream that waits for the provider.
module Fx.Models.Tests.Conformance.WireConformance

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Models
open Fx.Sdk

/// What a wire must make of a case's response.
[<RequireQualifiedAccess>]
type Expected =
    | Response of ChatResponse
    | Failure of ChatError

    override this.ToString() =
        match this with
        | Response r -> string r
        | Failure e -> string e

/// A second request that continues the conversation after the response (the assistant's message,
/// then these messages): it must replay the response's provider state.
type FollowUp =
    {
        Messages: Message list
        /// Whether each state's data must appear verbatim as a JSON string in the follow-up's body.
        /// Adapters that unpack a state into several fields turn it off and rely on the cassette.
        StatesVerbatim: bool
    }

    override this.ToString() =
        $"follow-up ({this.Messages.Length} messages)"

/// One conformance case: a request, the provider exchange recorded for it (and for the follow-up),
/// and what the wire must make of it.
type WireCase =
    {
        Name: string
        /// The provider's base URL for the wire's family.
        Endpoint: string
        Request: ChatRequest
        /// The first interaction answers the request; the second, the follow-up.
        Cassette: Cassette
        Expect: Expected
        FollowUp: FollowUp option
    }

    override this.ToString() = this.Name

module WireCase =
    let private expected: Codec<Expected> =
        Codec.record {
            let! response =
                Codec.optionalField
                    "response"
                    (fun e ->
                        match e with
                        | Expected.Response r -> Some r
                        | Expected.Failure _ -> None)
                    ChatResponse.codec

            and! failure =
                Codec.optionalField
                    "failure"
                    (fun e ->
                        match e with
                        | Expected.Failure f -> Some f
                        | Expected.Response _ -> None)
                    ChatError.codec

            return
                match response, failure with
                | Some r, _ -> Expected.Response r
                | None, Some f -> Expected.Failure f
                | None, None -> Expected.Failure(Unavailable "the case expects nothing")
        }

    let private followUp: Codec<FollowUp> =
        Codec.record {
            let! messages =
                Codec.defaultField "messages" (fun (f: FollowUp) -> f.Messages) [] (Codec.list Message.codec)

            and! verbatim =
                Codec.defaultField "statesVerbatim" (fun f -> f.StatesVerbatim) true Codec.bool

            return
                {
                    Messages = messages
                    StatesVerbatim = verbatim
                }
        }

    /// The JSON of a case file.
    let codec: Codec<WireCase> =
        Codec.record {
            let! name = Codec.field "name" (fun (c: WireCase) -> c.Name) Codec.string
            and! endpoint = Codec.field "endpoint" (fun c -> c.Endpoint) Codec.string
            and! request = Codec.field "request" (fun c -> c.Request) ChatRequest.codec

            and! interactions = Codec.field "cassette" (fun c -> c.Cassette) Cassette.codec

            and! expect = Codec.field "expect" (fun c -> c.Expect) expected
            and! follow = Codec.optionalField "followUp" (fun c -> c.FollowUp) followUp

            return
                {
                    Name = name
                    Endpoint = endpoint
                    Request = request
                    Cassette = interactions
                    Expect = expect
                    FollowUp = follow
                }
        }

    let parse (text: string) : Result<WireCase, string> =
        Codec.decodeText codec text
        |> Result.mapError (fun issues -> Issue.renderAtMost 10 issues)

    let render (case: WireCase) : string =
        Json.serializeIndented (codec.Encode case)

    /// Every `*.json` case in a directory, by file name.
    let loadDirectory (directory: string) : WireCase list =
        [
            for path in Directory.GetFiles(directory, "*.json") |> Array.sort do
                match parse (File.ReadAllText path) with
                | Ok case -> case
                | Error issues -> failwith $"{Path.GetFileName path}: {issues}"
        ]

/// One check of one case: a name, and code that throws when the wire fails it.
type Check =
    {
        Case: string
        Name: string
        Run: unit -> unit
    }

    override this.ToString() = $"{this.Case}: {this.Name}"

/// What running a request through a wire gave.
type private Run =
    {
        Events: ChatEvent list
        Http: CassetteHttp
    }

let private limit = TimeSpan.FromSeconds 30.0

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(limit).GetAwaiter().GetResult()

let private provider (wire: ChatWire) (case: WireCase) : ProviderDef =
    ProviderDef.create case.Request.Model.Provider wire.Family case.Endpoint

let private access (wire: ChatWire) (case: WireCase) (http: Http) (timeouts: HttpTimeouts) : ModelAccess =
    { ModelAccess.create [ wire ] [ provider wire case ] http (FakeClock.Create()).Clock with
        Timeouts = timeouts
    }

/// Pulls every event of a stream (each pull bounded), then closes it.
let private drain (stream: ChatStream) (cancellation: CancellationToken) : ChatEvent list =
    let events = ResizeArray<ChatEvent>()
    let mutable reading = true

    try
        while reading do
            match wait (stream.Next cancellation) with
            | Some event -> events.Add event
            | None -> reading <- false
    finally
        wait (stream.Close())

    List.ofSeq events

let private start
    (wire: ChatWire)
    (case: WireCase)
    (http: CassetteHttp)
    (timeouts: HttpTimeouts)
    (request: ChatRequest)
    =
    let llm = ModelAccess.llm (access wire case http.Http timeouts)
    wait (llm.Stream request CancellationToken.None)

let private runWith
    (wire: ChatWire)
    (case: WireCase)
    (cassette: Cassette)
    (chunking: Chunking)
    (timeouts: HttpTimeouts)
    : Run =
    let http = CassetteHttp(cassette, chunking)
    let stream = start wire case http timeouts case.Request
    let events = drain stream CancellationToken.None

    { Events = events; Http = http }

let private result (events: ChatEvent list) =
    ChatAggregate.result (ChatAggregate.ofEvents events)

let private expectRules (what: string) (events: ChatEvent list) =
    match ChatEvents.check events with
    | [] -> ()
    | broken ->
        let list = String.concat "\n  " broken
        failtest $"{what}: the stream breaks the event rules:\n  {list}"

let private expectOutcome (expected: Expected) (events: ChatEvent list) =
    match expected, result events with
    | Expected.Response response, Ok actual ->
        Expect.equal actual.Parts response.Parts "the parts"
        Expect.equal actual.Stop response.Stop "the stop reason"
        Expect.equal actual.Usage response.Usage "the usage"
        Expect.equal actual.Id response.Id "the response id"
        Expect.equal actual.Model response.Model "the model"
    | Expected.Response _, Error failure -> failtest $"expected a response, got {failure}"
    | Expected.Failure error, Error failure -> Expect.equal failure.Error error "the typed error"
    | Expected.Failure error, Ok response -> failtest $"expected {error}, got {response}"

/// The body of the first interaction cut in half, ending as given.
let private cut (ending: BodyEnd) (cassette: Cassette) : Cassette option =
    match cassette.Interactions with
    | {
          Outcome = RecordedOutcome.Response response
      } as first :: rest when response.Status >= 200 && response.Status <= 299 && response.Body.Length >= 2 ->
        let half = response.Body.Substring(0, response.Body.Length / 2)

        let half =
            // Never cut a surrogate pair.
            if Char.IsHighSurrogate half[half.Length - 1] then
                half.Substring(0, half.Length - 1)
            else
                half

        Some
            {
                Interactions =
                    { first with
                        Outcome =
                            RecordedOutcome.Response
                                { response with
                                    Body = half
                                    End = ending
                                }
                    }
                    :: rest
            }
    | _ -> None

/// Pieces to cut bodies into: small fixed sizes (splitting lines, line endings and UTF-8 sequences),
/// and pseudo-random cuts from fixed seeds (the same on every run).
let chunkings (bodyLength: int) : Chunking list =
    let random (seed: int) =
        let mutable state = (uint32 seed * 2654435761u) ||| 1u

        let next () =
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            int (state % uint32 (max 1 bodyLength))

        Chunking.At [ for _ in 1 .. max 1 (bodyLength / 5) -> next () ]

    [
        Chunking.Every 1
        Chunking.Every 2
        Chunking.Every 3
        Chunking.Every 7
        Chunking.Every 64
    ]
    @ [ for seed in 1..8 -> random seed ]

let private bodyLength (case: WireCase) : int =
    match case.Cassette.Interactions with
    | { Outcome = RecordedOutcome.Response r } :: _ -> UTF8Encoding(false).GetByteCount r.Body
    | _ -> 0

let private isSuccess (case: WireCase) : bool =
    match case.Cassette.Interactions with
    | { Outcome = RecordedOutcome.Response r } :: _ -> r.Status >= 200 && r.Status <= 299
    | _ -> false

/// The checks of one case.
let caseChecks (wire: ChatWire) (case: WireCase) : Check list =
    let check name run =
        {
            Case = case.Name
            Name = name
            Run = run
        }

    let followUps = if case.FollowUp.IsSome then 1 else 0

    let whole () =
        runWith wire case case.Cassette Chunking.Whole HttpTimeouts.none

    [
        check "sends the recorded request and folds the response as expected" (fun () ->
            let run = whole ()
            Expect.equal run.Http.Remaining.Length followUps "every interaction of the request was used"
            expectRules "whole body" run.Events
            expectOutcome case.Expect run.Events
            Expect.equal run.Http.OpenBodies 0 "no body is left open")

        check "gives the same events however the body is cut" (fun () ->
            let reference = whole ()
            let expected = ChatEvents.coalesce reference.Events

            for chunking in chunkings (bodyLength case) do
                let run = runWith wire case case.Cassette chunking HttpTimeouts.none
                expectRules $"cut {chunking}" run.Events
                Expect.equal (ChatEvents.coalesce run.Events) expected $"the events, cut {chunking}"
                Expect.equal (result run.Events) (result reference.Events) $"the outcome, cut {chunking}")

        check "accounts usage: never decreasing, ending as reported" (fun () ->
            let run = whole ()

            let reports =
                run.Events
                |> List.choose (fun e ->
                    match e with
                    | ChatEvent.UsageUpdated u -> Some u
                    | _ -> None)

            for earlier, later in List.pairwise reports do
                Expect.isTrue (Usage.isMonotonic earlier later) $"usage decreased: {earlier} then {later}"

            let final = reports |> List.tryLast |> Option.defaultValue Usage.zero

            match result run.Events, case.Expect with
            | Ok response, _ -> Expect.equal response.Usage final "the response's usage is the last report"
            | Error failure, _ -> Expect.equal failure.Usage final "the failure's usage is the last report")

        check "stops for the recorded reason" (fun () ->
            let run = whole ()

            match case.Expect, result run.Events with
            | Expected.Response expected, Ok actual -> Expect.equal actual.Stop expected.Stop "stop reason"
            | Expected.Failure expected, Error actual -> Expect.equal actual.Error expected "typed error"
            | _, outcome -> failtest $"unexpected outcome {outcome}")

        check "keeps provider state byte for byte through its JSON" (fun () ->
            let run = whole ()

            let parts =
                match result run.Events with
                | Ok response -> response.Parts
                | Error failure -> failure.Parts

            let text = Codec.encodeText (Codec.list Part.codec) parts

            match Codec.decodeText (Codec.list Part.codec) text with
            | Ok decoded ->
                let before = parts |> List.choose Part.state |> List.map (fun s -> s.Data)
                let after = decoded |> List.choose Part.state |> List.map (fun s -> s.Data)
                Expect.sequenceEqual after before "state data"

                for a, b in List.zip before after do
                    Expect.isTrue (String.Equals(a, b, StringComparison.Ordinal)) "ordinal equality"
            | Error issues -> failtest $"the parts do not decode: {Issue.renderAtMost 5 issues}")

        match case.FollowUp with
        | None -> ()
        | Some follow ->
            check "replays the provider state verbatim in the follow-up request" (fun () ->
                let http = CassetteHttp(case.Cassette, Chunking.Whole)

                let first =
                    drain (start wire case http HttpTimeouts.none case.Request) CancellationToken.None

                match result first with
                | Error failure -> failtest $"the first response failed: {failure}"
                | Ok response ->
                    let next =
                        { case.Request with
                            Messages = case.Request.Messages @ [ ChatResponse.message response ] @ follow.Messages
                        }

                    let second =
                        drain (start wire case http HttpTimeouts.none next) CancellationToken.None

                    Expect.equal http.Remaining [] "every interaction was used"
                    expectRules "the follow-up" second

                    match result second with
                    | Ok _ -> ()
                    | Error failure -> failtest $"the follow-up failed: {failure}"

                    let states = response.Parts |> List.choose Part.state
                    Expect.isNonEmpty states "the response has provider state to replay"

                    if follow.StatesVerbatim then
                        let sent = http.Sent |> List.last

                        let strings =
                            match Json.parse (HttpRequest.bodyText sent) with
                            | Ok json ->
                                let rec collect (json: Json) : string list =
                                    match json with
                                    | Json.String s -> [ s ]
                                    | Json.Array items -> items |> List.collect collect
                                    | Json.Object members -> members |> List.collect (snd >> collect)
                                    | _ -> []

                                collect json
                            | Error e -> failtest $"the follow-up body is not JSON: {e}"

                        for state in states do
                            Expect.isTrue
                                (strings
                                 |> List.exists (fun s -> String.Equals(s, state.Data, StringComparison.Ordinal)))
                                $"the follow-up carries the {state.Kind} state verbatim")

        if isSuccess case then
            check "reports a connection that breaks off mid-stream as a transport failure" (fun () ->
                match cut BodyEnd.Reset case.Cassette with
                | None -> ()
                | Some broken ->
                    let full = whole ()
                    let run = runWith wire case broken Chunking.Whole HttpTimeouts.none
                    expectRules "broken off" run.Events

                    if result run.Events <> result full.Events then
                        match result run.Events with
                        | Error { Error = Transport(HttpError.Reset _) } -> ()
                        | other -> failtest $"expected a transport reset, got {other}"

                    Expect.equal run.Http.OpenBodies 0 "the body is closed")

            check "reports a stalled stream as a timeout" (fun () ->
                match cut BodyEnd.Hang case.Cassette with
                | None -> ()
                | Some stalled ->
                    let full = whole ()

                    let timeouts =
                        { HttpTimeouts.none with
                            Idle = Some(TimeSpan.FromSeconds 5.0)
                        }

                    let run = runWith wire case stalled Chunking.Whole timeouts
                    expectRules "stalled" run.Events

                    if result run.Events <> result full.Events then
                        match result run.Events with
                        | Error {
                                    Error = Transport(HttpError.TimedOut HttpPhase.Body)
                                } -> ()
                        | other -> failtest $"expected a body timeout, got {other}")

            check "ends at once when cancelled while waiting for the provider" (fun () ->
                match cut BodyEnd.Hang case.Cassette with
                | None -> ()
                | Some stalled ->
                    let http = CassetteHttp(stalled, Chunking.Every 16)
                    let stream = start wire case http HttpTimeouts.none case.Request
                    use cancel = new CancellationTokenSource()
                    let mutable waiting = None

                    // Pull until a pull waits (the provider sends nothing more) or the stream ends.
                    while waiting.IsNone do
                        let next = stream.Next cancel.Token

                        if next.Wait(TimeSpan.FromMilliseconds 200.0) then
                            match next.Result with
                            | Some _ -> ()
                            | None -> waiting <- Some None
                        else
                            waiting <- Some(Some next)

                    match waiting with
                    | Some(Some pending) ->
                        cancel.Cancel()

                        let outcome =
                            try
                                pending.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult() |> ignore
                                "completed"
                            with
                            | :? OperationCanceledException -> "cancelled"
                            | :? TimeoutException -> "still waiting"
                            | e -> e.GetType().Name

                        Expect.equal outcome "cancelled" "the waiting pull is cancelled"
                    | _ -> ()

                    wait (stream.Close())
                    Expect.equal http.OpenBodies 0 "closing releases the body")

        check "reports a failed connection as a transport failure" (fun () ->
            let refused =
                {
                    Interactions =
                        case.Cassette.Interactions
                        |> List.mapi (fun i interaction ->
                            if i = 0 then
                                { interaction with
                                    Outcome = RecordedOutcome.Failure(HttpError.ConnectFailed "the connection failed")
                                }
                            else
                                interaction)
                }

            let run = runWith wire case refused Chunking.Whole HttpTimeouts.none
            expectRules "connection failed" run.Events

            match result run.Events with
            | Error {
                        Error = Transport(HttpError.ConnectFailed _)
                    } -> ()
            | other -> failtest $"expected a connection failure, got {other}")
    ]

/// Every check of every case.
let checks (wire: ChatWire) (cases: WireCase list) : Check list = cases |> List.collect (caseChecks wire)

/// The suite for a wire and its cases.
let tests (wire: ChatWire) (cases: WireCase list) : Test =
    testList
        $"Wire conformance ({wire.Family})"
        [
            test "there are cases" { Expect.isNonEmpty cases "conformance cases" }

            for case in cases do
                testList case.Name [ for c in caseChecks wire case -> testCase c.Name c.Run ]
        ]
