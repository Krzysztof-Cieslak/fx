namespace Fx.Contracts

open System.Text

/// A JSON value (RFC 8259).
///
/// - Numbers keep their exact text (`JsonNumber`).
/// - Arrays and objects are immutable lists; objects keep their member order, so output is stable.
/// - Equality is structural and compares number text, member order included. Compare
///   `Json.canonicalize` results to ignore member order and number notation.
///
/// Values read by `Json.parse` never have duplicate keys and are at most `ReadOptions.MaxDepth` deep.
/// Code that builds `Json` must keep it so: canonicalization rejects duplicate keys, and the writer,
/// equality and the codecs recurse once per level of nesting.
[<RequireQualifiedAccess; NoComparison>]
type Json =
    | Null
    | Bool of bool
    | Number of JsonNumber
    | String of string
    | Array of Json list
    | Object of (string * Json) list

    /// Compact JSON text.
    override this.ToString() = JsonWriter.Write(this, 0)

/// Serializes `Json` to text. Strings are escaped minimally, as in RFC 8785 (JCS) and JavaScript's
/// `JSON.stringify`: `"` `\` and control characters only (`\b \f \n \r \t`, others as lowercase
/// `\u00xx`). Lone surrogates are escaped as `\udxxx`, so the output is always valid UTF-16 and
/// every string reads back unchanged.
and [<AbstractClass; Sealed>] internal JsonWriter =

    static member private Hex = "0123456789abcdef"

    static member internal IsHighSurrogate(c: char) = c >= '\uD800' && c <= '\uDBFF'

    static member internal IsLowSurrogate(c: char) = c >= '\uDC00' && c <= '\uDFFF'

    static member private AppendUnicodeEscape(sb: StringBuilder, c: char) =
        let code = int c

        sb
            .Append("\\u")
            .Append(JsonWriter.Hex[(code >>> 12) &&& 0xF])
            .Append(JsonWriter.Hex[(code >>> 8) &&& 0xF])
            .Append(JsonWriter.Hex[(code >>> 4) &&& 0xF])
            .Append(JsonWriter.Hex[code &&& 0xF])
        |> ignore

    /// Appends the escaped form of `s[i]` (and of the low surrogate after it, for a pair).
    /// Returns how many characters of `s` were consumed.
    static member internal AppendEscapedChar(sb: StringBuilder, s: string, i: int) : int =
        let c = s[i]

        match c with
        | '"' ->
            sb.Append "\\\"" |> ignore
            1
        | '\\' ->
            sb.Append "\\\\" |> ignore
            1
        | '\b' ->
            sb.Append "\\b" |> ignore
            1
        | '\f' ->
            sb.Append "\\f" |> ignore
            1
        | '\n' ->
            sb.Append "\\n" |> ignore
            1
        | '\r' ->
            sb.Append "\\r" |> ignore
            1
        | '\t' ->
            sb.Append "\\t" |> ignore
            1
        | c when c < ' ' ->
            JsonWriter.AppendUnicodeEscape(sb, c)
            1
        | c when JsonWriter.IsHighSurrogate c ->
            if i + 1 < s.Length && JsonWriter.IsLowSurrogate s[i + 1] then
                sb.Append(c).Append(s[i + 1]) |> ignore
                2
            else
                JsonWriter.AppendUnicodeEscape(sb, c)
                1
        | c when JsonWriter.IsLowSurrogate c ->
            JsonWriter.AppendUnicodeEscape(sb, c)
            1
        | c ->
            sb.Append c |> ignore
            1

    static member private NeedsEscape(c: char) =
        c < ' ' || c = '"' || c = '\\' || (c >= '\uD800' && c <= '\uDFFF')

    /// Appends a quoted, escaped JSON string.
    static member internal AppendString(sb: StringBuilder, s: string) =
        sb.Append '"' |> ignore
        let mutable start = 0
        let mutable i = 0

        while i < s.Length do
            if JsonWriter.NeedsEscape s[i] then
                sb.Append(s, start, i - start) |> ignore
                i <- i + JsonWriter.AppendEscapedChar(sb, s, i)
                start <- i
            else
                i <- i + 1

        sb.Append(s, start, s.Length - start).Append '"' |> ignore

    static member private NewLine(sb: StringBuilder, indent: int, level: int) =
        sb.Append('\n').Append(' ', indent * level) |> ignore

    /// Appends `json`; `indent` > 0 writes one member or element per line, indented by that many
    /// spaces per level, with `\n` line breaks on every platform.
    static member internal Append(sb: StringBuilder, json: Json, indent: int, level: int) =
        match json with
        | Json.Null -> sb.Append "null" |> ignore
        | Json.Bool true -> sb.Append "true" |> ignore
        | Json.Bool false -> sb.Append "false" |> ignore
        | Json.Number n -> sb.Append n.Text |> ignore
        | Json.String s -> JsonWriter.AppendString(sb, s)
        | Json.Array [] -> sb.Append "[]" |> ignore
        | Json.Array items ->
            sb.Append '[' |> ignore
            let mutable first = true

            for item in items do
                if not first then
                    sb.Append ',' |> ignore

                first <- false

                if indent > 0 then
                    JsonWriter.NewLine(sb, indent, level + 1)

                JsonWriter.Append(sb, item, indent, level + 1)

            if indent > 0 then
                JsonWriter.NewLine(sb, indent, level)

            sb.Append ']' |> ignore
        | Json.Object [] -> sb.Append "{}" |> ignore
        | Json.Object members ->
            sb.Append '{' |> ignore
            let mutable first = true

            for key, value in members do
                if not first then
                    sb.Append ',' |> ignore

                first <- false

                if indent > 0 then
                    JsonWriter.NewLine(sb, indent, level + 1)

                JsonWriter.AppendString(sb, key)
                sb.Append(if indent > 0 then ": " else ":") |> ignore
                JsonWriter.Append(sb, value, indent, level + 1)

            if indent > 0 then
                JsonWriter.NewLine(sb, indent, level)

            sb.Append '}' |> ignore

    static member internal Write(json: Json, indent: int) : string =
        let sb = StringBuilder()
        JsonWriter.Append(sb, json, indent, 0)
        sb.ToString()

    static member internal Quote(s: string) : string =
        let sb = StringBuilder(s.Length + 2)
        JsonWriter.AppendString(sb, s)
        sb.ToString()

/// Where and why JSON text could not be read. `Line` and `Column` are 1-based; columns count UTF-16
/// code units. `Offset` is the 0-based index into the text.
type JsonError =
    {
        Message: string
        Offset: int
        Line: int
        Column: int
    }

    override this.ToString() =
        $"{this.Message} at line {this.Line}, column {this.Column}"

/// Options for reading JSON text.
type ReadOptions =
    {
        /// Maximum nesting of arrays and objects; `[]` has depth 1. Deeper input is rejected.
        MaxDepth: int
    }

    override this.ToString() =
        $"ReadOptions(MaxDepth={this.MaxDepth})"

module ReadOptions =
    /// `MaxDepth` 128.
    let defaults = { MaxDepth = 128 }
