namespace Fx.Tui

open System
open System.Text

/// Text inside a block of Markdown.
[<RequireQualifiedAccess>]
type Inline =
    | Plain of string
    | Strong of Inline list
    | Emphasis of Inline list
    | Strike of Inline list
    | Code of string
    | Link of text: Inline list * url: string

    override this.ToString() =
        match this with
        | Plain text -> text
        | Strong inlines -> "**" + (inlines |> List.map string |> String.concat "") + "**"
        | Emphasis inlines -> "*" + (inlines |> List.map string |> String.concat "") + "*"
        | Strike inlines -> "~~" + (inlines |> List.map string |> String.concat "") + "~~"
        | Code text -> "`" + text + "`"
        | Link(text, url) -> "[" + (text |> List.map string |> String.concat "") + "](" + url + ")"

/// A block of Markdown.
[<RequireQualifiedAccess>]
type MdBlock =
    | Paragraph of Inline list
    | Heading of level: int * Inline list
    /// Items of a list, each its own blocks; `start` numbers an ordered list.
    | List of ordered: bool * start: int * items: MdBlock list list
    | Quote of MdBlock list
    | Code of language: string * code: string
    | Rule
    | Table of header: Inline list list * rows: Inline list list list

    override this.ToString() =
        match this with
        | Paragraph inlines -> "paragraph " + string inlines.Length
        | Heading(level, _) -> "heading " + string level
        | List(ordered, _, items) -> (if ordered then "ordered " else "list ") + string items.Length
        | Quote blocks -> "quote " + string blocks.Length
        | Code(language, _) -> "code " + language
        | Rule -> "rule"
        | Table(header, rows) -> $"table {header.Length}x{rows.Length}"

/// What a token of code is, for highlighting.
[<RequireQualifiedAccess>]
type TokenKind =
    | Text
    | Keyword
    | String
    | Comment
    | Number

    override this.ToString() =
        match this with
        | Text -> "text"
        | Keyword -> "keyword"
        | String -> "string"
        | Comment -> "comment"
        | Number -> "number"

