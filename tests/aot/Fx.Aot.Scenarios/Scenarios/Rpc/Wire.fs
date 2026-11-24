/// The protocol's pure parts: reading and writing messages, the stream framing and channel frames.
module Fx.Aot.Scenarios.Rpc.Wire

open System
open System.Text
open Fx.Contracts
open Fx.Rpc
open Fx.Aot.Scenarios

let private parsing () =
    [
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"a/b\",\"params\":{\"x\":[1,2.50]}}"
        "{\"jsonrpc\":\"2.0\",\"method\":\"note\"}"
        "{\"jsonrpc\":\"2.0\",\"id\":\"s\",\"result\":null}"
        "{\"jsonrpc\":\"2.0\",\"id\":9223372036854775807,\"error\":{\"code\":-32601,\"message\":\"m\",\"data\":{\"k\":1e400}}}"
        "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"p\"}}"
        "{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"x\"}"
        "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"x\"}"
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"x\",\"params\":\"s\"}"
        "{\"id\":3,\"method\":\"x\"}"
        "{\"jsonrpc\":\"2.0\",\"id\":4,\"result\":1,\"error\":{\"code\":1,\"message\":\"m\"}}"
        "{\"jsonrpc\":\"2.0\",\"id\":5}"
        "[1,{\"jsonrpc\":\"2.0\",\"method\":\"n\"}]"
        "[]"
        "{\"a\":1,\"a\":2}"
        "nope"
    ]
    |> List.map (fun text ->
        let shown =
            match Wire.parse text with
            | Incoming.Single(Parsed.Valid m) -> "valid " + Wire.serialize m
            | Incoming.Single other -> string other
            | Incoming.Batch entries -> "batch: " + String.Join(" | ", entries |> List.map string)
            | Incoming.Unreadable e -> "unreadable: " + string e

        Show.escape text + " -> " + Show.escape shown)

let private writing () =
    let meta = Map.ofList [ Protocol.metaKey "trace", Json.String "t" ]

    [
        Message.Request(RequestId.Number 1L, "a", Meta.attach meta (Some(Json.Object [ "x", Json.int 1 ])))
        Message.Request(RequestId.String "s\n", "b", Meta.attach meta None)
        Message.Notification(Protocol.CancelRequest, Some(Json.Object [ "id", Json.int 1 ]))
        Message.Response(RequestId.Number -2L, Ok(Json.String "é\u2028"))
        Message.Response(RequestId.Number 3L, Error(RpcError.invalidParams [ Issue.at [ PathSegment.Key "a" ] "bad" ]))
        Message.ErrorWithoutId(RpcError.parseError "x")
    ]
    |> List.map (fun m -> Show.escape (Wire.serialize m))

let private framing () =
    let limits = { MaxText = 64; MaxBinary = 16 }

    let frames =
        [
            Frame.Text "{\"a\":\"é\"}"
            Frame.Binary [| 1uy; 0uy; 0uy; 0uy; 7uy |]
            Frame.Text "[]"
        ]

    let encoded = frames |> List.map StreamFraming.encode |> Array.concat

    let rec decodeAll (offset: int) (acc: string list) =
        if offset >= encoded.Length then
            List.rev acc
        else
            match StreamFraming.decode limits encoded offset (encoded.Length - offset) true with
            | Decoded.Frame(frame, n) -> decodeAll (offset + n) (string frame :: acc)
            | Decoded.Skip n -> decodeAll (offset + n) ("skip" :: acc)
            | other -> List.rev (string other :: acc)

    let one (bytes: byte array) (atEnd: bool) =
        string (StreamFraming.decode limits bytes 0 bytes.Length atEnd)

    [
        "encoded: " + Convert.ToHexString encoded
        "decoded: " + String.Join(", ", decodeAll 0 [])
        "crlf: " + one (Encoding.UTF8.GetBytes "{}\r\n") false
        "empty line: " + one [| 13uy; 10uy |] false
        "partial line: " + one (Encoding.UTF8.GetBytes "{\"a\"") false
        "last line: " + one (Encoding.UTF8.GetBytes "{\"a\":1}") true
        "long line: " + one (Array.create 70 (byte 'a')) false
        "bad utf-8: " + one [| 0xC3uy; 0x28uy; 10uy |] false
        "big binary: " + one [| 0uy; 0uy; 0uy; 1uy; 0uy |] false
        "cut binary: " + one [| 0uy; 0uy; 0uy; 0uy; 9uy; 1uy |] true
    ]

let private decoded (bytes: byte array) =
    match ChannelFrame.decode bytes with
    | Ok frame -> "ok " + string frame
    | Error e -> "error " + e

let private channelFrames () =
    let segment = ArraySegment([| 9uy; 8uy; 7uy |])

    [
        ChannelFrame.Open 1u
        ChannelFrame.Data(3u, segment)
        ChannelFrame.Credit(2u, 65536u)
        ChannelFrame.Close 5u
        ChannelFrame.Reset(7u, 2u, "too many channels: é")
        ChannelFrame.Reset(9u, 0u, String('ü', 300))
    ]
    |> List.map (fun frame ->
        let bytes = ChannelFrame.encode frame

        let back =
            match ChannelFrame.decode bytes with
            | Ok decoded -> string decoded
            | Error e -> "error " + e

        let hex = Convert.ToHexString bytes
        let hex = if hex.Length > 40 then hex.Substring(0, 40) + "…" else hex
        $"{frame |> string |> Show.escape |> fun s -> s.Substring(0, min 40 s.Length)} = {hex} -> {back.Length} chars")
    |> List.append
        [
            "bad kind: " + decoded [| 9uy; 0uy; 0uy; 0uy; 1uy |]
            "short: " + decoded [| 1uy; 0uy |]
            "open with payload: " + decoded [| 1uy; 0uy; 0uy; 0uy; 1uy; 5uy |]
        ]

let scenarios: Scenario list =
    [
        Scenario.lines "rpc-wire-parse" parsing
        Scenario.lines "rpc-wire-write" writing
        Scenario.lines "rpc-framing" framing
        Scenario.lines "rpc-channel-frames" channelFrames
    ]
