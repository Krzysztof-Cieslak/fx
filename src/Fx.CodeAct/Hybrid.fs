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
        /// The cell ran in a new kernel (`Receipt.Restarted`).
        Restarted: KernelRestart option
        /// How its host calls ran that no grant in force allowed (`Receipt.Approvals`).
        Approvals: Approval list
        /// The background jobs it started (`Receipt.Jobs`).
        Jobs: StartedJob list
    }

    override this.ToString() =
        $"executed ({this.Pieces.Length} pieces, error {this.IsError})"

/// The hybrid surface's direct tools (spec §6.9): `read`, `edit` and `bash` run as host calls of the
/// bridge's own bindings (`fs.read`, `fs.edit`, `shell.run`), through the same guard, settings and
/// content exclusions as cells, so both surfaces do the same things the same way.
[<RequireQualifiedAccess>]
module Hybrid =
    type private Call<'K> = HostCall -> Eff<'K, Result<Value, HostCallError>>

    let private failed (message: string) : Executed =
        {
            Pieces = Receipts.failed message
            IsError = true
            Restarted = None
            Approvals = []
            Jobs = []
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
    let read (call: Call<'K>) (bounds: Receipts.ReadBounds) (input: ReadInput) : Eff<'K, Executed> =
        eff {
            match! readFile call input.Path with
            | Error message -> return failed message
            | Ok file ->
                return
                    {
                        Pieces = Receipts.read file input.Offset input.Limit bounds
                        IsError = false
                        Restarted = None
                        Approvals = []
                        Jobs = []
                    }
        }

    /// `edit`: a host call of `fs.edit` (the binding replaces the text and patches the lines that
    /// changed, checked against the digest it read), then, for a file in the workspace, a ranged
    /// `fs.read` of three lines around them for the receipt (outside it, that read would ask a person
    /// again, so the receipt shows the changed lines alone).
    let edit (call: Call<'K>) (maxLines: int) (input: EditInput) : Eff<'K, Executed> =
        eff {
            let change =
                CoreBindings.Codecs.textChange.Encode
                    {
                        Old = input.Old
                        New = input.New
                        All = input.All
                    }

            match! call (host "fs.edit" [ Json.String input.Path; change ]) with
            | Error e -> return failed (describe e)
            | Ok value ->
                match Value.decode CoreBindings.Codecs.edited value with
                | Error issues -> return failed ("fs.edit gave an unexpected value:\n" + Issue.renderAtMost 5 issues)
                | Ok edited ->
                    let! around =
                        match edited.Ranges with
                        | [] -> Eff.ret None
                        | first :: _ ->
                            let start = max 1 (first.Start - 3)
                            let stop = (List.last edited.Ranges).End + 3

                            if
                                Fx.Models.WorkspacePath.isAbsolute (Fx.Models.WorkspacePath.normalize edited.Path)
                                || stop - start + 1 > maxLines
                            then
                                Eff.ret None
                            else
                                let options = CoreBindings.Codecs.readOptions.Encode { Lines = Some(start, stop) }

                                call (host "fs.read" [ Json.String edited.Path; options ])
                                |> Eff.map (fun result ->
                                    match result with
                                    | Ok value ->
                                        match Value.decode CoreBindings.Codecs.fileText value with
                                        | Ok file -> Some file
                                        | Error _ -> None
                                    | Error _ -> None)

                    return
                        {
                            Pieces = Receipts.edited maxLines edited around
                            IsError = false
                            Restarted = None
                            Approvals = []
                            Jobs = []
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
                            Restarted = None
                            Approvals = []
                            Jobs = []
                        }
        }
