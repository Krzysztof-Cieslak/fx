/// A kernel written in F#, speaking the kernel protocol (docs/modules/kernel-protocol.md) over
/// `Fx.Rpc`: the second implementation of the protocol next to the Bun reference kernel, so the
/// conformance suite (tests/Fx.Kernel.Tests) proves it is language-neutral, and the subject of the
/// `kernel-*` scenarios of the JIT-vs-native diff, which drive the host's `Kernel` client against it
/// in process and as a child process (this program's `kernel-peer` command).
///
/// Its language is a script, one statement per line (`#` starts a comment):
///
/// | Statement | Does |
/// |---|---|
/// | `let x = <json>` | binds `x` |
/// | `x` | the value of `x` (a completion value if last) |
/// | `value <json>` | a literal value |
/// | `print <text>` | prints a line |
/// | `call x = <binding> [<args>]` | a host call; an argument `"$y"` passes the value of `y` |
/// | `finish <json>` / `finish $x` | ends the turn |
/// | `throw <message>` | throws |
/// | `sleep` | waits until cancelled |
/// | `loop` | a busy loop that the kernel can interrupt |
/// | `spin` | a busy loop that blocks the whole kernel: only a kill stops it |
/// | `later <text>` | prints after the cell returned |
/// | `activity <kind> <title>` | reports an activity |
/// | `env x = <VARIABLE>` | binds an environment variable (or null) |
/// | `stray <text>` | writes to standard output past the protocol (in a process) |
/// | `out x = <cell id>` | binds `Out[cellId]` |
///
/// It has snapshots, is deterministic (durations are 0), and makes host calls look synchronous.
module Fx.Aot.Scenarios.Kernel.ScriptKernel

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Kernel
open Fx.Aot.Scenarios.Rpc.TestService

[<RequireQualifiedAccess>]
type Statement =
    | Let of name: string * value: Json
    | Get of name: string
    | Literal of Json
    | Print of string
    | Call of name: string * binding: string * args: Json list
    | Finish of Json
    | FinishVar of name: string
    | Throw of string
    | Sleep
    | Loop
    | Spin
    | Later of string
    | Activity of kind: string * title: string
    | Env of name: string * variable: string
    | Stray of string
    | Out of name: string * cellId: string

    override this.ToString() =
        match this with
        | Let(name, _) -> "let " + name
        | Get name -> name
        | Literal _ -> "value"
        | Print _ -> "print"
        | Call(name, binding, _) -> $"call {name} = {binding}"
        | Finish _ -> "finish"
        | FinishVar name -> "finish $" + name
        | Throw _ -> "throw"
        | Sleep -> "sleep"
        | Loop -> "loop"
        | Spin -> "spin"
        | Later _ -> "later"
        | Activity(kind, _) -> "activity " + kind
        | Env(name, _) -> "env " + name
        | Stray _ -> "stray"
        | Out(name, _) -> "out " + name

let private identifier = "[A-Za-z_][A-Za-z0-9_]*"
let private letPattern = Regex($"^let\\s+({identifier})\\s*=\\s*(.+)$")

let private callPattern =
    Regex($"^call\\s+({identifier})\\s*=\\s*([A-Za-z_][A-Za-z0-9_.]*)\\s*(.*)$")

let private bindPattern = Regex($"^(env|out)\\s+({identifier})\\s*=\\s*(\\S+)$")
let private namePattern = Regex($"^{identifier}$")

let private rest (line: string) (word: string) = line.Substring(word.Length).TrimStart()

