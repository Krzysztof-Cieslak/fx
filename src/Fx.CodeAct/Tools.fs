namespace Fx.CodeAct

open System
open Fx
open Fx.Contracts
open Fx.Effects
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

/// `cell`'s input: the code, and the texts it uses by name (T140).
type CellInput =
    {
        Code: string
        /// Names to texts, in the order given: each is bound in the kernel as a string of that name
        /// before the code runs, so file bodies, messages and patches need no escaping in the code.
        Texts: (string * string) list
    }

    override this.ToString() =
        $"cell ({this.Code.Length} chars, {this.Texts.Length} texts)"

/// What the model asked for with a tool call (spec §6.9).
[<RequireQualifiedAccess>]
type ToolAction =
    /// A cell: code for the session's kernel.
    | Cell of CellInput
    | Read of ReadInput
    | Edit of EditInput
    | Bash of BashInput

    override this.ToString() =
        match this with
        | Cell input -> string input
        | Read input -> string input
        | Edit input -> string input
        | Bash input -> string input

/// The tools a conversation runs with (spec §6.9): its action surface and the version of the tools'
/// schemas. A conversation keeps the set its frame recorded (the `tools` section's version): tools
/// precede the prompt prefix that providers cache and check.
type ToolSet =
    {
        Surface: ActionSurface
        /// 1: `cell {code}`; 2: `cell {code, texts?}` (T140).
        Version: int
    }

    override this.ToString() =
        $"{this.Surface} tools, version {this.Version}"

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

    /// The version of the tools' schemas new conversations get.
    [<Literal>]
    let Version = 2

    /// The most texts one cell takes.
    [<Literal>]
    let MaxTexts = 32

    /// The longest name of a text.
    [<Literal>]
    let MaxNameChars = 64

    /// The tools a new conversation on a surface runs with.
    let current (surface: ActionSurface) : ToolSet =
        { Surface = surface; Version = Version }

    let private namePattern = Text.RegularExpressions.Regex "^[A-Za-z_][A-Za-z0-9_]*$"

    /// Names a text cannot have in any kernel: the bridge's global and the cells' values (`Out`).
    let reservedNames: string list = [ g; "Out" ]

    /// What is wrong with a text's name, if anything.
    let checkName (name: string) : string option =
        if name.Length > MaxNameChars || not (namePattern.IsMatch name) then
            Some
                $"{Json.quote name} is not a name: a text's name is an identifier (letters, digits and _, not starting with a digit) of at most {MaxNameChars} characters"
        elif List.contains name reservedNames then
            let kept = String.concat ", " reservedNames
            Some $"{Json.quote name} is a name the kernel keeps ({kept}); choose another"
        else
            None

    let private texts: Codec<(string * string) list> =
        let pairs = Codec.keyValues Codec.string

        let check (texts: (string * string) list) : Issue list =
            let names = texts |> List.map fst

            let count =
                if texts.Length > MaxTexts then
                    [ Issue.create $"expected at most {MaxTexts} texts, got {texts.Length}" ]
                else
                    []

            let invalid =
                names
                |> List.choose (fun name ->
                    checkName name
                    |> Option.map (fun message -> Issue.at [ PathSegment.Key name ] message))

            let repeated =
                names
                |> List.countBy id
                |> List.filter (fun (_, n) -> n > 1)
                |> List.map (fun (name, _) -> Issue.at [ PathSegment.Key name ] $"{Json.quote name} is given twice")

            count @ invalid @ repeated

        { pairs with
            Decode =
                fun json ->
                    pairs.Decode json
                    |> Result.bind (fun decoded ->
                        match check decoded with
                        | [] -> Ok decoded
                        | issues -> Error issues)
        }
        |> Codec.describe
            $"Texts the code uses, by name: file bodies, commit messages, patches, a command's input. Each is a string constant of its name (an identifier) before the code runs, so it needs no escaping: use it for any text with backticks or more than a few lines. At most {MaxTexts}."

    /// `cell`'s input (version 2 of the tools; version 1 had no `texts`).
    let cellCodec: Codec<CellInput> =
        Codec.record {
            let! code =
                Codec.field
                    "code"
                    (fun (c: CellInput) -> c.Code)
                    (Codec.string
                     |> Codec.nonEmpty
                     |> Codec.describe "The cell's code; its first line is `// intent: <what and why>`.")

            and! texts = Codec.defaultField "texts" (fun c -> c.Texts) [] texts

            return { Code = code; Texts = texts }
        }
        |> Codec.named "CellInput"

    /// `cell`'s input in version 1 of the tools, which conversations framed with it keep.
    let private cellCodecV1: Codec<string> =
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

    let private cellDescription (language: string) =
        $"Runs a cell: {language} code in this session's persistent kernel (top-level await; declarations persist across cells). Start it with a `// intent: <what and why>` line. The `{g}` global reaches the workspace (files, commands); `await {g}.finish(result)` ends the turn. The result shows what the cell printed and the value of its last expression, bounded."

    /// The `cell` tool for cells in a language (`typescript`), in a version of the tools.
    let cellOf (version: int) (language: string) : ToolSpec =
        if version <= 1 then
            ToolSpec.ofCodec CellName (cellDescription language) cellCodecV1
        else
            ToolSpec.ofCodec
                CellName
                (cellDescription language
                 + " Put file bodies, commit messages, patches and any text with backticks or more than a few lines in `texts`, not in the code: each is a string constant of its name when the code runs, with nothing to escape.")
                cellCodec

    /// The `cell` tool for cells in a language (`typescript`), as new conversations get it.
    let cell (language: string) : ToolSpec = cellOf Version language

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

    /// The tools of a set: its surface's, in its version.
    let specs (tools: ToolSet) (language: string) : ToolSpec list =
        match tools.Surface with
        | ActionSurface.CellsOnly -> [ cellOf tools.Version language ]
        | ActionSurface.Hybrid -> [ cellOf tools.Version language; read; edit; bash ]

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

    /// The action a tool call asks for on a surface, or why it cannot run. Arguments decode with the
    /// latest schemas, whose older versions are subsets.
    let decode (surface: ActionSurface) (call: ToolCall) : Result<ToolAction, string> =
        match surface, call.Name with
        | _, CellName -> decodeWith cellCodec call.Arguments |> Result.map ToolAction.Cell
        | ActionSurface.Hybrid, ReadName -> decodeWith readCodec call.Arguments |> Result.map ToolAction.Read
        | ActionSurface.Hybrid, EditName -> decodeWith editCodec call.Arguments |> Result.map ToolAction.Edit
        | ActionSurface.Hybrid, BashName -> decodeWith bashCodec call.Arguments |> Result.map ToolAction.Bash
        | _, name ->
            let names =
                specs (current surface) "code"
                |> List.map (fun t -> t.Name)
                |> String.concat ", "

            Error $"There is no tool named {Json.quote name}; the tools are: {names}."

    /// The `tools` section's version for a set: the surface's name for version 1, then
    /// `<surface>/<version>`.
    let sectionVersion (tools: ToolSet) : string =
        if tools.Version <= 1 then
            string tools.Surface
        else
            $"{tools.Surface}/{tools.Version}"

    /// The set a `tools` section's version names, if this host knows it.
    let ofSectionVersion (version: string) : ToolSet option =
        let surface (name: string) =
            match name with
            | "cells-only" -> Some ActionSurface.CellsOnly
            | "hybrid" -> Some ActionSurface.Hybrid
            | _ -> None

        match version.Split('/') with
        | [| name |] -> surface name |> Option.map (fun s -> { Surface = s; Version = 1 })
        | [| name; number |] ->
            match
                Int32.TryParse(number, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
            with
            | true, n when n >= 2 && n <= Version -> surface name |> Option.map (fun s -> { Surface = s; Version = n })
            | _ -> None
        | _ -> None

/// A cell's texts (T140), bound in its kernel before its code runs.
[<RequireQualifiedAccess>]
module CellTexts =
    /// The bytes of a text's JSON, as the kernel protocol carries it.
    let jsonBytes (text: string) : int64 =
        int64 (Text.Encoding.UTF8.GetByteCount(Json.serialize (Json.String text)))

    /// Binds each text as a string of its name (`value/decode`), in order, each up to `maxBytes` of
    /// JSON (`values.argumentMaxBytes`, as a host call's argument): `Ok`, or why the first that was not
    /// bound was not, for the model (the cell does not run then).
    let bind<'E when 'E :> IScope>
        (kernel: Fx.Kernel.Kernel)
        (maxBytes: int)
        (texts: (string * string) list)
        : Eff<'E, Result<unit, string>> =
        eff {
            let mutable failed = None

            for name, text in texts do
                if failed.IsNone then
                    let bytes = jsonBytes text

                    if bytes > int64 maxBytes then
                        failed <-
                            Some
                                $"The text {Json.quote name} has {bytes} bytes of JSON, more than the {maxBytes} a text may have (values.argumentMaxBytes): split it into several texts."
                    else
                        match! Fx.Kernel.Kernel.decode kernel name (Fx.Kernel.Value.Inline(Json.String text)) None with
                        | Ok _ -> ()
                        | Error(Fx.Kernel.KernelFailure.Refused(Fx.Kernel.KernelError.InvalidName _)) ->
                            failed <-
                                Some
                                    $"The kernel cannot bind the text {Json.quote name}: its language cannot have a variable of that name (a reserved word, or one of the kernel's own names such as its built-in globals). Rename the text."
                        | Error failure -> failed <- Some $"The text {Json.quote name} could not be bound: {failure}."

            match failed with
            | Some reason -> return Error(reason + " Nothing ran.")
            | None -> return Ok()
        }
