/// The kernel protocol (spec §6.4, docs/modules/kernel-protocol.md): how the host drives a CodeAct
/// kernel, in any language, over JSON-RPC 2.0 (`src/Fx.Rpc`) on the kernel process's standard input
/// and output. The host is the client: it starts the kernel, sends `initialize` with the `kernel`
/// member (`KernelSetup`), and the kernel answers with its own (`KernelInfo`). Every message is
/// defined here once, with its codecs; the TypeScript declarations for kernels come from the same
/// codecs (`KernelDeclarations`).
module Fx.Kernel.KernelProtocol

open System.Text.RegularExpressions
open Fx.Contracts
open Fx.Rpc
open Fx.Sdk

/// The kernel protocol versions this host speaks, preferred first.
let versions = [ "1" ]

/// The `initialize` member that carries `KernelSetup` (params) and `KernelInfo` (result).
[<Literal>]
let InitializeMember = "kernel"

/// Cell ids: 1 to 64 of `A-Z a-z 0-9 _ -`, so they can appear in file names and stack frames.
[<Literal>]
let CellIdPattern = "^[A-Za-z0-9_-]{1,64}$"

let private cellIdRegex = Regex(CellIdPattern, RegexOptions.CultureInvariant)

/// Whether a cell id is valid.
let isCellId (text: string) = cellIdRegex.IsMatch text

/// The host's defaults: 16 000 output characters (as the Bun kernel spike measured), the
/// contracts' preview limits, and 64 KiB inline. Their values are variants (spec §6.6).
let defaultLimits: KernelLimits =
    {
        OutputChars = 16000
        Preview = Limits.defaults
        InlineBytes = 64 * 1024
    }

