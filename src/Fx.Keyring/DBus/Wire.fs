/// The D-Bus wire format (https://dbus.freedesktop.org/doc/dbus-specification.html#message-protocol):
/// marshalling of values and messages, as far as a client needs it. Pure: bytes in, bytes out.
///
/// Messages are written little-endian; both byte orders are read, since the bus forwards messages
/// in their sender's byte order.
module Fx.Keyring.DBus.Wire

open System
open System.Buffers.Binary
open System.IO
open System.Text

/// The largest message this client sends or accepts. The specification allows 128 MiB; secrets and
/// their metadata are tiny, so anything this large is a broken or hostile peer.
[<Literal>]
let MaxMessageBytes = 16777216

/// A single complete type, parsed from a signature.
type SigType =
    | Basic of char
    | ArrayOf of SigType
    | StructOf of SigType list
    | DictEntryOf of key: SigType * value: SigType
    | VariantType

    override this.ToString() =
        match this with
        | Basic c -> string c
        | ArrayOf element -> "a" + string element
        | StructOf items -> "(" + String.concat "" (items |> List.map string) + ")"
        | DictEntryOf(key, value) -> "{" + string key + string value + "}"
        | VariantType -> "v"

let private fail (message: string) : 'T = raise (InvalidDataException message)

let private isBasic (c: char) = "ybnqiuxtdsogh".Contains c

let rec private parseOne (signature: string) (index: int) (depth: int) : SigType * int =
    if index >= signature.Length then
        fail "truncated signature"
    elif depth > 64 then
        fail "signature nested too deeply"
    else
        match signature[index] with
        | c when isBasic c -> Basic c, index + 1
        | 'v' -> VariantType, index + 1
        | 'a' when index + 1 < signature.Length && signature[index + 1] = '{' ->
            let key, afterKey = parseOne signature (index + 2) (depth + 1)

            match key with
            | Basic _ -> ()
            | _ -> fail "dictionary keys must be basic types"

            let value, afterValue = parseOne signature afterKey (depth + 1)

            if afterValue >= signature.Length || signature[afterValue] <> '}' then
                fail "unterminated dictionary entry"

            ArrayOf(DictEntryOf(key, value)), afterValue + 1
        | 'a' ->
            let element, next = parseOne signature (index + 1) (depth + 1)
            ArrayOf element, next
        | '(' ->
            let items = ResizeArray()
            let mutable next = index + 1

            while next < signature.Length && signature[next] <> ')' do
                let item, after = parseOne signature next (depth + 1)
                items.Add item
                next <- after

            if next >= signature.Length then fail "unterminated struct"
            elif items.Count = 0 then fail "empty struct"
            else StructOf(List.ofSeq items), next + 1
        | c -> fail $"invalid type code '{c}' in a signature"

/// Parses a signature into its complete types. Throws `InvalidDataException` if it is malformed.
let parseSignature (signature: string) : SigType list =
    if signature.Length > 255 then
        fail "signature longer than 255 characters"

    let types = ResizeArray()
    let mutable index = 0

    while index < signature.Length do
        let t, next = parseOne signature index 0
        types.Add t
        index <- next

    List.ofSeq types

let private parseSingle (signature: string) : SigType =
    match parseSignature signature with
    | [ t ] -> t
    | _ -> fail $"'{signature}' is not a single complete type"

let private alignment (t: SigType) : int =
    match t with
    | Basic('y' | 'g')
    | VariantType -> 1
    | Basic('n' | 'q') -> 2
    | Basic('x' | 't' | 'd')
    | StructOf _
    | DictEntryOf _ -> 8
    | Basic _
    | ArrayOf _ -> 4

/// An object path: `/`, or `/`-separated non-empty elements of `[A-Za-z0-9_]`.
let isObjectPath (path: string) : bool =
    path = "/"
    || (path.Length > 1
        && path[0] = '/'
        && path[path.Length - 1] <> '/'
        && not (path.Contains "//")
        && Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '/') path)

