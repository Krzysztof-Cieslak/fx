module Fx.Rpc.Tests.FramingTests

open System
open System.Text
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Rpc

let private limits = { MaxText = 4096; MaxBinary = 4096 }

let private frame: Gen<Frame> =
    Gen.oneof
        [
            ArbMap.defaults
            |> ArbMap.generate<string>
            |> Gen.map (fun s ->
                let s = if String.IsNullOrEmpty s then "" else s
                // Text frames hold compact JSON: no raw line breaks, never empty, never a leading NUL.
                let clean =
                    s
                    |> String.map (fun c ->
                        if c = '\n' || c = '\r' || Char.IsSurrogate c then
                            ' '
                        else
                            c)

                Frame.Text("x" + clean))
            Gen.arrayOf (Gen.choose (0, 255) |> Gen.map byte) |> Gen.map Frame.Binary
        ]

/// Decodes a stream fed in chunks of the given sizes, as a reader would see it.
let private decodeChunked (bytes: byte array) (chunks: int list) : Result<Frame list, string> =
    let buffer = ResizeArray<byte>()
    let frames = ResizeArray<Frame>()
    let mutable fed = 0
    let mutable failure = None
    let mutable sizes = chunks

    let drain (atEnd: bool) =
        let mutable go = true

        while go && failure.IsNone do
            let data = buffer.ToArray()

            match StreamFraming.decode limits data 0 data.Length atEnd with
            | Decoded.Frame(f, n) ->
                frames.Add f
                buffer.RemoveRange(0, n)
            | Decoded.Skip n -> buffer.RemoveRange(0, n)
            | Decoded.Incomplete -> go <- false
            | Decoded.Invalid e -> failure <- Some(string e)

    while fed < bytes.Length && failure.IsNone do
        let size =
            match sizes with
            | s :: rest ->
                sizes <- rest
                max 1 s
            | [] -> bytes.Length

        let n = min size (bytes.Length - fed)
        buffer.AddRange(ArraySegment(bytes, fed, n))
        fed <- fed + n
        drain false

    drain true

    match failure with
    | Some e -> Error e
    | None when buffer.Count > 0 -> Error "left over"
    | None -> Ok(List.ofSeq frames)

let private one (bytes: byte array) (atEnd: bool) =
    string (StreamFraming.decode { MaxText = 8; MaxBinary = 4 } bytes 0 bytes.Length atEnd)

let private channelFrame: Gen<ChannelFrame> =
    let channel = Gen.choose64 (0L, int64 UInt32.MaxValue) |> Gen.map uint32

    Gen.oneof
        [
            channel |> Gen.map ChannelFrame.Open
            Gen.map2
                (fun c (d: byte array) -> ChannelFrame.Data(c, ArraySegment d))
                channel
                (Gen.arrayOf (Gen.constant 7uy))
            Gen.map2 (fun c n -> ChannelFrame.Credit(c, n)) channel (channel)
            channel |> Gen.map ChannelFrame.Close
            Gen.map3
                (fun c code reason -> ChannelFrame.Reset(c, code, reason))
                channel
                channel
                (Gen.elements [ ""; "refused"; "é🌍" ])
        ]

let private same (a: ChannelFrame) (b: ChannelFrame) =
    match a, b with
    | ChannelFrame.Data(c1, d1), ChannelFrame.Data(c2, d2) -> c1 = c2 && d1.ToArray() = d2.ToArray()
    | _ -> string a = string b

