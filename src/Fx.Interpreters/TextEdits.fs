namespace Fx.Interpreters

open System
open System.Text
open Fx.Envd

/// One change of `fs.edit` (spec §6.8): `Old` replaced with `New`, where it occurs exactly once or,
/// with `All`, wherever it occurs.
type TextChange =
    {
        Old: string
        New: string
        All: bool
    }

    override this.ToString() =
        $"change ({this.Old.Length} chars -> {this.New.Length} chars, all {this.All})"

/// Why changes do not apply to a text. `Change` is the change's index in the list (from 0).
[<RequireQualifiedAccess>]
type ChangeFailure =
    /// `Old` does not occur in the text as the changes before it left it.
    | Missing of change: int
    /// `Old` occurs `count` times, and the change is not for all of them.
    | Ambiguous of change: int * count: int
    /// `Old` and `New` are the same.
    | Same of change: int
    /// The changes leave every line as it was.
    | Unchanged

    override this.ToString() =
        match this with
        | Missing change -> $"change {change}: missing"
        | Ambiguous(change, count) -> $"change {change}: {count} occurrences"
        | Same change -> $"change {change}: the same"
        | Unchanged -> "unchanged"

/// What changes do to a text: the line edits that make it (they refer to the text's lines, in order,
/// without overlaps: one `FilePatch.Edit`), and how many occurrences were replaced.
type Changed =
    {
        Edits: TextEdit list
        Replacements: int
    }

    override this.ToString() =
        $"{this.Edits.Length} edits, {this.Replacements} replacements"