module Codecs =
    let private issues = Codec.list Codecs.issue

    let private cellId =
        Codec.string
        |> Codec.pattern CellIdPattern
        |> Codec.describe "A cell id: 1 to 64 of A-Z a-z 0-9 _ -, unique per kernel."

    let private count = Codec.int |> Codec.atLeast 0

    // Interpreter caps are part of the public SDK (Fx.Sdk): interpreters declare them (spec §6.2).
    let asyncHostCalls: Codec<AsyncHostCalls> = AsyncHostCalls.codec
    let packageSupport: Codec<PackageSupport> = PackageSupport.codec
    let interpreterCaps: Codec<InterpreterCaps> = InterpreterCaps.codec

    let kernelLimits: Codec<KernelLimits> =
        Codec.record {
            let! output =
                Codec.field "outputChars" (fun (l: KernelLimits) -> l.OutputChars) (Codec.int |> Codec.atLeast 1)

            and! preview = Codec.field "preview" (fun l -> l.Preview) Codecs.limits

            and! inlineBytes =
                Codec.field "inlineBytes" (fun l -> l.InlineBytes) (Codec.int |> Codec.atLeast 1)

            return
                {
                    OutputChars = output
                    Preview = preview
                    InlineBytes = inlineBytes
                }
        }
        |> Codec.describe "Bounds of a kernel's output and previews."
        |> Codec.named "KernelLimits"

    let kernelSetup: Codec<KernelSetup> =
        Codec.record {
            let! versions =
                Codec.field
                    "versions"
                    (fun (s: KernelSetup) -> s.Versions)
                    (Codec.list Codec.string |> Codec.minItems 1)

            and! globalName =
                Codec.field "global" (fun s -> s.Global) (Codec.string |> Codec.nonEmpty)

            and! bindings =
                Codec.field "bindings" (fun s -> s.Bindings) (Codec.list Codec.string)

            and! moduleBase =
                Codec.optionalField "moduleBase" (fun s -> s.ModuleBase) Codec.string

            and! limits = Codec.field "limits" (fun s -> s.Limits) kernelLimits

            return
                {
                    Versions = versions
                    Global = globalName
                    Bindings = bindings
                    ModuleBase = moduleBase
                    Limits = limits
                }
        }
        |> Codec.describe "The host's `kernel` member of the `initialize` params."
        |> Codec.named "KernelSetup"

    let kernelInfo: Codec<KernelInfo> =
        Codec.record {
            let! version = Codec.field "version" (fun (i: KernelInfo) -> i.Version) Codec.string

            and! language =
                Codec.field "language" (fun i -> i.Language) (Codec.string |> Codec.nonEmpty)

            and! caps = Codec.field "caps" (fun i -> i.Caps) interpreterCaps
            and! details = Codec.optionalField "details" (fun i -> i.Details) Codec.json

            return
                {
                    Version = version
                    Language = language
                    Caps = caps
                    Details = details
                }
        }
        |> Codec.describe "The kernel's `kernel` member of the `initialize` result."
        |> Codec.named "KernelInfo"

    let cellRun: Codec<CellRun> =
        Codec.record {
            let! id = Codec.field "cellId" (fun (r: CellRun) -> r.CellId) cellId
            and! code = Codec.field "code" (fun r -> r.Code) Codec.string
            and! intent = Codec.optionalField "intent" (fun r -> r.Intent) Codec.string

            and! timeout =
                Codec.optionalField "timeoutMs" (fun r -> r.TimeoutMs) (Codec.int |> Codec.atLeast 1)

            and! check = Codec.defaultField "check" (fun r -> r.Check) false Codec.bool

            return
                {
                    CellId = id
                    Code = code
                    Intent = intent
                    TimeoutMs = timeout
                    Check = check
                }
        }
        |> Codec.describe "Runs a cell."
        |> Codec.named "CellRun"

    let boundedText: Codec<BoundedText> =
        Codec.record {
            let! text = Codec.field "text" (fun (t: BoundedText) -> t.Text) Codec.string
            and! truncated = Codec.field "truncated" (fun t -> t.Truncated) Codec.bool

            and! total =
                Codec.field "totalChars" (fun t -> t.TotalChars) (Codec.int64 |> Codec.atLeast 0L)

            return
                {
                    Text = text
                    Truncated = truncated
                    TotalChars = total
                }
        }
        |> Codec.describe "Text kept within a bound: its head and tail, and how much there was."
        |> Codec.named "BoundedText"

    let cellLocation: Codec<CellLocation> =
        Codec.record {
            let! id = Codec.field "cellId" (fun (l: CellLocation) -> l.CellId) Codec.string
            and! line = Codec.field "line" (fun l -> l.Line) (Codec.int |> Codec.atLeast 1)
            and! column = Codec.field "column" (fun l -> l.Column) (Codec.int |> Codec.atLeast 0)

            return
                {
                    CellId = id
                    Line = line
                    Column = column
                }
        }
        |> Codec.describe "A 1-based position in a cell."
        |> Codec.named "CellLocation"

    let diagnostic: Codec<Diagnostic> =
        Codec.record {
            let! line = Codec.field "line" (fun (d: Diagnostic) -> d.Line) count
            and! column = Codec.field "column" (fun d -> d.Column) count
            and! code = Codec.field "code" (fun d -> d.Code) Codec.string
            and! message = Codec.field "message" (fun d -> d.Message) Codec.string

            return
                {
                    Line = line
                    Column = column
                    Code = code
                    Message = message
                }
        }
        |> Codec.describe "A type-checking diagnostic; line 0 is code carried over from earlier cells."
        |> Codec.named "Diagnostic"

    let syntaxFailure: Codec<SyntaxFailure> =
        Codec.record {
            let! message =
                Codec.field "message" (fun (s: SyntaxFailure) -> s.Message) Codec.string

            and! line = Codec.optionalField "line" (fun s -> s.Line) count
            and! column = Codec.optionalField "column" (fun s -> s.Column) count
            and! lineText = Codec.optionalField "lineText" (fun s -> s.LineText) Codec.string

            return
                {
                    Message = message
                    Line = line
                    Column = column
                    LineText = lineText
                }
        }

    let runtimeFailure: Codec<RuntimeFailure> =
        Codec.record {
            let! name = Codec.field "name" (fun (r: RuntimeFailure) -> r.Name) Codec.string
            and! message = Codec.field "message" (fun r -> r.Message) Codec.string
            and! code = Codec.optionalField "code" (fun r -> r.Code) Codec.string
            and! location = Codec.optionalField "location" (fun r -> r.Location) cellLocation
            and! stack = Codec.field "stack" (fun r -> r.Stack) Codec.string

            return
                {
                    Name = name
                    Message = message
                    Code = code
                    Location = location
                    Stack = stack
                }
        }

    let cellOutcome: Codec<CellOutcome> =
        Codec.union "kind" {
            let! completed =
                Codec.case1 "completed" CellOutcome.Completed (Field.optional "value" ValueCodecs.valueInfo)

            and! syntax = Codec.caseOf "syntaxError" CellOutcome.SyntaxError syntaxFailure

            and! typeError =
                Codec.case1 "typeError" CellOutcome.TypeError (Field.required "diagnostics" (Codec.list diagnostic))

            and! runtime = Codec.caseOf "runtimeError" CellOutcome.RuntimeError runtimeFailure

            and! timedOut =
                Codec.case1 "timedOut" CellOutcome.TimedOut (Field.required "afterMs" count)

            and! cancelled = Codec.case0 "cancelled" CellOutcome.Cancelled

            return
                fun outcome ->
                    match outcome with
                    | CellOutcome.Completed value -> completed value
                    | CellOutcome.SyntaxError failure -> syntax failure
                    | CellOutcome.TypeError diagnostics -> typeError diagnostics
                    | CellOutcome.RuntimeError failure -> runtime failure
                    | CellOutcome.TimedOut after -> timedOut after
                    | CellOutcome.Cancelled -> cancelled
        }
        |> Codec.describeCase "completed" "It ran to the end; the value of its last expression, if any."
        |> Codec.describeCase "timedOut" "It ran longer than its timeoutMs."
        |> Codec.describeCase "cancelled" "cell/cancel stopped it."
        |> Codec.describe "How a cell ended."
        |> Codec.named "CellOutcome"

    let cellResult: Codec<CellResult> =
        Codec.record {
            let! id = Codec.field "cellId" (fun (r: CellResult) -> r.CellId) Codec.string
            and! outcome = Codec.field "outcome" (fun r -> r.Outcome) cellOutcome
            and! output = Codec.field "output" (fun r -> r.Output) boundedText

            and! declared =
                Codec.defaultField "declared" (fun r -> r.Declared) [] (Codec.list Codec.string)

            and! finished = Codec.defaultField "finished" (fun r -> r.Finished) false Codec.bool
            and! notified = Codec.defaultField "notified" (fun r -> r.Notified) 0 count

            and! duration =
                Codec.defaultField "durationMs" (fun r -> r.DurationMs) 0L (Codec.int64 |> Codec.atLeast 0L)

            return
                {
                    CellId = id
                    Outcome = outcome
                    Output = output
                    Declared = declared
                    Finished = finished
                    Notified = notified
                    DurationMs = duration
                }
        }
        |> Codec.describe "How a cell ran."
        |> Codec.named "CellResult"

    let cellRef: Codec<CellRef> =
        Codec.record {
            let! id = Codec.field "cellId" (fun (r: CellRef) -> r.CellId) Codec.string
            return { CellId = id }
        }
        |> Codec.named "CellRef"

    let cellCheck: Codec<CellCheck> =
        Codec.record {
            let! id = Codec.field "cellId" (fun (c: CellCheck) -> c.CellId) cellId
            and! code = Codec.field "code" (fun c -> c.Code) Codec.string
            return { CellId = id; Code = code }
        }
        |> Codec.describe "Type-checks a cell without running it."
        |> Codec.named "CellCheck"

    let cancelled: Codec<Cancelled> =
        Codec.record {
            let! running =
                Codec.field "wasRunning" (fun (c: Cancelled) -> c.WasRunning) Codec.bool

            return { WasRunning = running }
        }
        |> Codec.named "Cancelled"

    let checkedCell: Codec<Checked> =
        Codec.record {
            let! diagnostics =
                Codec.field "diagnostics" (fun (c: Checked) -> c.Diagnostics) (Codec.list diagnostic)

            return { Diagnostics = diagnostics }
        }
        |> Codec.named "Checked"

    let previewParams: Codec<PreviewParams> =
        Codec.record {
            let! ref = Codec.field "ref" (fun (p: PreviewParams) -> p.Ref) ValueCodecs.valueRef
            and! limits = Codec.optionalField "limits" (fun p -> p.Limits) Codecs.limits
            return { Ref = ref; Limits = limits }
        }
        |> Codec.describe "Previews a value the kernel holds."
        |> Codec.named "PreviewParams"

    let encodeParams: Codec<EncodeParams> =
        Codec.record {
            let! ref = Codec.field "ref" (fun (p: EncodeParams) -> p.Ref) ValueCodecs.valueRef

            and! maxBytes =
                Codec.optionalField "maxBytes" (fun p -> p.MaxBytes) (Codec.int |> Codec.atLeast 1)

            return { Ref = ref; MaxBytes = maxBytes }
        }
        |> Codec.describe "Encodes a value the kernel holds."
        |> Codec.named "EncodeParams"

    let decodeParams: Codec<DecodeParams> =
        Codec.record {
            let! name =
                Codec.field "name" (fun (p: DecodeParams) -> p.Name) (Codec.string |> Codec.nonEmpty)

            and! value = Codec.field "value" (fun p -> p.Value) ValueCodecs.value
            and! typeName = Codec.optionalField "type" (fun p -> p.Type) Codec.string

            return
                {
                    Name = name
                    Value = value
                    Type = typeName
                }
        }
        |> Codec.describe "Binds a value to a top-level variable."
        |> Codec.named "DecodeParams"

    let recallParams: Codec<RecallParams> =
        Codec.record {
            let! id = Codec.field "cellId" (fun (p: RecallParams) -> p.CellId) cellId
            and! value = Codec.field "value" (fun p -> p.Value) ValueCodecs.value
            return { CellId = id; Value = value }
        }
        |> Codec.describe "Restores the completion value of a cell (Out[cellId])."
        |> Codec.named "RecallParams"

    let kernelState: Codec<KernelState> =
        Codec.record {
            let! state = Codec.field "state" (fun (s: KernelState) -> s.State) ValueCodecs.value
            return { State = state }
        }
        |> Codec.describe "A kernel's state, in its own format."
        |> Codec.named "KernelState"

    let kernelError: Codec<KernelError> =
        Codec.union "kind" {
            let! busy =
                Codec.case1 "busy" KernelError.Busy (Field.required "running" Codec.string)

            and! duplicate =
                Codec.case1 "duplicateCell" KernelError.DuplicateCell (Field.required "cellId" Codec.string)

            and! invalidId =
                Codec.case1 "invalidCellId" KernelError.InvalidCellId (Field.required "cellId" Codec.string)

            and! unknown =
                Codec.case1 "unknownCell" KernelError.UnknownCell (Field.required "cellId" Codec.string)

            and! notFound =
                Codec.case1 "notFound" KernelError.NotFound (Field.required "ref" ValueCodecs.valueRef)

            and! notEncodable =
                Codec.case2
                    "notEncodable"
                    (fun typeName reason -> KernelError.NotEncodable(typeName, reason))
                    (Field.required "type" Codec.string)
                    (Field.required "reason" Codec.string)

            and! tooLarge =
                Codec.case2
                    "tooLarge"
                    (fun bytes limit -> KernelError.TooLarge(bytes, limit))
                    (Field.required "bytes" Codec.int64)
                    (Field.required "limit" Codec.int)

            and! invalidName =
                Codec.case1 "invalidName" KernelError.InvalidName (Field.required "name" Codec.string)

            and! invalidValue =
                Codec.case1 "invalidValue" KernelError.InvalidValue (Field.required "reason" Codec.string)

            return
                fun error ->
                    match error with
                    | KernelError.Busy running -> busy running
                    | KernelError.DuplicateCell id -> duplicate id
                    | KernelError.InvalidCellId id -> invalidId id
                    | KernelError.UnknownCell id -> unknown id
                    | KernelError.NotFound ref -> notFound ref
                    | KernelError.NotEncodable(typeName, reason) -> notEncodable typeName reason
                    | KernelError.TooLarge(bytes, limit) -> tooLarge bytes limit
                    | KernelError.InvalidName name -> invalidName name
                    | KernelError.InvalidValue reason -> invalidValue reason
        }
        |> Codec.describe "The typed errors of the methods the host calls on a kernel."
        |> Codec.named "KernelError"

    let hostCall: Codec<HostCall> =
        Codec.record {
            let! id = Codec.optionalField "cellId" (fun (c: HostCall) -> c.CellId) Codec.string

            and! binding =
                Codec.field "binding" (fun c -> c.Binding) (Codec.string |> Codec.nonEmpty)

            and! args =
                Codec.defaultField "args" (fun c -> c.Args) [] (Codec.list ValueCodecs.value)

            return
                {
                    CellId = id
                    Binding = binding
                    Args = args
                }
        }
        |> Codec.describe "A cell calls a binding of the host bridge."
        |> Codec.named "HostCall"

    let hostCallError: Codec<HostCallError> =
        Codec.union "kind" {
            let! unknownBinding =
                Codec.case1 "unknownBinding" HostCallError.UnknownBinding (Field.required "binding" Codec.string)

            and! invalidArguments =
                Codec.case1 "invalidArguments" HostCallError.InvalidArguments (Field.required "issues" issues)

            and! unknownHandle =
                Codec.case1 "unknownHandle" HostCallError.UnknownHandle (Field.required "handle" ValueCodecs.handleId)

            and! denied =
                Codec.case1 "denied" HostCallError.Denied (Field.required "reason" Codec.string)

            and! failed =
                Codec.case3
                    "failed"
                    (fun code message data -> HostCallError.Failed(code, message, data))
                    (Field.required "code" Codec.string)
                    (Field.required "message" Codec.string)
                    (Field.optional "data" Codec.json)

            return
                fun error ->
                    match error with
                    | HostCallError.UnknownBinding binding -> unknownBinding binding
                    | HostCallError.InvalidArguments i -> invalidArguments i
                    | HostCallError.UnknownHandle handle -> unknownHandle handle
                    | HostCallError.Denied reason -> denied reason
                    | HostCallError.Failed(code, message, data) -> failed code message data
        }
        |> Codec.describe "The typed errors of host/call; the cell gets an error whose code is the kind."
        |> Codec.named "HostCallError"

    let finish: Codec<Finish> =
        Codec.record {
            let! id = Codec.optionalField "cellId" (fun (f: Finish) -> f.CellId) Codec.string
            and! result = Codec.field "result" (fun f -> f.Result) ValueCodecs.value
            return { CellId = id; Result = result }
        }
        |> Codec.describe "A cell ends the turn with a result for the host to validate."
        |> Codec.named "Finish"

    let finishRejected: Codec<FinishRejected> =
        Codec.record {
            let! issues = Codec.field "issues" (fun (r: FinishRejected) -> r.Issues) issues
            return { Issues = issues }
        }
        |> Codec.describe "The result does not decode, or fails a check; the issues say why."
        |> Codec.named "FinishRejected"

    let outputKind: Codec<OutputKind> =
        Codec.enum [ OutputKind.Text, "text"; OutputKind.Error, "error" ]
        |> Codec.named "OutputKind"

    let cellOutput: Codec<CellOutput> =
        Codec.record {
            let! id = Codec.optionalField "cellId" (fun (o: CellOutput) -> o.CellId) Codec.string
            and! kind = Codec.field "kind" (fun o -> o.Kind) outputKind
            and! text = Codec.field "text" (fun o -> o.Text) Codec.string
            and! late = Codec.defaultField "late" (fun o -> o.Late) false Codec.bool

            return
                {
                    CellId = id
                    Kind = kind
                    Text = text
                    Late = late
                }
        }
        |> Codec.describe "Output of a cell, as it happens; late output came after the cell's result."
        |> Codec.named "CellOutput"

    let activity: Codec<Activity> =
        Codec.record {
            let! kind =
                Codec.field "kind" (fun (a: Activity) -> a.Kind) (Codec.string |> Codec.nonEmpty)

            and! title = Codec.field "title" (fun a -> a.Title) Codec.string
            and! detail = Codec.optionalField "detail" (fun a -> a.Detail) Codec.json

            return
                {
                    Kind = kind
                    Title = title
                    Detail = detail
                }
        }
        |> Codec.describe "Something a cell did, reported by a kernel-side wrapper."
        |> Codec.named "Activity"

    let cellActivity: Codec<CellActivity> =
        Codec.record {
            let! id =
                Codec.optionalField "cellId" (fun (a: CellActivity) -> a.CellId) Codec.string

            and! value = Codec.field "activity" (fun a -> a.Activity) activity
            and! late = Codec.defaultField "late" (fun a -> a.Late) false Codec.bool

            return
                {
                    CellId = id
                    Activity = value
                    Late = late
                }
        }
        |> Codec.named "CellActivity"