/// Parses a cell: its statements with their 1-based lines, or the first syntax error.
let parse (code: string) : Result<(int * Statement) list, SyntaxFailure> =
    let lines = code.Replace("\r\n", "\n").Split '\n'
    let statements = ResizeArray()
    let error = ref None
    let mutable index = 0

    let fail (line: int) (text: string) (message: string) =
        error.Value <-
            Some
                {
                    Message = message
                    Line = Some line
                    Column = Some 1
                    LineText = Some text
                }

    let json (line: int) (raw: string) (text: string) =
        match Json.parse text with
        | Ok value -> Some value
        | Error e ->
            fail line raw ("invalid JSON: " + string e)
            None

    while error.Value.IsNone && index < lines.Length do
        let raw = lines[index]
        let line = raw.Trim()
        let number = index + 1
        index <- index + 1
        let word = line.Split(' ', 2)[0]

        let add (statement: Statement) = statements.Add((number, statement))

        if line = "" || line.StartsWith '#' then
            ()
        else
            match word with
            | "let" ->
                let m = letPattern.Match line

                if m.Success then
                    json number raw m.Groups[2].Value
                    |> Option.iter (fun value -> add (Statement.Let(m.Groups[1].Value, value)))
                else
                    fail number raw "expected let <name> = <json>"
            | "value" -> json number raw (rest line word) |> Option.iter (Statement.Literal >> add)
            | "print" -> add (Statement.Print(rest line word))
            | "call" ->
                let m = callPattern.Match line

                if not m.Success then
                    fail number raw "expected call <name> = <binding> [<args>]"
                else
                    let args = if m.Groups[3].Value = "" then "[]" else m.Groups[3].Value

                    match json number raw args with
                    | Some(Json.Array items) -> add (Statement.Call(m.Groups[1].Value, m.Groups[2].Value, items))
                    | Some _ -> fail number raw "the arguments must be a JSON array"
                    | None -> ()
            | "finish" ->
                let argument = rest line word

                if argument.StartsWith '$' then
                    add (Statement.FinishVar(argument.Substring 1))
                else
                    json number raw argument |> Option.iter (Statement.Finish >> add)
            | "throw" -> add (Statement.Throw(rest line word))
            | "sleep" when line = word -> add Statement.Sleep
            | "loop" when line = word -> add Statement.Loop
            | "spin" when line = word -> add Statement.Spin
            | "later" -> add (Statement.Later(rest line word))
            | "activity" ->
                match (rest line word).Split(' ', 2) with
                | [| kind; title |] when kind <> "" -> add (Statement.Activity(kind, title))
                | _ -> fail number raw "expected activity <kind> <title>"
            | "stray" -> add (Statement.Stray(rest line word))
            | "env"
            | "out" ->
                let m = bindPattern.Match line

                if not m.Success then
                    fail number raw $"expected {word} <name> = <value>"
                elif word = "env" then
                    add (Statement.Env(m.Groups[2].Value, m.Groups[3].Value))
                else
                    add (Statement.Out(m.Groups[2].Value, m.Groups[3].Value))
            | _ when namePattern.IsMatch line -> add (Statement.Get line)
            | _ -> fail number raw $"unknown statement {Json.quote word}"

    match error.Value with
    | Some e -> Error e
    | None -> Ok(List.ofSeq statements)

/// A cell's output: head and tail within the limit, as kernels keep it.
type private OutputBuffer(limit: int) =
    let head = StringBuilder()
    let mutable tail = ""
    let mutable total = 0L

    member _.Append(text: string) =
        total <- total + int64 text.Length
        let headLimit = (limit + 1) / 2
        let take = min text.Length (max 0 (headLimit - head.Length))
        head.Append(text, 0, take) |> ignore
        let rest = text.Substring take

        if rest <> "" then
            let combined = tail + rest
            let tailLimit = limit - headLimit
            tail <- combined.Substring(max 0 (combined.Length - tailLimit))

    member _.Take() : BoundedText =
        let kept = int64 head.Length + int64 tail.Length
        let truncated = total > kept

        {
            Text =
                if truncated then
                    $"{head}\n… [{total - kept} chars omitted] …\n{tail}"
                else
                    string head + tail
            Truncated = truncated
            TotalChars = total
        }

exception private CellStopped of RuntimeFailure

