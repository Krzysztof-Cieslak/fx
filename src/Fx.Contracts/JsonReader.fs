namespace Fx.Contracts

open System
open System.Collections.Generic
open System.Text

/// An array or object that is being read.
[<Sealed>]
type internal JsonFrame(isObject: bool) =
    let values = ResizeArray<Json>()
    // Parallel to `values` for objects: every key is followed by exactly one value.
    let keys = ResizeArray<string>(if isObject then 4 else 0)
    let mutable seen: HashSet<string> | null = null

    member _.IsObject = isObject
    member _.Values = values

    /// Records a key; false if the object already has it. Small objects are scanned linearly.
    member _.AddKey(key: string) : bool =
        let fresh =
            match seen with
            | null when keys.Count < 16 -> not (keys.Contains key)
            | null ->
                let set = HashSet<string>(keys, StringComparer.Ordinal)
                seen <- set
                set.Add key
            | set -> set.Add key

        if fresh then
            keys.Add key

        fresh

    member _.ToJson() : Json =
        if isObject then
            let mutable members = []

            for i in values.Count - 1 .. -1 .. 0 do
                members <- (keys[i], values[i]) :: members

            Json.Object members
        else
            let mutable items = []

            for i in values.Count - 1 .. -1 .. 0 do
                items <- values[i] :: items

            Json.Array items

