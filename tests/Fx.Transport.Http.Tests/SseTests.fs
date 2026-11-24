module Fx.Transport.Http.Tests.SseTests

open System
open System.IO
open System.Net.ServerSentEvents
open System.Text
open Expecto
open Fx.Transport.Http

/// Parses frames with the BCL's SSE parser, the one HTTP clients use.
let private parse (frames: string) : (string * string * string) list =
    use stream = new MemoryStream(Encoding.UTF8.GetBytes frames)

    [
        for item in SseParser.Create(stream).Enumerate() do
            let id =
                match item.EventId with
                | null -> "-"
                | id -> id

            id, item.EventType, item.Data
    ]

[<Tests>]
let tests =
    testList
        "Sse"
        [
            test "an event with data only" { Expect.equal (Sse.format (Sse.event "hello")) "data: hello\n\n" "frame" }

            test "id and type come before the data" {
                Expect.equal
                    (Sse.format
                        {
                            Id = Some "7"
                            Type = Some "update"
                            Data = "x"
                        })
                    "id: 7\nevent: update\ndata: x\n\n"
                    "frame"
            }

            test "every line break starts a data line" {
                Expect.equal (Sse.format (Sse.event "a\nb\r\nc\rd")) "data: a\ndata: b\ndata: c\ndata: d\n\n" "frame"
            }

            test "empty data is one empty data line" { Expect.equal (Sse.format (Sse.event "")) "data: \n\n" "frame" }

            test "Unicode line separators are data, not line breaks" {
                Expect.equal (Sse.format (Sse.event "a\u2028b\u0085c")) "data: a\u2028b\u0085c\n\n" "frame"
            }

            test "ids and types must be single lines" {
                Expect.throwsT<ArgumentException>
                    (fun () -> Sse.format { Sse.event "x" with Id = Some "1\n2" } |> ignore)
                    "id"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Sse.format
                            { Sse.event "x" with
                                Type = Some "a\rb"
                            }
                        |> ignore)
                    "type"

                Expect.throwsT<ArgumentException> (fun () -> Sse.comment "a\nb" |> ignore) "comment"
            }

            test "a comment" { Expect.equal (Sse.comment "keep-alive") ": keep-alive\n\n" "frame" }

            test "the BCL parser reads back what was formatted" {
                let frames =
                    String.concat
                        ""
                        [
                            Sse.format (Sse.event "plain")
                            Sse.comment "ignored"
                            Sse.format
                                {
                                    Id = Some "2"
                                    Type = Some "patch"
                                    Data = "line 1\r\nline 2\n  indented"
                                }
                            Sse.format
                                { Sse.event " leading space" with
                                    Id = Some "3"
                                }
                            Sse.format (Sse.event "")
                        ]

                Expect.equal
                    (parse frames)
                    [
                        "-", "message", "plain"
                        "2", "patch", "line 1\nline 2\n  indented"
                        "3", "message", " leading space"
                        // SseItem.EventId is the event's own id; the parser's LastEventId persists.
                        "-", "message", ""
                    ]
                    "events"
            }
        ]