/// The kernel. One instance per connection.
type Kernel(stray: string -> unit) =
    let gate = obj ()
    let variables = Dictionary<string, Value>(StringComparer.Ordinal)
    let outputs = Dictionary<string, Value>(StringComparer.Ordinal)
    let ran = HashSet<string>(StringComparer.Ordinal)
    let mutable running: (string * CancellationTokenSource) option = None
    let mutable setup: KernelSetup option = None

    /// Completes when the kernel is killed; a `spin` blocks every handler on it until then.
    let killed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable frozen = false

    let limits () =
        match setup with
        | Some s -> s.Limits
        | None -> KernelProtocol.defaultLimits

    let info: KernelInfo =
        {
            Version = "1"
            Language = "script"
            Caps =
                {
                    Fx.Sdk.InterpreterCaps.Snapshot = true
                    Fx.Sdk.InterpreterCaps.AsyncHostCalls = Fx.Sdk.AsyncHostCalls.Transparent
                    Fx.Sdk.InterpreterCaps.Packages = None
                    Fx.Sdk.InterpreterCaps.Deterministic = true
                }
            Details = Some(Json.Object [ "implementation", Json.String "ScriptKernel" ])
        }

    let valueInfo (preview: Limits) (value: Value) : ValueInfo =
        match value with
        | Value.Inline json ->
            {
                Type = Json.kind json
                Preview = Preview.ofJson preview json
            }
        | Value.Blob _ ->
            {
                Type = "blob"
                Preview = Value.preview preview value
            }
        | Value.Handle(_, typeRef, p) ->
            {
                Type = typeRef.Name
                Preview = Preview.clamp preview p
            }

    /// Every handler first waits here while a `spin` blocks the kernel.
    let unfrozen () : Task =
        if lock gate (fun () -> frozen) then
            killed.Task
        else
            Task.CompletedTask

    let lookup (ref: ValueRef) : Value option =
        lock gate (fun () ->
            match ref with
            | ValueRef.Variable name ->
                match variables.TryGetValue name with
                | true, v -> Some v
                | _ -> None
            | ValueRef.Output cellId ->
                match outputs.TryGetValue cellId with
                | true, v -> Some v
                | _ -> None)

    let runtime (name: string) (message: string) (code: string option) (line: int) (cellId: string) =
        {
            Name = name
            Message = message
            Code = code
            Location =
                Some
                    {
                        CellId = cellId
                        Line = line
                        Column = 1
                    }
            Stack = $"{name}: {message}\n    at cell {cellId}:{line}:1"
        }

    let notify (env: Env) (peer: Peer) (n: Notification<'P>) (value: 'P) : Task =
        task {
            let! _ = Rpc.notify peer n value env
            ()
        }

    let snapshotCodec: Codec<(string * Value) list * (string * Value) list> =
        Codec.record {
            let! vars =
                Codec.field "variables" (fun (v: (string * Value) list, _) -> v) (Codec.keyValues ValueCodecs.value)

            and! outs =
                Codec.field "outputs" (fun (_, o) -> o) (Codec.keyValues ValueCodecs.value)

            return vars, outs
        }

    /// Runs the statements; returns the completion value, or throws `CellStopped` or a cancellation.
    let execute
        (env: Env)
        (peer: Peer)
        (cellId: string)
        (statements: (int * Statement) list)
        (output: OutputBuffer)
        (stream: string -> Task)
        (report: Activity -> Task)
        (finished: bool ref)
        (cancellation: CancellationToken)
        : Task<Value option> =
        task {
            let mutable last = None

            let callEnv =
                { env with
                    Scope =
                        { env.Scope with
                            Cancellation = cancellation
                        }
                }

            for line, statement in statements do
                cancellation.ThrowIfCancellationRequested()

                let stop name message code =
                    raise (CellStopped(runtime name message code line cellId))

                let get (name: string) =
                    match lookup (ValueRef.Variable name) with
                    | Some v -> v
                    | None -> stop "ReferenceError" $"{name} is not defined" None

                let bind (name: string) (value: Value) =
                    lock gate (fun () -> variables[name] <- value)

                last <- None

                match statement with
                | Statement.Let(name, json) -> bind name (Value.Inline json)
                | Statement.Get name -> last <- Some(get name)
                | Statement.Literal json -> last <- Some(Value.Inline json)
                | Statement.Print text ->
                    output.Append(text + "\n")
                    do! stream (text + "\n")
                | Statement.Call(name, binding, args) ->
                    let values =
                        args
                        |> List.map (fun arg ->
                            match arg with
                            | Json.String s when s.StartsWith '$' -> get (s.Substring 1)
                            | json -> Value.Inline json)

                    let! outcome =
                        Rpc.call
                            peer
                            KernelProtocol.hostCall
                            {
                                CellId = Some cellId
                                Binding = binding
                                Args = values
                            }
                            callEnv

                    match outcome with
                    | Ok value -> bind name value
                    | Error(CallError.Failed e) ->
                        let code =
                            match KernelProtocol.Codecs.hostCallError.Encode e with
                            | Json.Object members ->
                                match List.tryFind (fun (k, _) -> k = "kind") members with
                                | Some(_, Json.String kind) -> Some kind
                                | _ -> None
                            | _ -> None

                        stop "HostError" (string e) code
                    | Error e -> stop "HostError" (string e) None
                | Statement.Finish _
                | Statement.FinishVar _ ->
                    let result =
                        match statement with
                        | Statement.FinishVar name -> get name
                        | Statement.Finish json -> Value.Inline json
                        | _ -> Value.Inline Json.Null

                    let! outcome =
                        Rpc.call
                            peer
                            KernelProtocol.finish
                            {
                                CellId = Some cellId
                                Result = result
                            }
                            callEnv

                    match outcome with
                    | Ok() -> finished.Value <- true
                    | Error(CallError.Failed rejected) ->
                        stop "FinishRejected" (string rejected) (Some "finishRejected")
                    | Error e -> stop "FinishRejected" (string e) None
                | Statement.Throw message -> stop "Error" message None
                | Statement.Sleep -> do! Task.Delay(Timeout.Infinite, cancellation)
                | Statement.Loop ->
                    while not cancellation.IsCancellationRequested do
                        Thread.SpinWait 1000

                    cancellation.ThrowIfCancellationRequested()
                | Statement.Spin ->
                    lock gate (fun () -> frozen <- true)
                    do! killed.Task
                    raise (OperationCanceledException "killed")
                | Statement.Later text ->
                    Task.Run(
                        Func<Task>(fun () ->
                            task {
                                do! Task.Delay 30

                                do!
                                    notify
                                        { env with Scope = Scope.root }
                                        peer
                                        KernelProtocol.cellOutput
                                        {
                                            CellId = Some cellId
                                            Kind = OutputKind.Text
                                            Text = text + "\n"
                                            Late = true
                                        }
                            })
                    )
                    |> ignore
                | Statement.Activity(kind, title) ->
                    do!
                        report
                            {
                                Kind = kind
                                Title = title
                                Detail = None
                            }
                | Statement.Env(name, variable) ->
                    let value =
                        match Environment.GetEnvironmentVariable variable with
                        | null -> Json.Null
                        | text -> Json.String text

                    bind name (Value.Inline value)
                | Statement.Stray text -> stray text
                | Statement.Out(name, id) ->
                    match lookup (ValueRef.Output id) with
                    | Some v -> bind name v
                    | None -> stop "ReferenceError" $"Out[{id}] is not defined" None

                match statement with
                | Statement.Get _
                | Statement.Literal _ -> ()
                | _ -> last <- None

            return last
        }

    let declared (statements: (int * Statement) list) =
        statements
        |> List.choose (fun (_, s) ->
            match s with
            | Statement.Let(name, _)
            | Statement.Call(name, _, _)
            | Statement.Env(name, _)
            | Statement.Out(name, _) -> Some name
            | _ -> None)
        |> List.distinct

    let runCell (context: RequestContext) (cell: CellRun) : Eff<Env, Result<CellResult, KernelError>> =
        fun env ->
            task {
                do! unfrozen ()

                let refused =
                    lock gate (fun () ->
                        if not (KernelProtocol.isCellId cell.CellId) then
                            Some(KernelError.InvalidCellId cell.CellId)
                        elif ran.Contains cell.CellId then
                            Some(KernelError.DuplicateCell cell.CellId)
                        else
                            match running with
                            | Some(id, _) -> Some(KernelError.Busy id)
                            | None -> None)

                match refused with
                | Some e -> return Error e
                | None ->
                    let limits = limits ()
                    let cancel = new CancellationTokenSource()
                    use timeout = new CancellationTokenSource()

                    lock gate (fun () ->
                        ran.Add cell.CellId |> ignore
                        running <- Some(cell.CellId, cancel))

                    let output = OutputBuffer limits.OutputChars
                    let streamed = ref 0
                    let notified = ref 0
                    let finished = ref false

                    let report (activity: Activity) : Task =
                        notified.Value <- notified.Value + 1

                        notify
                            env
                            context.Peer
                            KernelProtocol.cellActivity
                            {
                                CellId = Some cell.CellId
                                Activity = activity
                                Late = false
                            }

                    let stream (text: string) : Task =
                        if streamed.Value >= limits.OutputChars then
                            Task.CompletedTask
                        else
                            let part = text.Substring(0, min text.Length (limits.OutputChars - streamed.Value))

                            streamed.Value <- streamed.Value + part.Length
                            notified.Value <- notified.Value + 1

                            notify
                                env
                                context.Peer
                                KernelProtocol.cellOutput
                                {
                                    CellId = Some cell.CellId
                                    Kind = OutputKind.Text
                                    Text = part
                                    Late = false
                                }

                    let result (outcome: CellOutcome) (names: string list) =
                        lock gate (fun () -> running <- None)
                        cancel.Dispose()

                        Ok
                            {
                                CellId = cell.CellId
                                Outcome = outcome
                                Output = output.Take()
                                Declared = names
                                Finished = finished.Value
                                Notified = notified.Value
                                DurationMs = 0L
                            }

                    match parse cell.Code with
                    | Error failure -> return result (CellOutcome.SyntaxError failure) []
                    | Ok statements ->
                        use linked =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                env.Scope.Cancellation,
                                cancel.Token,
                                timeout.Token
                            )

                        cell.TimeoutMs |> Option.iter timeout.CancelAfter

                        try
                            let! value =
                                execute
                                    env
                                    context.Peer
                                    cell.CellId
                                    statements
                                    output
                                    stream
                                    report
                                    finished
                                    linked.Token

                            let info =
                                value
                                |> Option.map (fun v ->
                                    lock gate (fun () -> outputs[cell.CellId] <- v)
                                    valueInfo limits.Preview v)

                            return result (CellOutcome.Completed info) (declared statements)
                        with
                        | CellStopped failure -> return result (CellOutcome.RuntimeError failure) (declared statements)
                        | :? OperationCanceledException when timeout.IsCancellationRequested ->
                            return result (CellOutcome.TimedOut cell.TimeoutMs.Value) (declared statements)
                        | :? OperationCanceledException -> return result CellOutcome.Cancelled (declared statements)
            }

    let cancelCell (cell: CellRef) : Eff<Env, Result<Cancelled, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()

                return
                    lock gate (fun () ->
                        match running with
                        | Some(id, cancel) when id = cell.CellId ->
                            cancel.Cancel()
                            Ok { WasRunning = true }
                        | _ when ran.Contains cell.CellId -> Ok { WasRunning = false }
                        | _ -> Error(KernelError.UnknownCell cell.CellId))
            }

    let checkCell (cell: CellCheck) : Eff<Env, Result<Checked, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()

                match parse cell.Code with
                | Ok _ -> return Ok { Diagnostics = [] }
                | Error failure ->
                    return
                        Ok
                            {
                                Diagnostics =
                                    [
                                        {
                                            Line = failure.Line |> Option.defaultValue 0
                                            Column = failure.Column |> Option.defaultValue 0
                                            Code = "syntax"
                                            Message = failure.Message
                                        }
                                    ]
                            }
            }

    let previewValue (p: PreviewParams) : Eff<Env, Result<ValueInfo, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()
                let preview = p.Limits |> Option.defaultValue (limits ()).Preview

                return
                    match lookup p.Ref with
                    | Some v -> Ok(valueInfo preview v)
                    | None -> Error(KernelError.NotFound p.Ref)
            }

    let encodeValue (p: EncodeParams) : Eff<Env, Result<Value, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()
                let limit = p.MaxBytes |> Option.defaultValue (limits ()).InlineBytes

                return
                    match lookup p.Ref with
                    | None -> Error(KernelError.NotFound p.Ref)
                    | Some(Value.Inline json) ->
                        let bytes = int64 (Encoding.UTF8.GetByteCount(Json.serialize json))

                        if bytes > int64 limit then
                            Error(KernelError.TooLarge(bytes, limit))
                        else
                            Ok(Value.Inline json)
                    | Some reference -> Ok reference
            }

    let decodeValue (p: DecodeParams) : Eff<Env, Result<ValueInfo, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()

                if not (namePattern.IsMatch p.Name) then
                    return Error(KernelError.InvalidName p.Name)
                else
                    lock gate (fun () -> variables[p.Name] <- p.Value)
                    return Ok(valueInfo (limits ()).Preview p.Value)
            }

    let recallValue (p: RecallParams) : Eff<Env, Result<ValueInfo, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()
                lock gate (fun () -> outputs[p.CellId] <- p.Value)
                return Ok(valueInfo (limits ()).Preview p.Value)
            }

    let snapshotState () : Eff<Env, Result<KernelState, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()

                let state =
                    lock gate (fun () ->
                        let sorted (d: Dictionary<string, Value>) =
                            d
                            |> Seq.map (fun kv -> kv.Key, kv.Value)
                            |> Seq.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                            |> List.ofSeq

                        sorted variables, sorted outputs)

                return
                    Ok
                        {
                            State = Value.Inline(snapshotCodec.Encode state)
                        }
            }

    let restoreState (state: KernelState) : Eff<Env, Result<unit, KernelError>> =
        fun _ ->
            task {
                do! unfrozen ()

                match state.State with
                | Value.Inline json ->
                    match snapshotCodec.Decode json with
                    | Ok(vars, outs) ->
                        lock gate (fun () ->
                            variables.Clear()
                            outputs.Clear()

                            for name, value in vars do
                                variables[name] <- value

                            for id, value in outs do
                                outputs[id] <- value)

                        return Ok()
                    | Error issues -> return Error(KernelError.InvalidValue(Issue.renderAtMost 3 issues))
                | _ -> return Error(KernelError.InvalidValue "not a snapshot of this kernel")
            }

    let accept (remote: Remote) : Eff<Env, Result<(string * Json) list, RpcError>> =
        fun _ ->
            task {
                let kernelMember =
                    match remote.Members with
                    | Json.Object members -> members |> List.tryFind (fun (k, _) -> k = KernelProtocol.InitializeMember)
                    | _ -> None

                match kernelMember with
                | None ->
                    return Error(RpcError.create Protocol.ErrorCode.InvalidParams "no kernel member in initialize")
                | Some(_, json) ->
                    match KernelProtocol.Codecs.kernelSetup.Decode json with
                    | Error issues -> return Error(RpcError.invalidParams issues)
                    | Ok s when not (List.contains info.Version s.Versions) ->
                        return Error(Handshake.unsupported [ info.Version ])
                    | Ok s ->
                        lock gate (fun () -> setup <- Some s)

                        return
                            Ok
                                [
                                    KernelProtocol.InitializeMember, KernelProtocol.Codecs.kernelInfo.Encode info
                                ]
            }

    /// The service a connection serves.
    member _.Service: Service<Env> =
        Service.create
            {
                Name = "script-kernel"
                Version = "1.0.0"
            }
            [
                Handler.request KernelProtocol.cellRun runCell
                Handler.request KernelProtocol.cellCancel (fun _ p -> cancelCell p)
                Handler.request KernelProtocol.cellCheck (fun _ p -> checkCell p)
                Handler.request KernelProtocol.valuePreview (fun _ p -> previewValue p)
                Handler.request KernelProtocol.valueEncode (fun _ p -> encodeValue p)
                Handler.request KernelProtocol.valueDecode (fun _ p -> decodeValue p)
                Handler.request KernelProtocol.recall (fun _ p -> recallValue p)
                Handler.request KernelProtocol.snapshot (fun _ () -> snapshotState ())
                Handler.request KernelProtocol.restore (fun _ p -> restoreState p)
            ]
        |> Service.withLimits
            { ConnectionLimits.defaults with
                Channels = None
            }
        |> Service.withAccept accept

    /// Ends every wait of a `spin` (the in-process stand-in for killing the process).
    member _.Kill() = killed.TrySetResult() |> ignore

    override _.ToString() = "ScriptKernel"

