namespace Fx.Envd

open System
open System.Buffers.Binary

/// A stream of a process's output. A terminal has one: `Out`.
[<RequireQualifiedAccess>]
type OutputStream =
    | Out
    | Err

    override this.ToString() =
        match this with
        | Out -> "out"
        | Err -> "err"

/// What an attachment channel (`exec/attach`) carries: sequenced pieces of output. Every byte's
/// sequence number is its offset in its stream, so a client that lost its connection resumes with the
/// offsets it has.
[<RequireQualifiedAccess>]
type OutputRecord =
    /// Output of a stream, starting at `offset`.
    | Data of stream: OutputStream * offset: int64 * bytes: byte array
    /// Output in `[from, until)` is no longer kept; the stream goes on at `until`.
    | Gap of stream: OutputStream * from: int64 * until: int64

    override this.ToString() =
        match this with
        | Data(stream, offset, bytes) -> $"{stream} {offset}+{bytes.Length}"
        | Gap(stream, from, until) -> $"{stream} gap {from}..{until}"

/// The byte layout of output records (big-endian): a kind byte, a stream byte (0 out, 1 err), then
/// for data (kind 1) the offset (uint64), the length (uint32) and the bytes; for a gap (kind 2) the
/// two offsets (uint64). The channel ends when every stream has ended and was delivered.
module OutputRecords =
    [<Literal>]
    let private DataKind = 1uy

    [<Literal>]
    let private GapKind = 2uy

    /// The size of a data record's header.
    let dataHeaderSize = 14

    let private streamByte (stream: OutputStream) =
        match stream with
        | OutputStream.Out -> 0uy
        | OutputStream.Err -> 1uy

    let encode (record: OutputRecord) : byte array =
        match record with
        | OutputRecord.Data(stream, offset, bytes) ->
            let buffer = Array.zeroCreate<byte>(dataHeaderSize + bytes.Length)
            buffer[0] <- DataKind
            buffer[1] <- streamByte stream
            BinaryPrimitives.WriteInt64BigEndian(Span(buffer, 2, 8), offset)
            BinaryPrimitives.WriteInt32BigEndian(Span(buffer, 10, 4), bytes.Length)
            Buffer.BlockCopy(bytes, 0, buffer, dataHeaderSize, bytes.Length)
            buffer
        | OutputRecord.Gap(stream, from, until) ->
            let buffer = Array.zeroCreate<byte> 18
            buffer[0] <- GapKind
            buffer[1] <- streamByte stream
            BinaryPrimitives.WriteInt64BigEndian(Span(buffer, 2, 8), from)
            BinaryPrimitives.WriteInt64BigEndian(Span(buffer, 10, 8), until)
            buffer

    /// What decoding the start of a buffer gives.
    [<RequireQualifiedAccess>]
    type Decoded =
        /// A record and how many bytes it took.
        | Record of OutputRecord * consumed: int
        /// The buffer holds only part of a record.
        | Incomplete
        | Invalid of reason: string

        override this.ToString() =
            match this with
            | Record(record, consumed) -> $"{record} ({consumed} bytes)"
            | Incomplete -> "incomplete"
            | Invalid reason -> "invalid: " + reason

    /// Decodes the record at the start of `buffer[start .. start + count)`.
    let decode (buffer: byte array) (start: int) (count: int) : Decoded =
        if count < 2 then
            Decoded.Incomplete
        else
            let stream =
                match buffer[start + 1] with
                | 0uy -> Some OutputStream.Out
                | 1uy -> Some OutputStream.Err
                | _ -> None

            match buffer[start], stream with
            | _, None -> Decoded.Invalid $"unknown stream {buffer[start + 1]}"
            | DataKind, Some stream ->
                if count < dataHeaderSize then
                    Decoded.Incomplete
                else
                    let offset = BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(buffer, start + 2, 8))

                    let length =
                        BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan(buffer, start + 10, 4))

                    if length < 0 || offset < 0L then
                        Decoded.Invalid "a negative length or offset"
                    elif count < dataHeaderSize + length then
                        Decoded.Incomplete
                    else
                        let bytes = Array.sub buffer (start + dataHeaderSize) length
                        Decoded.Record(OutputRecord.Data(stream, offset, bytes), dataHeaderSize + length)
            | GapKind, Some stream ->
                if count < 18 then
                    Decoded.Incomplete
                else
                    let from = BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(buffer, start + 2, 8))
                    let until = BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(buffer, start + 10, 8))
                    Decoded.Record(OutputRecord.Gap(stream, from, until), 18)
            | kind, _ -> Decoded.Invalid $"unknown record kind {kind}"