// ---- Host to kernel ----------------------------------------------------------------------------

/// Runs a cell. Refused with `Busy` while another cell runs. Cancelling the request
/// (`$/cancelRequest`) interrupts the cell as `cell/cancel` does.
let cellRun =
    Method.define "cell/run" Codecs.cellRun Codecs.cellResult
    |> Method.withErrors Codecs.kernelError

/// Interrupts a running cell, which then ends `Cancelled`, and stops what any cell left running.
let cellCancel =
    Method.define "cell/cancel" Codecs.cellRef Codecs.cancelled
    |> Method.withErrors Codecs.kernelError

/// Optional: type-checks a cell without running it.
let cellCheck =
    Method.define "cell/check" Codecs.cellCheck Codecs.checkedCell
    |> Method.withErrors Codecs.kernelError

/// A bounded preview of a value the kernel holds.
let valuePreview =
    Method.define "value/preview" Codecs.previewParams ValueCodecs.valueInfo
    |> Method.withErrors Codecs.kernelError

/// A value the kernel holds, as a `Value`: inline JSON within the limit, or the handle it holds.
let valueEncode =
    Method.define "value/encode" Codecs.encodeParams ValueCodecs.value
    |> Method.withErrors Codecs.kernelError

/// Binds a value to a top-level variable.
let valueDecode =
    Method.define "value/decode" Codecs.decodeParams ValueCodecs.valueInfo
    |> Method.withErrors Codecs.kernelError