/// The `kernel-peer` command: serves one connection on standard input and output, as a kernel
/// process does. `stray` writes go to standard output between messages.
let serve () : int =
    let input = Console.OpenStandardInput()
    let output = Console.OpenStandardOutput()
    let writes = new SemaphoreSlim(1, 1)

    let shared =
        { new IO.Stream() with
            member _.CanRead = false
            member _.CanSeek = false
            member _.CanWrite = true
            member _.Length = raise (NotSupportedException())

            member _.Position
                with get () = raise (NotSupportedException())
                and set _ = raise (NotSupportedException())

            member _.Flush() = output.Flush()
            member _.Seek(_, _) = raise (NotSupportedException())
            member _.SetLength _ = raise (NotSupportedException())
            member _.Read(_: byte array, _: int, _: int) = raise (NotSupportedException())

            member _.Write(buffer: byte array, offset: int, count: int) =
                writes.Wait()

                try
                    output.Write(buffer, offset, count)
                    output.Flush()
                finally
                    writes.Release() |> ignore

            override _.Dispose(disposing: bool) =
                if disposing then
                    output.Dispose()
        }

    let stray (text: string) =
        let bytes = Encoding.UTF8.GetBytes text
        shared.Write(bytes, 0, bytes.Length)

    let kernel = Kernel stray
    let limits = ConnectionLimits.frames kernel.Service.ConnectionLimits
    let transport = StreamTransport.create limits input shared

    let env =
        {
            Clock = Fx.Effects.Live.SystemClock.clock
            Scope = Scope.root
        }

    match (Eff.run env (Rpc.accept kernel.Service transport)).GetAwaiter().GetResult() with
    | Ok peer ->
        peer.Completion.GetAwaiter().GetResult() |> ignore
        0
    | Error e ->
        stderr.WriteLine("kernel-peer: " + string e)
        1