type private Writer() =
    let bytes = ResizeArray<byte>(256)

    member _.Position = bytes.Count

    member _.Pad(alignment: int) =
        while bytes.Count % alignment <> 0 do
            bytes.Add 0uy

    member _.Byte(value: byte) = bytes.Add value

    member w.Raw(data: byte array) = bytes.AddRange data

    member w.UInt16(value: uint16) =
        w.Pad 2
        bytes.Add(byte value)
        bytes.Add(byte (value >>> 8))

    member w.UInt32(value: uint32) =
        w.Pad 4

        for shift in [ 0; 8; 16; 24 ] do
            bytes.Add(byte (value >>> shift))

    member w.UInt64(value: uint64) =
        w.Pad 8

        for shift in [ 0; 8; 16; 24; 32; 40; 48; 56 ] do
            bytes.Add(byte (value >>> shift))

    member _.Patch(position: int, value: uint32) =
        for k in 0..3 do
            bytes[position + k] <- byte (value >>> (8 * k))

    member _.ToArray() = bytes.ToArray()

let private utf8 = UTF8Encoding(false, true)

let private text (value: string) : byte array =
    if value.Contains '\000' then
        invalidArg (nameof value) "D-Bus strings cannot contain NUL"

    utf8.GetBytes value

let rec private write (w: Writer) (value: DBusValue) : unit =
    match value with
    | DBusValue.Byte b -> w.Byte b
    | DBusValue.Boolean b -> w.UInt32(if b then 1u else 0u)
    | DBusValue.Int16 n -> w.UInt16(uint16 n)
    | DBusValue.UInt16 n -> w.UInt16 n
    | DBusValue.Int32 n -> w.UInt32(uint32 n)
    | DBusValue.UInt32 n -> w.UInt32 n
    | DBusValue.Int64 n -> w.UInt64(uint64 n)
    | DBusValue.UInt64 n -> w.UInt64 n
    | DBusValue.Double d -> w.UInt64(BitConverter.DoubleToUInt64Bits d)
    | DBusValue.String s ->
        let bytes = text s
        w.UInt32(uint32 bytes.Length)
        w.Raw bytes
        w.Byte 0uy
    | DBusValue.ObjectPath p ->
        if not (isObjectPath p) then
            invalidArg (nameof value) $"invalid object path '{p}'"

        write w (DBusValue.String p)
    | DBusValue.Signature s ->
        parseSignature s |> ignore
        let bytes = Encoding.ASCII.GetBytes s
        w.Byte(byte bytes.Length)
        w.Raw bytes
        w.Byte 0uy
    | DBusValue.UnixFd n -> w.UInt32 n
    | DBusValue.Variant inner ->
        write w (DBusValue.Signature inner.Type)
        write w inner
    | DBusValue.Bytes bytes ->
        w.UInt32(uint32 bytes.Length)
        w.Raw bytes
    | DBusValue.Array(elementSignature, items) ->
        let element =
            match parseSingle ("a" + elementSignature) with
            | ArrayOf element -> element
            | _ -> fail $"invalid array element type '{elementSignature}'"

        w.Pad 4
        let lengthAt = w.Position
        w.UInt32 0u
        w.Pad(alignment element)
        let start = w.Position

        for item in items do
            if item.Type <> elementSignature then
                invalidArg (nameof value) $"an array of {elementSignature} holds a {item.Type}"

            write w item

        w.Patch(lengthAt, uint32 (w.Position - start))
    | DBusValue.Struct items ->
        if items.IsEmpty then
            invalidArg (nameof value) "empty struct"

        w.Pad 8

        for item in items do
            write w item
    | DBusValue.DictEntry(key, item) ->
        w.Pad 8
        write w key
        write w item

/// Marshals values on their own, from an 8-aligned offset (as a message body).
let marshal (values: DBusValue list) : byte array =
    let w = Writer()

    for value in values do
        write w value

    w.ToArray()

type private Reader(data: byte array, littleEndian: bool) =
    let mutable position = 0

    member _.Position
        with get () = position
        and set value = position <- value

    member _.Take(count: int) : ReadOnlySpan<byte> =
        if count < 0 || position + count > data.Length then
            fail "message truncated"

        let span = ReadOnlySpan<byte>(data, position, count)
        position <- position + count
        span

    member r.Align(alignment: int) =
        let padded = (position + alignment - 1) / alignment * alignment
        let padding = r.Take(padded - position)

        for i in 0 .. padding.Length - 1 do
            if padding[i] <> 0uy then
                fail "non-zero padding"

    member r.Byte() = r.Take(1)[0]

    member r.UInt16() =
        r.Align 2
        let span = r.Take 2

        if littleEndian then
            BinaryPrimitives.ReadUInt16LittleEndian span
        else
            BinaryPrimitives.ReadUInt16BigEndian span

    member r.UInt32() =
        r.Align 4
        let span = r.Take 4

        if littleEndian then
            BinaryPrimitives.ReadUInt32LittleEndian span
        else
            BinaryPrimitives.ReadUInt32BigEndian span

    member r.UInt64() =
        r.Align 8
        let span = r.Take 8

        if littleEndian then
            BinaryPrimitives.ReadUInt64LittleEndian span
        else
            BinaryPrimitives.ReadUInt64BigEndian span

    member r.Text(length: int) : string =
        let value =
            try
                utf8.GetString(r.Take length)
            with :? DecoderFallbackException ->
                fail "invalid UTF-8 in a string"

        if r.Byte() <> 0uy then
            fail "string not NUL-terminated"

        if value.Contains '\000' then
            fail "NUL inside a string"

        value

