namespace Fx.Kernel

open Fx.Contracts

/// How a kernel's language exposes host calls (spec §6.2).
[<RequireQualifiedAccess>]
type AsyncHostCalls =
    /// Cells await host calls (`await fx.shell.run(…)`).
    | Await
    /// Host calls look synchronous to cells; the kernel suspends the cell while the host answers.
    | Transparent

    override this.ToString() =
        match this with
        | Await -> "await"
        | Transparent -> "transparent"

/// How a kernel installs packages for a session (spec §6.3), if it does.
type PackageSupport =
    {
        /// The package manager, e.g. `bun` or `nuget`.
        Manager: string
    }

    override this.ToString() = this.Manager

/// What an interpreter's kernels can do (spec §6.2 `InterpreterCaps`). The interpreter declares
/// them; its kernels announce them in `initialize`, and the conformance suite checks that they hold.
type InterpreterCaps =
    {
        /// The kernel serializes its state (`snapshot`) and restores it in a fresh process (`restore`).
        Snapshot: bool
        AsyncHostCalls: AsyncHostCalls
        Packages: PackageSupport option
        /// The same cells with the same host answers give the same results (no clock, randomness or
        /// I/O of its own).
        Deterministic: bool
    }

    override this.ToString() =
        let packages =
            match this.Packages with
            | Some p -> " packages " + p.Manager
            | None -> ""

        $"snapshot {this.Snapshot}, host calls {this.AsyncHostCalls}, deterministic {this.Deterministic}{packages}"

/// The bounds a host sets for a kernel's output and previews. Their values are variants.
type KernelLimits =
    {
        /// Characters of a cell's output kept in its result (head and tail), and streamed at most.
        OutputChars: int
        /// Bounds of value previews.
        Preview: Limits
        /// The largest JSON (UTF-8 bytes) a kernel copies inline; larger values are references.
        InlineBytes: int
    }

    override this.ToString() =
        $"output {this.OutputChars} chars, inline {this.InlineBytes} bytes, preview {this.Preview}"

/// What the host tells a kernel in `initialize` (the `kernel` member of the params).
type KernelSetup =
    {
        /// The kernel protocol versions the host speaks, preferred first.
        Versions: string list
        /// The name of the host bridge in the kernel's language (`Fx.Product.kernelGlobal`).
        Global: string
        /// The host calls cells can make, as dotted paths (`shell.run`); each is a `host/call`.
        Bindings: string list
        /// The directory relative imports in cells resolve against, if the language has them.
        ModuleBase: string option
        Limits: KernelLimits
    }

    override this.ToString() =
        $"setup {this.Global} ({this.Bindings.Length} bindings; {this.Limits})"

/// What a kernel tells the host in `initialize` (the `kernel` member of the result).
type KernelInfo =
    {
        /// The kernel protocol version it chose from the host's.
        Version: string
        /// The language of its cells, e.g. `typescript`.
        Language: string
        Caps: InterpreterCaps
        /// Facts about its runtime, for diagnostics (runtime and compiler versions).
        Details: Json option
    }

    override this.ToString() =
        $"kernel {this.Language} (protocol {this.Version}; {this.Caps})"

/// `cell/run` params.
type CellRun =
    {
        /// Unique per kernel: 1 to 64 of `A-Z a-z 0-9 _ -`.
        CellId: string
        Code: string
        /// The cell's `// intent:` line, for activity and the transcript (spec §19.2).
        Intent: string option
        /// Wall-clock limit; the kernel interrupts the cell after it.
        TimeoutMs: int option
        /// Type-check first and do not run on errors (kernels that announce `cell/check`).
        Check: bool
    }

    override this.ToString() =
        $"cell {this.CellId} ({this.Code.Length} chars)"

/// Text a kernel kept within a bound: the head and the tail, with a marker for what was left out.
type BoundedText =
    {
        Text: string
        Truncated: bool
        /// Characters produced in total.
        TotalChars: int64
    }

    override this.ToString() =
        if this.Truncated then
            $"{this.TotalChars} chars, truncated"
        else
            $"{this.TotalChars} chars"

/// A position in a cell's source, 1-based.
type CellLocation =
    {
        CellId: string
        Line: int
        Column: int
    }

    override this.ToString() =
        $"cell {this.CellId}:{this.Line}:{this.Column}"

/// A type-checking diagnostic, at a 1-based line of the cell (0: in code carried over from earlier
/// cells).
type Diagnostic =
    {
        Line: int
        Column: int
        Code: string
        Message: string
    }

    override this.ToString() =
        $"{this.Line}:{this.Column} {this.Code}: {this.Message}"