[<Tests>]
let tests =
    testList
        "Framing"
        [
            testProperty "frames survive any chunking of the stream"
            <| Prop.forAll
                (Arb.fromGen (Gen.zip (Gen.listOf frame) (Gen.listOf (Gen.choose (1, 64)))))
                (fun (frames, chunks) ->
                    let bytes = frames |> List.map StreamFraming.encode |> Array.concat
                    decodeChunked bytes chunks = Ok frames)

            test "text frames are lines; CR before LF and empty lines are ignored" {
                let bytes = Encoding.UTF8.GetBytes "{\"a\":1}\r\n\n\r\n[]\n{\"last\":true}"

                Expect.equal
                    (decodeChunked bytes [ 3 ])
                    (Ok [ Frame.Text "{\"a\":1}"; Frame.Text "[]"; Frame.Text "{\"last\":true}" ])
                    "lines"
            }

            test "the decoder reports what it found" {
                Expect.equal (one (Encoding.UTF8.GetBytes "{\"a\"") false) "incomplete" "partial line"
                Expect.equal (one (Encoding.UTF8.GetBytes "{\"a\"") true) "text (4 chars) in 4 bytes" "last line"

                Expect.equal
                    (one (Encoding.UTF8.GetBytes "12345678\r\n") false)
                    "text (8 chars) in 10 bytes"
                    "at the limit"

                Expect.equal (one (Encoding.UTF8.GetBytes "123456789\n") false) "frame larger than 8 bytes" "over"

                Expect.equal
                    (one (Encoding.UTF8.GetBytes "1234567890") false)
                    "frame larger than 8 bytes"
                    "no newline yet"

                Expect.equal (one [| 0xC3uy; 0x28uy; 10uy |] false) "malformed: a text frame that is not UTF-8" "utf-8"
                Expect.equal (one [| 0uy; 0uy; 0uy; 0uy |] false) "incomplete" "binary header"
                Expect.equal (one [| 0uy; 0uy; 0uy; 0uy; 5uy |] false) "frame larger than 4 bytes" "binary over"
                Expect.equal (one [| 0uy; 0uy; 0uy; 0uy; 2uy; 1uy |] true) "malformed: a cut binary frame" "cut"
                Expect.equal (one [| 0uy; 0uy; 0uy; 0uy; 0uy |] false) "binary (0 bytes) in 5 bytes" "empty binary"
                Expect.equal (one [| 13uy; 10uy |] false) "skip 2" "blank"
            }

            test "text frames cannot contain line breaks" {
                Expect.throwsT<ArgumentException>
                    (fun () -> StreamFraming.encode (Frame.Text "a\nb") |> ignore)
                    "newline"
            }

            testProperty "channel frames round-trip"
            <| Prop.forAll (Arb.fromGen channelFrame) (fun f ->
                match ChannelFrame.decode (ChannelFrame.encode f) with
                | Ok back -> same f back
                | Error _ -> false)

            test "channel frame layout" {
                Expect.equal
                    (ChannelFrame.encode (ChannelFrame.Credit(0x01020304u, 65536u)))
                    [| 3uy; 1uy; 2uy; 3uy; 4uy; 0uy; 1uy; 0uy; 0uy |]
                    "credit"

                Expect.equal (ChannelFrame.encode (ChannelFrame.Open 1u)) [| 1uy; 0uy; 0uy; 0uy; 1uy |] "open"
            }

            test "reset reasons are cut at a character boundary" {
                match ChannelFrame.decode (ChannelFrame.encode (ChannelFrame.Reset(1u, 2u, String('é', 200)))) with
                | Ok(ChannelFrame.Reset(_, _, reason)) -> Expect.equal reason (String('é', 128)) "256 bytes"
                | other -> failtest (string other)
            }

            test "invalid channel frames are errors" {
                let error (bytes: byte array) =
                    match ChannelFrame.decode bytes with
                    | Ok f -> "ok " + string f
                    | Error e -> e

                Expect.equal (error [| 1uy |]) "a channel frame shorter than its header" "short"
                Expect.equal (error [| 9uy; 0uy; 0uy; 0uy; 1uy |]) "unknown channel frame kind 9" "kind"

                Expect.equal
                    (error [| 3uy; 0uy; 0uy; 0uy; 1uy; 1uy |])
                    "a channel frame of kind 3 with 1 bytes after the header"
                    "credit"

                Expect.equal
                    (error [| 5uy; 0uy; 0uy; 0uy; 1uy; 0uy; 0uy; 0uy; 0uy; 0xFFuy |])
                    "a reset reason that is not UTF-8"
                    "reason"
            }
        ]