/// Restores the completion value of a cell (`Out[cellId]`).
let recall =
    Method.define "recall" Codecs.recallParams ValueCodecs.valueInfo
    |> Method.withErrors Codecs.kernelError

/// Optional (`InterpreterCaps.Snapshot`): the kernel's state, serialized.
let snapshot =
    Method.define "snapshot" Codec.unit Codecs.kernelState
    |> Method.withErrors Codecs.kernelError

/// Optional (`InterpreterCaps.Snapshot`): replaces the kernel's state with a snapshot's.
let restore =
    Method.define "restore" Codecs.kernelState Codec.unit
    |> Method.withErrors Codecs.kernelError

// ---- Kernel to host ----------------------------------------------------------------------------

/// A cell calls a host binding. The kernel cancels it (`$/cancelRequest`) when the cell is cancelled.
let hostCall =
    Method.define "host/call" Codecs.hostCall ValueCodecs.value
    |> Method.withErrors Codecs.hostCallError

/// A cell ends the turn. The host validates the result (codecs and `Ensure`); a rejection throws in
/// the cell.
let finish =
    Method.define "finish" Codecs.finish Codec.unit
    |> Method.withErrors Codecs.finishRejected

/// Output of a cell, streamed as it happens (bounded by `OutputChars` per cell), and late output.
let cellOutput = Notification.define "cell/output" Codecs.cellOutput

