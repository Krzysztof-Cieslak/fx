namespace Fx.CodeAct

open System
open System.Security.Cryptography
open System.Text
open Fx.Contracts
open Fx.Models
open Fx.Kernel
open Fx.Interpreters
open Fx.Prompt

/// The files a cell read through the bridge, in the order first read: its receipt's provenance.
type Reads =
    {
        Files: ContentSource list
    }

    override this.ToString() = $"reads ({this.Files.Length} files)"

[<RequireQualifiedAccess>]
module Reads =
    let none: Reads = { Files = [] }

    let add (path: string) (reads: Reads) : Reads =
        let source = ContentSource.file (Paths.normalize path)

        if reads.Files |> List.contains source then
            reads
        else
            { Files = reads.Files @ [ source ] }

/// What one host call of a cell returned, as the cell's receipt uses it: the binding, and the digest of
/// the result's canonical JSON. A value the cell ends with whose JSON has that digest is that result,
/// and its receipt shows it the way the binding's direct tool would (T203a).
type Returned =
    {
        Binding: string
        Digest: string
    }

    override this.ToString() =
        $"{this.Binding} returned {this.Digest}"

[<RequireQualifiedAccess>]
module Returned =
    /// The digest of a value's canonical JSON.
    let digest (json: Json) : string =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json.serializeCanonical json)))

    let ofResult (binding: string) (json: Json) : Returned =
        {
            Binding = binding
            Digest = digest json
        }

/// How a cell's receipt shows the value the cell ended with (spec §6.6, T203a).
[<RequireQualifiedAccess>]
type CellValue =
    /// The kernel's preview (`values.previewLimits`).
    | Preview of ValueInfo
    /// A string: the text itself, bounded as output is.
    | Text of string
    /// What `shell.run` returned: the command, how it ended, its output.
    | Ran of RunResult
    /// What `fs.read` returned: the file's lines (a file piece: they count as seen).
    | File of FileText
    /// What `fs.write` or `fs.patch` returned: what was written.
    | Written of Written list
    /// What `fs.edit` or `fs.editRange` returned: the lines the edit left (a file piece).
    | Edited of Edited

    override this.ToString() =
        match this with
        | Preview info -> $"preview ({info.Type})"
        | Text text -> $"text ({text.Length} chars)"
        | Ran result -> $"ran {result.Command}"
        | File file -> $"file {file.Path}"
        | Written files -> $"written ({files.Length} files)"
        | Edited edited -> $"edited {edited.Path}"

[<RequireQualifiedAccess>]
module CellValue =
    /// How to show a cell's value: its JSON (when the kernel encoded it) and what the cell's host calls
    /// returned decide; anything else is the kernel's preview.
    let ofValue (info: ValueInfo) (json: Json option) (returned: Returned list) : CellValue =
        match json with
        | Some(Json.String text) -> CellValue.Text text
        | Some json ->
            let digest = Returned.digest json

            let decoded (codec: Codec<'T>) (shown: 'T -> CellValue) =
                match codec.Decode json with
                | Ok value -> shown value
                | Error _ -> CellValue.Preview info

            match returned |> List.tryFind (fun r -> r.Digest = digest) with
            | Some r when r.Binding = "shell.run" -> decoded CoreBindings.Codecs.runResult CellValue.Ran
            | Some r when r.Binding = "fs.read" -> decoded CoreBindings.Codecs.fileText CellValue.File
            | Some r when r.Binding = "fs.write" ->
                decoded CoreBindings.Codecs.written (fun w -> CellValue.Written [ w ])
            | Some r when r.Binding = "fs.patch" -> decoded (Codec.list CoreBindings.Codecs.written) CellValue.Written
            | Some r when r.Binding = "fs.edit" || r.Binding = "fs.editRange" ->
                decoded CoreBindings.Codecs.edited CellValue.Edited
            | _ -> CellValue.Preview info
        | None -> CellValue.Preview info

