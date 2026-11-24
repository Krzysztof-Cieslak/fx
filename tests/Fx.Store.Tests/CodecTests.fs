module Fx.Store.Tests.CodecTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Tests.Notes

let private roundTrip (codec: Codec<'T>) (value: 'T) =
    match Codec.decode codec (Codec.encode codec value) with
    | Ok back -> back
    | Error issues -> failtest (Issue.render issues)

let private recorded: Recorded<Note> =
    {
        Stream = StreamId "notes/1"
        Seq = 3L
        Event = Edited(0, "b")
        Meta =
            {
                At = Instant.ofUnixMicroseconds 1_764_000_000_123_456L
                By = Attribution.create "user:alice" "tui"
                Key = IdempotencyKey "k3"
                Cause = Some(EventRef.create (StreamId "chat/1") 7L)
            }
    }

[<Tests>]
let tests =
    testList
        "Codecs"
        [
            test "an event's envelope is flat JSON with microseconds" {
                Expect.equal
                    (Codec.encodeText (Codecs.recorded codec) recorded)
                    """{"stream":"notes/1","seq":3,"event":{"kind":"edited","index":0,"body":"b"},"at":1764000000123456,"by":{"principal":"user:alice","via":"tui"},"key":"k3","cause":{"stream":"chat/1","seq":7}}"""
                    "json"

                Expect.equal (roundTrip (Codecs.recorded codec) recorded) recorded "round trip"

                let uncaused =
                    { recorded with
                        Meta = { recorded.Meta with Cause = None }
                    }

                Expect.equal (roundTrip (Codecs.recorded codec) uncaused) uncaused "without a cause"
            }

            test "a stored envelope carries the payload with its version" {
                let stored: Recorded<Payload> =
                    {
                        Stream = recorded.Stream
                        Seq = recorded.Seq
                        Event = EventSchema.encode schema (Added "x")
                        Meta = recorded.Meta
                    }

                let text = Codec.encodeText (Codecs.recorded Codecs.payload) stored

                Expect.stringContains
                    text
                    "\"event\":{\"version\":2,\"data\":{\"body\":\"x\",\"kind\":\"added\"}}"
                    "json"

                Expect.equal (roundTrip (Codecs.recorded Codecs.payload) stored) stored "round trip"
            }

            test "invalid envelopes are reported field by field" {
                match
                    Codec.decodeText
                        (Codecs.recorded codec)
                        """{"stream":"","seq":0,"event":{"kind":"added","body":"x"},"at":"now","by":{"principal":"p"},"key":"k"}"""
                with
                | Ok value -> failtest $"decoded {value}"
                | Error issues ->
                    Expect.equal
                        (issues |> List.map string)
                        [
                            "$.stream: expected a non-empty string"
                            "$.seq: expected an integer of at least 1, got number 0"
                            "$.at: expected integer, got string \"now\""
                            "$.by.via: missing required field"
                        ]
                        "issues"
            }

            test "blob references" {
                let blob =
                    {
                        Hash = BlobHash.ofBytes [| 1uy; 2uy |]
                        Size = 2L
                    }

                Expect.equal
                    (Codec.encodeText Codecs.blobRef blob)
                    """{"hash":"sha256:a12871fee210fb8619291eaea194581cbd2531e4b23759d225f6806923f63222","size":2}"""
                    "json"

                Expect.equal (roundTrip Codecs.blobRef blob) blob "round trip"

                Expect.isError (Codec.decodeText Codecs.blobHash "\"sha256:xyz\"") "not a hash"
            }

            test "blob hashes parse in either case and print in lowercase" {
                let hash = BlobHash.ofBytes [| 1uy; 2uy |]
                Expect.equal (BlobHash.tryParse (string hash)) (Some hash) "round trip"

                Expect.equal
                    (BlobHash.tryParse ((string hash).ToUpperInvariant().Replace("SHA256", "sha256")))
                    (Some hash)
                    "upper case"

                Expect.equal (BlobHash.tryParse hash.Hex) None "without the prefix"
                Expect.equal (BlobHash.tryParse ("sha256:" + hash.Hex.Substring 1)) None "too short"
                Expect.equal (BlobHash.tryParse ("sha256:" + hash.Hex.Replace('a', 'g'))) None "not hex"

                Expect.throwsT<System.ArgumentException>
                    (fun () -> BlobHash.ofDigest [| 1uy |] |> ignore)
                    "digest length"
            }
        ]
