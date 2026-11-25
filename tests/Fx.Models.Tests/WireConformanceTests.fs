/// The wire-conformance suite on the canonical wire, and proof that it catches broken wires.
module Fx.Models.Tests.WireConformanceTests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Ext.ModelCanonical
open Fx.Models
open Fx.Models.Tests.Conformance
open Fx.Models.Tests.Conformance.WireConformance
open Fx.Models.Tests.Support
open Fx.Sdk

/// The cases as the files hold them (written by the golden test below).
let private cases () =
    if Directory.Exists CanonicalCases.directory then
        WireCase.loadDirectory CanonicalCases.directory
    else
        []

/// A wire that parses each piece of the body on its own: it loses events cut across pieces.
let private piecewise: ChatWire =
    {
        Family = Canonical.Family
        Stream =
            fun request ->
                fun ctx ->
                    task {
                        match Canonical.request (ctx, request.Request) with
                        | Error e -> return ChatStream.failed e
                        | Ok http ->
                            match! ctx.Http.Send http ctx.Scope.Cancellation with
                            | Error e -> return ChatStream.failed (Transport e)
                            | Ok response when response.Status <> 200 ->
                                do! response.Body.Close()
                                return ChatStream.failed (ServerError(response.Status, ""))
                            | Ok response ->
                                let pending = Queue<ChatEvent>()
                                let mutable ended = false

                                let next (cancellation: CancellationToken) =
                                    task {
                                        while pending.Count = 0 && not ended do
                                            match! response.Body.Read cancellation with
                                            | Ok(Some piece) ->
                                                match SseParser().Push piece with
                                                | Ok events ->
                                                    for event in events do
                                                        for e in Canonical.decode ctx event do
                                                            pending.Enqueue e
                                                | Error _ -> ()
                                            | Ok None
                                            | Error _ -> ended <- true

                                        return if pending.Count = 0 then None else Some(pending.Dequeue())
                                    }

                                return
                                    {
                                        Next = next
                                        Close = response.Body.Close
                                    }
                    }
    }

/// A wire whose streams ignore the caller's cancellation.
let private deaf: ChatWire =
    { Canonical.wire with
        Stream =
            fun request ->
                fun ctx ->
                    task {
                        let! stream = Canonical.wire.Stream request ctx

                        return
                            { stream with
                                Next = fun _ -> stream.Next CancellationToken.None
                            }
                    }
    }

/// A wire that sends its requests to the wrong path.
let private misdirected: ChatWire =
    SseWire.create
        { Canonical.adapter with
            Request =
                fun (ctx, chat) ->
                    Canonical.request (ctx, chat)
                    |> Result.map (fun http ->
                        { http with
                            Url = WireCtx.url ctx "/complete"
                        })
        }

/// A wire that maps every error status to a server error.
let private careless: ChatWire =
    SseWire.create
        { Canonical.adapter with
            Error = fun response -> ServerError(response.Status, response.Body)
        }

/// A wire that drops provider state.
let private forgetful: ChatWire =
    SseWire.create
        { Canonical.adapter with
            Decode =
                fun (state, event) ->
                    let decoded = Canonical.adapter.Decode(state, event)

                    { decoded with
                        Events =
                            decoded.Events
                            |> List.filter (fun e ->
                                match e with
                                | ChatEvent.PartState _ -> false
                                | _ -> true)
                    }
        }

/// A wire that throws when the connection breaks instead of failing the stream.
let private throwing: ChatWire =
    { Canonical.wire with
        Stream =
            fun request ->
                fun ctx ->
                    task {
                        let! stream = Canonical.wire.Stream request ctx

                        return
                            { stream with
                                Next =
                                    fun cancellation ->
                                        task {
                                            match! stream.Next cancellation with
                                            | Some(ChatEvent.Failed(Transport _)) -> return raise (IOException "reset")
                                            | other -> return other
                                        }
                            }
                    }
    }

/// The names of the checks a wire fails on a case, among those named.
let private failures (wire: ChatWire) (caseName: string) (names: string list) : string list =
    let case = CanonicalCases.all |> List.find (fun c -> c.Name = caseName)

    WireConformance.caseChecks wire case
    |> List.filter (fun c -> List.contains c.Name names)
    |> List.choose (fun c ->
        try
            c.Run()
            None
        with _ ->
            Some c.Name)

let private named = "sends the recorded request and folds the response as expected"
let private chunked = "gives the same events however the body is cut"

let private replayed =
    "replays the provider state verbatim in the follow-up request"

let private cancelled = "ends at once when cancelled while waiting for the provider"

let private reset =
    "reports a connection that breaks off mid-stream as a transport failure"

[<Tests>]
let tests =
    testList
        "Wire conformance"
        [
            testList
                "canonical cases"
                [
                    for case in CanonicalCases.all do
                        test $"{case.Name} is current (golden)" {
                            expectFile (CanonicalCases.path case) (WireCase.render case)
                        }

                    test "the files round-trip" {
                        for case in cases () do
                            Expect.equal (WireCase.parse (WireCase.render case)) (Ok case) case.Name
                    }

                    test "every case has a file and no file is stale" {
                        let files =
                            Directory.GetFiles(CanonicalCases.directory, "*.json")
                            |> Array.map Path.GetFileNameWithoutExtension
                            |> Array.sort
                            |> List.ofArray

                        let names = CanonicalCases.all |> List.map (fun c -> c.Name) |> List.sort
                        Expect.equal files names "case files"
                    }
                ]

            WireConformance.tests Canonical.wire (cases ())

            testList
                "the suite catches broken wires"
                [
                    test "a wire that loses events cut across pieces" {
                        Expect.equal (failures piecewise "unicode" [ named; chunked ]) [ chunked ] "fails"
                    }

                    test "a wire that ignores cancellation" {
                        Expect.equal (failures deaf "text" [ named; cancelled ]) [ cancelled ] "fails"
                    }

                    test "a wire that sends another request" {
                        Expect.equal (failures misdirected "text" [ named ]) [ named ] "fails"
                    }

                    test "a wire that maps errors wrong" {
                        Expect.equal (failures careless "rate-limited" [ named ]) [ named ] "fails"
                        Expect.equal (failures careless "context-overflow" [ named ]) [ named ] "fails"
                    }

                    test "a wire that drops provider state" {
                        Expect.equal (failures forgetful "tool-call" [ named; replayed ]) [ named; replayed ] "fails"
                    }

                    test "a wire that throws on a broken connection" {
                        Expect.equal (failures throwing "text" [ named; reset ]) [ reset ] "fails"
                    }
                ]
        ]