let rec private read (r: Reader) (t: SigType) (depth: int) : DBusValue =
    if depth > 64 then
        fail "value nested too deeply"

    match t with
    | Basic 'y' -> DBusValue.Byte(r.Byte())
    | Basic 'b' ->
        match r.UInt32() with
        | 0u -> DBusValue.Boolean false
        | 1u -> DBusValue.Boolean true
        | _ -> fail "invalid boolean"
    | Basic 'n' -> DBusValue.Int16(int16 (r.UInt16()))
    | Basic 'q' -> DBusValue.UInt16(r.UInt16())
    | Basic 'i' -> DBusValue.Int32(int32 (r.UInt32()))
    | Basic 'u' -> DBusValue.UInt32(r.UInt32())
    | Basic 'x' -> DBusValue.Int64(int64 (r.UInt64()))
    | Basic 't' -> DBusValue.UInt64(r.UInt64())
    | Basic 'd' -> DBusValue.Double(BitConverter.UInt64BitsToDouble(r.UInt64()))
    | Basic 'h' -> DBusValue.UnixFd(r.UInt32())
    | Basic 's' ->
        let length = r.UInt32()

        if length > uint32 MaxMessageBytes then
            fail "string too long"

        DBusValue.String(r.Text(int length))
    | Basic 'o' ->
        let length = r.UInt32()

        if length > uint32 MaxMessageBytes then
            fail "object path too long"

        let path = r.Text(int length)

        if not (isObjectPath path) then
            fail "invalid object path"

        DBusValue.ObjectPath path
    | Basic 'g' -> DBusValue.Signature(r.Text(int (r.Byte())))
    | Basic c -> fail $"invalid type code '{c}'"
    | VariantType ->
        let signature = r.Text(int (r.Byte()))
        DBusValue.Variant(read r (parseSingle signature) (depth + 1))
    | ArrayOf(Basic 'y') ->
        let length = r.UInt32()

        if length > uint32 MaxMessageBytes then
            fail "array too long"

        DBusValue.Bytes(r.Take(int length).ToArray())
    | ArrayOf element ->
        let length = r.UInt32()

        if length > uint32 MaxMessageBytes then
            fail "array too long"

        r.Align(alignment element)
        let finish = r.Position + int length
        let items = ResizeArray()

        while r.Position < finish do
            items.Add(read r element (depth + 1))

        if r.Position <> finish then
            fail "array elements overrun the array's length"

        DBusValue.Array(string element, List.ofSeq items)
    | StructOf types ->
        r.Align 8
        DBusValue.Struct(types |> List.map (fun item -> read r item (depth + 1)))
    | DictEntryOf(key, value) ->
        r.Align 8
        let k = read r key (depth + 1)
        let v = read r value (depth + 1)
        DBusValue.DictEntry(k, v)

/// Reads values of a signature from bytes that start 8-aligned (a message body).
let unmarshal (littleEndian: bool) (signature: string) (data: byte array) : Result<DBusValue list, string> =
    try
        let r = Reader(data, littleEndian)
        let values = parseSignature signature |> List.map (fun t -> read r t 0)

        if r.Position <> data.Length then
            Error "trailing bytes after the values"
        else
            Ok values
    with :? InvalidDataException as e ->
        Error e.Message