/// How kernels ended, in words for the model and for people (T135).
[<RequireQualifiedAccess>]
module KernelEnds =
    /// Signals whose numbers are the same on Linux and macOS.
    let private signals =
        [
            1, "SIGHUP"
            2, "SIGINT"
            3, "SIGQUIT"
            6, "SIGABRT"
            9, "SIGKILL"
            11, "SIGSEGV"
            13, "SIGPIPE"
            14, "SIGALRM"
            15, "SIGTERM"
        ]

    /// The most characters of a kernel's standard error a description quotes (its end).
    [<Literal>]
    let StderrChars = 300

    /// The end of a kernel's standard error worth quoting: at most `StderrChars`, trimmed.
    let stderrTail (stderr: string) : string =
        let text = stderr.Trim()

        if text.Length <= StderrChars then
            text
        else
            let start = text.Length - StderrChars + 1

            let start = if Char.IsLowSurrogate text[start] then start + 1 else start

            "…" + text.Substring start

    /// What happened to a kernel, after "the kernel": `was killed by signal 9 (SIGKILL)`. Exit codes
    /// above 128 are the signals that ended a process, except on Windows.
    let describe (os: Fx.Sdk.Os) (how: KernelEnd) : string =
        let quoting (stderr: string) =
            match stderrTail stderr with
            | "" -> ""
            | tail -> $"; its standard error ended with: {tail}"

        match how with
        | KernelEnd.Exited(Some code, stderr) when os <> Fx.Sdk.Os.Windows && code > 128 && code < 160 ->
            let signal = code - 128

            let name =
                signals
                |> List.tryFind (fun (n, _) -> n = signal)
                |> Option.map (fun (_, name) -> $" ({name})")
                |> Option.defaultValue ""

            $"was killed by signal {signal}{name}" + quoting stderr
        | KernelEnd.Exited(Some code, stderr) -> $"exited with code {code}" + quoting stderr
        | KernelEnd.Exited(None, stderr) -> "exited" + quoting stderr
        | KernelEnd.NotStarted reason -> $"did not start ({reason})"
        | KernelEnd.Killed -> "was killed by the host: an interrupted cell did not stop"
        | KernelEnd.HostStopped -> "stopped when the host restarted"

/// What happened to a kernel around a cell, for its receipt.
[<RequireQualifiedAccess>]
type KernelNote =
    /// Nothing worth saying.
    | Same
    /// A new kernel (this launch) started for this cell: what earlier cells declared is gone, because
    /// the kernel before it ended (`why`, after "the previous kernel"); the values of these cells were
    /// restored from the journal (`Out[cell]`).
    | Restarted of launch: int * why: string * restored: string list

    override this.ToString() =
        match this with
        | Same -> "same kernel"
        | Restarted(launch, why, restored) -> $"restarted (launch {launch}): {why} ({restored.Length} values restored)"

/// The bounds of a cell's receipt.
type CellLimits =
    {
        /// The whole receipt (`codeact.receipt.maxChars`).
        ReceiptChars: int
        /// A value shown as text (`codeact.output.maxChars`, as printed output).
        OutputChars: int
        /// A file's lines shown (`codeact.read.maxLines`).
        ReadLines: int
    }

    override this.ToString() =
        $"cell limits ({this.ReceiptChars} chars, values {this.OutputChars}, {this.ReadLines} lines)"

