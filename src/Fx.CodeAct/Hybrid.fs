namespace Fx.CodeAct

open System
open Fx.Contracts
open Fx.Effects
open Fx.Envd
open Fx.Kernel
open Fx.Interpreters
open Fx.Prompt

/// What an action did, as its receipt shows it.
type Executed =
    {
        Pieces: Piece list
        IsError: bool
    }

    override this.ToString() =
        $"executed ({this.Pieces.Length} pieces, error {this.IsError})"

/// The hybrid surface's direct tools (spec §6.9): `read`, `edit` and `bash` run as host calls of the
/// bridge's own bindings (`fs.read`, `fs.patch`, `shell.run`), through the same guard, settings and
/// content exclusions as cells, so both surfaces do the same things the same way.
[<RequireQualifiedAccess>]
module Hybrid =
    type private Call<'K> = HostCall -> Eff<'K, Result<Value, HostCallError>>

    let private failed (message: string) : Executed =
        {
            Pieces = Receipts.failed message
            IsError = true
        }

    /// A refused or failed host call, as the model reads it.
    let describe (error: HostCallError) : string =
        match error with
        | HostCallError.Failed(code, message, _) -> $"{message} [code {code}]"
        | HostCallError.Denied reason -> "Refused: " + reason
        | HostCallError.InvalidArguments issues -> "Invalid arguments:\n" + Issue.renderAtMost 10 issues
        | other -> string other

    let private host (binding: string) (args: Json list) : HostCall =
        {
            CellId = None
            Binding = binding
            Args = args |> List.map Value.Inline
        }

    let private readFile (call: Call<'K>) (path: string) : Eff<'K, Result<FileText, string>> =
        eff {
            match! call (host "fs.read" [ Json.String path ]) with
            | Error e -> return Error(describe e)
            | Ok value ->
                match Value.decode CoreBindings.Codecs.fileText value with
                | Ok file -> return Ok file
                | Error issues -> return Error("fs.read gave an unexpected value:\n" + Issue.renderAtMost 5 issues)
        }

    /// `read`: the file's lines, numbered, as a file piece.
    let read (call: Call<'K>) (maxLines: int) (input: ReadInput) : Eff<'K, Executed> =
        eff {
            match! readFile call input.Path with
            | Error message -> return failed message
            | Ok file ->
                return
                    {
                        Pieces = Receipts.read file input.Offset input.Limit maxLines
                        IsError = false
                    }
        }

    let private occurrences (text: string) (part: string) : int =
        let mutable count = 0
        let mutable at = text.IndexOf(part, StringComparison.Ordinal)

        while at >= 0 do
            count <- count + 1
            at <- text.IndexOf(part, at + part.Length, StringComparison.Ordinal)

        count

    let private replaceFirst (text: string) (part: string) (replacement: string) : string =
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

    /// `edit`: replaces text read from the file, then patches the lines that changed, checked against
    /// the digest it read (a file changed in between is not overwritten).
    let edit (call: Call<'K>) (input: EditInput) : Eff<'K, Executed> =
        eff {
            match! readFile call input.Path with
            | Error message -> return failed message
            | Ok file ->
                let lf (text: string) = text.Replace("\r\n", "\n")
                let text = lf file.Text
                let old = lf input.Old
                let replacement = lf input.New
                let count = occurrences text old

                if count = 0 then
                    return
                        failed
                            $"`old` does not occur in {file.Path}: copy it from the file as read (without line numbers), including whitespace."
                elif count > 1 && not input.All then
                    return
                        failed
                            $"`old` occurs {count} times in {file.Path}: include more of the surrounding text so it occurs once, or set `all` to replace every occurrence."
                elif old = replacement then
                    return failed "`old` and `new` are the same: nothing to change."
                else
                    let edited =
                        if input.All then
                            text.Replace(old, replacement, StringComparison.Ordinal)
                        else
                            replaceFirst text old replacement

                    let after = Receipts.splitLines edited
                    let change = lineEdit (Receipts.splitLines text) after
                    let patch = FilePatch.Edit(file.Path, Some file.Digest, [ change ])

                    match! call (host "fs.patch" [ Json.Array [ CoreBindings.Codecs.filePatch.Encode patch ] ]) with
                    | Error e -> return failed (describe e)
                    | Ok value ->
                        let digest =
                            match Value.decode (Codec.list CoreBindings.Codecs.written) value with
                            | Ok [ written ] -> written.Digest |> Option.map string |> Option.defaultValue ""
                            | _ -> ""

                        let first = change.Line

                        let before =
                            after |> List.skip (max 0 (first - 4)) |> List.truncate (min 3 (first - 1))

                        let following =
                            after
                            |> List.skip (min after.Length (first - 1 + change.Lines.Length))
                            |> List.truncate 3

                        return
                            {
                                Pieces =
                                    Receipts.edited
                                        file.Path
                                        digest
                                        (if input.All then count else 1)
                                        first
                                        change.Lines
                                        (before, following)
                                IsError = false
                            }
        }

    /// `bash`: a command line with the environment's shell, through `shell.run`.
    let bash (call: Call<'K>) (maxChars: int) (input: BashInput) : Eff<'K, Executed> =
        eff {
            let options =
                CoreBindings.Codecs.runOptions.Encode
                    {
                        Cwd = None
                        TimeoutMs = input.TimeoutMs
                        Stdin = None
                    }

            match! call (host "shell.run" [ Json.String input.Command; options ]) with
            | Error e -> return failed (describe e)
            | Ok value ->
                match Value.decode CoreBindings.Codecs.runResult value with
                | Error issues -> return failed ("shell.run gave an unexpected value:\n" + Issue.renderAtMost 5 issues)
                | Ok result ->
                    return
                        {
                            Pieces = Receipts.ran maxChars result
                            IsError = result.TimedOut
                        }
        }
