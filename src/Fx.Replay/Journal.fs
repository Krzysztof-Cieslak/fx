namespace Fx.Replay

open System
open System.Security.Cryptography
open System.Text
open Fx.Contracts
open Fx.Machines
open Fx.Machines.Host
open Fx.Store

/// A journaled value: inline when small, otherwise in the blob store, named by its hash (so equal
/// values are stored once).
[<RequireQualifiedAccess>]
type Stored =
    | Inline of Json
    | Blob of BlobRef

    override this.ToString() =
        match this with
        | Inline json -> Json.serialize json
        | Blob blob -> string blob

/// How a journaled call ended.
[<RequireQualifiedAccess>]
type Outcome =
    /// It returned this value (an expected failure is a value too: a `Result`).
    | Returned of Stored
    /// It threw (a defect): the exception's type, never its message.
    | Threw of exceptionType: string
    /// It was cancelled (`OperationCanceledException`).
    | Cancelled
    /// It was still running when the host stopped recording.
    | Pending

    override this.ToString() =
        match this with
        | Returned stored -> "returned " + string stored
        | Threw name -> "threw " + name
        | Cancelled -> "cancelled"
        | Pending -> "pending"

/// One capability call of a session, or a marker (a turn began, was cancelled, ended; the host closed).
///
/// - `Seq` orders a host process's entries as they completed (from 1 in each segment): replay waits for
///   what happened before a cancellation, an elapsed delay or a turn's start before it lets them happen.
/// - `Lane` and `Index` say which capability instance was called and in which order calls started:
///   replay matches a call to the first unreplayed entry of its lane with the same `Op` and `Input`.
/// - `Input` is a digest of the call's arguments (never the arguments: they may be large, and
///   environment values may hold secrets); `Summary` says what the call was, bounded, for reports.
type JournalEntry =
    {
        Seq: int64
        Lane: string
        Index: int64
        Op: string
        Input: string
        Summary: string
        Outcome: Outcome
    }

    override this.ToString() =
        let summary = if this.Summary = "" then "" else " " + this.Summary
        $"#{this.Seq} {this.Lane}[{this.Index}] {this.Op}{summary} -> {this.Outcome}"

/// What the journal stream of a session holds (docs/modules/replay.md). A segment is one host process
/// recording the session: `Began`, batches of entries in `Seq` order, and `Ended` if the host closed
/// cleanly (a crash leaves a segment without it).
[<RequireQualifiedAccess>]
type JournalEvent =
    | Began of segment: int
    | Recorded of segment: int * entries: JournalEntry list
    | Ended of segment: int

    member this.Segment =
        match this with
        | Began segment
        | Recorded(segment, _)
        | Ended segment -> segment

    override this.ToString() =
        match this with
        | Began segment -> $"segment {segment} began"
        | Recorded(segment, entries) -> $"segment {segment}: {entries.Length} entries"
        | Ended segment -> $"segment {segment} ended"

/// Digests of journaled inputs: the first 128 bits of the SHA-256 of their canonical JSON, as hex.
[<RequireQualifiedAccess>]
module Digest =
    let private hex (bytes: byte array) =
        Convert.ToHexStringLower(SHA256.HashData bytes).Substring(0, 32)

    /// The digest of a JSON value (its canonical form).
    let json (json: Json) : string =
        hex (Encoding.UTF8.GetBytes(Json.serializeCanonical json))

    /// The digest of bytes.
    let bytes (bytes: byte array) : string = hex bytes

    /// Text cut to at most `max` characters (for summaries).
    let bound (max: int) (text: string) : string =
        let text = text.Replace("\r", "").Replace('\n', ' ')

        if text.Length <= max then
            text
        else
            text.Substring(0, max) + "…"

/// The JSON of the journal (version 1). Golden: `tests/Fx.Replay.Tests/golden/journal-events-v1.jsonl`.
[<RequireQualifiedAccess>]
module JournalCodecs =
    let outcome: Codec<Outcome> =
        Codec.union "kind" {
            let! value =
                Codec.case1
                    "value"
                    (fun json -> Outcome.Returned(Stored.Inline json))
                    (Field.required "value" Codec.json)

            and! blob =
                Codec.case1
                    "blob"
                    (fun blob -> Outcome.Returned(Stored.Blob blob))
                    (Field.required "blob" Codecs.blobRef)

            and! threw = Codec.case1 "threw" Outcome.Threw (Field.required "type" Codec.string)
            and! cancelled = Codec.case0 "cancelled" Outcome.Cancelled
            and! pending = Codec.case0 "pending" Outcome.Pending

            return
                fun outcome ->
                    match outcome with
                    | Outcome.Returned(Stored.Inline json) -> value json
                    | Outcome.Returned(Stored.Blob ref) -> blob ref
                    | Outcome.Threw name -> threw name
                    | Outcome.Cancelled -> cancelled
                    | Outcome.Pending -> pending
        }
        |> Codec.describe "How a journaled call ended."

    let private nonEmpty (text: string) = if text = "" then None else Some text

    let entry: Codec<JournalEntry> =
        Codec.record {
            let! seq =
                Codec.field "seq" (fun (e: JournalEntry) -> e.Seq) (Codec.int64 |> Codec.atLeast 1L)

            and! lane = Codec.field "lane" (fun e -> e.Lane) (Codec.string |> Codec.nonEmpty)
            and! index = Codec.field "index" (fun e -> e.Index) (Codec.int64 |> Codec.atLeast 0L)
            and! op = Codec.field "op" (fun e -> e.Op) (Codec.string |> Codec.nonEmpty)
            and! input = Codec.optionalField "input" (fun e -> nonEmpty e.Input) Codec.string

            and! summary =
                Codec.optionalField "summary" (fun e -> nonEmpty e.Summary) Codec.string

            and! outcome = Codec.field "outcome" (fun e -> e.Outcome) outcome

            return
                {
                    Seq = seq
                    Lane = lane
                    Index = index
                    Op = op
                    Input = input |> Option.defaultValue ""
                    Summary = summary |> Option.defaultValue ""
                    Outcome = outcome
                }
        }
        |> Codec.describe "A journaled capability call or marker."

    let event: Codec<JournalEvent> =
        Codec.union "kind" {
            let! began =
                Codec.case1 "began" JournalEvent.Began (Field.required "segment" Codec.int)

            and! recorded =
                Codec.case2
                    "recorded"
                    (fun segment entries -> JournalEvent.Recorded(segment, entries))
                    (Field.required "segment" Codec.int)
                    (Field.required "entries" (Codec.list entry))

            and! ended =
                Codec.case1 "ended" JournalEvent.Ended (Field.required "segment" Codec.int)

            return
                fun event ->
                    match event with
                    | JournalEvent.Began segment -> began segment
                    | JournalEvent.Recorded(segment, entries) -> recorded segment entries
                    | JournalEvent.Ended segment -> ended segment
        }
        |> Codec.describe "An event of a session's journal."

/// Where a session's journal lives: the stream `journal/<session>` of the host's event store.
[<RequireQualifiedAccess>]
module JournalLog =
    [<Literal>]
    let StreamKind = "journal"

    let stream (session: string) : StreamId = StreamId.create StreamKind session

    /// Version 1 of the journal's events.
    let schema: EventSchema<JournalEvent> = EventSchema.create JournalCodecs.event

    let typed (log: EventLog<Payload>) : EventLog<JournalEvent> = EventLog.typed schema log