/// Receipts (spec §5: stable, bounded when made, never re-rendered): what the model sees of an action's
/// result. Pure.
[<RequireQualifiedAccess>]
module Receipts =
    let private lines (items: string list) =
        items |> List.filter (fun s -> s <> "") |> String.concat "\n"

    /// Text bounded to `maxChars` (head and tail), carrying `reads` as provenance when there are any.
    let private quoted (maxChars: int) (reads: Reads) (text: string) : Piece =
        match Piece.output maxChars text with
        | Piece.Text bounded when not reads.Files.IsEmpty ->
            Piece.Quote
                {
                    Text = bounded
                    Sources = reads.Files
                }
        | piece -> piece

    /// The note a receipt starts with when its cell ran in a new kernel (always its first piece).
    let private restarted (note: KernelNote) : Piece list =
        let g = Fx.Product.kernelGlobal

        match note with
        | KernelNote.Same -> []
        | KernelNote.Restarted(_, why, []) ->
            [
                Piece.Text
                    $"[A new kernel started for this cell (the previous kernel {why}): names and values from earlier cells are gone; `{g}.recall(id)` gives back a value an earlier cell returned, if it was kept.]"
            ]
        | KernelNote.Restarted(_, why, restored) ->
            let outs =
                restored
                |> List.map (fun cell -> "Out[" + Json.quote cell + "]")
                |> String.concat ", "

            [
                Piece.Text
                    $"[A new kernel started for this cell (the previous kernel {why}): names from earlier cells are gone. The values earlier cells returned are back as {outs}; `{g}.recall(id)` gives back others that were kept.]"
            ]

    /// The new kernel a receipt reports, for people (`Receipt.Restarted`).
    let restart (note: KernelNote) : KernelRestart option =
        match note with
        | KernelNote.Same -> None
        | KernelNote.Restarted(launch, why, _) ->
            Some
                {
                    Launch = launch
                    Reason = "the previous kernel " + why
                }

    let private outcomeText (cellId: string) (outcome: CellOutcome) : string =
        match outcome with
        | CellOutcome.Completed(Some value) -> $"Value of Out[{Json.quote cellId}] ({value.Type}): {value.Preview.Text}"
        | CellOutcome.Completed None -> ""
        | CellOutcome.SyntaxError failure ->
            let at =
                match failure.Line, failure.Column with
                | Some line, Some column -> $" (line {line}, column {column})"
                | Some line, None -> $" (line {line})"
                | _ -> ""

            let text =
                match failure.LineText with
                | Some text -> "\n" + text
                | None -> ""

            $"SyntaxError{at}: {failure.Message}{text}\nNothing ran."
        | CellOutcome.TypeError diagnostics ->
            let shown =
                diagnostics
                |> List.truncate 20
                |> List.map (fun d -> $"{d.Line}:{d.Column} {d.Code} {d.Message}")

            lines ("Type errors; the cell did not run:" :: shown)
        | CellOutcome.RuntimeError failure ->
            let code =
                match failure.Code with
                | Some code -> $" [code {code}]"
                | None -> ""

            let stack =
                if String.IsNullOrWhiteSpace failure.Stack then
                    ""
                else
                    "\n" + failure.Stack

            $"{failure.Name}{code}: {failure.Message}{stack}"
        | CellOutcome.TimedOut after -> $"The cell timed out after {after} ms and was stopped."
        | CellOutcome.Cancelled -> "The cell was cancelled."

    /// Whether a cell's outcome is an error for the model.
    let isError (outcome: CellOutcome) : bool =
        match outcome with
        | CellOutcome.Completed _ -> false
        | _ -> true

    /// A cell the kernel did not answer; `ended`: how the kernel ended (after "the kernel"), if it did.
    let kernelFailed (note: KernelNote) (failure: KernelFailure) (ended: string option) : Piece list =
        let text =
            match ended with
            | Some how ->
                $"The cell did not complete: the kernel {how}. The next cell starts a new kernel: names and values from earlier cells will be gone."
            | None -> $"The cell did not complete: {failure}."

        restarted note @ [ Piece.Text text ]

    /// A cell whose texts could not all be bound, so its code did not run (`CellTexts.bind`'s reason).
    let unbound (note: KernelNote) (reason: string) : Piece list = restarted note @ [ Piece.Text reason ]

    /// An action that could not run (invalid arguments, an unknown tool, a refused call).
    let failed (message: string) : Piece list = [ Piece.Text message ]

    /// A call the turn did not run because it had already ended.
    let skipped: Piece list =
        [ Piece.Text "Not run: the turn's result was accepted before this call." ]

    /// Lines numbered as `read` shows them (`cat -n`).
    let numbered (first: int) (lines: string list) : string =
        let sb = StringBuilder()

        lines
        |> List.iteri (fun i line ->
            if i > 0 then
                sb.Append('\n') |> ignore

            sb.Append((first + i).ToString().PadLeft(6)).Append('\t').Append(line) |> ignore)

        sb.ToString()

    /// The lines of a text without their endings.
    let splitLines (text: string) : string list =
        if text = "" then
            []
        else
            let parts = text.Replace("\r\n", "\n").Split '\n' |> List.ofArray

            if text.EndsWith '\n' then
                parts |> List.truncate (parts.Length - 1)
            else
                parts

    /// Lines of a file read from line `first` (in the file), at most `count`, as a file piece (they
    /// count as seen; exclusions withhold them), and a note when they are not the whole file, where
    /// `more` says how to read on from a line.
    let private fileLines (file: FileText) (first: int) (count: int) (more: int -> string) : Piece list =
        let held = splitLines file.Text

        let shown =
            held
            |> List.skip (min held.Length (max 0 (first - file.Start)))
            |> List.truncate count

        let last = first + shown.Length - 1
        let whole = first = 1 && shown.Length = file.Lines

        let view: FileView =
            {
                Path = Paths.normalize file.Path
                Digest = string file.Digest
                Lines =
                    if whole || shown.IsEmpty then
                        []
                    else
                        [ LineRange.create first last ]
                Text = numbered first shown
            }

        [
            if shown.IsEmpty && file.Lines > 0 then
                Piece.Text $"{file.Path} has {file.Lines} lines; nothing at line {first}."
            elif file.Lines = 0 then
                Piece.Text $"{file.Path} is empty."
            else
                Piece.File view

                if not whole then
                    Piece.Text $"[Lines {first}–{last} of {file.Lines}.{more (last + 1)}]"
        ]

    /// `read`'s receipt: the lines shown, as a file piece (they count as seen; exclusions withhold
    /// them), and how to see more.
    let read (file: FileText) (offset: int option) (limit: int option) (maxLines: int) : Piece list =
        fileLines file (defaultArg offset file.Start) (min maxLines (defaultArg limit maxLines)) (fun next ->
            $" Read on with offset {next}.")

    /// A cell's `FileText` value: its lines from where it starts (a ranged read's), up to `maxLines`.
    let private fileValue (file: FileText) (maxLines: int) : Piece list =
        fileLines file file.Start maxLines (fun next ->
            if next <= file.End then
                $" Read on with `{Fx.Product.kernelGlobal}.fs.read({Json.quote file.Path}, {{ lines: [{next}, {file.End}] }})`."
            else
                "")

    /// `edit`'s receipt, and a cell's `Edited` value's: what changed, then the lines it left as file
    /// pieces of the new version (they count as seen). `around`: the lines around them, read after the
    /// edit (`edit`'s context), shown if the file is still as the edit left it; otherwise each fresh
    /// range, up to `maxLines` lines in all.
    let edited (maxLines: int) (edited: Edited) (around: FileText option) : Piece list =
        let times =
            if edited.Replacements = 1 then
                "1 replacement"
            else
                $"{edited.Replacements} replacements"

        let view (first: int) (lines: string list) : Piece =
            Piece.File
                {
                    Path = Paths.normalize edited.Path
                    Digest = string edited.Digest
                    Lines = [ LineRange.create first (first + lines.Length - 1) ]
                    Text = numbered first lines
                }

        let shown =
            match around with
            | Some file when file.Digest = edited.Digest && file.End >= file.Start ->
                [ view file.Start (splitLines file.Text) ]
            | _ ->
                let pieces = ResizeArray<Piece>()
                let mutable left = maxLines
                let mutable hidden = 0

                for range in edited.Ranges do
                    let lines = splitLines range.Text

                    if lines.IsEmpty then
                        pieces.Add(Piece.Text $"[Lines removed before line {range.Start}.]")
                    elif left > 0 then
                        let fits = lines |> List.truncate left
                        pieces.Add(view range.Start fits)
                        left <- left - fits.Length
                        hidden <- hidden + lines.Length - fits.Length
                    else
                        hidden <- hidden + lines.Length

                if hidden = 1 then
                    pieces.Add(Piece.Text "[1 more changed line is not shown.]")
                elif hidden > 1 then
                    pieces.Add(Piece.Text $"[{hidden} more changed lines are not shown.]")

                List.ofSeq pieces

        Piece.Text $"Edited {edited.Path} ({times})." :: shown

    /// A command's line and how it ended, then its output: what `bash` and a cell's `RunResult` show.
    let private ranParts (result: RunResult) : string * string =
        let ending =
            match result.ExitCode with
            | _ when result.TimedOut -> "timed out (killed)"
            | Some code -> $"exit code {code}"
            | None -> "no exit code"

        let streams =
            lines
                [
                    (if result.Stdout = "" then "" else result.Stdout.TrimEnd())
                    (if result.Stderr = "" then
                         ""
                     else
                         "[stderr]\n" + result.Stderr.TrimEnd())
                ]

        $"$ {result.Command}\n{ending}", streams

    let private matchCount (result: RunResult) =
        if result.Matches.Length = 1 then
            "1 match"
        else
            $"{result.Matches.Length} matches"

    /// The line a search's receipt ends with (T142): how to edit what it found. `bash`'s, whose
    /// result the model cannot reach from a cell.
    let private bashMatches (result: RunResult) : string =
        let g = Fx.Product.kernelGlobal

        $"[{matchCount result}, each checked against its file: `edit` one with its line as `old`; in a cell, `{g}.shell.run` gives them as `matches`, ranges `{g}.fs.editRange` replaces.]"

    /// A cell's, whose value is `Out[cell]`.
    let private cellMatches (label: string) (result: RunResult) : string =
        let g = Fx.Product.kernelGlobal

        $"[{matchCount result}, each a line checked against its file: `{g}.fs.editRange({label}.matches[0], text)` replaces the first one's line.]"

    /// `bash`'s receipt: the command, how it ended and its output (bounded already); a search's
    /// matches add how to edit them.
    let ran (maxChars: int) (result: RunResult) : Piece list =
        let header, streams = ranParts result

        [
            Piece.Text header
            if streams <> "" then
                Piece.output maxChars streams
            if not result.Matches.IsEmpty then
                Piece.Text(bashMatches result)
        ]

    /// Text cut to `maxChars` (head and tail), as output is.
    let private bounded (maxChars: int) (text: string) : string =
        match Piece.output maxChars text with
        | Piece.Text cut -> cut
        | _ -> text

    /// What a cell's receipt says about the value it ended with (a file's lines are a piece of their own).
    let private valueText (outputChars: int) (cellId: string) (value: CellValue) : string =
        let label = $"Out[{Json.quote cellId}]"

        match value with
        | CellValue.Preview info -> $"Value of {label} ({info.Type}): {info.Preview.Text}"
        | CellValue.Text "" -> $"Value of {label} (string): \"\""
        | CellValue.Text text -> $"Value of {label} (string, {text.Length} chars):\n" + bounded outputChars text
        | CellValue.Ran result ->
            let header, streams = ranParts result

            lines
                [
                    $"Value of {label} (RunResult):"
                    header
                    bounded outputChars streams
                    (if result.Matches.IsEmpty then
                         ""
                     else
                         cellMatches label result)
                ]
        | CellValue.File file -> $"Value of {label} (FileText): {file.Path}, {file.Lines} lines, {file.Digest}"
        | CellValue.Written files ->
            let written =
                files
                |> List.map (fun w ->
                    match w.Digest with
                    | Some _ -> $"{w.Path} ({w.Lines} lines)"
                    | None -> $"{w.Path} (deleted)")
                |> String.concat ", "

            $"Value of {label} (Written): {written}"
        | CellValue.Edited edited -> $"Value of {label} (Edited): {edited.Path}, {edited.Lines} lines, {edited.Digest}"

    /// The most characters of a host call's first argument a receipt names it by.
    [<Literal>]
    let CallChars = 120

    /// A host call in a line: its binding and its first argument (a command, a path), cut to
    /// `CallChars`, e.g. `shell.run: strace -f ls`.
    let callTitle (call: HostCall) : string =
        let argument =
            match call.Args with
            | Value.Inline(Json.String text) :: _ -> Some text
            | Value.Inline json :: _ -> Some(Json.serialize json)
            | _ -> None

        match argument with
        | None -> call.Binding
        | Some text ->
            let text = text.Replace("\r", "").Replace('\n', ' ').Trim()

            let cut =
                if text.Length <= CallChars then
                    text
                else
                    let n =
                        if Char.IsHighSurrogate text[CallChars - 1] then
                            CallChars - 1
                        else
                            CallChars

                    text.Substring(0, n) + "…"

            $"{call.Binding}: {cut}"

    /// The host calls a stopped cell (timed out, cancelled) stopped while the guard had them wait for a
    /// person's approval (their requests are withdrawn): one line each.
    let private stoppedWaiting (outcome: CellOutcome) (waiting: HostCall list) : string =
        match outcome with
        | CellOutcome.TimedOut _
        | CellOutcome.Cancelled ->
            waiting
            |> List.map (fun call -> $"{callTitle call} (stopped while it waited for its approval)")
            |> lines
        | _ -> ""

    /// A cell's receipt: what it printed, the value it ended with or its error, the host calls it
    /// stopped while they waited for approval, the names it declared and those it never assigned,
    /// whether its `finish` was accepted, and a note if the kernel started afresh. The value shows as
    /// `value` says (`CellValue.ofValue`): a string as its text, a command's result as `bash` shows it,
    /// a file or lines it read as numbered lines (a file piece: they count as seen), an edit as `edit`
    /// shows it; anything else as the kernel's preview. The rest may quote what the cell read through the bridge, so it carries those
    /// files as provenance.
    let cell
        (limits: CellLimits)
        (note: KernelNote)
        (intent: string option)
        (reads: Reads)
        (waiting: HostCall list)
        (value: CellValue option)
        (result: CellResult)
        : Piece list =
        let shown =
            match result.Outcome with
            | CellOutcome.Completed(Some info) -> Some(value |> Option.defaultValue (CellValue.Preview info))
            | _ -> None

        let outcome =
            match shown with
            | Some value -> valueText limits.OutputChars result.CellId value
            | None -> outcomeText result.CellId result.Outcome

        let head =
            lines [ result.Output.Text.TrimEnd(); outcome; stoppedWaiting result.Outcome waiting ]

        let assigned =
            result.Declared
            |> List.filter (fun name -> not (List.contains name result.Unassigned))

        let tail =
            lines
                [
                    (if assigned.IsEmpty then
                         ""
                     else
                         "Declared: " + String.concat ", " assigned)
                    (if result.Unassigned.IsEmpty then
                         ""
                     else
                         "Not assigned (the cell stopped first): " + String.concat ", " result.Unassigned)
                    (if result.Finished then
                         "The turn's result was accepted."
                     else
                         "")
                    (if intent.IsNone then
                         "[Note: start each cell with an `// intent: …` line.]"
                     else
                         "")
                ]

        match shown with
        | Some(CellValue.File file) ->
            restarted note
            @ [ quoted limits.ReceiptChars reads head ]
            @ fileValue file limits.ReadLines
            @ (if tail = "" then [] else [ Piece.Text tail ])
        | Some(CellValue.Edited value) ->
            restarted note
            @ [ quoted limits.ReceiptChars reads head ]
            @ edited limits.ReadLines value None
            @ (if tail = "" then [] else [ Piece.Text tail ])
        | _ ->
            let text = lines [ head; tail ]

            restarted note
            @ [ quoted limits.ReceiptChars reads (if text = "" then "(no output)" else text) ]
