namespace Fx.Contracts

open System

/// Reading, writing, canonicalizing and inspecting `Json` values.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Json =

    /// Reads JSON text with `ReadOptions.defaults`. See `parseWith`.
    let parseWith (options: ReadOptions) (text: string) : Result<Json, JsonError> = JsonReader(text, options).Read()

    /// Reads JSON text strictly (RFC 8259): no comments, trailing commas, single quotes, unquoted keys,
    /// `NaN` or leading zeros. Duplicate keys and input nested deeper than `ReadOptions.MaxDepth` are
    /// errors; one leading byte order mark is ignored.
    let parse (text: string) : Result<Json, JsonError> = parseWith ReadOptions.defaults text

    /// Compact JSON text: no whitespace, members in order, numbers as written.
    let serialize (json: Json) : string = JsonWriter.Write(json, 0)

    /// JSON text with two-space indentation and `\n` line breaks (as `JSON.stringify(v, null, 2)`).
    let serializeIndented (json: Json) : string = JsonWriter.Write(json, 2)

    /// A JSON string literal: `s` quoted and escaped as by the writer.
    let quote (s: string) : string = JsonWriter.Quote s

    /// The canonical form of a value, the basis for content hashes:
    ///
    /// - object members are sorted by key, comparing UTF-16 code units (ordinal), as in RFC 8785;
    /// - numbers are normalized exactly (`JsonNumber.normalize`): equal values get equal text;
    /// - everything else, including array order and string content, is unchanged.
    ///
    /// `serializeCanonical` writes it compactly with the writer's minimal escaping. The result equals
    /// RFC 8785 (JCS) output, except that numbers keep every digit instead of going through binary
    /// floating point; for numbers that are exact doubles the two agree.
    /// Throws if an object has duplicate keys: such a value has no canonical form.
    let rec canonicalize (json: Json) : Json =
        match json with
        | Json.Number n -> Json.Number(JsonNumber.normalize n)
        | Json.Array items -> Json.Array(List.map canonicalize items)
        | Json.Object members ->
            let sorted =
                members
                |> List.map (fun (key, value) -> key, canonicalize value)
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

            let rec checkUnique (members: (string * Json) list) =
                match members with
                | (a, _) :: ((b, _) :: _ as rest) ->
                    if String.Equals(a, b, StringComparison.Ordinal) then
                        invalidArg (nameof json) $"Duplicate key {quote a}: the value has no canonical form."

                    checkUnique rest
                | _ -> ()

            checkUnique sorted
            Json.Object sorted
        | Json.Null
        | Json.Bool _
        | Json.String _ -> json

    /// The canonical text (see `canonicalize`); equal values give byte-identical UTF-8.
    let serializeCanonical (json: Json) : string = serialize (canonicalize json)

    /// The JSON type of a value: `null`, `boolean`, `number`, `string`, `array` or `object`.
    let kind (json: Json) : string =
        match json with
        | Json.Null -> "null"
        | Json.Bool _ -> "boolean"
        | Json.Number _ -> "number"
        | Json.String _ -> "string"
        | Json.Array _ -> "array"
        | Json.Object _ -> "object"

    [<Literal>]
    let private DescribeChars = 40

    let private shorten (s: string) =
        if s.Length <= DescribeChars then
            s
        else
            let cut =
                if JsonWriter.IsHighSurrogate s[DescribeChars - 1] then
                    DescribeChars - 1
                else
                    DescribeChars

            s.Substring(0, cut) + "…"

    /// A short description of a value for messages, e.g. `number 42`, `string "abc"`,
    /// `array of 3 items` or `object`. Long numbers and strings are cut after 40 characters.
    let describe (json: Json) : string =
        match json with
        | Json.Null -> "null"
        | Json.Bool true -> "boolean true"
        | Json.Bool false -> "boolean false"
        | Json.Number n -> "number " + shorten n.Text
        | Json.String s -> "string " + quote (shorten s)
        | Json.Array [] -> "empty array"
        | Json.Array [ _ ] -> "array of 1 item"
        | Json.Array items -> $"array of {List.length items} items"
        | Json.Object [] -> "empty object"
        | Json.Object _ -> "object"

    /// The value of the first member named `name`, if `json` is an object that has one.
    let tryField (name: string) (json: Json) : Json option =
        match json with
        | Json.Object members ->
            members
            |> List.tryFind (fun (key, _) -> String.Equals(key, name, StringComparison.Ordinal))
            |> Option.map snd
        | _ -> None

    /// A number from exact JSON number text, if `text` follows the JSON number grammar.
    let tryNumber (text: string) : Json option =
        JsonNumber.tryParse text |> Option.map Json.Number

    // Constructors last: they shadow the conversion functions of the same names.

    let int (value: int) : Json = Json.Number(JsonNumber.ofInt value)

    let int64 (value: int64) : Json = Json.Number(JsonNumber.ofInt64 value)

    let decimal (value: decimal) : Json = Json.Number(JsonNumber.ofDecimal value)

    /// A finite float as its shortest round-trip text. Throws for NaN and infinities.
    let float (value: float) : Json = Json.Number(JsonNumber.ofFloat value)
