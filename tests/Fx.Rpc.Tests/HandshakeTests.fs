module Fx.Rpc.Tests.HandshakeTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Rpc

let private capabilities =
    {
        MaxMessageSize = 4096
        MaxPendingRequests = Some 8
        Channels =
            Some
                {
                    InitialWindow = 1024
                    MaxFramePayload = 512
                    MaxChannels = 4
                }
        Methods = [ "a"; "b" ]
    }

let private none = CancellationToken.None

let private within (t: Task<'T>) = t.WaitAsync(TimeSpan.FromSeconds 10.0)

[<Tests>]
let tests =
    testList
        "Handshake and in-process transport"
        [
            test "initialize params round-trip and ignore unknown members" {
                let hello =
                    {
                        ProtocolVersions = [ "2"; "1" ]
                        Peer = { Name = "c"; Version = "0.1" }
                        Capabilities = capabilities
                    }

                let json =
                    Codec.encode Handshake.initializeParams hello
                    |> Handshake.withMembers [ "identity", Json.String "me" ]

                Expect.equal
                    (Json.serialize json)
                    """{"protocolVersions":["2","1"],"peer":{"name":"c","version":"0.1"},"capabilities":{"maxMessageSize":4096,"maxPendingRequests":8,"channels":{"initialWindow":1024,"maxFramePayload":512,"maxChannels":4},"methods":["a","b"]},"identity":"me"}"""
                    "json"

                Expect.equal (Codec.decode Handshake.initializeParams json) (Ok hello) "decoded"

                Expect.throwsT<ArgumentException>
                    (fun () -> Handshake.withMembers [ "peer", Json.Null ] json |> ignore)
                    "a protocol member cannot be replaced"
            }

            test "channels and methods are optional" {
                let json =
                    """{"protocolVersion":"1","peer":{"name":"s","version":"1"},"capabilities":{"maxMessageSize":1024}}"""

                match Codec.decodeText Handshake.initializeResult json with
                | Ok result ->
                    Expect.isNone result.Capabilities.Channels "no channels"
                    Expect.isNone result.Capabilities.MaxPendingRequests "no pending limit"
                    Expect.isEmpty result.Capabilities.Methods "no methods"
                | Error issues -> failtest (Issue.render issues)
            }

            test "invalid settings are explained" {
                let json =
                    """{"maxMessageSize":10,"channels":{"initialWindow":0,"maxFramePayload":99999999,"maxChannels":-1}}"""

                match Codec.decodeText Handshake.capabilities json with
                | Error issues ->
                    Expect.equal
                        (issues |> List.map string)
                        [
                            "$.maxMessageSize: expected an integer of at least 1024, got number 10"
                            "$.channels.initialWindow: expected an integer of at least 1, got number 0"
                            "$.channels.maxFramePayload: expected an integer from 1 to 16777216, got number 99999999"
                            "$.channels.maxChannels: expected an integer of at least 0, got number -1"
                        ]
                        "issues"
                | Ok _ -> failtest "accepted"
            }

            test "the server picks the client's first supported version" {
                Expect.equal (Handshake.choose [ "3"; "2"; "1" ] [ "1"; "2" ]) (Some "2") "first offered"
                Expect.equal (Handshake.choose [ "3" ] [ "1" ]) None "none"
            }

            testTask "in-process frames arrive in order, then the end" {
                let limits = { MaxText = 100; MaxBinary = 100 }
                let a, b = InProcess.pair 1024 limits limits
                let! sent1 = a.Send (Frame.Text "one") none
                let! sent2 = a.Send (Frame.Binary [| 1uy; 2uy |]) none
                Expect.isOk sent1 "sent"
                Expect.isOk sent2 "sent"
                do! a.CloseOutput Shutdown.Normal none
                let! first = within (b.Receive none)
                let! second = within (b.Receive none)
                let! third = within (b.Receive none)
                Expect.equal (string first) "text (3 chars)" "text"
                Expect.equal (string second) "binary (2 bytes)" "binary"
                Expect.equal third Received.End "end"
                // The other direction is still open.
                let! back = b.Send (Frame.Text "back") none
                Expect.isOk back "half-closed"
                let! received = within (a.Receive none)
                Expect.equal (string received) "text (4 chars)" "back"
            }

            testTask "in-process sends wait while the buffer is full" {
                let limits = { MaxText = 100; MaxBinary = 100 }
                let a, b = InProcess.pair 10 limits limits
                let! _ = a.Send (Frame.Binary(Array.zeroCreate 8)) none
                let blocked = a.Send (Frame.Binary(Array.zeroCreate 8)) none
                Expect.isFalse blocked.IsCompleted "over the buffer"
                let! _ = within (b.Receive none)
                let! unblocked = within blocked
                Expect.isOk unblocked "sent once read"
            }

            testTask "in-process frames over the limit fail the connection" {
                let a, b =
                    InProcess.pair 1000 { MaxText = 100; MaxBinary = 100 } { MaxText = 4; MaxBinary = 100 }

                let! _ = a.Send (Frame.Text "ééé") none
                let! received = within (b.Receive none)
                Expect.equal received (Received.Failed(TransportError.TooLarge 4)) "UTF-8 bytes count"
                let! peer = within (a.Receive none)
                Expect.equal peer (Received.Failed(TransportError.Broken "connection reset")) "reset"
            }

            testTask "frames sent before an abort are still delivered, as on a pipe" {
                let limits = { MaxText = 100; MaxBinary = 100 }
                // Closed first: the peer reads the frame, then the end.
                let a, b = InProcess.pair 1000 limits limits
                let! _ = a.Send (Frame.Text "answer") none
                do! a.CloseOutput Shutdown.Normal none
                a.Abort()
                let! first = within (b.Receive none)
                let! second = within (b.Receive none)
                Expect.equal (string first) "text (6 chars)" "the frame survives the abort"
                Expect.equal second Received.End "then the end"
                // Not closed first (a crash): the frame, then a reset.
                let c, d = InProcess.pair 1000 limits limits
                let! _ = c.Send (Frame.Text "last") none
                c.Abort()
                let! first = within (d.Receive none)
                let! second = within (d.Receive none)
                Expect.equal (string first) "text (4 chars)" "the frame survives the crash"
                Expect.equal second (Received.Failed(TransportError.Broken "connection reset")) "then a reset"
                let! sent = d.Send (Frame.Text "x") none
                Expect.isError sent "the crashed end reads no more"
            }

            testTask "aborting resets both ends" {
                let limits = { MaxText = 100; MaxBinary = 100 }
                let a, b = InProcess.pair 1000 limits limits
                let pending = b.Receive none
                a.Abort()
                let! received = within pending
                Expect.equal received (Received.Failed(TransportError.Broken "connection reset")) "reset"
                let! sent = a.Send (Frame.Text "x") none
                Expect.isError sent "no sends"
            }
        ]