/// Code that does not parse.
type SyntaxFailure =
    {
        Message: string
        Line: int option
        Column: int option
        LineText: string option
    }

    override this.ToString() = "syntax error: " + this.Message

/// An error a cell threw (or a value it threw).
type RuntimeFailure =
    {
        /// The error's type, e.g. `TypeError`.
        Name: string
        Message: string
        /// A code the error carries, e.g. a host call's error kind.
        Code: string option
        /// Where it was thrown in a cell, if the kernel can tell.
        Location: CellLocation option
        /// The stack, with frames in cells only.
        Stack: string
    }

    override this.ToString() = $"{this.Name}: {this.Message}"

/// How a cell ended.
[<RequireQualifiedAccess>]
type CellOutcome =
    /// It ran to the end; the value of its last expression, if it has one.
    | Completed of value: ValueInfo option
    | SyntaxError of SyntaxFailure
    | TypeError of diagnostics: Diagnostic list
    | RuntimeError of RuntimeFailure
    /// It ran longer than its `timeoutMs`.
    | TimedOut of afterMs: int
    /// `cell/cancel` (or `$/cancelRequest` on its `cell/run`) stopped it.
    | Cancelled

    override this.ToString() =
        match this with
        | Completed(Some value) -> "completed: " + string value
        | Completed None -> "completed"
        | SyntaxError failure -> string failure
        | TypeError diagnostics -> $"{diagnostics.Length} type errors"
        | RuntimeError failure -> string failure
        | TimedOut after -> $"timed out after {after} ms"
        | Cancelled -> "cancelled"

/// `cell/run` result.
type CellResult =
    {
        CellId: string
        Outcome: CellOutcome
        /// What the cell printed, bounded by `OutputChars`.
        Output: BoundedText
        /// Top-level names the cell declared; they persist in the kernel.
        Declared: string list
        /// The cell called `finish` and the host accepted it.
        Finished: bool
        /// How many `cell/output` and `cell/activity` notifications the kernel sent about this cell
        /// before this result: the host has seen them all once it has handled that many.
        Notified: int
        DurationMs: int64
    }

    override this.ToString() =
        $"cell {this.CellId}: {this.Outcome} ({this.Output})"

/// `cell/cancel` and `cell/check` refer to a cell.
type CellCheck =
    {
        CellId: string
        Code: string
    }

    override this.ToString() = $"check {this.CellId}"

type CellRef =
    {
        CellId: string
    }

    override this.ToString() = this.CellId

type Cancelled =
    {
        /// The cell was still running. Cancelling a finished cell still stops what it left running
        /// in the background (timers, processes).
        WasRunning: bool
    }

    override this.ToString() = $"was running: {this.WasRunning}"

type Checked =
    {
        Diagnostics: Diagnostic list
    }

    override this.ToString() =
        $"{this.Diagnostics.Length} diagnostics"

/// `value/preview` params.
type PreviewParams =
    {
        Ref: ValueRef
        /// Bounds other than the kernel's defaults (`KernelLimits.Preview`).
        Limits: Limits option
    }

    override this.ToString() = $"preview {this.Ref}"

/// `value/encode` params.
type EncodeParams =
    {
        Ref: ValueRef
        /// The largest JSON to return inline, in UTF-8 bytes; the kernel's `InlineBytes` by default.
        MaxBytes: int option
    }

    override this.ToString() = $"encode {this.Ref}"

/// `value/decode` params: binds a value to a top-level variable (task inputs, a prefill cell).
type DecodeParams =
    {
        Name: string
        Value: Value
        /// The value's declared type in the kernel's language, for its type checker.
        Type: string option
    }

    override this.ToString() = $"decode into {this.Name}"

/// `recall` params: restores the completion value of a cell, e.g. from the journal into a restarted
/// kernel (spec §5 "`Out[n]` restore values").
type RecallParams =
    {
        CellId: string
        Value: Value
    }

    override this.ToString() = $"recall {this.CellId}"

/// A kernel's state, serialized by `snapshot` and restored by `restore` in any process of the same
/// kernel. Its content is the kernel's own format.
type KernelState =
    {
        State: Value
    }

    override this.ToString() = $"state {this.State}"

