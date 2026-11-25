module Fx.Kernel.Tests.FramingTests

open System
open System.Text
open Expecto
open Fx.Kernel
open Fx.Rpc

let private utf8 (text: string) = Encoding.UTF8.GetBytes text

let private frame (n: int) =
    $"{{\"jsonrpc\":\"2.0\",\"method\":\"m\",\"params\":{{\"n\":{n}}}}}"

let private lines (input: byte array) =
    match KernelFraming.split 4096 input with
    | Ok lines -> lines
    | Error e -> failtest (string e)

/// Feeds the input in pieces of the given sizes, then ends it.
let private feed (pieces: int list) (input: byte array) : Result<KernelLine list, TransportError> =
    let reader = KernelLineReader(4096, 256)
    let output = ResizeArray()
    let mutable offset = 0
    let mutable error = None

    for size in pieces do
        if error.IsNone && offset < input.Length then
            let count = min size (input.Length - offset)

            match reader.Feed(ReadOnlySpan(input, offset, count)) with
            | Ok got -> output.AddRange got
            | Error e -> error <- Some e

            offset <- offset + count

    if error.IsNone && offset < input.Length then
        match reader.Feed(ReadOnlySpan(input, offset, input.Length - offset)) with
        | Ok got -> output.AddRange got
        | Error e -> error <- Some e

    match error with
    | Some e -> Error e
    | None ->
        match reader.End() with
        | Ok rest -> Ok(List.ofSeq output @ rest)
        | Error e -> Error e

/// Joins adjacent stray pieces, which a reader may report in any split.
let private normalize (lines: KernelLine list) =
    lines
    |> List.fold
        (fun acc line ->
            match line, acc with
            | KernelLine.Stray text, KernelLine.Stray previous :: rest -> KernelLine.Stray(previous + text) :: rest
            | line, acc -> line :: acc)
        []
    |> List.rev
    |> List.map string

[<Tests>]
let tests =
    testList
        "kernel framing"
        [
            test "messages on their own lines" {
                let input = utf8 (frame 1 + "\n" + frame 2 + "\r\n\n")
                Expect.equal (lines input) [ KernelLine.Frame(frame 1); KernelLine.Frame(frame 2) ] "two frames"
            }

            test "a message after stray bytes on the same line is recovered" {
                let input = utf8 ("stray text" + frame 1 + "\n")

                Expect.equal (lines input) [ KernelLine.Stray "stray text"; KernelLine.Frame(frame 1) ] "both"
            }

            test "lines without the prefix are stray, zero bytes and invalid UTF-8 included" {
                let input =
                    Array.concat
                        [
                            utf8 "a\u0000b\n"
                            [| 0xffuy; 0xfeuy; 10uy |]
                            utf8 "[1,2]\n"
                            utf8 (frame 3)
                        ]

                match lines input with
                | [ KernelLine.Stray "a\u0000b"; KernelLine.Stray invalid; KernelLine.Stray "[1,2]"; KernelLine.Frame f ] ->
                    Expect.equal invalid "\ufffd\ufffd" "replacement characters"
                    Expect.equal f (frame 3) "the last line without a newline"
                | other -> failtest $"{other}"
            }

            test "a line that starts like a message but is not JSON is stray" {
                let broken = "{\"jsonrpc\":\"2.0\",oops"
                Expect.equal (lines (utf8 (broken + "\n"))) [ KernelLine.Stray broken ] "stray"
            }

            test "a long stray line is reported in pieces, and a message after it still found" {
                let stray = String('s', 1000)

                match feed [ 100; 100; 100; 100; 100; 100; 100; 100; 100; 100 ] (utf8 (stray + frame 7 + "\n")) with
                | Ok got ->
                    Expect.isGreaterThan
                        (got
                         |> List.filter (fun l ->
                             match l with
                             | KernelLine.Stray _ -> true
                             | _ -> false)
                         |> List.length)
                        1
                        "in pieces"

                    Expect.equal
                        (normalize got)
                        [ string (KernelLine.Stray stray); string (KernelLine.Frame(frame 7)) ]
                        "all of it"
                | Error e -> failtest (string e)
            }

            test "a message larger than the limit ends the stream" {
                let big =
                    "{\"jsonrpc\":\"2.0\",\"method\":\"m\",\"params\":\""
                    + String('x', 5000)
                    + "\"}"

                match feed [ 1000; 1000; 1000; 1000; 1000; 1000 ] (utf8 (big + "\n")) with
                | Error(TransportError.TooLarge 4096) -> ()
                | other ->
                    let shown =
                        match other with
                        | Ok l -> $"{l.Length} lines"
                        | Error e -> string e

                    failtest shown
            }

            testProperty "splitting the input anywhere gives the same lines"
            <| fun (cuts: byte list) ->
                let input =
                    utf8 (
                        "noise "
                        + frame 1
                        + "\n"
                        + frame 2
                        + "\r\n\nmore noise\n"
                        + "x"
                        + frame 3
                        + "\n\u00e9\u2713"
                    )

                let pieces = cuts |> List.map (fun c -> int c % 17 + 1)
                let whole = feed [ input.Length ] input |> Result.map normalize
                let split = feed pieces input |> Result.map normalize
                split = whole
        ]
