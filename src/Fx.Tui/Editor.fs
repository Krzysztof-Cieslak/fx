namespace Fx.Tui

open System
open System.Globalization

/// The prompt being typed: its text (lines separated by `\n`) and the cursor, an index into the
/// text that is always at the start of a grapheme cluster.
type Editor =
    {
        Text: string
        Cursor: int
    }

    override this.ToString() =
        $"editor ({this.Text.Length} chars, cursor {this.Cursor})"

/// Editing the prompt (pure): the usual line-editing keys, over grapheme clusters.
[<RequireQualifiedAccess>]
module Editor =
    let empty: Editor = { Text = ""; Cursor = 0 }

    let ofText (text: string) : Editor = { Text = text; Cursor = text.Length }

    let isEmpty (editor: Editor) = editor.Text = ""

    /// The cluster boundaries of a text, from 0 to its length.
    let private boundaries (text: string) : int list =
        let result = ResizeArray<int>([ 0 ])
        let mutable i = 0

        while i < text.Length do
            i <- i + max 1 (StringInfo.GetNextTextElementLength(text, i))
            result.Add i

        List.ofSeq result

    let private previous (text: string) (index: int) : int =
        boundaries text
        |> List.filter (fun b -> b < index)
        |> List.tryLast
        |> Option.defaultValue 0

    let private next (text: string) (index: int) : int =
        boundaries text
        |> List.tryFind (fun b -> b > index)
        |> Option.defaultValue text.Length

    /// The cluster boundary at or before an index.
    let private snap (text: string) (index: int) : int =
        boundaries text
        |> List.filter (fun b -> b <= index)
        |> List.tryLast
        |> Option.defaultValue 0

    let private lineStart (text: string) (index: int) : int =
        if index = 0 then
            0
        else
            text.LastIndexOf('\n', index - 1) + 1

    let private lineEnd (text: string) (index: int) : int =
        match text.IndexOf('\n', index) with
        | -1 -> text.Length
        | i -> i

    /// Inserts text at the cursor (pasted text with its line ends normalized, control characters
    /// dropped).
    let insert (text: string) (editor: Editor) : Editor =
        let text = Text.clean text

        {
            Text = editor.Text.Insert(editor.Cursor, text)
            Cursor = editor.Cursor + text.Length
        }

    let newline (editor: Editor) : Editor = insert "\n" editor

    let backspace (editor: Editor) : Editor =
        if editor.Cursor = 0 then
            editor
        else
            let from = previous editor.Text editor.Cursor

            {
                Text = editor.Text.Remove(from, editor.Cursor - from)
                Cursor = from
            }

    let delete (editor: Editor) : Editor =
        if editor.Cursor >= editor.Text.Length then
            editor
        else
            let until = next editor.Text editor.Cursor

            { editor with
                Text = editor.Text.Remove(editor.Cursor, until - editor.Cursor)
            }

    let left (editor: Editor) : Editor =
        { editor with
            Cursor = previous editor.Text editor.Cursor
        }

    let right (editor: Editor) : Editor =
        { editor with
            Cursor = next editor.Text editor.Cursor
        }

    let home (editor: Editor) : Editor =
        { editor with
            Cursor = lineStart editor.Text editor.Cursor
        }

    let ``end`` (editor: Editor) : Editor =
        { editor with
            Cursor = lineEnd editor.Text editor.Cursor
        }

    let private isWordChar (c: char) = Char.IsLetterOrDigit c || c = '_'

    let wordLeft (editor: Editor) : Editor =
        let text = editor.Text
        let mutable i = editor.Cursor

        while i > 0 && not (isWordChar text[i - 1]) do
            i <- i - 1

        while i > 0 && isWordChar text[i - 1] do
            i <- i - 1

        { editor with Cursor = snap text i }

    let wordRight (editor: Editor) : Editor =
        let text = editor.Text
        let mutable i = editor.Cursor

        while i < text.Length && not (isWordChar text[i]) do
            i <- i + 1

        while i < text.Length && isWordChar text[i] do
            i <- i + 1

        { editor with Cursor = snap text i }

    /// Deletes the word before the cursor (Ctrl+W).
    let deleteWord (editor: Editor) : Editor =
        let target = wordLeft editor

        {
            Text = editor.Text.Remove(target.Cursor, editor.Cursor - target.Cursor)
            Cursor = target.Cursor
        }

    /// Deletes to the end of the line (Ctrl+K), or the line end itself at the end of a line.
    let killToEnd (editor: Editor) : Editor =
        let stop = lineEnd editor.Text editor.Cursor

        let stop =
            if stop = editor.Cursor && stop < editor.Text.Length then
                stop + 1
            else
                stop

        { editor with
            Text = editor.Text.Remove(editor.Cursor, stop - editor.Cursor)
        }

    /// Deletes to the start of the line (Ctrl+U).
    let killToStart (editor: Editor) : Editor =
        let start = lineStart editor.Text editor.Cursor

        {
            Text = editor.Text.Remove(start, editor.Cursor - start)
            Cursor = start
        }

    /// The line the cursor is on (from 0) and how many lines there are.
    let line (editor: Editor) : int * int =
        let before = editor.Text.Substring(0, editor.Cursor)

        before |> Seq.filter (fun c -> c = '\n') |> Seq.length,
        (editor.Text |> Seq.filter (fun c -> c = '\n') |> Seq.length) + 1

    let private moveLine (delta: int) (editor: Editor) : Editor =
        let text = editor.Text
        let start = lineStart text editor.Cursor
        let column = Text.width (text.Substring(start, editor.Cursor - start))

        let target =
            if delta < 0 then
                if start = 0 then None else Some(lineStart text (start - 1))
            else
                let stop = lineEnd text editor.Cursor
                if stop >= text.Length then None else Some(stop + 1)

        match target with
        | None -> editor
        | Some targetStart ->
            let targetEnd = lineEnd text targetStart
            let line = text.Substring(targetStart, targetEnd - targetStart)
            let prefix = Text.take column line

            { editor with
                Cursor = targetStart + prefix.Length
            }

    let up (editor: Editor) : Editor = moveLine -1 editor
    let down (editor: Editor) : Editor = moveLine 1 editor

    /// The text as rows of at most `columns`, wrapped at the column (not at words, so the cursor
    /// stays where it is typed), and where the cursor is among them.
    let layout (columns: int) (editor: Editor) : string list * (int * int) =
        let columns = max 1 columns
        let rows = ResizeArray<string>()
        let mutable cursor = (0, 0)
        let mutable position = 0

        for logical in editor.Text.Split('\n') do
            let current = Text.clusters logical
            let row = System.Text.StringBuilder()
            let mutable used = 0
            let mutable index = position

            if editor.Cursor = index then
                cursor <- (rows.Count, 0)

            for cluster in current do
                let w = Text.clusterWidth cluster

                if used + w > columns then
                    rows.Add(row.ToString())
                    row.Clear() |> ignore
                    used <- 0

                if editor.Cursor = index then
                    cursor <- (rows.Count, used)

                row.Append cluster |> ignore
                used <- used + w
                index <- index + cluster.Length

            if editor.Cursor = index then
                // At the end of a full row the cursor goes to the start of the next one.
                if used >= columns then
                    rows.Add(row.ToString())
                    row.Clear() |> ignore
                    used <- 0

                cursor <- (rows.Count, used)

            rows.Add(row.ToString())
            position <- position + logical.Length + 1

        List.ofSeq rows, cursor
