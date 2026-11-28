/// The kernel conformance suite, v1 (spec §6.4, docs/modules/kernel-protocol.md "Conformance"): a
/// function from a kernel under test to tests. A kernel passes it before an interpreter can be
/// selected, on Linux, macOS and Windows. The suite is language-neutral: a subject gives the code of
/// each cell it needs in its own language (`Snippets`), and every check goes through the protocol.
module Fx.Kernel.Tests.KernelConformance

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Kernel
open Fx.Store

/// The environment the host side runs in: real clock, shell and ids.
type Env =
    {
        Clock: Clock
        Shell: Shell
        Ids: Ids
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IShell with
        member env.Shell = env.Shell

    interface IIds with
        member env.Ids = env.Ids

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

let env () : Env =
    {
        Clock = SystemClock.clock
        Shell = LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory
        Ids = SystemIds.create SystemClock.clock
        Scope = Scope.root
    }

/// Code in the kernel's language for each kind of cell the suite runs.
type Snippets =
    {
        /// Binds a top-level variable to a JSON value.
        Assign: string -> Json -> string
        /// A cell whose value is a variable's.
        Read: string -> string
        /// A cell whose value is a JSON literal.
        Literal: Json -> string
        /// Prints a line.
        Print: string -> string
        /// Prints a line `count` times.
        PrintLines: int -> string -> string
        /// Binds a variable to a host call's result (JSON arguments).
        Call: string -> string -> Json list -> string
        /// Binds a variable to a host call's result, passing a variable as the argument.
        CallWith: string -> string -> string -> string
        /// Finishes the turn with a JSON result.
        Finish: Json -> string
        /// Throws an error with a message.
        Throw: string -> string
        /// Waits until interrupted.
        SleepForever: string
        /// A busy loop the kernel interrupts on a timeout.
        BusyLoop: string
        /// A busy loop the kernel cannot interrupt, which prints the text once nothing but a kill can
        /// stop the cell: the suite interrupts when it sees the text.
        Freeze: string -> string
        /// Prints a line after the cell returned.
        LateOutput: string -> string
        /// Reports an activity (kind, title), if the kernel has kernel-side wrappers cells can use.
        Activity: (string -> string -> string) option
        /// Binds a variable to an environment variable's value, or null.
        ReadEnv: string -> string -> string
        /// Writes text to the process's standard output past the protocol, if it can.
        Stray: (string -> string) option
        /// Binds a variable to `Out[cellId]`.
        ReadOut: string -> string -> string
        /// Binds a variable to a value without a JSON form (a function), if the language has one.
        Unencodable: (string -> string) option
        /// Code that does not parse.
        Unparsable: string
        /// Statements as one cell.
        Statements: string list -> string
    }

/// A kernel started for a test, and how to release it.
type Started =
    {
        Kernel: Kernel
        Dispose: unit -> Task
    }

    override this.ToString() = string this.Kernel

/// A kernel implementation under test.
type KernelSubject =
    {
        Name: string
        Snippets: Snippets
        /// Starts a kernel. The variables are added to its launch's environment (process subjects).
        Start: Env -> KernelHost<Env> -> KernelSetup -> (string * string) list -> Task<Result<Started, StartError>>
        /// The kernel runs in a process of its own: stray output, exit and launch hygiene apply.
        IsProcess: bool
        /// For kernels launched through a runtime's launch spec (spec §6.2): starts one with these
        /// variables in the launcher's own environment, which the launch must not pass on.
        StartFromLauncher:
            (Env -> KernelHost<Env> -> KernelSetup -> (string * string) list -> Task<Result<Started, StartError>>) option
    }

    override this.ToString() = this.Name

/// A result shaped like `TurnResult` (spec §4.5), for `finish`.
[<RequireQualifiedAccess>]
type TestTurn =
    | Done of summary: string * verify: string option
    | NeedInput of decisions: string list
    | Waiting of on: string list * note: string

    override this.ToString() =
        match this with
        | Done(summary, _) -> "done: " + summary
        | NeedInput decisions -> $"need input ({decisions.Length})"
        | Waiting(_, note) -> "waiting: " + note

let turnCodec: Codec<TestTurn> =
    Codec.union "kind" {
        let! isDone =
            Codec.case2
                "done"
                (fun summary verify -> TestTurn.Done(summary, verify))
                (Field.required "summary" Codec.string)
                (Field.optional "verify" Codec.string)

        and! needInput =
            Codec.case1 "needInput" TestTurn.NeedInput (Field.required "decisions" (Codec.list Codec.string))

        and! waiting =
            Codec.case2
                "waiting"
                (fun on note -> TestTurn.Waiting(on, note))
                (Field.required "on" (Codec.list Codec.string))
                (Field.required "note" Codec.string)

        return
            fun turn ->
                match turn with
                | TestTurn.Done(summary, verify) -> isDone summary verify
                | TestTurn.NeedInput decisions -> needInput decisions
                | TestTurn.Waiting(on, note) -> waiting on note
    }
    |> Codec.strict

/// `Ensure`: a summary says something.
let summaryEnsure (turn: TestTurn) : Issue list =
    match turn with
    | TestTurn.Done(summary, _) when String.IsNullOrWhiteSpace summary ->
        [ Issue.at [ PathSegment.Key "summary" ] "the summary must say what was done" ]
    | _ -> []

let thingType = TypeRef "Thing"
let thingPreview = Preview.full "a thing"

let blobValue =
    Value.Blob(
        {
            Hash = BlobHash.ofBytes [| 1uy; 2uy; 3uy |]
            Size = 3L
        },
        Preview.full "three bytes"
    )

/// The host side of a test: answers the test bindings and records what the kernel reports.
/// What `test.read` refuses: a secret file's content must never reach a cell.
let private excludedFiles: Fx.Models.Exclusions =
    match
        Fx.Models.Exclusions.create
            [
                {
                    Origin = "host"
                    Patterns = [ "*.env" ]
                }
            ]
    with
    | Ok e -> e
    | Error issues -> failwith (string issues)

type Recorder() =
    let handles = HandleTable<string>()
    let printedWaits = ResizeArray<string * string * TaskCompletionSource>()

    member val Outputs = ConcurrentQueue<CellOutput>()
    member val Activities = ConcurrentQueue<CellActivity>()
    member val Calls = ConcurrentQueue<HostCall>()
    member val Finishes = ConcurrentQueue<TestTurn>()
    member val Signalled = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    member val SlowStarted = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    member val SlowCancelled = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    member _.Handles = handles

    member this.OutputOf(cellId: string) =
        this.Outputs
        |> Seq.filter (fun o -> o.CellId = Some cellId && not o.Late)
        |> Seq.map (fun o -> o.Text)
        |> String.concat ""

    /// Completes once a cell's streamed output holds the text.
    member this.Printed (cellId: string) (text: string) : Task =
        let waiting =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        lock printedWaits (fun () -> printedWaits.Add(cellId, text, waiting))
        this.CheckPrinted()
        waiting.Task

    member private this.CheckPrinted() =
        let reached =
            lock printedWaits (fun () ->
                let reached =
                    printedWaits
                    |> Seq.filter (fun (cellId, text, _) -> (this.OutputOf cellId).Contains text)
                    |> List.ofSeq

                for wait in reached do
                    printedWaits.Remove wait |> ignore

                reached)

        for _, _, waiting in reached do
            waiting.TrySetResult() |> ignore

    member this.Host: KernelHost<Env> =
        let answer (call: HostCall) : Eff<Env, Result<Value, HostCallError>> =
            fun env ->
                task {
                    this.Calls.Enqueue call
                    let first = List.tryHead call.Args

                    match call.Binding with
                    | "test.echo" -> return Ok(first |> Option.defaultValue (Value.Inline Json.Null))
                    | "test.add" ->
                        let numbers =
                            call.Args
                            |> List.choose (fun v ->
                                match v with
                                | Value.Inline(Json.Number n) -> JsonNumber.tryToInt64 n
                                | _ -> None)

                        if numbers.Length <> call.Args.Length then
                            return Error(HostCallError.InvalidArguments [ Issue.create "expected integers" ])
                        else
                            return Ok(Value.Inline(Json.int64 (List.sum numbers)))
                    | "test.fail" ->
                        return
                            Error(
                                HostCallError.Failed(
                                    "boom",
                                    "it failed on purpose",
                                    Some(Json.Object [ "detail", Json.int 1 ])
                                )
                            )
                    | "test.handle" ->
                        return! (HandleTable.issue handles thingType thingPreview "thing-1" |> Eff.map Ok) env
                    | "test.blob" -> return Ok blobValue
                    | "test.inspect" ->
                        let description =
                            match first with
                            | Some(Value.Inline json) -> Json.Object [ "kind", Json.String "inline"; "value", json ]
                            | Some(Value.Handle(id, typeRef, _)) ->
                                let resolved =
                                    match HandleTable.resolveAs handles thingType id with
                                    | Ok target -> Json.String target
                                    | Error _ -> Json.Null

                                Json.Object
                                    [
                                        "kind", Json.String "handle"
                                        "type", Json.String typeRef.Name
                                        "target", resolved
                                    ]
                            | Some(Value.Blob(blob, _)) ->
                                Json.Object [ "kind", Json.String "blob"; "size", Json.int64 blob.Size ]
                            | None -> Json.Null

                        return Ok(Value.Inline description)
                    | "test.slow" ->
                        this.SlowStarted.TrySetResult() |> ignore

                        try
                            do! Task.Delay(Timeout.Infinite, env.Scope.Cancellation)
                            return Ok(Value.Inline Json.Null)
                        with :? OperationCanceledException ->
                            this.SlowCancelled.TrySetResult() |> ignore
                            return Error(HostCallError.Failed("cancelled", "cancelled", None))
                    | "test.signal" ->
                        this.Signalled.TrySetResult() |> ignore
                        return Ok(Value.Inline Json.Null)
                    | "test.read" ->
                        // A file read as the core bindings do it, with content exclusions (T124).
                        match first with
                        | Some(Value.Inline(Json.String path)) ->
                            match Fx.Models.Exclusions.test excludedFiles path with
                            | Some found -> return Error(Fx.Interpreters.CoreBindings.excluded path found)
                            | None -> return Ok(Value.Inline(Json.String("the content of " + path)))
                        | _ -> return Error(HostCallError.InvalidArguments [ Issue.create "expected a path" ])
                    | other -> return Error(HostCallError.UnknownBinding other)
                }

        {
            HostCall = answer
            Finish =
                fun finish ->
                    Eff.ret (
                        KernelProtocol.Finish.validate turnCodec [ summaryEnsure ] finish
                        |> Result.map (fun turn -> this.Finishes.Enqueue turn)
                    )
            Output =
                fun output ->
                    Eff.ret (
                        this.Outputs.Enqueue output
                        this.CheckPrinted()
                    )
            Activity = fun activity -> Eff.ret (this.Activities.Enqueue activity)
        }

    override _.ToString() = "Recorder"

/// The bindings every kernel under test gets.
let bindings =
    [
        "test.echo"
        "test.add"
        "test.fail"
        "test.handle"
        "test.blob"
        "test.inspect"
        "test.slow"
        "test.signal"
        "test.read"
    ]

/// Small limits, so that bounds are cheap to reach.
let limits: KernelLimits =
    {
        OutputChars = 1000
        Preview = Limits.defaults
        InlineBytes = 4096
        ArgumentBytes = Some 65536
    }

let setup: KernelSetup =
    {
        Versions = KernelProtocol.versions
        Global = Product.kernelGlobal
        Bindings = bindings
        ModuleBase = None
        Limits = limits
    }

/// The marker a kernel adds where it left out output: at most this many characters.
let markerChars = 64

/// Fails a test instead of hanging it.
let within (work: Task<'T>) : Task<'T> =
    work.WaitAsync(TimeSpan.FromSeconds 60.0)

/// Fails a test instead of hanging it.
let withinTask (work: Task) : Task =
    work.WaitAsync(TimeSpan.FromSeconds 60.0)

/// What a test works with.
type Context =
    {
        Subject: KernelSubject
        Started: Started
        Recorder: Recorder
        Env: Env
        Snippets: Snippets
        mutable Next: int
    }

    member this.Kernel = this.Started.Kernel

    member this.NextId() =
        this.Next <- this.Next + 1
        $"c{this.Next}"

    /// Runs a cell; the kernel must answer with a result.
    member this.Try(code: string) : Task<Result<CellResult, KernelFailure>> =
        let cell =
            {
                CellId = this.NextId()
                Code = code
                Intent = None
                TimeoutMs = None
                Check = false
            }

        Eff.run this.Env (Kernel.run this.Kernel cell) |> within

    member this.RunCell(cell: CellRun) : Task<CellResult> =
        task {
            match! Eff.run this.Env (Kernel.run this.Kernel cell) |> within with
            | Ok result -> return result
            | Error e -> return failtest $"cell {cell.CellId} failed: {e}"
        }

    member this.Run(code: string) : Task<CellResult> =
        this.RunCell
            {
                CellId = this.NextId()
                Code = code
                Intent = None
                TimeoutMs = None
                Check = false
            }

    member this.Encode(ref: ValueRef) : Task<Result<Value, KernelFailure>> =
        Eff.run this.Env (Kernel.encode this.Kernel ref None) |> within

    member this.EncodeVariable(name: string) : Task<Json> =
        task {
            match! this.Encode(ValueRef.Variable name) with
            | Ok(Value.Inline json) -> return json
            | Ok other -> return failtest $"{name} encoded as {other}"
            | Error e -> return failtest $"encoding {name} failed: {e}"
        }

    override this.ToString() = $"Context({this.Subject.Name})"

let private start (subject: KernelSubject) (recorder: Recorder) (variables: (string * string) list) =
    task {
        let env = env ()

        match! subject.Start env recorder.Host setup variables |> within with
        | Ok started -> return env, started
        | Error e -> return failtest $"the kernel did not start: {e}"
    }

let private using (subject: KernelSubject) (body: Context -> Task<unit>) : unit -> Task =
    fun () ->
        task {
            let recorder = Recorder()
            let! env, started = start subject recorder []

            let context =
                {
                    Subject = subject
                    Started = started
                    Recorder = recorder
                    Env = env
                    Snippets = subject.Snippets
                    Next = 0
                }

            try
                do! body context
            finally
                (started.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
        }

let private case (subject: KernelSubject) (name: string) (body: Context -> Task<unit>) =
    testTask name { do! using subject body () }

let private completed (result: CellResult) : ValueInfo option =
    match result.Outcome with
    | CellOutcome.Completed value -> value
    | other -> failtest $"cell {result.CellId} did not complete: {other}; output: {result.Output.Text}"

let private runtimeError (result: CellResult) : RuntimeFailure =
    match result.Outcome with
    | CellOutcome.RuntimeError failure -> failure
    | other -> failtest $"cell {result.CellId} did not fail at run time: {other}"

let private canonical (json: Json) = Json.serializeCanonical json

/// A result, for failure messages.
let private show (result: Result<'T, 'E>) : string =
    match result with
    | Ok value -> "Ok " + string value
    | Error e -> "Error " + string e

let private expectJson (actual: Json) (expected: Json) (message: string) =
    Expect.equal (canonical actual) (canonical expected) message

let private parse (text: string) =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failtest (string e)

/// Retries a cell while the kernel is still finishing an interrupted one.
let private runWhenIdle (context: Context) (code: string) : Task<CellResult> =
    task {
        let mutable result = None
        let mutable attempts = 0

        while result.IsNone do
            match! context.Try code with
            | Ok r -> result <- Some r
            | Error(KernelFailure.Refused(KernelError.Busy _)) when attempts < 100 ->
                attempts <- attempts + 1
                do! Task.Delay 50
            | Error e -> failtest $"the kernel did not run the cell: {e}"

        return result.Value
    }

/// Waits until a condition holds, polling.
let private eventually (what: string) (condition: unit -> bool) : Task =
    task {
        let mutable attempts = 0

        while not (condition ()) do
            if attempts > 400 then
                failtest $"timed out waiting for {what}"

            attempts <- attempts + 1
            do! Task.Delay 25
    }

let private handshake (subject: KernelSubject) =
    testList
        "handshake"
        [
            case subject "announces protocol 1, a language and the required methods"
            <| fun c ->
                task {
                    let info = Kernel.info c.Kernel
                    Expect.equal info.Version "1" "protocol version"
                    Expect.isNotEmpty info.Language "language"

                    for m in KernelProtocol.requiredMethods do
                        Expect.isTrue (Kernel.supports c.Kernel m) $"handles {m}"
                }

            case subject "announces snapshots exactly when it handles snapshot and restore"
            <| fun c ->
                task {
                    let both =
                        Kernel.supports c.Kernel KernelProtocol.snapshot.Name
                        && Kernel.supports c.Kernel KernelProtocol.restore.Name

                    let either =
                        Kernel.supports c.Kernel KernelProtocol.snapshot.Name
                        || Kernel.supports c.Kernel KernelProtocol.restore.Name

                    Expect.equal (Kernel.info c.Kernel).Caps.Snapshot both "caps.snapshot"
                    Expect.equal both either "snapshot and restore go together"
                }

            case subject "refuses a host that speaks no protocol version it does"
            <| fun c ->
                task {
                    let recorder = Recorder()

                    let! outcome =
                        c.Subject.Start (env ()) recorder.Host { setup with Versions = [ "99" ] } []
                        |> within

                    match outcome with
                    | Ok started ->
                        do! started.Dispose()
                        failtest "a kernel started with protocol 99"
                    | Error _ -> ()
                }
        ]

let private cells (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "cells"
        [
            case subject "state persists across cells"
            <| fun c ->
                task {
                    let value = parse """{"a": [1, 2, {"b": "c"}], "d": null}"""
                    let! assigned = c.Run(s.Assign "x" value)
                    completed assigned |> ignore
                    Expect.contains assigned.Declared "x" "x is declared"
                    let! read = c.Run(s.Read "x")
                    Expect.isSome (completed read) "x has a value"
                    let! encoded = c.EncodeVariable "x"
                    expectJson encoded value "x as assigned"
                }

            case subject "a later cell redefines a name"
            <| fun c ->
                task {
                    let! _ = c.Run(s.Assign "x" (Json.int 1))
                    let! _ = c.Run(s.Assign "x" (Json.int 2))
                    let! encoded = c.EncodeVariable "x"
                    expectJson encoded (Json.int 2) "the later value"
                }

            case subject "a cell's last expression is its value, kept as its output"
            <| fun c ->
                task {
                    let! result = c.Run(s.Literal(Json.int 42))
                    let info = completed result

                    Expect.equal (info |> Option.map (fun i -> i.Preview.Text)) (Some "42") "the preview of 42"

                    match! c.Encode(ValueRef.Output result.CellId) with
                    | Ok(Value.Inline json) -> expectJson json (Json.int 42) "Out[cell]"
                    | other -> failtest $"Out[cell] encoded as {show other}"
                }

            case subject "a cell whose last statement has no value has none"
            <| fun c ->
                task {
                    let! result = c.Run(s.Print "no value")
                    Expect.isNone (completed result) "no value"

                    match! c.Encode(ValueRef.Output result.CellId) with
                    | Error(KernelFailure.Refused(KernelError.NotFound _)) -> ()
                    | other -> failtest $"Out[cell] of a cell without a value: {show other}"
                }

            case subject "a cell id runs once"
            <| fun c ->
                task {
                    let cell =
                        {
                            CellId = "dup"
                            Code = s.Literal(Json.int 1)
                            Intent = Some "run it twice"
                            TimeoutMs = None
                            Check = false
                        }

                    let! _ = c.RunCell cell

                    match! Eff.run c.Env (Kernel.run c.Kernel cell) |> within with
                    | Error(KernelFailure.Refused(KernelError.DuplicateCell "dup")) -> ()
                    | other -> failtest $"a second run of a cell id: {show other}"
                }

            case subject "runs one cell at a time"
            <| fun c ->
                task {
                    let sleeping =
                        c.RunCell
                            {
                                CellId = "sleeper"
                                Code = s.Statements [ s.Call "_s" "test.signal" []; s.SleepForever ]
                                Intent = None
                                TimeoutMs = None
                                Check = false
                            }

                    do! c.Recorder.Signalled.Task |> withinTask

                    match! c.Try(s.Literal(Json.int 1)) with
                    | Error(KernelFailure.Refused(KernelError.Busy "sleeper")) -> ()
                    | other -> failtest $"a cell while another runs: {show other}"

                    let! _ = Eff.run c.Env (Kernel.cancel c.Kernel "sleeper") |> within
                    let! result = sleeping
                    Expect.equal result.Outcome CellOutcome.Cancelled "the sleeper was cancelled"
                }
        ]

let private output (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "output"
        [
            case subject "printed output is in the result"
            <| fun c ->
                task {
                    let! result = c.Run(s.Print "hello")
                    completed result |> ignore
                    Expect.equal result.Output.Text "hello\n" "output"
                    Expect.isFalse result.Output.Truncated "not truncated"
                    Expect.equal result.Output.TotalChars 6L "total"
                }

            case subject "output is streamed to the host before the result"
            <| fun c ->
                task {
                    let! result = c.Run(s.Statements [ s.Print "one"; s.Print "two" ])
                    completed result |> ignore
                    Expect.equal (c.Recorder.OutputOf result.CellId) "one\ntwo\n" "streamed, in order"
                    Expect.isGreaterThan result.Notified 0 "notifications counted"
                }

            case subject "output is bounded: head and tail, with what was left out counted"
            <| fun c ->
                task {
                    let! result = c.Run(s.PrintLines 300 "0123456789")
                    completed result |> ignore
                    let output = result.Output
                    Expect.isTrue output.Truncated "truncated"
                    Expect.equal output.TotalChars 3300L "every character counted"

                    Expect.isLessThanOrEqual
                        output.Text.Length
                        (limits.OutputChars + markerChars)
                        "at most the limit and a marker"

                    Expect.stringStarts output.Text "0123456789\n" "the head"
                    Expect.stringEnds output.Text "0123456789\n" "the tail"

                    Expect.isLessThanOrEqual
                        (c.Recorder.OutputOf result.CellId).Length
                        limits.OutputChars
                        "streamed output is bounded"
                }

            case subject "output after a cell returned is reported late, for that cell"
            <| fun c ->
                task {
                    let! result = c.Run(s.LateOutput "afterwards")
                    completed result |> ignore

                    do!
                        eventually "late output" (fun () ->
                            c.Recorder.Outputs
                            |> Seq.exists (fun o ->
                                o.Late && o.CellId = Some result.CellId && o.Text.Contains "afterwards"))
                }
        ]

let private errors (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "errors"
        [
            case subject "a thrown error is a runtime error with its message"
            <| fun c ->
                task {
                    let! result = c.Run(s.Throw "bad things happened")
                    let failure = runtimeError result
                    Expect.stringContains failure.Message "bad things happened" "message"
                }

            case subject "a cell that does not parse is a syntax error, and none of it runs"
            <| fun c ->
                task {
                    let! result = c.Run(s.Statements [ s.Assign "y" (Json.int 1); s.Unparsable ])

                    match result.Outcome with
                    | CellOutcome.SyntaxError _ -> ()
                    | other -> failtest $"expected a syntax error, got {other}"

                    match! c.Encode(ValueRef.Variable "y") with
                    | Error(KernelFailure.Refused(KernelError.NotFound _)) -> ()
                    | other -> failtest $"y after a syntax error: {show other}"
                }

            case subject "the kernel and its state survive errors"
            <| fun c ->
                task {
                    let! _ = c.Run(s.Assign "x" (Json.int 5))
                    let! failed = c.Run(s.Throw "oops")
                    runtimeError failed |> ignore
                    let! encoded = c.EncodeVariable "x"
                    expectJson encoded (Json.int 5) "x"
                    let! next = c.Run(s.Literal(Json.int 1))
                    completed next |> ignore
                }
        ]

let private previews (subject: KernelSubject) =
    let s = subject.Snippets

    let small =
        {
            MaxChars = 100
            MaxItems = 5
            MaxDepth = 2
            MaxStringChars = 20
        }

    let decode (c: Context) (name: string) (json: Json) =
        task {
            match! Eff.run c.Env (Kernel.decode c.Kernel name (Value.Inline json) None) |> within with
            | Ok info -> return info
            | Error e -> return failtest $"decode failed: {e}"
        }

    let previewOf (c: Context) (name: string) (bounds: Limits option) =
        task {
            match!
                Eff.run c.Env (Kernel.preview c.Kernel (ValueRef.Variable name) bounds)
                |> within
            with
            | Ok info -> return info
            | Error e -> return failtest $"preview failed: {e}"
        }

    testList
        "previews"
        [
            case subject "previews stay within the limits"
            <| fun c ->
                task {
                    let long = Json.String(String('a', 5000))
                    let many = Json.Array [ for i in 1..1000 -> Json.int i ]

                    let deep =
                        parse """{"a": {"b": {"c": {"d": {"e": [1, [2, [3, [4]]]]}}}}, "f": "x"}"""

                    for name, json in [ "long", long; "many", many; "deep", deep ] do
                        let! decoded = decode c name json

                        Expect.isLessThanOrEqual
                            decoded.Preview.Text.Length
                            limits.Preview.MaxChars
                            $"{name}: the default limits"

                        let! bounded = previewOf c name (Some small)
                        Expect.isLessThanOrEqual bounded.Preview.Text.Length small.MaxChars $"{name}: the given limits"
                        Expect.isTrue bounded.Preview.Truncated $"{name}: truncated"
                }

            case subject "a small value previews whole"
            <| fun c ->
                task {
                    let! _ = decode c "n" (Json.int 42)
                    let! info = previewOf c "n" None
                    Expect.equal info.Preview.Text "42" "42"
                    Expect.isFalse info.Preview.Truncated "whole"
                }

            case subject "a cell's value preview stays within the limits"
            <| fun c ->
                task {
                    let! result =
                        c.Run(s.Literal(Json.Array [ for i in 1..500 -> Json.String $"item {i}" ]))

                    match completed result with
                    | Some info ->
                        Expect.isLessThanOrEqual info.Preview.Text.Length limits.Preview.MaxChars "bounded"
                        Expect.isTrue info.Preview.Truncated "truncated"
                    | None -> failtest "no value"
                }

            case subject "previewing what does not exist is NotFound"
            <| fun c ->
                task {
                    match!
                        Eff.run c.Env (Kernel.preview c.Kernel (ValueRef.Variable "nothing_here") None)
                        |> within
                    with
                    | Error(KernelFailure.Refused(KernelError.NotFound(ValueRef.Variable "nothing_here"))) -> ()
                    | other -> failtest $"preview of an unknown variable: {show other}"

                    match! Eff.run c.Env (Kernel.preview c.Kernel (ValueRef.Output "c999") None) |> within with
                    | Error(KernelFailure.Refused(KernelError.NotFound(ValueRef.Output "c999"))) -> ()
                    | other -> failtest $"preview of an unknown output: {show other}"
                }
        ]

let private marshalling (subject: KernelSubject) =
    let s = subject.Snippets

    let decode (c: Context) (name: string) (value: Value) =
        task {
            match! Eff.run c.Env (Kernel.decode c.Kernel name value None) |> within with
            | Ok info -> return info
            | Error e -> return failtest $"decode failed: {e}"
        }

    let samples =
        [
            "object", """{"a": 1, "b": [true, false, null], "c": {"d": "e"}}"""
            "unicode", "\"h\u00e9llo \u2713 \ud834\udd1e \\u0000 \\n\\t\\\"\""
            "empty string", "\"\""
            "numbers", "[0, -1.5, 9007199254740991, -9007199254740991, 1e-7, 123456.789]"
            "nested arrays", "[[[]], [{}], [[1, 2], [3]]]"
            "boolean", "true"
            "null", "null"
        ]

    testList
        "marshalling"
        [
            case subject "JSON values round trip through decode and encode"
            <| fun c ->
                task {
                    for name, text in samples do
                        let json = parse text
                        let! _ = decode c "v" (Value.Inline json)
                        let! encoded = c.EncodeVariable "v"
                        expectJson encoded json name
                }

            case subject "values bound by decode are visible to cells"
            <| fun c ->
                task {
                    let json = parse """{"n": 3, "items": ["a", "b"]}"""
                    let! _ = decode c "input" (Value.Inline json)
                    let! result = c.Run(s.Read "input")
                    completed result |> ignore

                    match! c.Encode(ValueRef.Output result.CellId) with
                    | Ok(Value.Inline encoded) -> expectJson encoded json "Out of a cell that reads it"
                    | other -> failtest $"Out: {show other}"
                }

            case subject "a value larger than the limit is TooLarge"
            <| fun c ->
                task {
                    let! _ = decode c "big" (Value.Inline(Json.String(String('b', 10000))))

                    match!
                        Eff.run c.Env (Kernel.encode c.Kernel (ValueRef.Variable "big") (Some 100))
                        |> within
                    with
                    | Error(KernelFailure.Refused(KernelError.TooLarge(bytes, 100))) ->
                        Expect.isGreaterThanOrEqual bytes 10000L "bytes"
                    | other -> failtest $"encode beyond the limit: {show other}"
                }

            match s.Unencodable with
            | Some unencodable ->
                case subject "a value without a JSON form is NotEncodable"
                <| fun c ->
                    task {
                        let! _ = c.Run(unencodable "f")

                        match! c.Encode(ValueRef.Variable "f") with
                        | Error(KernelFailure.Refused(KernelError.NotEncodable _)) -> ()
                        | other -> failtest $"encode of a function: {show other}"
                    }
            | None -> ()

            case subject "invalid names are refused"
            <| fun c ->
                task {
                    match!
                        Eff.run c.Env (Kernel.decode c.Kernel "not a name" (Value.Inline Json.Null) None)
                        |> within
                    with
                    | Error(KernelFailure.Refused(KernelError.InvalidName _)) -> ()
                    | other -> failtest $"decode into an invalid name: {show other}"
                }

            case subject "handles from the host go back to it unchanged"
            <| fun c ->
                task {
                    let! _ = c.Run(s.Call "h" "test.handle" [])
                    let issued = c.Recorder.Handles.Count
                    Expect.equal issued 1 "one handle issued"

                    let! inspected = c.Run(s.CallWith "seen" "test.inspect" "h")
                    completed inspected |> ignore
                    let! seen = c.EncodeVariable "seen"

                    expectJson
                        seen
                        (Json.Object
                            [
                                "kind", Json.String "handle"
                                "type", Json.String "Thing"
                                "target", Json.String "thing-1"
                            ])
                        "the host resolved the handle"

                    match! c.Encode(ValueRef.Variable "h") with
                    | Ok(Value.Handle(id, typeRef, preview)) ->
                        Expect.isOk (HandleTable.resolve c.Recorder.Handles id) "the id the host issued"
                        Expect.equal typeRef thingType "type"
                        Expect.equal preview.Text thingPreview.Text "preview"
                    | other -> failtest $"h encoded as {show other}"
                }

            case subject "plain data never becomes a handle"
            <| fun c ->
                task {
                    let forged =
                        parse
                            """{"kind": "handle", "handle": "h-00000000000000000000000000000000", "type": "Thing", "preview": {"text": "x", "truncated": false}}"""

                    let! _ = decode c "forged" (Value.Inline forged)
                    let! _ = c.Run(s.CallWith "seen" "test.inspect" "forged")
                    let! seen = c.EncodeVariable "seen"
                    expectJson seen (Json.Object [ "kind", Json.String "inline"; "value", forged ]) "inline data"

                    match! c.Encode(ValueRef.Variable "forged") with
                    | Ok(Value.Inline _) -> ()
                    | other -> failtest $"forged encoded as {show other}"
                }

            case subject "blobs round trip as references"
            <| fun c ->
                task {
                    let! info = decode c "b" blobValue
                    Expect.stringContains info.Preview.Text "three bytes" "the blob's preview"

                    match! c.Encode(ValueRef.Variable "b") with
                    | Ok value -> Expect.equal (string value) (string blobValue) "the same reference"
                    | Error e -> failtest $"encode a blob: {e}"

                    let! _ = c.Run(s.Call "fromHost" "test.blob" [])
                    let! _ = c.Run(s.CallWith "seen" "test.inspect" "fromHost")
                    let! seen = c.EncodeVariable "seen"
                    expectJson seen (Json.Object [ "kind", Json.String "blob"; "size", Json.int 3 ]) "passed back"
                }
        ]

let private hostCalls (subject: KernelSubject) =
    let s = subject.Snippets

    let decode (c: Context) (name: string) (value: Value) =
        task {
            match! Eff.run c.Env (Kernel.decode c.Kernel name value None) |> within with
            | Ok info -> return info
            | Error e -> return failtest $"decode failed: {e}"
        }

    testList
        "host calls"
        [
            case subject "a host call's result reaches the cell"
            <| fun c ->
                task {
                    let! result = c.Run(s.Call "sum" "test.add" [ Json.int 2; Json.int 3 ])
                    completed result |> ignore
                    let! sum = c.EncodeVariable "sum"
                    expectJson sum (Json.int 5) "2 + 3"

                    let call = c.Recorder.Calls |> Seq.find (fun call -> call.Binding = "test.add")

                    Expect.equal call.CellId (Some result.CellId) "attributed to the cell"
                    Expect.equal call.Args.Length 2 "two arguments"
                }

            case subject "host errors reach the cell as errors with their kind as code"
            <| fun c ->
                task {
                    let! result = c.Run(s.Call "r" "test.fail" [])
                    let failure = runtimeError result
                    Expect.equal failure.Code (Some "failed") "code"
                    Expect.stringContains failure.Message "it failed on purpose" "message"
                }

            case subject "a binding the host does not have fails the cell"
            <| fun c ->
                task {
                    let! result = c.Run(s.Call "r" "test.missing" [])
                    runtimeError result |> ignore
                }

            case subject "an argument larger than inlineBytes, within argumentBytes, reaches the host whole"
            <| fun c ->
                task {
                    let text = String('a', 20000)
                    let! _ = decode c "big" (Value.Inline(Json.String text))
                    let! result = c.Run(s.CallWith "seen" "test.echo" "big")
                    completed result |> ignore

                    let call = c.Recorder.Calls |> Seq.find (fun call -> call.Binding = "test.echo")

                    match call.Args with
                    | [ Value.Inline(Json.String passed) ] -> Expect.equal passed text "the whole argument"
                    | other -> failtest $"""the host got {other |> List.map string |> String.concat ", "}"""
                }

            case subject "an argument larger than argumentBytes fails the cell before the host sees it"
            <| fun c ->
                task {
                    let! _ = decode c "huge" (Value.Inline(Json.String(String('h', 100000))))
                    let! result = c.Run(s.CallWith "seen" "test.echo" "huge")
                    let failure = runtimeError result
                    Expect.equal failure.Code (Some "invalidArguments") "code"
                    Expect.stringContains failure.Message "test.echo" "names the binding"

                    Expect.isFalse
                        (c.Recorder.Calls |> Seq.exists (fun call -> call.Binding = "test.echo"))
                        "the host never saw the call"
                }

            case subject "cancelling a cell cancels its host calls"
            <| fun c ->
                task {
                    let running =
                        c.RunCell
                            {
                                CellId = "slow"
                                Code = s.Call "r" "test.slow" []
                                Intent = None
                                TimeoutMs = None
                                Check = false
                            }

                    do! c.Recorder.SlowStarted.Task |> withinTask

                    match! Eff.run c.Env (Kernel.cancel c.Kernel "slow") |> within with
                    | Ok cancelled -> Expect.isTrue cancelled.WasRunning "it was running"
                    | Error e -> failtest $"cancel failed: {e}"

                    let! result = running
                    Expect.equal result.Outcome CellOutcome.Cancelled "cancelled"
                    do! c.Recorder.SlowCancelled.Task |> withinTask
                }
        ]

let private finishing (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "finish"
        [
            case subject "an accepted finish ends the turn"
            <| fun c ->
                task {
                    let! result =
                        c.Run(s.Finish(parse """{"kind": "done", "summary": "all good", "verify": "dotnet test"}"""))

                    completed result |> ignore
                    Expect.isTrue result.Finished "finished"

                    Expect.sequenceEqual
                        c.Recorder.Finishes
                        [ TestTurn.Done("all good", Some "dotnet test") ]
                        "the host decoded the result"
                }

            case subject "a result that does not decode is rejected in the cell, with the issues"
            <| fun c ->
                task {
                    let! result = c.Run(s.Finish(parse """{"kind": "don", "summary": "typo"}"""))
                    let failure = runtimeError result
                    Expect.stringContains failure.Message "expected one of" "the issue"
                    Expect.isFalse result.Finished "not finished"
                    Expect.isEmpty c.Recorder.Finishes "nothing accepted"
                }

            case subject "a result that fails a check (Ensure) is rejected in the cell"
            <| fun c ->
                task {
                    let! result = c.Run(s.Finish(parse """{"kind": "done", "summary": " "}"""))
                    let failure = runtimeError result
                    Expect.stringContains failure.Message "the summary must say what was done" "the check's issue"
                    Expect.isFalse result.Finished "not finished"
                }
        ]

let private cancellation (subject: KernelSubject) =
    let s = subject.Snippets

    let cell (id: string) (code: string) (timeout: int option) =
        {
            CellId = id
            Code = code
            Intent = None
            TimeoutMs = timeout
            Check = false
        }

    testList
        "cancellation"
        [
            case subject "cell/cancel stops a waiting cell, and the kernel lives on"
            <| fun c ->
                task {
                    let! _ = c.Run(s.Assign "kept" (Json.int 7))

                    let running =
                        c.RunCell(cell "waiting" (s.Statements [ s.Call "_s" "test.signal" []; s.SleepForever ]) None)

                    do! c.Recorder.Signalled.Task |> withinTask

                    match! Eff.run c.Env (Kernel.cancel c.Kernel "waiting") |> within with
                    | Ok cancelled -> Expect.isTrue cancelled.WasRunning "it was running"
                    | Error e -> failtest $"cancel failed: {e}"

                    let! result = running
                    Expect.equal result.Outcome CellOutcome.Cancelled "cancelled"
                    let! kept = c.EncodeVariable "kept"
                    expectJson kept (Json.int 7) "state kept"
                }

            case subject "cancelling the cell/run request interrupts the cell"
            <| fun c ->
                task {
                    use cancel = new CancellationTokenSource()

                    let scoped =
                        { c.Env with
                            Scope =
                                { c.Env.Scope with
                                    Cancellation = cancel.Token
                                }
                        }

                    let running =
                        Eff.run
                            scoped
                            (Kernel.run
                                c.Kernel
                                (cell "abandoned" (s.Statements [ s.Call "_s" "test.signal" []; s.SleepForever ]) None))

                    do! c.Recorder.Signalled.Task |> withinTask
                    cancel.Cancel()

                    let! outcome =
                        task {
                            try
                                let! _ = running
                                return "answered"
                            with :? OperationCanceledException ->
                                return "cancelled"
                        }
                        |> within

                    Expect.equal outcome "cancelled" "the call was cancelled"
                    let! next = runWhenIdle c (s.Literal(Json.int 1))
                    completed next |> ignore
                }

            case subject "a timeout stops a waiting cell"
            <| fun c ->
                task {
                    let! result = c.RunCell(cell "slow" s.SleepForever (Some 300))
                    Expect.equal result.Outcome (CellOutcome.TimedOut 300) "timed out"
                }

            case subject "a timeout stops a busy loop, and the kernel lives on"
            <| fun c ->
                task {
                    let! _ = c.Run(s.Assign "kept" (Json.int 8))
                    let! result = c.RunCell(cell "busy" s.BusyLoop (Some 300))
                    Expect.equal result.Outcome (CellOutcome.TimedOut 300) "timed out"
                    let! kept = c.EncodeVariable "kept"
                    expectJson kept (Json.int 8) "state kept"
                }

            case subject "a cell the kernel cannot interrupt gets it killed; a new kernel restores values"
            <| fun c ->
                task {
                    let! out = c.Run(s.Literal(parse """{"journaled": true}"""))

                    let! journal =
                        task {
                            match! c.Encode(ValueRef.Output out.CellId) with
                            | Ok value -> return value
                            | Error e -> return failtest $"journal: {e}"
                        }

                    let running =
                        Eff.run c.Env (Kernel.run c.Kernel (cell "frozen" (s.Freeze "frozen now") None))

                    // Once the text is out, a cancel can no longer stop the cell: the interrupt must kill.
                    do! c.Recorder.Printed "frozen" "frozen now" |> withinTask
                    let! interruption = Eff.run c.Env (Kernel.interrupt c.Kernel) |> within
                    Expect.equal interruption Interruption.Killed "killed"

                    match! running |> within with
                    | Error(KernelFailure.Exited _) -> ()
                    | Error other -> failtest $"the frozen cell failed otherwise: {other}"
                    | Ok result -> failtest $"the frozen cell returned: {result}"

                    let! _ = Kernel.exited c.Kernel |> within

                    // Jupyter semantics: a new kernel starts empty, and the journal restores outputs.
                    let recorder = Recorder()
                    let! env, restarted = start c.Subject recorder []

                    try
                        let fresh =
                            { c with
                                Started = restarted
                                Recorder = recorder
                                Env = env
                            }

                        match! fresh.Encode(ValueRef.Output out.CellId) with
                        | Error(KernelFailure.Refused(KernelError.NotFound _)) -> ()
                        | other -> failtest $"a new kernel's outputs: {show other}"

                        match! Eff.run env (Kernel.recall restarted.Kernel out.CellId journal) |> within with
                        | Ok _ -> ()
                        | Error e -> failtest $"recall: {e}"

                        let! read = fresh.Run(s.ReadOut "restored" out.CellId)
                        completed read |> ignore
                        let! restored = fresh.EncodeVariable "restored"
                        expectJson restored (parse """{"journaled": true}""") "Out[cell] restored"
                    finally
                        (restarted.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                }
        ]

let private activity (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "activity"
        [
            match s.Activity with
            | Some report ->
                case subject "activity is reported for its cell before the result"
                <| fun c ->
                    task {
                        let! result = c.Run(report "run" "dotnet test")
                        completed result |> ignore

                        let reported =
                            c.Recorder.Activities
                            |> Seq.filter (fun a -> a.CellId = Some result.CellId && not a.Late)
                            |> List.ofSeq

                        Expect.equal
                            (reported |> List.map (fun a -> a.Activity.Kind, a.Activity.Title))
                            [ "run", "dotnet test" ]
                            "the activity"
                    }
            | None -> ()

            case subject "intent lines do not change what a cell does"
            <| fun c ->
                task {
                    let! result =
                        c.RunCell
                            {
                                CellId = c.NextId()
                                Code = s.Literal(Json.int 3)
                                Intent = Some "compute three"
                                TimeoutMs = None
                                Check = false
                            }

                    Expect.isSome (completed result) "a value"
                }
        ]

let private recall (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "recall"
        [
            case subject "recall restores a cell's output"
            <| fun c ->
                task {
                    let value = Value.Inline(parse """{"x": 1}""")

                    match! Eff.run c.Env (Kernel.recall c.Kernel "r1" value) |> within with
                    | Ok info -> Expect.isNotEmpty info.Preview.Text "a preview"
                    | Error e -> failtest $"recall: {e}"

                    match! c.Encode(ValueRef.Output "r1") with
                    | Ok encoded -> Expect.equal (string encoded) (string value) "Out[r1]"
                    | Error e -> failtest $"encode Out[r1]: {e}"

                    let! _ = c.Run(s.ReadOut "v" "r1")
                    let! v = c.EncodeVariable "v"
                    expectJson v (parse """{"x": 1}""") "a cell reads it"
                }
        ]

let private optional (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "optional"
        [
            case subject "a snapshot restores the state in a new kernel (if caps.snapshot)"
            <| fun c ->
                task {
                    if (Kernel.info c.Kernel).Caps.Snapshot then
                        let! _ = c.Run(s.Assign "x" (Json.int 7))

                        let! state =
                            task {
                                match! Eff.run c.Env (Kernel.snapshot c.Kernel) |> within with
                                | Ok state -> return state
                                | Error e -> return failtest $"snapshot: {e}"
                            }

                        let recorder = Recorder()
                        let! env, other = start c.Subject recorder []

                        try
                            match! Eff.run env (Kernel.restore other.Kernel state) |> within with
                            | Ok() -> ()
                            | Error e -> failtest $"restore: {e}"

                            let fresh =
                                { c with
                                    Started = other
                                    Recorder = recorder
                                    Env = env
                                }

                            let! x = fresh.EncodeVariable "x"
                            expectJson x (Json.int 7) "x restored"
                        finally
                            (other.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                }

            case subject "cell/check reports what does not parse (if handled)"
            <| fun c ->
                task {
                    if Kernel.supports c.Kernel KernelProtocol.cellCheck.Name then
                        let check code =
                            Eff.run c.Env (Kernel.check c.Kernel { CellId = c.NextId(); Code = code })
                            |> within

                        match! check s.Unparsable with
                        | Ok checkedCell -> Expect.isNonEmpty checkedCell.Diagnostics "diagnostics"
                        | Error e -> failtest $"check: {e}"

                        match! check (s.Literal(Json.int 1)) with
                        | Ok checkedCell -> Expect.isEmpty checkedCell.Diagnostics "no diagnostics"
                        | Error e -> failtest $"check: {e}"
                }

            case subject "the same cells give the same results (if caps.deterministic)"
            <| fun c ->
                task {
                    if (Kernel.info c.Kernel).Caps.Deterministic then
                        let script =
                            [
                                s.Assign "x" (parse """{"a": [1, 2]}""")
                                s.Print "hello"
                                s.Read "x"
                                s.Throw "expected"
                                s.Call "r" "test.add" [ Json.int 1; Json.int 2 ]
                            ]

                        let runAll (context: Context) =
                            task {
                                let results = ResizeArray()

                                for code in script do
                                    let! result = context.Run code

                                    results.Add(
                                        KernelProtocol.Codecs.cellResult.Encode { result with DurationMs = 0L }
                                        |> Json.serialize
                                    )

                                return List.ofSeq results
                            }

                        let! first = runAll c
                        let recorder = Recorder()
                        let! env, other = start c.Subject recorder []

                        try
                            let! second =
                                runAll
                                    { c with
                                        Started = other
                                        Recorder = recorder
                                        Env = env
                                        Next = 0
                                    }

                            Expect.sequenceEqual second first "identical results"
                        finally
                            (other.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                }
        ]

let private hygiene (subject: KernelSubject) =
    let s = subject.Snippets

    if not subject.IsProcess then
        testList "launch hygiene" []
    else
        testList
            "launch hygiene"
            [
                testTask "the kernel sees exactly its launch's environment" {
                    Environment.SetEnvironmentVariable("KERNEL_HYGIENE_PARENT", "leaked")
                    let recorder = Recorder()
                    let! env, started = start subject recorder [ "KERNEL_HYGIENE_PROBE", "given" ]

                    let context =
                        {
                            Subject = subject
                            Started = started
                            Recorder = recorder
                            Env = env
                            Snippets = s
                            Next = 0
                        }

                    try
                        let! _ = context.Run(s.ReadEnv "probe" "KERNEL_HYGIENE_PROBE")
                        let! probe = context.EncodeVariable "probe"
                        expectJson probe (Json.String "given") "a variable the launch sets"
                        let! _ = context.Run(s.ReadEnv "parent" "KERNEL_HYGIENE_PARENT")
                        let! parent = context.EncodeVariable "parent"
                        expectJson parent Json.Null "a variable only the host has"
                    finally
                        (started.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                }

                testTask "a launch that sets a runtime injection vector is refused" {
                    let recorder = Recorder()

                    for name in [ "NODE_OPTIONS"; "BUN_OPTIONS"; "DOTNET_STARTUP_HOOKS"; "LD_PRELOAD" ] do
                        match! subject.Start (env ()) recorder.Host setup [ name, "--inject" ] |> within with
                        | Error(StartError.UnsafeLaunch _) -> ()
                        | Ok started ->
                            do! started.Dispose()
                            failtest $"started with {name}"
                        | Error e -> failtest $"{name}: {e}"
                }

                match subject.StartFromLauncher with
                | Some startFrom ->
                    testTask "runtime injection vectors in the launcher's environment never reach the kernel" {
                        // Each would change what the runtime loads if it were passed on (a preload that
                        // does not exist would stop Bun from starting at all); the positive controls
                        // that show they work are fx.runtime.bun's (tests/Fx.Ext.RuntimeBun.Tests).
                        let vectors =
                            [
                                "BUN_OPTIONS", "--preload=./injected-preload.ts"
                                "bun_inspect", "127.0.0.1:9"
                                "BUN_CONFIG_REGISTRY", "http://127.0.0.1:9/"
                                "NODE_OPTIONS", "--require=./injected.js"
                                "NODE_PATH", "./injected"
                                "LD_PRELOAD", "./injected.so"
                                "DYLD_INSERT_LIBRARIES", "./injected.dylib"
                                "DOTNET_STARTUP_HOOKS", "./injected.dll"
                            ]

                        let recorder = Recorder()

                        match! startFrom (env ()) recorder.Host setup vectors |> within with
                        | Error e -> failtest $"the kernel did not start: {e}"
                        | Ok started ->
                            let context =
                                {
                                    Subject = subject
                                    Started = started
                                    Recorder = recorder
                                    Env = env ()
                                    Snippets = s
                                    Next = 0
                                }

                            try
                                for i, (name, _) in List.indexed vectors do
                                    let! _ = context.Run(s.ReadEnv $"v{i}" name)
                                    let! value = context.EncodeVariable $"v{i}"
                                    expectJson value Json.Null $"{name} is not set in the kernel"

                                    if name <> name.ToUpperInvariant() then
                                        let! _ = context.Run(s.ReadEnv $"u{i}" (name.ToUpperInvariant()))
                                        let! upper = context.EncodeVariable $"u{i}"
                                        expectJson upper Json.Null $"{name.ToUpperInvariant()} is not set in the kernel"
                            finally
                                (started.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                    }
                | None -> ()

                match s.Stray with
                | Some stray ->
                    case subject "bytes on standard output outside the protocol do not break it"
                    <| fun c ->
                        task {
                            let! result = c.Run(stray "stray bytes without a newline")
                            completed result |> ignore
                            let! next = c.Run(s.Literal(Json.int 1))
                            completed next |> ignore
                            Expect.stringContains (Kernel.stray c.Kernel) "stray bytes without a newline" "reported"
                        }
                | None -> ()

                case subject "closing the connection ends the kernel process"
                <| fun c ->
                    task {
                        let! code = Eff.run c.Env (Kernel.close c.Kernel) |> within
                        Expect.equal code (Some 0) "exit code"
                    }
            ]

/// Content exclusions (spec §6.4, §6.5): the host refuses excluded files, and the kernel reports
/// the refusal in the cell, with no content, and carries on.
let private contentExclusions (subject: KernelSubject) =
    let s = subject.Snippets

    testList
        "content exclusions"
        [
            case subject "a read the host refuses as excluded fails in the cell, and nothing of the file reaches it"
            <| fun c ->
                task {
                    let! refused = c.Run(s.Call "secret" "test.read" [ Json.String "deploy/prod.env" ])
                    let failure = runtimeError refused
                    Expect.equal failure.Code (Some "failed") "a host failure"
                    Expect.stringContains failure.Message "is excluded" "says why"
                    Expect.isFalse (failure.Message.Contains "the content of") "no content"
                    Expect.isFalse ((c.Recorder.OutputOf refused.CellId).Contains "the content of") "no output"

                    let! allowed = c.Run(s.Call "readme" "test.read" [ Json.String "README.md" ])
                    completed allowed |> ignore
                    let! readme = c.EncodeVariable "readme"
                    expectJson readme (Json.String "the content of README.md") "other files are read"
                }
        ]

/// The suite for one kernel implementation.
let tests (subject: KernelSubject) : Test =
    testList
        subject.Name
        [
            handshake subject
            cells subject
            output subject
            errors subject
            previews subject
            marshalling subject
            hostCalls subject
            finishing subject
            cancellation subject
            activity subject
            recall subject
            optional subject
            hygiene subject
            contentExclusions subject
        ]
