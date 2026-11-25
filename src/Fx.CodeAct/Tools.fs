namespace Fx.CodeAct

open System
open Fx
open Fx.Contracts
open Fx.Models
open Fx.Variants

/// `read`'s input (hybrid action surface).
type ReadInput =
    {
        Path: string
        /// The first line shown, 1-based.
        Offset: int option
        /// How many lines are shown.
        Limit: int option
    }

    override this.ToString() = $"read {this.Path}"

/// `edit`'s input (hybrid action surface).
type EditInput =
    {
        Path: string
        Old: string
        New: string
        /// Replace every occurrence, not exactly one.
        All: bool
    }

    override this.ToString() = $"edit {this.Path}"

/// `bash`'s input (hybrid action surface).
type BashInput =
    {
        Command: string
        TimeoutMs: int option
    }

    override this.ToString() = $"bash {this.Command}"

/// What the model asked for with a tool call (spec §6.9).
[<RequireQualifiedAccess>]
type ToolAction =
    /// A cell: code for the session's kernel.
    | Cell of code: string
    | Read of ReadInput
    | Edit of EditInput
    | Bash of BashInput

    override this.ToString() =
        match this with
        | Cell code -> $"cell ({code.Length} chars)"
        | Read input -> string input
        | Edit input -> string input
        | Bash input -> string input

/// The action surfaces (spec §6.9) as tools made from codecs: `cells-only` is the `cell` tool (a turn
/// ends with the bridge's `finish`, called from a cell, so evidence stays values); `hybrid` adds `read`,
/// `edit` and `bash`, which run through the same bindings as cells.
[<RequireQualifiedAccess>]
module Tools =
    [<Literal>]
    let CellName = "cell"

    [<Literal>]
    let ReadName = "read"

    [<Literal>]
    let EditName = "edit"

    [<Literal>]
    let BashName = "bash"

    let private g = Product.kernelGlobal

    let cellCodec: Codec<string> =
        Codec.record {
            let! code =
                Codec.field
                    "code"
                    id
                    (Codec.string
                     |> Codec.nonEmpty
                     |> Codec.describe "The cell's code; its first line is `// intent: <what and why>`.")

            return code
        }
        |> Codec.named "CellInput"

    let readCodec: Codec<ReadInput> =
        Codec.record {
            let! path =
                Codec.field
                    "path"
                    (fun (r: ReadInput) -> r.Path)
                    (Codec.string |> Codec.nonEmpty |> Codec.describe "Relative to the workspace.")

            and! offset =
                Codec.optionalField
                    "offset"
                    (fun r -> r.Offset)
                    (Codec.int
                     |> Codec.atLeast 1
                     |> Codec.describe "The first line to show (1-based).")

            and! limit =
                Codec.optionalField
                    "limit"
                    (fun r -> r.Limit)
                    (Codec.int |> Codec.atLeast 1 |> Codec.describe "How many lines to show.")

            return
                {
                    Path = path
                    Offset = offset
                    Limit = limit
                }
        }
        |> Codec.named "ReadInput"

    let editCodec: Codec<EditInput> =
        Codec.record {
            let! path =
                Codec.field
                    "path"
                    (fun (e: EditInput) -> e.Path)
                    (Codec.string |> Codec.nonEmpty |> Codec.describe "Relative to the workspace.")

            and! old =
                Codec.field
                    "old"
                    (fun e -> e.Old)
                    (Codec.string
                     |> Codec.nonEmpty
                     |> Codec.describe "The text to replace, exactly as in the file (without line numbers).")

            and! replacement =
                Codec.field "new" (fun e -> e.New) (Codec.string |> Codec.describe "The text to put in its place.")

            and! all =
                Codec.defaultField
                    "all"
                    (fun e -> e.All)
                    false
                    (Codec.bool
                     |> Codec.describe "Replace every occurrence; otherwise `old` must occur exactly once.")

            return
                {
                    Path = path
                    Old = old
                    New = replacement
                    All = all
                }
        }
        |> Codec.named "EditInput"

    let bashCodec: Codec<BashInput> =
        Codec.record {
            let! command =
                Codec.field
                    "command"
                    (fun (b: BashInput) -> b.Command)
                    (Codec.string |> Codec.nonEmpty |> Codec.describe "The command line.")

            and! timeout =
                Codec.optionalField
                    "timeoutMs"
                    (fun b -> b.TimeoutMs)
                    (Codec.int
                     |> Codec.atLeast 1
                     |> Codec.describe "Kills the command after this many milliseconds.")

            return
                {
                    Command = command
                    TimeoutMs = timeout
                }
        }
        |> Codec.named "BashInput"

    /// The `cell` tool for cells in a language (`typescript`).
    let cell (language: string) : ToolSpec =
        ToolSpec.ofCodec
            CellName
            $"Runs a cell: {language} code in this session's persistent kernel (top-level await; declarations persist across cells). Start it with a `// intent: <what and why>` line. The `{g}` global reaches the workspace (files, commands); `await {g}.finish(result)` ends the turn. The result shows what the cell printed and the value of its last expression, bounded."
            cellCodec

    let read: ToolSpec =
        ToolSpec.ofCodec
            ReadName
            "Reads a text file of the workspace, with line numbers (use offset and limit for long files). Files that content exclusions keep from models are refused."
            readCodec

    let edit: ToolSpec =
        ToolSpec.ofCodec
            EditName
            "Edits a text file of the workspace: replaces `old` (which must occur exactly once, unless `all`) with `new`. Copy `old` from the file as read, without the line numbers."
            editCodec

    let bash: ToolSpec =
        ToolSpec.ofCodec
            BashName
            "Runs a command line in the workspace with its shell (sh; PowerShell 7 on Windows) and returns its exit code and output (the head and tail of each stream)."
            bashCodec

    /// The tools of an action surface.
    let specs (surface: ActionSurface) (language: string) : ToolSpec list =
        match surface with
        | ActionSurface.CellsOnly -> [ cell language ]
        | ActionSurface.Hybrid -> [ cell language; read; edit; bash ]

    let private decodeWith (codec: Codec<'T>) (arguments: string) : Result<'T, string> =
        let text =
            if String.IsNullOrWhiteSpace arguments then
                "{}"
            else
                arguments

        match Json.parse text with
        | Error e -> Error $"The arguments are not JSON ({e}); nothing ran."
        | Ok json ->
            match codec.Decode json with
            | Ok value -> Ok value
            | Error issues -> Error("Invalid arguments; nothing ran:\n" + Issue.renderAtMost 10 issues)

    /// The action a tool call asks for on a surface, or why it cannot run.
    let decode (surface: ActionSurface) (call: ToolCall) : Result<ToolAction, string> =
        match surface, call.Name with
        | _, CellName -> decodeWith cellCodec call.Arguments |> Result.map ToolAction.Cell
        | ActionSurface.Hybrid, ReadName -> decodeWith readCodec call.Arguments |> Result.map ToolAction.Read
        | ActionSurface.Hybrid, EditName -> decodeWith editCodec call.Arguments |> Result.map ToolAction.Edit
        | ActionSurface.Hybrid, BashName -> decodeWith bashCodec call.Arguments |> Result.map ToolAction.Bash
        | _, name ->
            let names = specs surface "code" |> List.map (fun t -> t.Name) |> String.concat ", "

            Error $"There is no tool named {Json.quote name}; the tools are: {names}."