/// A strict RFC 8259 reader. It is iterative, so nesting depth is only a policy (`MaxDepth`), never a
/// stack overflow.
///
/// Beyond the grammar it rejects duplicate object keys and number exponents with more than 9
/// significant digits; it ignores one leading byte order mark. Escaped and raw lone surrogates are
/// accepted (RFC 8259 permits them) and kept, so every string the writer produces reads back unchanged.
[<Sealed>]
type internal JsonReader(text: string, options: ReadOptions) =
    let mutable pos = 0
    let mutable error: (int * string) option = None
    let stack = Stack<JsonFrame>()

    static let describeChar (c: char) =
        if c = '\'' then
            "\"'\""
        elif c > ' ' && c <= '~' then
            $"'{c}'"
        else
            let hex = "0123456789ABCDEF"
            let code = int c

            String
                [|
                    'U'
                    '+'
                    hex[code >>> 12]
                    hex[(code >>> 8) &&& 0xF]
                    hex[(code >>> 4) &&& 0xF]
                    hex[code &&& 0xF]
                |]

    let fail (offset: int) (message: string) =
        if error.IsNone then
            error <- Some(offset, message)

    let failHere (expected: string) =
        if pos >= text.Length then
            fail pos $"unexpected end of input; expected {expected}"
        else
            fail pos $"unexpected character {describeChar text[pos]}; expected {expected}"

    let skipWhitespace () =
        let mutable scanning = true

        while scanning && pos < text.Length do
            match text[pos] with
            | ' '
            | '\t'
            | '\n'
            | '\r' -> pos <- pos + 1
            | _ -> scanning <- false

    let hexValue (c: char) =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    /// Reads the escape sequence at `pos` (after the backslash) into `sb`.
    let readEscape (sb: StringBuilder) =
        let escapeStart = pos - 1

        if pos >= text.Length then
            fail pos "unexpected end of input in a string escape"
        else
            match text[pos] with
            | '"' -> sb.Append '"' |> ignore
            | '\\' -> sb.Append '\\' |> ignore
            | '/' -> sb.Append '/' |> ignore
            | 'b' -> sb.Append '\b' |> ignore
            | 'f' -> sb.Append '\f' |> ignore
            | 'n' -> sb.Append '\n' |> ignore
            | 'r' -> sb.Append '\r' |> ignore
            | 't' -> sb.Append '\t' |> ignore
            | 'u' ->
                let mutable code = 0

                for k in 1..4 do
                    let h = if pos + k < text.Length then hexValue text[pos + k] else -1

                    if h < 0 then
                        fail escapeStart "invalid \\u escape; expected 4 hex digits"
                    else
                        code <- code * 16 + h

                if error.IsNone then
                    sb.Append(char code) |> ignore
                    pos <- pos + 4
            | c -> fail escapeStart $"invalid escape sequence \\{c}"

            pos <- pos + 1

    /// Reads the string whose opening quote is at `pos`.
    let readString () : string =
        let quote = pos
        pos <- pos + 1
        let start = pos
        let mutable result: string | null = null
        let mutable sb: StringBuilder | null = null

        while error.IsNone && isNull result do
            if pos >= text.Length then
                fail quote "unterminated string"
            else
                let c = text[pos]

                if c = '"' then
                    result <-
                        match sb with
                        | null -> text.Substring(start, pos - start)
                        | sb -> sb.ToString()

                    pos <- pos + 1
                elif c = '\\' then
                    let builder =
                        match sb with
                        | null ->
                            let b = StringBuilder(text, start, pos - start, pos - start + 16)
                            sb <- b
                            b
                        | b -> b

                    pos <- pos + 1
                    readEscape builder
                elif c < ' ' then
                    fail pos $"control character {describeChar c} must be escaped in a string"
                else
                    match sb with
                    | null -> ()
                    | b -> b.Append c |> ignore

                    pos <- pos + 1

        match result with
        | null -> ""
        | s -> s

    let readLiteral (literal: string) (value: Json) : Json =
        let mutable matches = pos + literal.Length <= text.Length

        for k in 0 .. literal.Length - 1 do
            if matches && text[pos + k] <> literal[k] then
                matches <- false

        if matches then
            pos <- pos + literal.Length
            value
        else
            fail pos $"invalid literal; expected {literal}"
            Json.Null

    let readNumber () : Json =
        match JsonNumber.scan text pos with
        | Ok finish ->
            let number = JsonNumber.ofValidText (text.Substring(pos, finish - pos))
            pos <- finish
            Json.Number number
        | Error(offset, message) ->
            fail offset message
            Json.Null

    let push (isObject: bool) =
        if stack.Count >= options.MaxDepth then
            fail pos $"maximum nesting depth {options.MaxDepth} exceeded"
        else
            stack.Push(JsonFrame isObject)
            pos <- pos + 1

    /// Reads `"key":` for the object on top of the stack.
    let readKey () =
        skipWhitespace ()

        if pos < text.Length && text[pos] = '"' then
            let keyStart = pos
            let key = readString ()

            if error.IsNone then
                let frame = stack.Peek()

                if not (frame.AddKey key) then
                    fail keyStart $"duplicate key {JsonWriter.Quote key}"
                else
                    skipWhitespace ()

                    if pos < text.Length && text[pos] = ':' then
                        pos <- pos + 1
                    else
                        failHere "':' after an object key"
        else
            failHere "a string key"

    let lineAndColumn (offset: int) =
        let mutable line = 1
        let mutable lineStart = 0

        for i in 0 .. offset - 1 do
            match text[i] with
            | '\n' ->
                line <- line + 1
                lineStart <- i + 1
            | '\r' when i + 1 >= text.Length || text[i + 1] <> '\n' ->
                line <- line + 1
                lineStart <- i + 1
            | _ -> ()

        line, offset - lineStart + 1

    member _.Read() : Result<Json, JsonError> =
        if text.Length > 0 && text[0] = '\uFEFF' then
            pos <- 1

        let mutable result: Json option = None
        // A completed value waiting to be added to the container on top of the stack.
        let mutable completed: Json option = None

        while error.IsNone && result.IsNone do
            match completed with
            | None ->
                // Expecting a value.
                skipWhitespace ()

                if pos >= text.Length then
                    failHere "a value"
                else
                    match text[pos] with
                    | '{' ->
                        push true

                        if error.IsNone then
                            skipWhitespace ()

                            if pos < text.Length && text[pos] = '}' then
                                pos <- pos + 1
                                completed <- Some(stack.Pop().ToJson())
                            else
                                readKey ()
                    | '[' ->
                        push false

                        if error.IsNone then
                            skipWhitespace ()

                            if pos < text.Length && text[pos] = ']' then
                                pos <- pos + 1
                                completed <- Some(stack.Pop().ToJson())
                    | '"' ->
                        let s = readString ()
                        completed <- Some(Json.String s)
                    | 't' -> completed <- Some(readLiteral "true" (Json.Bool true))
                    | 'f' -> completed <- Some(readLiteral "false" (Json.Bool false))
                    | 'n' -> completed <- Some(readLiteral "null" Json.Null)
                    | c when c = '-' || (c >= '0' && c <= '9') -> completed <- Some(readNumber ())
                    | _ -> failHere "a value"
            | Some value ->
                if stack.Count = 0 then
                    result <- Some value
                else
                    let frame = stack.Peek()
                    frame.Values.Add value
                    completed <- None
                    skipWhitespace ()

                    let closing = if frame.IsObject then '}' else ']'

                    if pos < text.Length && text[pos] = ',' then
                        pos <- pos + 1

                        if frame.IsObject then
                            readKey ()
                    elif pos < text.Length && text[pos] = closing then
                        pos <- pos + 1
                        completed <- Some(stack.Pop().ToJson())
                    elif frame.IsObject then
                        failHere "',' or '}' after an object member"
                    else
                        failHere "',' or ']' after an array element"

        if error.IsNone then
            skipWhitespace ()

            if pos < text.Length then
                fail pos $"unexpected character {describeChar text[pos]} after the JSON value"

        match error, result with
        | None, Some json -> Ok json
        | Some(offset, message), _ ->
            let line, column = lineAndColumn offset

            Error
                {
                    Message = message
                    Offset = offset
                    Line = line
                    Column = column
                }
        | None, None -> failwith "unreachable: the reader stopped without a value or an error"