let private headerFields (m: Message) : DBusValue =
    let field (code: byte) (value: DBusValue option) =
        value
        |> Option.map (fun v -> DBusValue.Struct [ DBusValue.Byte code; DBusValue.Variant v ])

    let signature = m.Signature

    DBusValue.Array(
        "(yv)",
        List.choose
            id
            [
                field 1uy (m.Path |> Option.map DBusValue.ObjectPath)
                field 2uy (m.Interface |> Option.map DBusValue.String)
                field 3uy (m.Member |> Option.map DBusValue.String)
                field 4uy (m.ErrorName |> Option.map DBusValue.String)
                field 5uy (m.ReplySerial |> Option.map DBusValue.UInt32)
                field 6uy (m.Destination |> Option.map DBusValue.String)
                field 7uy (m.Sender |> Option.map DBusValue.String)
                field
                    8uy
                    (if signature = "" then
                         None
                     else
                         Some(DBusValue.Signature signature))
            ]
    )

/// Marshals a message, little-endian. Throws `ArgumentException` for values that cannot be sent
/// (a defect in the caller).
let encode (m: Message) : byte array =
    if m.Serial = 0u then
        invalidArg (nameof m) "a message needs a non-zero serial"

    let body = marshal m.Body
    let w = Writer()
    w.Byte(byte 'l')
    w.Byte m.Type.Code
    w.Byte m.Flags
    w.Byte 1uy
    w.UInt32(uint32 body.Length)
    w.UInt32 m.Serial
    write w (headerFields m)
    w.Pad 8
    w.Raw body
    let bytes = w.ToArray()

    if bytes.Length > MaxMessageBytes then
        invalidArg (nameof m) "message too large"

    bytes

let private roundUp8 (n: int64) = (n + 7L) / 8L * 8L

/// The total length of the message whose first 16 bytes these are.
let frameLength (header: ReadOnlySpan<byte>) : Result<int, string> =
    if header.Length < 16 then
        Error "a message header has at least 16 bytes"
    elif header[0] <> byte 'l' && header[0] <> byte 'B' then
        Error "invalid byte-order mark"
    else
        let little = header[0] = byte 'l'

        let bodyLength, fieldsLength =
            if little then
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4))
            else
                BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12, 4))

        let total = roundUp8 (16L + int64 fieldsLength) + int64 bodyLength

        if total > int64 MaxMessageBytes then
            Error "message too large"
        else
            Ok(int total)

/// Unmarshals a whole message.
let decode (data: byte array) : Result<Message, string> =
    try
        if data.Length < 16 then
            fail "message truncated"

        let little =
            match char data[0] with
            | 'l' -> true
            | 'B' -> false
            | _ -> fail "invalid byte-order mark"

        let r = Reader(data, little)
        r.Position <- 1

        let messageType =
            match r.Byte() with
            | 1uy -> MessageType.MethodCall
            | 2uy -> MessageType.MethodReturn
            | 3uy -> MessageType.Error
            | 4uy -> MessageType.Signal
            | other -> fail $"unknown message type {other}"

        let flags = r.Byte()

        if r.Byte() <> 1uy then
            fail "unsupported protocol version"

        let bodyLength = r.UInt32()
        let serial = r.UInt32()

        let fields =
            match read r (ArrayOf(StructOf [ Basic 'y'; VariantType ])) 0 with
            | DBusValue.Array(_, items) ->
                items
                |> List.choose (fun item ->
                    match item with
                    | DBusValue.Struct [ DBusValue.Byte code; DBusValue.Variant value ] -> Some(code, value)
                    | _ -> None)
            | _ -> []

        r.Align 8

        if int64 r.Position + int64 bodyLength <> int64 data.Length then
            fail "body length does not match the message"

        let field (code: byte) =
            fields |> List.tryFind (fun (c, _) -> c = code) |> Option.map snd

        let text code =
            match field code with
            | Some(DBusValue.String s)
            | Some(DBusValue.ObjectPath s) -> Some s
            | Some _ -> fail $"header field {code} has the wrong type"
            | None -> None

        let signature =
            match field 8uy with
            | Some(DBusValue.Signature s) -> s
            | Some _ -> fail "header field 8 has the wrong type"
            | None -> ""

        let body = parseSignature signature |> List.map (fun t -> read r t 0)

        if r.Position <> data.Length then
            fail "trailing bytes after the body"

        Ok
            {
                Type = messageType
                Flags = flags
                Serial = serial
                Path = text 1uy
                Interface = text 2uy
                Member = text 3uy
                ErrorName = text 4uy
                ReplySerial =
                    match field 5uy with
                    | Some(DBusValue.UInt32 n) -> Some n
                    | Some _ -> fail "header field 5 has the wrong type"
                    | None -> None
                Destination = text 6uy
                Sender = text 7uy
                Body = body
            }
    with :? InvalidDataException as e ->
        Error e.Message
