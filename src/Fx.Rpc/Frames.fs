namespace Fx.Rpc

open System
open System.Buffers.Binary
open System.Text
open System.Text.Unicode

/// What transports move: one JSON-RPC text (a message or a batch) or one binary channel frame.
[<RequireQualifiedAccess>]
type Frame =
    | Text of string
    | Binary of byte array

    override this.ToString() =
        match this with
        | Text text -> $"text ({text.Length} chars)"
        | Binary bytes -> $"binary ({bytes.Length} bytes)"

/// Why a transport stopped delivering frames.
[<RequireQualifiedAccess>]
type TransportError =
    /// The connection failed: reset, broken pipe, aborted.
    | Broken of reason: string
    /// A frame is larger than the receive limit.
    | TooLarge of limit: int
    /// The bytes do not follow the framing, or a text frame is not valid UTF-8.
    | Malformed of reason: string

    override this.ToString() =
        match this with
        | Broken reason -> "broken: " + reason
        | TooLarge limit -> $"frame larger than {limit} bytes"
        | Malformed reason -> "malformed: " + reason

/// The outcome of receiving from a transport.
[<RequireQualifiedAccess>]
type Received =
    | Frame of Frame
    /// The peer closed its side cleanly: nothing more will arrive.
    | End
    | Failed of TransportError

    override this.ToString() =
        match this with
        | Frame f -> string f
        | End -> "end"
        | Failed e -> string e

/// Receive limits of a transport, in bytes.
type FrameLimits =
    {
        /// Largest text frame: the UTF-8 bytes of one JSON-RPC message or batch.
        MaxText: int
        /// Largest binary frame (channel header included).
        MaxBinary: int
    }

/// A binary channel frame. Channel ids are odd for channels the client side opens and even for the
/// server side's, so both can open channels without coordination.
[<RequireQualifiedAccess>]
type ChannelFrame =
    /// Opens a channel. The opener may send data at once, up to the receiver's initial window.
    | Open of channel: uint32
    /// Bytes in order; each consumes as much credit as it carries.
    | Data of channel: uint32 * data: ArraySegment<byte>
    /// The receiver read data and grants that many more bytes.
    | Credit of channel: uint32 * bytes: uint32
    /// The sender's half-close: it sends no more data on the channel.
    | Close of channel: uint32
    /// Aborts the channel in both directions; buffered data is discarded.
    | Reset of channel: uint32 * code: uint32 * reason: string

    member this.Channel =
        match this with
        | Open id
        | Data(id, _)
        | Credit(id, _)
        | Close id
        | Reset(id, _, _) -> id

    override this.ToString() =
        match this with
        | Open id -> $"open {id}"
        | Data(id, data) -> $"data {id} ({data.Count} bytes)"
        | Credit(id, bytes) -> $"credit {id} (+{bytes})"
        | Close id -> $"close {id}"
        | Reset(id, code, reason) -> $"reset {id} ({code}: {reason})"