/// Keyword highlighting for the languages agents write most here: F#, TypeScript and JavaScript,
/// JSON and shells. Line by line: a comment or string that spans lines is not followed.
[<RequireQualifiedAccess>]
module Highlight =
    type private Language =
        {
            Keywords: Set<string>
            LineComments: string list
            Quotes: char list
        }

    let private fsharp =
        {
            Keywords =
                set
                    [
                        "let"
                        "mutable"
                        "rec"
                        "and"
                        "in"
                        "if"
                        "then"
                        "elif"
                        "else"
                        "match"
                        "with"
                        "function"
                        "fun"
                        "type"
                        "module"
                        "namespace"
                        "open"
                        "member"
                        "override"
                        "static"
                        "private"
                        "internal"
                        "public"
                        "interface"
                        "inherit"
                        "abstract"
                        "default"
                        "for"
                        "while"
                        "do"
                        "done"
                        "to"
                        "downto"
                        "yield"
                        "return"
                        "use"
                        "new"
                        "not"
                        "true"
                        "false"
                        "null"
                        "try"
                        "finally"
                        "raise"
                        "of"
                        "when"
                        "as"
                        "upcast"
                        "downcast"
                        "inline"
                        "task"
                        "async"
                        "seq"
                        "struct"
                        "val"
                        "exception"
                        "lazy"
                        "assert"
                        "base"
                        "begin"
                        "end"
                    ]
            LineComments = [ "//" ]
            Quotes = [ '"' ]
        }

    let private typescript =
        {
            Keywords =
                set
                    [
                        "const"
                        "let"
                        "var"
                        "function"
                        "return"
                        "if"
                        "else"
                        "for"
                        "while"
                        "do"
                        "switch"
                        "case"
                        "default"
                        "break"
                        "continue"
                        "new"
                        "class"
                        "extends"
                        "implements"
                        "interface"
                        "type"
                        "enum"
                        "import"
                        "export"
                        "from"
                        "as"
                        "async"
                        "await"
                        "yield"
                        "try"
                        "catch"
                        "finally"
                        "throw"
                        "typeof"
                        "instanceof"
                        "in"
                        "of"
                        "void"
                        "null"
                        "undefined"
                        "true"
                        "false"
                        "this"
                        "super"
                        "static"
                        "public"
                        "private"
                        "protected"
                        "readonly"
                        "declare"
                        "namespace"
                        "keyof"
                        "satisfies"
                    ]
            LineComments = [ "//" ]
            Quotes = [ '"'; '\''; '`' ]
        }

    let private json =
        {
            Keywords = set [ "true"; "false"; "null" ]
            LineComments = [ "//" ]
            Quotes = [ '"' ]
        }

    let private shell =
        {
            Keywords =
                set
                    [
                        "if"
                        "then"
                        "else"
                        "elif"
                        "fi"
                        "for"
                        "in"
                        "do"
                        "done"
                        "while"
                        "case"
                        "esac"
                        "function"
                        "return"
                        "export"
                        "local"
                        "set"
                        "unset"
                        "echo"
                        "cd"
                        "exit"
                    ]
            LineComments = [ "#" ]
            Quotes = [ '"'; '\'' ]
        }

    let private language (name: string) : Language option =
        match name.Trim().ToLowerInvariant() with
        | "fsharp"
        | "f#"
        | "fs"
        | "fsx"
        | "fsi" -> Some fsharp
        | "ts"
        | "typescript"
        | "tsx"
        | "js"
        | "javascript"
        | "jsx"
        | "mjs" -> Some typescript
        | "json"
        | "jsonc" -> Some json
        | "sh"
        | "bash"
        | "zsh"
        | "shell"
        | "console"
        | "pwsh"
        | "powershell"
        | "ps1" -> Some shell
        | _ -> None

    let private isWordChar (c: char) =
        Char.IsLetterOrDigit c || c = '_' || c = '$'

    let private scan (lang: Language) (line: string) : (TokenKind * string) list =
        let tokens = ResizeArray<TokenKind * string>()
        let text = StringBuilder()

        let flush () =
            if text.Length > 0 then
                tokens.Add((TokenKind.Text, text.ToString()))
                text.Clear() |> ignore

        let mutable i = 0

        while i < line.Length do
            let c = line[i]

            let here = i

            let comment =
                lang.LineComments
                |> List.exists (fun marker -> String.CompareOrdinal(line, here, marker, 0, marker.Length) = 0)

            match comment with
            | true when i = 0 || not (isWordChar line[i - 1]) ->
                flush ()
                tokens.Add((TokenKind.Comment, line.Substring i))
                i <- line.Length
            | _ ->
                if List.contains c lang.Quotes then
                    flush ()
                    let mutable j = i + 1

                    while j < line.Length && line[j] <> c do
                        if line[j] = '\\' then j <- j + 2 else j <- j + 1

                    let stop = min line.Length (j + 1)
                    tokens.Add((TokenKind.String, line.Substring(i, stop - i)))
                    i <- stop
                elif Char.IsDigit c && (i = 0 || not (isWordChar line[i - 1])) then
                    flush ()
                    let mutable j = i

                    while j < line.Length
                          && (Char.IsLetterOrDigit line[j] || line[j] = '.' || line[j] = '_') do
                        j <- j + 1

                    tokens.Add((TokenKind.Number, line.Substring(i, j - i)))
                    i <- j
                elif isWordChar c then
                    let mutable j = i

                    while j < line.Length && isWordChar line[j] do
                        j <- j + 1

                    let word = line.Substring(i, j - i)

                    if lang.Keywords.Contains word then
                        flush ()
                        tokens.Add((TokenKind.Keyword, word))
                    else
                        text.Append word |> ignore

                    i <- j
                else
                    text.Append c |> ignore
                    i <- i + 1

        flush ()
        List.ofSeq tokens

    /// A line of code as tokens, by the fence's language (one plain token when it is not known).
    let line (languageName: string) (text: string) : (TokenKind * string) list =
        match language languageName with
        | Some lang -> scan lang text
        | None -> [ TokenKind.Text, text ]

