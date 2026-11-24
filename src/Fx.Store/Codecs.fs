namespace Fx.Store

open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host

/// Codecs for what the stores keep and serve: event envelopes, their metadata, and blob references
/// (so event payloads can refer to blobs). Used to export logs and to send events over the wire.
[<RequireQualifiedAccess>]
module Codecs =
    /// Microseconds since the Unix epoch, UTC, as an integer: exact, and exact in a JavaScript number.
    let instant: Codec<Instant> =
        Codec.int64
        |> Codec.map Instant.ofUnixMicroseconds (fun instant -> instant.UnixMicroseconds)
        |> Codec.describe "Microseconds since the Unix epoch, UTC."

    let streamId: Codec<StreamId> =
        Codec.string
        |> Codec.nonEmpty
        |> Codec.map StreamId (fun stream -> stream.Value)
        |> Codec.describe "An event stream, `kind/id`."

    let idempotencyKey: Codec<IdempotencyKey> =
        Codec.string
        |> Codec.nonEmpty
        |> Codec.map IdempotencyKey (fun key -> key.Value)
        |> Codec.describe "The idempotency key of a command."

    /// `{"principal": "user:alice", "via": "tui"}`.
    let attribution: Codec<Attribution> =
        Codec.record {
            let! principal =
                Codec.field "principal" (fun (a: Attribution) -> a.Principal) Codec.string

            and! via = Codec.field "via" (fun a -> a.Via) Codec.string
            return Attribution.create principal via
        }
        |> Codec.named "Attribution"
        |> Codec.describe "Who sent a command (the principal) and what delivered it."

    /// `{"stream": "chat/1", "seq": 4}`.
    let eventRef: Codec<EventRef> =
        Codec.record {
            let! stream = Codec.field "stream" (fun (r: EventRef) -> r.Stream) streamId
            and! seq = Codec.field "seq" (fun r -> r.Seq) (Codec.int64 |> Codec.atLeast 1L)
            return EventRef.create stream seq
        }
        |> Codec.named "EventRef"
        |> Codec.describe "The position of an event: its stream and sequence number."

    /// `{"version": 2, "data": {...}}`.
    let payload: Codec<Payload> =
        Codec.record {
            let! version =
                Codec.field "version" (fun (p: Payload) -> p.Version) (Codec.int |> Codec.atLeast 1)

            and! data = Codec.field "data" (fun p -> p.Data) Codec.json
            return { Version = version; Data = data }
        }
        |> Codec.named "Payload"
        |> Codec.describe "An event as stored: its JSON and the schema version that wrote it."

    /// An event with its position and metadata, flat:
    /// `{"stream", "seq", "event", "at", "by", "key", "cause"?}`.
    let recorded (event: Codec<'Evt>) : Codec<Recorded<'Evt>> =
        Codec.record {
            let! stream = Codec.field "stream" (fun (r: Recorded<'Evt>) -> r.Stream) streamId
            and! seq = Codec.field "seq" (fun r -> r.Seq) (Codec.int64 |> Codec.atLeast 1L)
            and! data = Codec.field "event" (fun r -> r.Event) event
            and! at = Codec.field "at" (fun r -> r.Meta.At) instant
            and! by = Codec.field "by" (fun r -> r.Meta.By) attribution
            and! key = Codec.field "key" (fun r -> r.Meta.Key) idempotencyKey
            and! cause = Codec.optionalField "cause" (fun r -> r.Meta.Cause) eventRef

            return
                {
                    Stream = stream
                    Seq = seq
                    Event = data
                    Meta =
                        {
                            At = at
                            By = by
                            Key = key
                            Cause = cause
                        }
                }
        }
        |> Codec.describe "An event in a log: its position, the event, and when, who and why."

    /// `"sha256:<64 hex digits>"`.
    let blobHash: Codec<BlobHash> =
        Codec.string
        |> Codec.pattern "^sha256:[0-9a-fA-F]{64}$"
        |> Codec.tryMap
            (fun text ->
                match BlobHash.tryParse text with
                | Some hash -> Ok hash
                | None -> Error "expected sha256: and 64 hex digits")
            string
        |> Codec.describe "The SHA-256 of a blob's content."

    /// `{"hash": "sha256:…", "size": 1234}`.
    let blobRef: Codec<BlobRef> =
        Codec.record {
            let! hash = Codec.field "hash" (fun (b: BlobRef) -> b.Hash) blobHash
            and! size = Codec.field "size" (fun b -> b.Size) (Codec.int64 |> Codec.atLeast 0L)
            return { Hash = hash; Size = size }
        }
        |> Codec.named "BlobRef"
        |> Codec.describe "A stored blob: the hash of its content and its size in bytes."