/// The binary layout of channel frames: one kind byte, the channel id (big-endian uint32), then the
/// kind's fields. Pure.
///
/// | Kind | Byte | Fields |
/// |---|---|---|
/// | open | 1 | — |
/// | data | 2 | the bytes |
/// | credit | 3 | bytes granted (uint32) |
/// | close | 4 | — |
/// | reset | 5 | code (uint32), reason (UTF-8, at most 256 bytes) |
module ChannelFrame =
    [<Literal>]
    let private OpenKind = 1uy

    [<Literal>]
    let private DataKind = 2uy

    [<Literal>]
    let private CreditKind = 3uy

    [<Literal>]
    let private CloseKind = 4uy

    [<Literal>]
    let private ResetKind = 5uy

    /// Kind byte and channel id.
    let headerSize = 5

    /// Longest reset reason, in UTF-8 bytes; longer reasons are cut.
    let maxReasonBytes = 256

    /// The largest frame for a data payload limit (data frames are the largest kind).
    let maxSize (maxPayload: int) =
        headerSize + max maxPayload (8 + maxReasonBytes)

    let private header (size: int) (kind: byte) (channel: uint32) : byte array =
        let bytes = Array.zeroCreate<byte> size
        bytes[0] <- kind
        BinaryPrimitives.WriteUInt32BigEndian(Span(bytes, 1, 4), channel)
        bytes

    /// A data frame holding a copy of `data`.
    let data (channel: uint32) (data: ReadOnlySpan<byte>) : byte array =
        let bytes = header (headerSize + data.Length) DataKind channel
        data.CopyTo(Span(bytes, headerSize, data.Length))
        bytes

    let private cutUtf8 (text: string) : byte array =
        let bytes = Encoding.UTF8.GetBytes text

        if bytes.Length <= maxReasonBytes then
            bytes
        else
            // Back up to a character boundary: continuation bytes are 10xxxxxx.
            let mutable cut = maxReasonBytes

            while cut > 0 && bytes[cut] &&& 0xC0uy = 0x80uy do
                cut <- cut - 1

            Array.sub bytes 0 cut

    let encode (frame: ChannelFrame) : byte array =
        match frame with
        | ChannelFrame.Open id -> header headerSize OpenKind id
        | ChannelFrame.Data(id, segment) -> data id (ReadOnlySpan(segment.Array, segment.Offset, segment.Count))
        | ChannelFrame.Credit(id, n) ->
            let bytes = header (headerSize + 4) CreditKind id
            BinaryPrimitives.WriteUInt32BigEndian(Span(bytes, headerSize, 4), n)
            bytes
        | ChannelFrame.Close id -> header headerSize CloseKind id
        | ChannelFrame.Reset(id, code, reason) ->
            let text = cutUtf8 reason
            let bytes = header (headerSize + 4 + text.Length) ResetKind id
            BinaryPrimitives.WriteUInt32BigEndian(Span(bytes, headerSize, 4), code)
            text.CopyTo(Span(bytes, headerSize + 4, text.Length))
            bytes

    /// Reads a frame; data frames refer to `bytes` without copying.
    let decode (bytes: byte array) : Result<ChannelFrame, string> =
        if bytes.Length < headerSize then
            Error "a channel frame shorter than its header"
        else
            let id = BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(bytes, 1, 4))
            let rest = bytes.Length - headerSize

            match bytes[0] with
            | OpenKind when rest = 0 -> Ok(ChannelFrame.Open id)
            | DataKind -> Ok(ChannelFrame.Data(id, ArraySegment(bytes, headerSize, rest)))
            | CreditKind when rest = 4 ->
                Ok(ChannelFrame.Credit(id, BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(bytes, headerSize, 4))))
            | CloseKind when rest = 0 -> Ok(ChannelFrame.Close id)
            | ResetKind when rest >= 4 && rest - 4 <= maxReasonBytes ->
                let code = BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(bytes, headerSize, 4))
                let text = ReadOnlySpan(bytes, headerSize + 4, rest - 4)

                if Utf8.IsValid text then
                    Ok(ChannelFrame.Reset(id, code, Encoding.UTF8.GetString text))
                else
                    Error "a reset reason that is not UTF-8"
            | OpenKind
            | CreditKind
            | CloseKind
            | ResetKind -> Error $"a channel frame of kind {bytes[0]} with {rest} bytes after the header"
            | kind -> Error $"unknown channel frame kind {kind}"

/// What the stream framing decoder found at the start of a buffer.
[<RequireQualifiedAccess>]
type Decoded =
    /// More bytes are needed.
    | Incomplete
    | Frame of frame: Frame * consumed: int
    /// An empty line, ignored.
    | Skip of consumed: int
    | Invalid of TransportError

    override this.ToString() =
        match this with
        | Incomplete -> "incomplete"
        | Frame(f, n) -> $"{f} in {n} bytes"
        | Skip n -> $"skip {n}"
        | Invalid e -> string e

