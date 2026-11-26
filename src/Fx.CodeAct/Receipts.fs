namespace Fx.CodeAct

open System
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

    /// A cell's receipt: what it printed, its outcome (its value's preview, or the error), the names it
    /// declared, whether its `finish` was accepted, and a note if the kernel started afresh. Any of it
    /// may quote what the cell read through the bridge, so it carries those files as provenance.
    let cell
        (maxChars: int)
        (note: KernelNote)
        (intent: string option)
        (reads: Reads)
        (result: CellResult)
        : Piece list =
        let text =
            lines
                [
                    result.Output.Text.TrimEnd()
                    outcomeText result.CellId result.Outcome
                    (if result.Declared.IsEmpty then
                         ""
                     else
                         "Declared: " + String.concat ", " result.Declared)
                    (if result.Finished then
                         "The turn's result was accepted."
                     else
                         "")
                    (if intent.IsNone then
                         "[Note: start each cell with an `// intent: …` line.]"
                     else
                         "")
                ]

        restarted note
        @ [ quoted maxChars reads (if text = "" then "(no output)" else text) ]

    /// A cell the kernel did not answer; `ended`: how the kernel ended (after "the kernel"), if it did.
    let kernelFailed (note: KernelNote) (failure: KernelFailure) (ended: string option) : Piece list =
        let text =
            match ended with
            | Some how ->
                $"The cell did not complete: the kernel {how}. The next cell starts a new kernel: names and values from earlier cells will be gone."
            | None -> $"The cell did not complete: {failure}."

        restarted note @ [ Piece.Text text ]

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

    /// `read`'s receipt: the lines shown, as a file piece (they count as seen; exclusions withhold
    /// them), and how to see more.
    let read (file: FileText) (offset: int option) (limit: int option) (maxLines: int) : Piece list =
        let all = splitLines file.Text
        let first = defaultArg offset 1
        let count = min maxLines (defaultArg limit maxLines)

        let shown =
            all |> List.skip (min all.Length (max 0 (first - 1))) |> List.truncate count

        let last = first + shown.Length - 1
        let whole = first = 1 && shown.Length = all.Length

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
            if shown.IsEmpty && not all.IsEmpty then
                Piece.Text $"{file.Path} has {all.Length} lines; nothing at line {first}."
            elif all.IsEmpty then
                Piece.Text $"{file.Path} is empty."
            else
                Piece.File view

                if not whole then
                    Piece.Text $"[Lines {first}–{last} of {all.Length}. Read on with offset {last + 1}.]"
        ]

    /// `edit`'s receipt: what changed, and the changed lines with some context as the file now is.
    let edited
        (path: string)
        (digest: string)
        (replacements: int)
        (first: int)
        (newLines: string list)
        (context: string list * string list)
        : Piece list =
        let before, after = context
        let shownFirst = first - before.Length
        let shown = before @ newLines @ after

        let summary =
            let times =
                if replacements = 1 then
                    "1 replacement"
                else
                    $"{replacements} replacements"

            $"Edited {path} ({times})."

        [
            Piece.Text summary
            if not shown.IsEmpty then
                Piece.File
                    {
                        Path = Paths.normalize path
                        Digest = digest
                        Lines = [ LineRange.create shownFirst (shownFirst + shown.Length - 1) ]
                        Text = numbered shownFirst shown
                    }
        ]

    /// `bash`'s receipt: the command, how it ended and its output (bounded already).
    let ran (maxChars: int) (result: RunResult) : Piece list =
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

        [
            Piece.Text $"$ {result.Command}\n{ending}"
            if streams <> "" then
                Piece.output maxChars streams
        ]
