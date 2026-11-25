module Fx.Models.Tests.SseTests

open System
open System.IO
open System.Text
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Contracts
open Fx.Models
open Fx.Models.Tests.Support

/// 300 tests per property; set PROPERTY_TESTS to run more.
let private config =
    { FsCheckConfig.defaultConfig with
        maxTest =
            match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
            | null -> 300
            | count -> int count
    }

/// Text without line breaks, with multi-byte characters and surrogate pairs.
let private lineText: Gen<string> =
    Gen.frequency
        [
            3,
            Gen.elements
                [
                    ""
                    "a"
                    "hello world"
                    "{\"k\":1}"
                    " leading"
                    "colon: inside"
                    "ü"
                    "ż🦀ź"
                    "✓✓"
                ]
            2,
            Gen.arrayOf (
                Gen.frequency
                    [
                        6, Gen.choose (0x20, 0x7E) |> Gen.map char
                        2, Gen.choose (0xA0, 0x2FFF) |> Gen.map char
                    ]
            )
            |> Gen.map String
        ]

type private Sample =
    {
        Events: SseEvent list
        Text: string
        Cuts: int list
    }

/// Events, their text with random line endings and comments, and cuts of its UTF-8 bytes.
let private sample: Gen<Sample> =
    gen {
        let! count = Gen.choose (0, 6)

        let! events =
            Gen.listOfLength
                count
                (gen {
                    let! name =
                        Gen.elements [ "message"; "delta"; "message_start"; "response.output_text.delta" ]

                    let! lines = Gen.choose (1, 3)
                    let! data = Gen.listOfLength lines lineText
                    return Sse.event name (String.concat "\n" data)
                })

        let! endings = Gen.listOfLength 64 (Gen.elements [ "\n"; "\r\n"; "\r" ])
        let! comments = Gen.listOfLength 64 (Gen.elements [ true; false; false; false ])
        let builder = StringBuilder()
        let mutable i = 0
        let mutable last = ""

        // A "\n" right after a "\r" would join it into one "\r\n": write "\r\n" instead.
        let ending (choice: string) =
            let e = if last = "\r" && choice = "\n" then "\r\n" else choice
            last <- e
            e

        let line (text: string) =
            builder.Append(text).Append(ending endings[i % endings.Length]) |> ignore

            if comments[i % comments.Length] then
                builder.Append(": keep-alive").Append(ending endings[(i + 1) % endings.Length])
                |> ignore

            i <- i + 1

        for event in events do
            if event.Event <> "message" then
                line ("event: " + event.Event)

            for data in event.Data.Split('\n') do
                line ("data: " + data)

            line ""

        let text = builder.ToString()
        let length = utf8.GetByteCount text
        let! cuts = Gen.listOf (Gen.choose (0, max 0 length))

        return
            {
                Events = events
                Text = text
                Cuts = cuts
            }
    }

let private parseIn (pieces: byte array list) : SseEvent list =
    match Sse.parseAll pieces with
    | Ok events -> events
    | Error e -> failtest (string e)

let private parseText (text: string) = parseIn [ bytes text ]

let private names (events: SseEvent list) = events |> List.map (fun e -> e.Event)

/// The documented example streams in `streams/`: Anthropic Messages and OpenAI Responses.
let private stream (name: string) =
    File.ReadAllText(Path.Combine(sourceDirectory "streams", name))