/// The framing of byte streams (stdio, pipes, sockets). Pure.
///
/// - A **text frame** is one JSON-RPC message or batch as UTF-8 on one line, ended by `\n`
///   (newline-delimited JSON, as the Bun kernel speaks it). A `\r` before the `\n` is dropped and
///   empty lines are ignored. Compact JSON has no raw line breaks, and the writer never emits one.
/// - A **binary frame** is a zero byte, the length (big-endian uint32), then the channel frame. JSON
///   text never contains a raw zero byte, so the first byte tells the two apart, and a peer that never
///   opens channels speaks plain NDJSON.
module StreamFraming =
    [<Literal>]
    let private Newline = 10uy

    [<Literal>]
    let private Return = 13uy

    /// Zero byte and length.
    let binaryHeaderSize = 5

    let encode (frame: Frame) : byte array =
        match frame with
        | Frame.Text text ->
            if text.Contains '\n' then
                invalidArg (nameof frame) "A text frame must not contain a line break."

            let count = Encoding.UTF8.GetByteCount text
            let bytes = Array.zeroCreate<byte>(count + 1)
            Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0) |> ignore
            bytes[count] <- Newline
            bytes
        | Frame.Binary payload ->
            let bytes = Array.zeroCreate<byte>(binaryHeaderSize + payload.Length)
            BinaryPrimitives.WriteUInt32BigEndian(Span(bytes, 1, 4), uint32 payload.Length)
            payload.CopyTo(bytes, binaryHeaderSize)
            bytes

    let private text (buffer: byte array) (offset: int) (length: int) (consumed: int) (limits: FrameLimits) =
        let length =
            if length > 0 && buffer[offset + length - 1] = Return then
                length - 1
            else
                length

        if length = 0 then
            Decoded.Skip consumed
        elif length > limits.MaxText then
            Decoded.Invalid(TransportError.TooLarge limits.MaxText)
        else
            let span = ReadOnlySpan(buffer, offset, length)

            if Utf8.IsValid span then
                Decoded.Frame(Frame.Text(Encoding.UTF8.GetString span), consumed)
            else
                Decoded.Invalid(TransportError.Malformed "a text frame that is not UTF-8")

    /// Decodes the frame at the start of `buffer[offset .. offset + count)`. `atEnd` says that no more
    /// bytes will come: a last line without `\n` is then a frame, and a cut binary frame is malformed.
    let decode (limits: FrameLimits) (buffer: byte array) (offset: int) (count: int) (atEnd: bool) : Decoded =
        if count = 0 then
            Decoded.Incomplete
        elif buffer[offset] = 0uy then
            if count < binaryHeaderSize then
                if atEnd then
                    Decoded.Invalid(TransportError.Malformed "a cut binary frame")
                else
                    Decoded.Incomplete
            else
                let length =
                    BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(buffer, offset + 1, 4))

                if length > uint32 limits.MaxBinary then
                    Decoded.Invalid(TransportError.TooLarge limits.MaxBinary)
                elif count < binaryHeaderSize + int length then
                    if atEnd then
                        Decoded.Invalid(TransportError.Malformed "a cut binary frame")
                    else
                        Decoded.Incomplete
                else
                    let payload = Array.sub buffer (offset + binaryHeaderSize) (int length)
                    Decoded.Frame(Frame.Binary payload, binaryHeaderSize + int length)
        else
            match Array.IndexOf(buffer, Newline, offset, count) with
            | -1 ->
                // One more byte than the limit may be a `\r`.
                if count > limits.MaxText + 1 then
                    Decoded.Invalid(TransportError.TooLarge limits.MaxText)
                elif atEnd then
                    text buffer offset count count limits
                else
                    Decoded.Incomplete
            | newline -> text buffer offset (newline - offset) (newline - offset + 1) limits
