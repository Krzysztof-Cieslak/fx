namespace Fx.Envd

open System
open System.Text

/// Line edits on file content (pure). Content is UTF-8, with or without a byte order mark (kept).
/// Only `\n` and `\r\n` end lines; untouched lines keep their endings, new lines get the file's (that
/// of its first line, else `\n`), and a file without a final line ending keeps lacking one.
module TextPatch =
    let private strict = UTF8Encoding(false, true)
    let private bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

    type private Line = { Text: string; Ending: string }

    /// The text of UTF-8 content without its byte order mark, and whether it had one; `None` if the
    /// content is not valid UTF-8.
    let decode (content: byte array) : (string * bool) option =
        let hasBom =
            content.Length >= 3
            && content[0] = bom[0]
            && content[1] = bom[1]
            && content[2] = bom[2]

        let skip = if hasBom then 3 else 0

        try
            Some(strict.GetString(content, skip, content.Length - skip), hasBom)
        with :? DecoderFallbackException ->
            None

    let private split (text: string) : ResizeArray<Line> =
        let lines = ResizeArray<Line>()
        let mutable start = 0

        for i in 0 .. text.Length - 1 do
            if text[i] = '\n' then
                let crlf = i > start && text[i - 1] = '\r'
                let stop = if crlf then i - 1 else i

                lines.Add
                    {
                        Text = text.Substring(start, stop - start)
                        Ending = if crlf then "\r\n" else "\n"
                    }

                start <- i + 1

        if start < text.Length then
            lines.Add
                {
                    Text = text.Substring start
                    Ending = ""
                }

        lines

    /// The number of lines of a text (a last line without an ending counts).
    let lineCount (text: string) : int = (split text).Count

    /// Applies edits to text. Edits refer to the original lines, must be in range and must not
    /// overlap (insertions at the same line apply in the order given).
    let applyText (text: string) (edits: TextEdit list) : Result<string * int, string> =
        let lines = split text

        let newline =
            if lines.Count > 0 && lines[0].Ending <> "" then
                lines[0].Ending
            else
                "\n"

        let finalNewline = lines.Count = 0 || lines[lines.Count - 1].Ending <> ""

        let sorted =
            edits
            |> List.mapi (fun i e -> i, e)
            |> List.sortWith (fun (i, a) (j, b) ->
                if a.Line <> b.Line then
                    compare a.Line b.Line
                else
                    compare i j)
            |> List.map snd

        let invalid =
            sorted
            |> List.tryPick (fun e ->
                if e.Line < 1 then
                    Some $"line {e.Line} is before the first line"
                elif e.Count < 0 then
                    Some $"a negative count ({e.Count}) at line {e.Line}"
                elif e.Line + e.Count - 1 > lines.Count || e.Line > lines.Count + 1 then
                    Some $"lines {e.Line}..{e.Line + e.Count - 1} are beyond the end ({lines.Count} lines)"
                else
                    None)

        let overlap =
            sorted
            |> List.pairwise
            |> List.tryPick (fun (a, b) ->
                if b.Line < a.Line + a.Count then
                    Some $"lines {a.Line}..{a.Line + a.Count - 1} and {b.Line}..{b.Line + b.Count - 1} overlap"
                else
                    None)

        match invalid, overlap with
        | Some reason, _
        | None, Some reason -> Error reason
        | None, None ->
            let result = ResizeArray<Line>()
            let mutable next = 1

            for edit in sorted do
                while next < edit.Line do
                    result.Add lines[next - 1]
                    next <- next + 1

                for text in edit.Lines do
                    result.Add { Text = text; Ending = newline }

                next <- next + edit.Count

            while next <= lines.Count do
                result.Add lines[next - 1]
                next <- next + 1

            let sb = StringBuilder(text.Length + 64)

            for i in 0 .. result.Count - 1 do
                let line = result[i]
                let last = i = result.Count - 1

                let ending =
                    if not last then
                        (if line.Ending = "" then newline else line.Ending)
                    elif not finalNewline then
                        ""
                    elif line.Ending = "" then
                        newline
                    else
                        line.Ending

                sb.Append(line.Text).Append(ending) |> ignore

            Ok(sb.ToString(), result.Count)

    /// Applies edits to UTF-8 content; the error names what is wrong.
    let apply (content: byte array) (edits: TextEdit list) : Result<byte array * int, Choice<unit, string>> =
        match decode content with
        | None -> Error(Choice1Of2())
        | Some(text, hasBom) ->
            match applyText text edits with
            | Error reason -> Error(Choice2Of2 reason)
            | Ok(edited, lines) ->
                let bytes = strict.GetBytes edited
                Ok((if hasBom then Array.append bom bytes else bytes), lines)