/// Activity of a cell, from kernel-side wrappers.
let cellActivity = Notification.define "cell/activity" Codecs.cellActivity

/// The methods every kernel handles.
let requiredMethods =
    [
        cellRun.Name
        cellCancel.Name
        valuePreview.Name
        valueEncode.Name
        valueDecode.Name
        recall.Name
    ]

/// The methods a kernel may handle; `snapshot` and `restore` go with `InterpreterCaps.Snapshot`.
let optionalMethods = [ cellCheck.Name; snapshot.Name; restore.Name ]

/// The methods and notifications the host handles.
let hostMethods = [ hostCall.Name; finish.Name; cellOutput.Name; cellActivity.Name ]

/// Validating `finish` on the host (spec §4.5, §6.5): codecs, then checks.
module Finish =
    /// Decodes a `finish` result into its declared type, then runs the checks (`Ensure`), each giving
    /// the issues it finds. Evidence stays the values the cell passed: nothing is retyped.
    let validate (codec: Codec<'T>) (ensure: ('T -> Issue list) list) (finish: Finish) : Result<'T, FinishRejected> =
        match Value.decode codec finish.Result with
        | Error issues ->
            Error
                {
                    Issues = issues |> List.map (Issue.under (PathSegment.Key "result"))
                }
        | Ok value ->
            match ensure |> List.collect (fun check -> check value) with
            | [] -> Ok value
            | issues -> Error { Issues = issues }