/// Edits by text (pure): changes applied in order, each to the text the ones before it left, turned
/// into the line edits of one patch. Line endings are `\n` here (callers replace `\r\n`); the patch
/// gives new lines the file's own endings (`TextPatch`).
[<RequireQualifiedAccess>]
module TextEdits =
    /// Where `part` occurs in `text`, left to right, without overlaps; nowhere if it is empty.
    let positions (text: string) (part: string) : int list =
        if part = "" then
            []
        else
            let found = ResizeArray<int>()
            let mutable at = text.IndexOf(part, StringComparison.Ordinal)

            while at >= 0 do
                found.Add at
                at <- text.IndexOf(part, at + part.Length, StringComparison.Ordinal)

            List.ofSeq found

    /// How many times `part` occurs in `text`, without overlaps.
    let occurrences (text: string) (part: string) : int = (positions text part).Length

    /// `text` with the first occurrence of `part` replaced (`part` must occur).
    let replaceFirst (text: string) (part: string) (replacement: string) : string =
        let at = text.IndexOf(part, StringComparison.Ordinal)
        text.Substring(0, at) + replacement + text.Substring(at + part.Length)

    /// The line edit that turns `before` into `after`: the lines between their common head and tail.
    let lineEdit (before: string list) (after: string list) : TextEdit =
        let a = Array.ofList before
        let b = Array.ofList after
        let mutable head = 0

        while head < a.Length && head < b.Length && a[head] = b[head] do
            head <- head + 1

        let mutable tail = 0

        while tail < a.Length - head
              && tail < b.Length - head
              && a[a.Length - 1 - tail] = b[b.Length - 1 - tail] do
            tail <- tail + 1

        {
            Line = head + 1
            Count = a.Length - head - tail
            Lines = b[head .. b.Length - tail - 1] |> List.ofArray
        }

    /// The lines of a text without their endings (`\n` or `\r\n`); a final line ending ends the last
    /// line, and an empty text has none.
    let lines (text: string) : string list =
        if text = "" then
            []
        else
            let parts = text.Replace("\r\n", "\n").Split '\n' |> List.ofArray

            if text.EndsWith '\n' then
                parts |> List.truncate (parts.Length - 1)
            else
                parts

    /// Lines `first` to `last` (1-based) of a text as they are in it, with their endings; empty when
    /// `last` is before `first` or the lines are past the end.
    let slice (text: string) (first: int) (last: int) : string =
        if last < first || first < 1 then
            ""
        else
            let mutable line = 1
            let mutable start = if first = 1 then 0 else -1
            let mutable stop = -1
            let mutable i = 0

            while stop < 0 && i < text.Length do
                if text[i] = '\n' then
                    if line = last then
                        stop <- i + 1

                    line <- line + 1

                    if line = first then
                        start <- i + 1

                i <- i + 1

            if start < 0 then
                ""
            else
                text.Substring(start, (if stop < 0 then text.Length else stop) - start)

    /// A part of the changed text that replacements wrote: `[Start, End)` in the text as it is now,
    /// standing for `Original` characters of the text the changes started from.
    type private Span =
        {
            Start: int
            End: int
            Original: int
        }

        member this.Growth = (this.End - this.Start) - this.Original

    /// Replaces `oldLength` characters at each of `at` (ascending, without overlaps) and moves the
    /// spans: those a replacement overlaps or touches become one with it.
    let private replace
        (text: string)
        (spans: Span list)
        (at: int list)
        (oldLength: int)
        (replacement: string)
        : string * Span list =
        let delta = replacement.Length - oldLength
        let sb = StringBuilder(text.Length + at.Length * max 0 delta)
        let mutable from = 0

        for p in at do
            sb.Append(text, from, p - from).Append(replacement) |> ignore
            from <- p + oldLength

        sb.Append(text, from, text.Length - from) |> ignore

        // Spans and the new writes in the old text's positions; the writes count, so new positions follow.
        let items =
            (spans |> List.map (fun s -> s.Start, s.End, s.Growth, 0))
            @ (at |> List.map (fun p -> p, p + oldLength, 0, 1))
            |> List.sortBy (fun (start, _, _, _) -> start)

        let groups = ResizeArray<int * int * int * int>()

        for start, stop, growth, writes in items do
            let last = groups.Count - 1

            match (if last >= 0 then Some(groups[last]) else None) with
            | Some(s, e, g, w) when start <= e -> groups[last] <- (s, max e stop, g + growth, w + writes)
            | _ -> groups.Add((start, stop, growth, writes))

        let moved = ResizeArray<Span>()
        let mutable before = 0

        for start, stop, growth, writes in groups do
            let first = start + before * delta

            moved.Add
                {
                    Start = first
                    End = first + (stop - start) + writes * delta
                    Original = (stop - start) - growth
                }

            before <- before + writes

        sb.ToString(), List.ofSeq moved

    let private atLineStart (text: string) (at: int) = at = 0 || text[at - 1] = '\n'

    let private lineStart (text: string) (at: int) =
        if at = 0 then 0 else text.LastIndexOf('\n', at - 1) + 1

    let private lineEnd (text: string) (at: int) =
        match text.IndexOf('\n', at) with
        | -1 -> text.Length
        | i -> i + 1

    let private newlines (text: string) (start: int) (length: int) =
        let mutable count = 0

        for i in start .. start + length - 1 do
            if text[i] = '\n' then
                count <- count + 1

        count

    /// The line edits between the text the changes started from and the changed one: the whole lines
    /// around each written span (spans on shared lines together), trimmed to the lines that differ.
    let private edits (original: string) (changed: string) (spans: Span list) : TextEdit list =
        let spans = Array.ofList spans
        // Growth of the spans before each one: positions before span i, outside spans, move by it.
        let growth = Array.zeroCreate (spans.Length + 1)

        for i in 0 .. spans.Length - 1 do
            growth[i + 1] <- growth[i] + spans[i].Growth

        // Regions: [first span, last span, start, end) in the changed text, whole lines in both texts.
        let regions = ResizeArray<int * int * int * int>()

        for i in 0 .. spans.Length - 1 do
            let span = spans[i]
            let start = lineStart changed span.Start
            let originalEnd = span.Start - growth[i] + span.Original

            let stop =
                if atLineStart changed span.End && atLineStart original originalEnd then
                    span.End
                else
                    lineEnd changed span.End

            let last = regions.Count - 1

            match (if last >= 0 then Some(regions[last]) else None) with
            | Some(first, _, s, e) when start < e -> regions[last] <- (first, i, s, max e stop)
            | _ -> regions.Add((i, i, start, stop))

        // A file without a final line ending keeps lacking one (`TextPatch` ends its last line with
        // nothing): there, a line ending the changes wrote at the end is followed by an empty line.
        let unended = original <> "" && not (original.EndsWith '\n')

        let linesOf (text: string) (atEnd: bool) =
            if atEnd && unended && changed <> "" then
                text.Split '\n' |> List.ofArray
            else
                lines text

        [
            for index, (first, last, start, stop) in Seq.indexed regions do
                let originalStart = start - growth[first]
                let originalStop = stop - growth[last + 1]
                let before = lines (original.Substring(originalStart, originalStop - originalStart))
                let atEnd = index = regions.Count - 1 && stop = changed.Length
                let after = linesOf (changed.Substring(start, stop - start)) atEnd
                let edit = lineEdit before after

                if edit.Count > 0 || not edit.Lines.IsEmpty then
                    { edit with
                        Line = newlines original 0 originalStart + edit.Line
                    }
        ]

    /// Applies changes in order, each to the text the ones before it left: a change's `Old` must occur
    /// exactly once (everywhere it occurs with `All`) and differ from its `New`. The result is the line
    /// edits of the text as given (`\n` endings), and how many occurrences were replaced.
    let apply (text: string) (changes: TextChange list) : Result<Changed, ChangeFailure> =
        let mutable current = text
        let mutable spans: Span list = []
        let mutable replaced = 0
        let mutable failure = None

        for index, change in List.indexed changes do
            if failure.IsNone then
                let found = positions current change.Old

                if found.IsEmpty then
                    failure <- Some(ChangeFailure.Missing index)
                elif found.Length > 1 && not change.All then
                    failure <- Some(ChangeFailure.Ambiguous(index, found.Length))
                elif change.Old = change.New then
                    failure <- Some(ChangeFailure.Same index)
                else
                    let next, moved = replace current spans found change.Old.Length change.New
                    current <- next
                    spans <- moved
                    replaced <- replaced + found.Length

        match failure with
        | Some failure -> Error failure
        | None ->
            match edits text current spans with
            | [] -> Error ChangeFailure.Unchanged
            | edits ->
                Ok
                    {
                        Edits = edits
                        Replacements = replaced
                    }

    /// Where the lines of each edit are once the edits are applied (1-based, inclusive; `last` is
    /// `first - 1` for an edit that only removed lines), in the edits' order.
    let placed (edits: TextEdit list) : (int * int) list =
        edits
        |> List.sortBy (fun e -> e.Line)
        |> List.mapFold
            (fun shift (edit: TextEdit) ->
                let first = edit.Line + shift
                (first, first + edit.Lines.Length - 1), shift + edit.Lines.Length - edit.Count)
            0
        |> fst