[<Tests>]
let tests =
    testList
        "Event streams"
        [
            testPropertyWithConfig config "the events do not depend on how the bytes are cut"
            <| Prop.forAll (Arb.fromGen sample) (fun s ->
                let whole = parseText s.Text
                Expect.equal whole s.Events "whole"
                let cut = parseIn (Chunking.split (Chunking.At s.Cuts) (bytes s.Text))
                Expect.equal cut s.Events "cut")

            test "one byte at a time" {
                let text = "event: a\r\ndata: ż🦀\r\n\r\ndata: b\rdata: c\r\r"
                let pieces = bytes text |> Array.map (fun b -> [| b |]) |> List.ofArray

                Expect.equal (parseIn pieces) [ Sse.event "a" "ż🦀"; Sse.event "message" "b\nc" ] "events"
            }

            test "the WHATWG examples" {
                // The examples of the HTML standard, §9.2.6.
                let stock = parseText "data: YHOO\ndata: +2\ndata: 10\n\n"
                Expect.equal stock [ Sse.event "message" "YHOO\n+2\n10" ] "multi-line data"

                let ids =
                    parseText
                        ": test stream\n\ndata: first event\nid: 1\n\ndata:second event\nid\n\ndata:  third event\n\n"

                Expect.equal
                    (ids |> List.map (fun e -> e.Data))
                    [ "first event"; "second event"; " third event" ]
                    "data"

                Expect.equal (ids |> List.map (fun e -> e.Id)) [ Some "1"; None; None ] "ids"

                let empty = parseText "data\n\ndata\ndata\n\ndata:"

                Expect.equal
                    (empty |> List.map (fun e -> e.Data))
                    [ ""; "\n" ]
                    "empty data, and an unfinished event dropped"
            }

            test "a byte order mark at the start is skipped, once" {
                let text = "\uFEFFdata: a\n\n"
                Expect.equal (parseText text) [ Sse.event "message" "a" ] "skipped"
                let pieces = bytes text |> Array.map (fun b -> [| b |]) |> List.ofArray
                Expect.equal (parseIn pieces) [ Sse.event "message" "a" ] "skipped, in pieces"
            }

            test "fields: retry, unknown fields, ids with NUL, no space after the colon" {
                let events =
                    parseText "retry: 1500\nfoo: bar\nid: 7\nevent:x\ndata:y\n\nid: a\u0000b\ndata: z\n\n"

                Expect.equal
                    events
                    [
                        {
                            Event = "x"
                            Data = "y"
                            Id = Some "7"
                            Retry = Some 1500
                        }
                        {
                            Event = "message"
                            Data = "z"
                            Id = Some "7"
                            Retry = None
                        }
                    ]
                    "events"
            }

            test "an event without data dispatches nothing" {
                Expect.equal (parseText "event: ping\n\ndata: x\n\n") [ Sse.event "message" "x" ] "the type resets"
            }

            test "Finish tells whether an unfinished event was dropped" {
                let parser = SseParser()
                Expect.equal (parser.Push(bytes "data: a\n\ndata: b")) (Ok [ Sse.event "message" "a" ]) "first"
                Expect.isTrue (parser.Finish()) "dropped"
                let clean = SseParser()
                clean.Push(bytes "data: a\n\n") |> ignore
                Expect.isFalse (clean.Finish()) "nothing dropped"
            }

            test "invalid UTF-8 becomes U+FFFD" {
                let text = [| yield! bytes "data: a"; 0xFFuy; yield! bytes "b\n\n" |]
                Expect.equal (parseIn [ text ]) [ Sse.event "message" "a\uFFFDb" ] "replaced"
            }

            test "an event larger than the limit is an error" {
                let parser = SseParser(16)
                Expect.equal (parser.Push(bytes "data: 0123456789\n")) (Ok []) "within"
                Expect.equal (parser.Push(bytes "data: 0123456789\n")) (Error(TooLarge 16)) "beyond"
                Expect.equal (parser.Push(bytes "\n")) (Error(TooLarge 16)) "stays failed"
            }

            test "render writes what the parser reads" {
                let events =
                    [
                        Sse.event "delta" "one\ntwo"
                        Sse.event "message" ""
                        { Sse.event "x" "y" with Id = Some "9" }
                    ]

                let text = events |> List.map Sse.render |> String.concat ""
                Expect.equal (parseText text) events "round trip"
            }

            test "the Anthropic Messages example stream" {
                let text = stream "anthropic-messages.sse"
                let expected = parseText text

                Expect.equal
                    (names expected |> List.distinct)
                    [
                        "message_start"
                        "content_block_start"
                        "ping"
                        "content_block_delta"
                        "content_block_stop"
                        "message_delta"
                        "message_stop"
                    ]
                    "event types"

                for e in expected do
                    Expect.isOk (Json.parse e.Data) $"{e.Event} data is JSON"

                for n in [ 1; 2; 3; 5; 17 ] do
                    Expect.equal (parseIn (Chunking.split (Chunking.Every n) (bytes text))) expected $"every {n}"
            }

            test "the OpenAI Responses example stream" {
                let text = stream "openai-responses.sse"
                let expected = parseText text
                Expect.contains (names expected) "response.output_text.delta" "text deltas"
                Expect.contains (names expected) "response.function_call_arguments.delta" "argument deltas"
                Expect.equal (List.last expected).Event "response.completed" "completed last"

                for e in expected do
                    Expect.isOk (Json.parse e.Data) $"{e.Event} data is JSON"

                for n in [ 1; 2; 3; 5; 17 ] do
                    Expect.equal (parseIn (Chunking.split (Chunking.Every n) (bytes text))) expected $"every {n}"
            }

            testList
                "NDJSON"
                [
                    test "values per line, in any pieces, with a last line without a line feed" {
                        let text = "{\"a\":1}\r\n\n[1,\"ż\"]\n  \n\"last\""

                        let expected =
                            [ "{\"a\":1}"; "[1,\"ż\"]"; "\"last\"" ]
                            |> List.map (Json.parse >> Result.toOption >> Option.get)

                        for n in [ 1; 2; 3; 100 ] do
                            let parser = NdjsonParser()
                            let mutable values = []

                            for piece in Chunking.split (Chunking.Every n) (bytes text) do
                                match parser.Push piece with
                                | Ok more -> values <- values @ more
                                | Error e -> failtest (string e)

                            match parser.Finish() with
                            | Ok more -> values <- values @ more
                            | Error e -> failtest (string e)

                            Expect.equal values expected $"every {n}"
                    }

                    test "a line that is not JSON is an error with its number" {
                        let parser = NdjsonParser()

                        match parser.Push(bytes "1\n{oops\n") with
                        | Error(InvalidLine(2, _)) -> ()
                        | other -> failtest $"expected an invalid line 2, got {other}"
                    }
                ]
        ]