/// The Markdown agents write (CommonMark's common part: headings, paragraphs, emphasis, inline code,
/// links, lists, quotes, fenced code, rules, and GitHub's tables) parsed into blocks and drawn as
/// rows in a theme. Pure; anything it does not follow shows as the text it is.
[<RequireQualifiedAccess>]
module Markdown =
    // ---- inlines --------------------------------------------------------------------------------

    let private isPunctuation (c: char) = Char.IsPunctuation c || Char.IsSymbol c

    /// Where `delimiter` closes after `from`: not after a space, and (for `_`) not inside a word.
    let private closing (text: string) (from: int) (delimiter: string) : int option =
        let mutable found = None
        let mutable j = from

        while found.IsNone && j <= text.Length - delimiter.Length do
            if
                String.CompareOrdinal(text, j, delimiter, 0, delimiter.Length) = 0
                && j > from
                && not (Char.IsWhiteSpace text[j - 1])
                && not (
                    delimiter[0] = '_'
                    && j + delimiter.Length < text.Length
                    && Char.IsLetterOrDigit text[j + delimiter.Length]
                )
                // `**` is not a closing `*`.
                && not (delimiter.Length = 1 && j + 1 < text.Length && text[j + 1] = delimiter[0])
            then
                found <- Some j
            elif text[j] = '`' then
                // Code spans hide delimiters.
                let next = text.IndexOf('`', j + 1)
                j <- if next < 0 then j + 1 else next + 1
            else
                j <- j + 1

        found

    let rec inlines (text: string) : Inline list =
        let out = ResizeArray<Inline>()
        let plain = StringBuilder()

        let flush () =
            if plain.Length > 0 then
                out.Add(Inline.Plain(plain.ToString()))
                plain.Clear() |> ignore

        let mutable i = 0

        while i < text.Length do
            let c = text[i]

            let at (k: int) =
                if k < text.Length then text[k] else '\000'

            if c = '\\' && i + 1 < text.Length && isPunctuation text[i + 1] then
                plain.Append text[i + 1] |> ignore
                i <- i + 2
            elif c = '`' then
                let mutable run = 0

                while at (i + run) = '`' do
                    run <- run + 1

                let fence = String('`', run)
                let mutable j = text.IndexOf(fence, i + run, StringComparison.Ordinal)

                // The closing run is exactly as long.
                while j >= 0 && at (j + run) = '`' do
                    let mutable k = j

                    while at k = '`' do
                        k <- k + 1

                    j <- text.IndexOf(fence, k, StringComparison.Ordinal)

                if j >= 0 then
                    flush ()
                    let code = text.Substring(i + run, j - i - run)

                    let code =
                        if
                            code.Length >= 2
                            && code[0] = ' '
                            && code[code.Length - 1] = ' '
                            && code.Trim() <> ""
                        then
                            code.Substring(1, code.Length - 2)
                        else
                            code

                    out.Add(Inline.Code code)
                    i <- j + run
                else
                    plain.Append fence |> ignore
                    i <- i + run
            elif
                (c = '*' || c = '_' || c = '~')
                && not (Char.IsWhiteSpace(at (i + 1)))
                && at (i + 1) <> '\000'
            then
                let double = at (i + 1) = c
                let intraword = c = '_' && i > 0 && Char.IsLetterOrDigit text[i - 1]
                let here = i

                let tryRun (size: int) =
                    let delimiter = String(c, size)
                    let start = here + size

                    if start < text.Length && not (Char.IsWhiteSpace text[start]) then
                        closing text start delimiter |> Option.map (fun j -> size, j)
                    else
                        None

                let found =
                    if intraword then None
                    elif double then tryRun 2
                    elif c = '~' then None
                    else tryRun 1

                match found with
                | Some(size, j) ->
                    flush ()
                    let inner = inlines (text.Substring(i + size, j - i - size))

                    out.Add(
                        match c, size with
                        | '~', _ -> Inline.Strike inner
                        | _, 2 -> Inline.Strong inner
                        | _ -> Inline.Emphasis inner
                    )

                    i <- j + size
                | None ->
                    plain.Append c |> ignore
                    i <- i + 1
            elif c = '[' then
                let close = text.IndexOf(']', i + 1)

                if close > i && at (close + 1) = '(' then
                    let paren = text.IndexOf(')', close + 2)

                    if paren > close then
                        flush ()
                        let label = text.Substring(i + 1, close - i - 1)
                        let url = text.Substring(close + 2, paren - close - 2).Trim()
                        out.Add(Inline.Link(inlines label, url))
                        i <- paren + 1
                    else
                        plain.Append c |> ignore
                        i <- i + 1
                else
                    plain.Append c |> ignore
                    i <- i + 1
            elif
                c = '<'
                && (String.CompareOrdinal(text, i + 1, "http://", 0, 7) = 0
                    || String.CompareOrdinal(text, i + 1, "https://", 0, 8) = 0)
            then
                let close = text.IndexOf('>', i + 1)

                if close > i then
                    flush ()
                    let url = text.Substring(i + 1, close - i - 1)
                    out.Add(Inline.Link([ Inline.Plain url ], url))
                    i <- close + 1
                else
                    plain.Append c |> ignore
                    i <- i + 1
            else
                plain.Append c |> ignore
                i <- i + 1

        flush ()
        List.ofSeq out

    /// The text of inlines without their markup.
    let rec plainText (inlines: Inline list) : string =
        inlines
        |> List.map (fun item ->
            match item with
            | Inline.Plain text
            | Inline.Code text -> text
            | Inline.Strong inner
            | Inline.Emphasis inner
            | Inline.Strike inner
            | Inline.Link(inner, _) -> plainText inner)
        |> String.concat ""

    // ---- blocks ---------------------------------------------------------------------------------

    let private indentOf (line: string) =
        let mutable n = 0

        while n < line.Length && line[n] = ' ' do
            n <- n + 1

        n

    let private blank (line: string) = String.IsNullOrWhiteSpace line

    /// A fence's character, length and language: three or more backticks or tildes, indented at most
    /// three spaces.
    let private fence (line: string) : (char * int * string) option =
        let indent = indentOf line

        if indent > 3 || indent >= line.Length then
            None
        else
            let c = line[indent]

            if c <> '`' && c <> '~' then
                None
            else
                let mutable n = 0

                while indent + n < line.Length && line[indent + n] = c do
                    n <- n + 1

                let info = line.Substring(indent + n).Trim()

                if n >= 3 && not (c = '`' && info.Contains '`') then
                    let language =
                        match info.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead with
                        | Some word -> word
                        | None -> ""

                    Some(c, n, language)
                else
                    None

    let private heading (line: string) : (int * string) option =
        let indent = indentOf line

        if indent > 3 then
            None
        else
            let rest = line.Substring indent
            let mutable n = 0

            while n < rest.Length && rest[n] = '#' do
                n <- n + 1

            if n >= 1 && n <= 6 && (n = rest.Length || rest[n] = ' ') then
                let text = rest.Substring(n).Trim()
                // A closing run of `#` goes.
                let text = text.TrimEnd('#').TrimEnd()
                Some(n, text)
            else
                None

    let private rule (line: string) : bool =
        let text = line.Trim()

        if text.Length < 3 || indentOf line > 3 then
            false
        else
            let c = text[0]

            (c = '-' || c = '*' || c = '_')
            && text |> Seq.forall (fun ch -> ch = c || ch = ' ')
            && (text |> Seq.filter (fun ch -> ch = c) |> Seq.length) >= 3

    let private quote (line: string) : string option =
        let indent = indentOf line

        if indent <= 3 && indent < line.Length && line[indent] = '>' then
            let rest = line.Substring(indent + 1)
            Some(if rest.StartsWith(' ') then rest.Substring 1 else rest)
        else
            None

    /// A list item's marker: its indent, whether it is ordered, its number, and where its content
    /// starts.
    let private marker (line: string) : (int * bool * int * int) option =
        let indent = indentOf line

        if indent >= line.Length then
            None
        else
            let c = line[indent]

            let after (width: int) =
                let at = indent + width

                if at = line.Length then Some at
                elif line[at] = ' ' then Some(min line.Length (at + 1))
                else None

            if c = '-' || c = '*' || c = '+' then
                after 1 |> Option.map (fun content -> indent, false, 1, content)
            elif Char.IsDigit c then
                let mutable n = 0

                while indent + n < line.Length && Char.IsDigit line[indent + n] && n < 9 do
                    n <- n + 1

                if indent + n < line.Length && (line[indent + n] = '.' || line[indent + n] = ')') then
                    match after (n + 1) with
                    | Some content ->
                        let number =
                            Int32.Parse(line.Substring(indent, n), Globalization.CultureInfo.InvariantCulture)

                        Some(indent, true, number, content)
                    | None -> None
                else
                    None
            else
                None

    let private cells (line: string) : string list =
        let text = line.Trim()
        let text = if text.StartsWith('|') then text.Substring 1 else text

        let text =
            if text.EndsWith('|') && not (text.EndsWith("\\|")) then
                text.Substring(0, text.Length - 1)
            else
                text

        let out = ResizeArray<string>()
        let cell = StringBuilder()
        let mutable i = 0
        let mutable code = false

        while i < text.Length do
            let c = text[i]

            if c = '\\' && i + 1 < text.Length && text[i + 1] = '|' then
                cell.Append '|' |> ignore
                i <- i + 2
            else
                if c = '`' then
                    code <- not code

                if c = '|' && not code then
                    out.Add(cell.ToString().Trim())
                    cell.Clear() |> ignore
                else
                    cell.Append c |> ignore

                i <- i + 1

        out.Add(cell.ToString().Trim())
        List.ofSeq out

    let private delimiterRow (line: string) : bool =
        if not (line.Contains '-') then
            false
        else
            let parts = cells line

            not parts.IsEmpty
            && parts
               |> List.forall (fun part ->
                   let p = part.Trim()

                   p.Length >= 1
                   && p |> Seq.forall (fun ch -> ch = '-' || ch = ':')
                   && p.Contains '-')

    let private startsBlock (line: string) =
        (fence line).IsSome
        || (heading line).IsSome
        || rule line
        || (quote line).IsSome
        || (marker line).IsSome

    let rec private blocksOf (lines: string array) : MdBlock list =
        let blocks = ResizeArray<MdBlock>()
        let mutable i = 0

        while i < lines.Length do
            let line = lines[i]

            if blank line then
                i <- i + 1
            else
                match fence line with
                | Some(c, n, language) ->
                    let code = ResizeArray<string>()
                    let indent = indentOf line
                    let mutable j = i + 1
                    let mutable closed = false

                    while not closed && j < lines.Length do
                        match fence lines[j] with
                        | Some(c2, n2, "") when c2 = c && n2 >= n -> closed <- true
                        | _ ->
                            let l = lines[j]
                            let strip = min indent (indentOf l)
                            code.Add(l.Substring strip)
                            j <- j + 1

                    blocks.Add(MdBlock.Code(language, String.Join("\n", code)))
                    i <- if closed then j + 1 else j
                | None ->
                    match heading line with
                    | Some(level, text) ->
                        blocks.Add(MdBlock.Heading(level, inlines text))
                        i <- i + 1
                    | None when rule line ->
                        blocks.Add MdBlock.Rule
                        i <- i + 1
                    | None ->
                        match quote line with
                        | Some _ ->
                            let inner = ResizeArray<string>()
                            let mutable j = i
                            let mutable going = true

                            while going && j < lines.Length && not (blank lines[j]) do
                                match quote lines[j] with
                                | Some rest ->
                                    inner.Add rest
                                    j <- j + 1
                                // A lazy continuation of the quote's paragraph.
                                | None when not (startsBlock lines[j]) ->
                                    inner.Add lines[j]
                                    j <- j + 1
                                | None -> going <- false

                            blocks.Add(MdBlock.Quote(blocksOf (inner.ToArray())))
                            i <- j
                        | None ->
                            match marker line with
                            | Some(indent, ordered, start, _) ->
                                let items = ResizeArray<MdBlock list>()
                                let mutable j = i
                                let mutable going = true

                                while going && j < lines.Length do
                                    match marker lines[j] with
                                    | Some(ind, ord, _, content) when ind = indent && ord = ordered ->
                                        let width = content - ind
                                        let body = ResizeArray<string>()
                                        body.Add(lines[j].Substring(min lines[j].Length content))
                                        let mutable k = j + 1
                                        let mutable inItem = true

                                        while inItem && k < lines.Length do
                                            let l = lines[k]

                                            if blank l then
                                                // A blank line continues the item when the next line is indented under it.
                                                let mutable n = k + 1

                                                while n < lines.Length && blank lines[n] do
                                                    n <- n + 1

                                                if n < lines.Length && indentOf lines[n] >= ind + width then
                                                    body.Add ""
                                                    k <- k + 1
                                                else
                                                    inItem <- false
                                            elif indentOf l >= ind + width then
                                                body.Add(l.Substring(ind + width))
                                                k <- k + 1
                                            elif not (startsBlock l) && not (blank lines[k - 1]) then
                                                // A lazy continuation of the item's paragraph.
                                                body.Add(l.Trim())
                                                k <- k + 1
                                            else
                                                inItem <- false

                                        items.Add(blocksOf (body.ToArray()))
                                        j <- k

                                        // Blank lines between items.
                                        let mutable n = j

                                        while n < lines.Length && blank lines[n] do
                                            n <- n + 1

                                        match (if n < lines.Length then marker lines[n] else None) with
                                        | Some(ind2, ord2, _, _) when ind2 = indent && ord2 = ordered -> j <- n
                                        | _ -> going <- false
                                    | _ -> going <- false

                                blocks.Add(MdBlock.List(ordered, start, List.ofSeq items))
                                i <- j
                            | None when line.Contains '|' && i + 1 < lines.Length && delimiterRow lines[i + 1] ->
                                let header = cells line |> List.map inlines
                                let rows = ResizeArray<Inline list list>()
                                let mutable j = i + 2

                                while j < lines.Length && not (blank lines[j]) && lines[j].Contains '|' do
                                    rows.Add(cells lines[j] |> List.map inlines)
                                    j <- j + 1

                                blocks.Add(MdBlock.Table(header, List.ofSeq rows))
                                i <- j
                            | None ->
                                let text = ResizeArray<string>()
                                text.Add(line.Trim())
                                let mutable j = i + 1

                                while j < lines.Length
                                      && not (blank lines[j])
                                      && not (startsBlock lines[j])
                                      && not (
                                          lines[j].Contains '|' && j + 1 < lines.Length && delimiterRow lines[j + 1]
                                      ) do
                                    text.Add(lines[j].Trim())
                                    j <- j + 1

                                blocks.Add(MdBlock.Paragraph(inlines (String.Join(" ", text))))
                                i <- j

        List.ofSeq blocks

    /// Markdown parsed into blocks.
    let parse (markdown: string) : MdBlock list =
        blocksOf ((Text.clean markdown).Split('\n'))

    // ---- drawing --------------------------------------------------------------------------------

    /// A style over another: its colour where it has one, its attributes added.
    let private over (top: Style) (base': Style) : Style =
        {
            Fg = (if top.Fg <> Color.Default then top.Fg else base'.Fg)
            Bg = (if top.Bg <> Color.Default then top.Bg else base'.Bg)
            Bold = top.Bold || base'.Bold
            Dim = top.Dim || base'.Dim
            Italic = top.Italic || base'.Italic
            Underline = top.Underline || base'.Underline
            Reverse = top.Reverse || base'.Reverse
        }

    let rec private spans (t: Theme) (style: Style) (items: Inline list) : Span list =
        items
        |> List.collect (fun item ->
            match item with
            | Inline.Plain text -> [ Span.create style text ]
            | Inline.Strong inner -> spans t (over t.Strong style) inner
            | Inline.Emphasis inner -> spans t (Style.withItalic style) inner
            | Inline.Strike inner -> spans t (over t.Muted style) inner
            | Inline.Code text -> [ Span.create (over t.Code style) text ]
            | Inline.Link(inner, url) ->
                let label = spans t (over t.Link style) inner

                if plainText inner = url || url = "" then
                    label
                else
                    label @ [ Span.create (over t.Muted style) (" (" + url + ")") ])

    /// Spans wrapped into rows of at most `width` columns: at spaces (a word may run across spans of
    /// different styles), inside words longer than a row.
    let wrap (width: int) (items: Span list) : Row list =
        let width = max 1 width

        // Words (their pieces in their styles) and the spaces between them.
        let tokens = ResizeArray<bool * Span list>()
        let word = ResizeArray<Span>()

        let endWord () =
            if word.Count > 0 then
                tokens.Add((false, List.ofSeq word))
                word.Clear()

        for span in items do
            let text = span.Text
            let mutable start = 0

            while start < text.Length do
                let space = text[start] = ' '
                let mutable stop = start

                while stop < text.Length && (text[stop] = ' ') = space do
                    stop <- stop + 1

                let piece = Span.create span.Style (text.Substring(start, stop - start))

                if space then
                    endWord ()
                    tokens.Add((true, [ piece ]))
                else
                    word.Add piece

                start <- stop

        endWord ()

        let rows = ResizeArray<Row>()
        let current = ResizeArray<Span>()
        let used = ref 0

        let newRow () =
            // Spaces at the end of a row go.
            while current.Count > 0 && current[current.Count - 1].Text.Trim() = "" do
                current.RemoveAt(current.Count - 1)

            rows.Add(Row.normalize (List.ofSeq current))
            current.Clear()
            used.Value <- 0

        for space, pieces in tokens do
            let w = pieces |> List.sumBy (fun piece -> Text.width piece.Text)

            if space then
                if used.Value > 0 then
                    if used.Value + w <= width then
                        current.AddRange pieces
                        used.Value <- used.Value + w
                    else
                        newRow ()
            elif used.Value + w <= width then
                current.AddRange pieces
                used.Value <- used.Value + w
            else
                if used.Value > 0 && w <= width then
                    newRow ()

                if w <= width then
                    current.AddRange pieces
                    used.Value <- used.Value + w
                else
                    // Longer than a row: cut it.
                    for piece in pieces do
                        let mutable rest = piece.Text

                        while rest <> "" do
                            let fits = Text.take (width - used.Value) rest

                            let fits =
                                if fits = "" && used.Value = 0 then
                                    Text.clusters rest |> List.head
                                else
                                    fits

                            if fits = "" then
                                newRow ()
                            else
                                current.Add(Span.create piece.Style fits)
                                used.Value <- used.Value + Text.width fits
                                rest <- rest.Substring fits.Length

                                if used.Value >= width then
                                    newRow ()

        if current.Count > 0 || rows.Count = 0 then
            newRow ()

        List.ofSeq rows

    let private codeStyle (t: Theme) (kind: TokenKind) : Style =
        match kind with
        | TokenKind.Text -> t.Text
        | TokenKind.Keyword -> t.Keyword
        | TokenKind.String -> t.String
        | TokenKind.Comment -> t.Comment
        | TokenKind.Number -> t.Number

    /// A line of code cut into rows of `width` columns (no word wrapping: code keeps its spacing).
    let private codeRows (t: Theme) (width: int) (language: string) (line: string) : Row list =
        let tokens =
            Highlight.line language line
            |> List.map (fun (kind, text) -> Span.create (codeStyle t kind) text)

        let rows = ResizeArray<Row>()
        let current = ResizeArray<Span>()
        let mutable used = 0

        for span in tokens do
            let mutable rest = span.Text

            while rest <> "" do
                let fits = Text.take (width - used) rest

                if fits = "" then
                    if used = 0 then
                        // A character wider than the row.
                        let first = Text.clusters rest |> List.head
                        current.Add(Span.create span.Style first)
                        rest <- rest.Substring first.Length

                    rows.Add(List.ofSeq current)
                    current.Clear()
                    used <- 0
                else
                    current.Add(Span.create span.Style fits)
                    used <- used + Text.width fits
                    rest <- rest.Substring fits.Length

        if current.Count > 0 || rows.Count = 0 then
            rows.Add(List.ofSeq current)

        rows |> Seq.map Row.normalize |> List.ofSeq

    let private prefix (first: Span list) (others: Span list) (rows: Row list) : Row list =
        rows |> List.mapi (fun i row -> (if i = 0 then first else others) @ row)

    let private bullets = [| "•"; "◦"; "▪" |]

    let rec private block (t: Theme) (width: int) (depth: int) (item: MdBlock) : Row list =
        let width = max 1 width

        match item with
        | MdBlock.Paragraph items -> wrap width (spans t t.Text items)
        | MdBlock.Heading(level, items) ->
            let style = if level <= 2 then t.Heading else over t.Strong t.Text
            wrap width (spans t style items)
        | MdBlock.Rule -> [ [ Span.create t.Muted (String.replicate (min width 40) "─") ] ]
        | MdBlock.Code(language, code) ->
            code.Split('\n')
            |> List.ofArray
            |> List.collect (fun line -> codeRows t (max 1 (width - 2)) language line)
            |> List.map (fun row -> Span.create t.Text "  " :: row)
        | MdBlock.Quote blocks ->
            blocks
            |> blocksRows t (width - 2) depth
            |> List.map (fun row ->
                Span.create t.Muted "│ "
                :: (row
                    |> List.map (fun span ->
                        { span with
                            Style = over t.Quote span.Style
                        })))
        | MdBlock.List(ordered, start, items) ->
            let markers =
                items
                |> List.mapi (fun i _ ->
                    if ordered then
                        string (start + i) + "."
                    else
                        bullets[depth % bullets.Length])

            let markerWidth = markers |> List.map Text.width |> List.fold max 1

            List.zip markers items
            |> List.collect (fun (mark, blocks) ->
                let rows =
                    blocks
                    |> List.collect (fun inner -> block t (width - markerWidth - 1) (depth + 1) inner)

                let rows = if rows.IsEmpty then [ [] ] else rows

                prefix
                    [ Span.create t.Accent (Text.pad markerWidth mark); Span.create t.Text " " ]
                    [ Span.create t.Text (String(' ', markerWidth + 1)) ]
                    rows)
        | MdBlock.Table(header, rows) ->
            let columns = (header :: rows) |> List.map List.length |> List.fold max 0

            let cell (row: Inline list list) (i: int) =
                List.tryItem i row |> Option.defaultValue []

            let natural =
                [
                    for i in 0 .. columns - 1 do
                        (header :: rows)
                        |> List.map (fun row -> Text.width (plainText (cell row i)))
                        |> List.fold max 1
                ]
                |> Array.ofList

            let separators = 3 * max 0 (columns - 1)
            let available = max columns (width - separators)
            // Narrow the widest column until the table fits.
            let widths = Array.copy natural

            while Array.sum widths > available && Array.max widths > 3 do
                let widest = Array.findIndex (fun w -> w = Array.max widths) widths
                widths[widest] <- widths[widest] - 1

            let line (style: Style) (row: Inline list list) : Row =
                [
                    for i in 0 .. columns - 1 do
                        if i > 0 then
                            Span.create t.Muted " │ "

                        let content = Row.fit widths[i] (spans t style (cell row i))
                        yield! content

                        let pad = widths[i] - Row.width content

                        if pad > 0 && i < columns - 1 then
                            Span.create t.Text (String(' ', pad))
                ]

            let rule =
                widths |> Array.map (fun w -> String.replicate w "─") |> String.concat "─┼─"

            [
                line (Style.withBold t.Text) header
                [ Span.create t.Muted rule ]
                for row in rows do
                    line t.Text row
            ]
            |> List.map (Row.fit width >> Row.normalize)

    and private blocksRows (t: Theme) (width: int) (depth: int) (blocks: MdBlock list) : Row list =
        blocks
        |> List.mapi (fun i item ->
            let rows = block t width depth item
            // Blocks are a blank row apart; a list's items and a list after a paragraph in an item
            // are not.
            if i = 0 then rows
            elif depth > 0 then rows
            else Row.empty :: rows)
        |> List.concat

    /// Markdown drawn as rows of at most `width` columns.
    let render (t: Theme) (width: int) (markdown: string) : Row list =
        let width = max 1 width
        // Nested blocks keep at least a column of their own: what still does not fit is cut.
        parse markdown |> blocksRows t width 0 |> List.map (Row.fit width)