/// The typed errors of the methods the host calls on a kernel.
[<RequireQualifiedAccess>]
type KernelError =
    /// Another cell is running; kernels run one cell at a time.
    | Busy of running: string
    /// A cell with this id already ran.
    | DuplicateCell of cellId: string
    | InvalidCellId of cellId: string
    | UnknownCell of cellId: string
    /// No variable or cell output by that reference.
    | NotFound of ref: ValueRef
    /// The value has no JSON form (a function, a cycle, a handle inside a value).
    | NotEncodable of typeName: string * reason: string
    /// The value's JSON is larger than allowed inline.
    | TooLarge of bytes: int64 * limit: int
    /// Not a name a top-level variable can have.
    | InvalidName of name: string
    /// The kernel cannot hold this value (e.g. a blob it cannot read), or a snapshot it cannot restore.
    | InvalidValue of reason: string

    override this.ToString() =
        match this with
        | Busy running -> $"cell {running} is running"
        | DuplicateCell cellId -> $"cell {cellId} already ran"
        | InvalidCellId cellId -> $"invalid cell id {Json.quote cellId}"
        | UnknownCell cellId -> $"unknown cell {cellId}"
        | NotFound ref -> $"no {ref}"
        | NotEncodable(typeName, reason) -> $"a {typeName} cannot be encoded: {reason}"
        | TooLarge(bytes, limit) -> $"the value has {bytes} bytes of JSON, more than {limit}"
        | InvalidName name -> $"invalid name {Json.quote name}"
        | InvalidValue reason -> "invalid value: " + reason

/// `host/call` params: a cell calls a binding of the host bridge.
type HostCall =
    {
        /// The calling cell, if the kernel can tell (background work may not belong to one).
        CellId: string option
        /// The binding's dotted path, e.g. `shell.run`.
        Binding: string
        /// The arguments by position.
        Args: Value list
    }

    override this.ToString() = $"{this.Binding}/{this.Args.Length}"

/// The typed errors of `host/call`. The kernel throws them in the calling cell with the `kind` as
/// the error's code.
[<RequireQualifiedAccess>]
type HostCallError =
    | UnknownBinding of binding: string
    /// The arguments do not decode into the binding's declared types.
    | InvalidArguments of issues: Issue list
    /// A handle argument that this session did not issue.
    | UnknownHandle of handle: HandleId
    /// A grant refused it (spec §13).
    | Denied of reason: string
    /// The capability failed with one of its expected failures.
    | Failed of code: string * message: string * data: Json option

    override this.ToString() =
        match this with
        | UnknownBinding binding -> "unknown binding " + binding
        | InvalidArguments issues -> "invalid arguments:\n" + Issue.renderAtMost 10 issues
        | UnknownHandle handle -> $"unknown handle {handle}"
        | Denied reason -> "denied: " + reason
        | Failed(code, message, _) -> $"{code}: {message}"

/// `finish` params: a cell ends the turn with a result the host validates (spec §4.5, §6.5).
type Finish =
    {
        CellId: string option
        Result: Value
    }

    override this.ToString() = $"finish {this.Result}"

/// The typed error of `finish`: the result does not decode, or fails a check (`Ensure`).
type FinishRejected =
    {
        Issues: Issue list
    }

    override this.ToString() =
        "finish rejected:\n" + Issue.renderAtMost 10 this.Issues

[<RequireQualifiedAccess>]
type OutputKind =
    /// Text the cell printed.
    | Text
    /// An error that escaped the cell: thrown by background work, an unhandled rejection.
    | Error

    override this.ToString() =
        match this with
        | Text -> "text"
        | Error -> "error"

/// `cell/output` params.
type CellOutput =
    {
        /// The cell the output belongs to, if the kernel can tell.
        CellId: string option
        Kind: OutputKind
        Text: string
        /// Sent after the cell's result (background work).
        Late: bool
    }

    override this.ToString() =
        let cell = this.CellId |> Option.defaultValue "-"
        $"{this.Kind} of {cell} ({this.Text.Length} chars, late {this.Late})"

/// Something a cell did that the host cannot see in its own calls, reported by a kernel-side
/// wrapper (spec §6.5 "best-effort wrappers").
type Activity =
    {
        /// What kind of thing: `read`, `refs`, `edit`, `run`, `test`, `review`, `agent`, `applies`, or
        /// another word.
        Kind: string
        /// One line for the transcript.
        Title: string
        Detail: Json option
    }

    override this.ToString() = $"{this.Kind}: {this.Title}"

/// `cell/activity` params.
type CellActivity =
    {
        CellId: string option
        Activity: Activity
        Late: bool
    }

    override this.ToString() =
        let cell = this.CellId |> Option.defaultValue "-"
        $"{this.Activity} ({cell}, late {this.Late})"
