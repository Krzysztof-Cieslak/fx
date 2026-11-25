namespace Fx.CodeAct

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Envd
open Fx.Interpreters
open Fx.Sdk
open Fx.Sessions

/// How a session's kernels start (spec §6.2): the interpreter, in the environment the kernel runs in.
[<NoEquality; NoComparison>]
type Interpreter<'K> =
    {
        /// The cells' language, as the `cell` tool names it (`TypeScript`).
        Language: string
        /// The interpreter's prompt for a binding surface: its prompt pack with the declarations.
        Prompt: BindingSurface -> string
        /// Starts a kernel: the host's side of the protocol, the `initialize` setup and the bridge's
        /// surface, and the launch's number in the session (each launch gets fresh owned directories).
        Start: StartKernel<'K> -> Eff<'K, Result<Kernel, string>>
    }

    override this.ToString() = $"interpreter ({this.Language})"

and [<NoEquality; NoComparison>] StartKernel<'K> =
    {
        Host: KernelHost<'K>
        Setup: KernelSetup
        Surface: BindingSurface
        Launch: int
    }

    override this.ToString() = $"start kernel (launch {this.Launch})"

/// A session's hands (spec §3): the environment its kernel runs in and its bindings act on.
[<NoEquality; NoComparison>]
type Hands<'K> =
    {
        /// The environment's capabilities (shell, files, envd's file operations, the clock). Kernels
        /// live in its scope, across turns: a turn's interrupt stops cells, not kernels.
        Env: 'K
        /// The workspace, absolute in the environment's syntax.
        Workspace: string
        /// The environment's operating system.
        Os: Os
        /// The variables commands get (`CoreSettings.CommandEnv`).
        CommandEnv: EnvVars
        Interpreter: Interpreter<'K>
        /// Checked before every host call, from cells and direct tools alike: `Error` refuses it (e.g.
        /// `Denied`). Where grants plug in (T127b).
        Guard: HostCall -> Eff<'K, Result<unit, HostCallError>>
        /// The workspace's snapshots (envd's), if the environment has them: each step records the
        /// workspace before its actions, so a call in doubt after a crash shows what changed.
        Snapshots: Snapshots option
    }

    override this.ToString() = $"hands ({this.Workspace})"

/// What the step journal sees of a session's kernel (docs/modules/codeact.md, "Crash recovery"): host
/// calls before they run and how they ended, and the output cells stream.
[<NoEquality; NoComparison>]
type Watch<'K> =
    {
        /// Before a host call runs (after the guard allowed it): a number for `Called`, if it was
        /// journaled.
        Calling: HostCall -> Eff<'K, int option>
        Called: int -> Result<Value, HostCallError> -> Eff<'K, unit>
        Output: CellOutput -> Eff<'K, unit>
    }

    override _.ToString() = "watch"

/// What a session's kernels need from the step journal: launch numbers that go on after the host
/// restarted, and the values kept for `recall`.
[<NoEquality; NoComparison>]
type KernelJournal<'K> =
    {
        /// The last launch the session journaled (0: none): read before its first launch here.
        LastLaunch: Eff<'K, int>
        /// Journals a launch before the kernel starts.
        Launching: int -> Eff<'K, unit>
        /// Puts at most this many kept values back into a new kernel (`Out[cell]`); the cells restored.
        Restore: int -> Kernel -> Eff<'K, string list>
    }

    override _.ToString() = "kernel journal"

[<RequireQualifiedAccess>]
module Watch =
    /// Sees nothing.
    let none<'K> : Watch<'K> =
        {
            Calling = fun _ -> Eff.ret None
            Called = fun _ _ -> Eff.unit
            Output = fun _ -> Eff.unit
        }

[<RequireQualifiedAccess>]
module KernelJournal =
    /// No journal: launches count from 1 in this process, nothing is restored.
    let none<'K> : KernelJournal<'K> =
        {
            LastLaunch = Eff.ret 0
            Launching = fun _ -> Eff.unit
            Restore = fun _ _ -> Eff.ret []
        }

/// Interpreters from providers (`fx.interpreter.bun-ts`, …).
[<RequireQualifiedAccess>]
module Interpreter =
    /// An interpreter provider's kernels, prepared and started in the environment (`Interpreter.start`):
    /// `session` gives each launch its own owned directories.
    let ofProvider<'K when 'K :> IFs and 'K :> IShell and 'K :> IClock and 'K :> IScopeWith<'K>>
        (options: KernelOptions)
        (session: int -> InterpreterSession)
        : Interpreter<'K> =
        let provider = (session 1).Provider

        {
            Language = provider.Language
            Prompt = fun surface -> PromptPack.render provider.PromptPack (provider.Bindings surface)
            Start =
                fun start ->
                    Fx.Interpreters.Interpreter.start
                        options
                        start.Host
                        start.Setup
                        (session start.Launch)
                        start.Surface
                    |> EffResult.mapError string
        }

/// The checks a turn's result must pass besides its codec (spec §4.3 `Ensure`, §4.5).
[<RequireQualifiedAccess>]
module Ensure =
    let private at (segments: PathSegment list) message =
        Issue.at (PathSegment.Key "result" :: segments) message

    /// `done` says what was done.
    let summary (result: TurnResult) : Issue list =
        match result with
        | TurnResult.Done(summary, _, _) when String.IsNullOrWhiteSpace summary ->
            [ at [ PathSegment.Key "summary" ] "the summary must say what was done" ]
        | _ -> []

    /// `done` points to evidence, when the policy requires it.
    let evidence (policy: EvidencePolicy) (result: TurnResult) : Issue list =
        match policy, result with
        | EvidencePolicy.Required, TurnResult.Done(_, [], _) ->
            [
                at
                    [ PathSegment.Key "evidence" ]
                    "a done result must list evidence: what you ran or checked, and what it showed"
            ]
        | _ -> []

    /// `needInput` names open requests the session waits for (as input).
    let requests (state: SessionState) (result: TurnResult) : Issue list =
        match result with
        | TurnResult.NeedInput ids ->
            let waiting =
                state.Requests
                |> List.filter (fun r -> r.Request.Reply = Reply.AsInput)
                |> List.map (fun r -> r.Request.Id)

            ids
            |> List.indexed
            |> List.filter (fun (_, id) -> not (List.contains id waiting))
            |> List.map (fun (i, id) ->
                at
                    [ PathSegment.Key "requests"; PathSegment.Index i ]
                    $"{Json.quote id} is not an open request; to ask the user, reply with your question as plain text instead")
        | _ -> []

    /// `waiting` names jobs that exist (none can be started yet: T212).
    let jobs (known: string list) (result: TurnResult) : Issue list =
        match result with
        | TurnResult.Waiting(ids, _) ->
            ids
            |> List.indexed
            |> List.filter (fun (_, id) -> not (List.contains id known))
            |> List.map (fun (i, id) ->
                at [ PathSegment.Key "jobs"; PathSegment.Index i ] $"there is no background job {Json.quote id}")
        | _ -> []

    /// Every check for a turn.
    let all (policy: EvidencePolicy) (state: SessionState) : (TurnResult -> Issue list) list =
        [ summary; evidence policy; requests state; jobs [] ]

/// How a cell ended: the kernel's result, the result `finish` accepted in it, and what it read.
type CellRecord =
    {
        Result: Result<CellResult, KernelFailure>
        /// The result `finish` accepted in this cell, if it did.
        Finished: TurnResult option
        /// The files the cell read through the bridge.
        Reads: Reads
    }

    override this.ToString() = $"cell record ({this.Reads})"

/// How a cell's run ended for the turn.
[<RequireQualifiedAccess>]
type private Ran =
    | Answered of Result<CellResult, KernelFailure>
    /// The turn was cancelled; how the cell was stopped.
    | Interrupted of Interruption

/// The cell running in a session's kernel: what its host calls and `finish` are attributed to.
type private RunningCell(id: string) =
    member val Id = id
    member val Reads = Reads.none with get, set
    member val Finished: TurnResult option = None with get, set

/// A session's kernel across turns (spec §6.5: kept across turns, restarted when it dies, with Jupyter
/// semantics), and the host's side of its protocol: the bridge's bindings for the current turn's
/// settings (content exclusions included) behind the hands' guard, provenance of what cells read, and
/// `finish` validated by the turn result's codec and the turn's checks. One turn at a time uses it.
[<Sealed>]
type SessionKernel<'K when 'K :> IShell and 'K :> IFs and 'K :> IFsOps and 'K :> IClock and 'K :> IScopeWith<'K>>
    (session: string, watch: Watch<'K>, journal: KernelJournal<'K>, extra: HostBinding<'K> list) =
    let gate = obj ()
    let turns = new SemaphoreSlim(1, 1)
    let mutable kernel: Kernel option = None
    // The last launch's number: from the journal before the first launch in this process.
    let mutable launches: int option = None
    let mutable settings: CoreSettings option = None
    let mutable checks: (TurnResult -> Issue list) list = [ Ensure.summary ]
    let mutable running: RunningCell option = None

    let finishing: Finishing<'K> =
        {
            Schema = Codecs.turnResult.Schema
            Validate =
                fun finish ->
                    let cell, ensure = lock gate (fun () -> running, checks)

                    match cell with
                    | Some cell when finish.CellId.IsNone || finish.CellId = Some cell.Id ->
                        match KernelProtocol.Finish.validate Codecs.turnResult ensure finish with
                        | Ok result ->
                            lock gate (fun () -> cell.Finished <- Some result)
                            EffResult.ok ()
                        | Error rejected -> EffResult.error rejected
                    | _ ->
                        EffResult.error
                            {
                                Issues =
                                    [
                                        Issue.create
                                            "finish was called outside the running cell: call it from the cell that ends the turn"
                                    ]
                            }
        }

    let bindings (current: CoreSettings) : HostBinding<'K> list = CoreBindings.all current @ extra

    let bridge (current: CoreSettings) : Bridge<'K> =
        Bridge.create finishing |> Bridge.add CoreBindings.groups (bindings current)

    let current () =
        lock gate (fun () ->
            match settings with
            | Some s -> s
            | None -> invalidOp "No turn is running in this session's kernel.")

    let record (call: HostCall) (result: Result<Value, HostCallError>) =
        match call.Binding, result with
        | "fs.read", Ok value ->
            match Value.decode CoreBindings.Codecs.fileText value with
            | Ok file ->
                lock gate (fun () ->
                    match running with
                    | Some cell when call.CellId = Some cell.Id -> cell.Reads <- Reads.add file.Path cell.Reads
                    | _ -> ())
            | Error _ -> ()
        | _ -> ()

    /// A session's kernel without a step journal (launches count from 1 in this process).
    new(session: string) = SessionKernel<'K>(session, Watch.none, KernelJournal.none, [])

    member _.Session = session

    /// The number of the last kernel this session started (journaled launches included).
    member _.Launches = lock gate (fun () -> launches |> Option.defaultValue 0)

    /// The live kernel, if any.
    member _.Kernel = lock gate (fun () -> kernel)

    /// Waits until no other turn uses the kernel (an interrupted one may still be stopping its cell),
    /// then holds it; dispose the result to release it.
    member _.Enter<'E when 'E :> IScope>() : Eff<'E, IDisposable> =
        fun env ->
            task {
                do! turns.WaitAsync env.Scope.Cancellation

                return
                    { new IDisposable with
                        member _.Dispose() = turns.Release() |> ignore
                    }
            }

    /// Starts a turn: the bindings' settings and the checks of its result.
    member _.Begin(core: CoreSettings, ensure: (TurnResult -> Issue list) list) =
        lock gate (fun () ->
            settings <- Some core
            checks <- ensure)

    /// The surface the bridge declares (for the interpreter's prompt), with these settings.
    member _.Surface(core: CoreSettings) : BindingSurface = Bridge.surface (bridge core)

    /// A host call, from a cell or a direct tool: the guard, then the binding with the turn's settings.
    member _.Call
        (guard: HostCall -> Eff<'K, Result<unit, HostCallError>>)
        : HostCall -> Eff<'K, Result<Value, HostCallError>> =
        fun call ->
            eff {
                match! guard call with
                | Error e -> return Error e
                | Ok() ->
                    let! journaled = watch.Calling call
                    let! result = HostBinding.dispatch (bindings (current ())) call

                    match journaled with
                    | Some index -> do! watch.Called index result
                    | None -> ()

                    record call result
                    return result
            }

    /// The live kernel, or a new one (`Restarted` when an earlier one died, here or before the host
    /// restarted: then at most `restore` kept values come back as `Out[cell]`). Launches are journaled
    /// before they start, so their numbers (and owned directories) are never reused. Runs in the hands'
    /// environment, so the kernel lives in its scope rather than the turn's.
    member this.Acquire
        (hands: Hands<'K>, limits: KernelLimits, restore: int)
        : Eff<'K, Result<Kernel * KernelNote, string>> =
        eff {
            let alive =
                lock gate (fun () ->
                    match kernel with
                    | Some k when not (Kernel.exited k).IsCompleted -> Some k
                    | _ -> None)

            match alive with
            | Some k -> return Ok(k, KernelNote.Same)
            | None ->
                let! known =
                    match lock gate (fun () -> launches) with
                    | Some last -> Eff.ret last
                    | None -> journal.LastLaunch

                let previous, launch, here =
                    lock gate (fun () ->
                        let previous = kernel
                        // Whether an earlier kernel of this session started in this process.
                        let here = launches |> Option.exists (fun last -> last > 0)
                        let last = launches |> Option.defaultValue known
                        kernel <- None
                        launches <- Some(last + 1)
                        previous, last + 1, here)

                match previous with
                | Some dead -> do! Eff.shield (Kernel.close dead |> Eff.ignore)
                | None -> ()

                let core = current ()
                let bridge = bridge core

                match Bridge.validate bridge with
                | _ :: _ as issues -> return Error("invalid bridge:\n" + Issue.renderAtMost 10 issues)
                | [] ->
                    let host: KernelHost<'K> =
                        {
                            HostCall = this.Call hands.Guard
                            Finish = finishing.Validate
                            Output = watch.Output
                            Activity = fun _ -> Eff.unit
                        }

                    let start: StartKernel<'K> =
                        {
                            Host = host
                            Setup = Bridge.setup bridge limits (Some hands.Workspace)
                            Surface = Bridge.surface bridge
                            Launch = launch
                        }

                    do! journal.Launching launch

                    match! hands.Interpreter.Start start with
                    | Error e -> return Error e
                    | Ok started ->
                        lock gate (fun () -> kernel <- Some started)

                        if launch > 1 then
                            let! restored = journal.Restore restore started

                            let why =
                                if here then
                                    "the previous kernel stopped"
                                else
                                    "the host restarted"

                            return Ok(started, KernelNote.Restarted(why, restored))
                        else
                            return Ok(started, KernelNote.Same)
        }

    /// Runs a cell, shielded from the turn: if the turn is cancelled (an interrupt), the cell is
    /// stopped in stages (`Kernel.interrupt`: cancel, then kill), and the cancellation goes on. A kernel
    /// that exited or was killed is dropped; the next cell starts a new one.
    member _.Run(k: Kernel, cell: CellRun, turn: CancellationToken) : Eff<'K, CellRecord> =
        eff {
            let state = RunningCell cell.CellId
            lock gate (fun () -> running <- Some state)

            let untilCancelled: Eff<'K, unit> =
                fun env ->
                    task {
                        let signal =
                            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                        use _ = turn.Register(fun () -> signal.TrySetResult() |> ignore)
                        use _ = env.Scope.Cancellation.Register(fun () -> signal.TrySetCanceled() |> ignore)
                        do! signal.Task
                    }

            let interrupt: Eff<'K, Ran> =
                eff {
                    do! untilCancelled
                    let! how = Kernel.interrupt k
                    return Ran.Interrupted how
                }

            try
                let! outcome =
                    Eff.shield (Eff.race [ Kernel.run k cell |> Eff.map Ran.Answered; interrupt ])

                let exited =
                    match outcome with
                    | Ran.Answered(Error(KernelFailure.Exited _))
                    | Ran.Interrupted Interruption.Killed -> true
                    | _ -> (Kernel.exited k).IsCompleted

                if exited then
                    lock gate (fun () ->
                        match kernel with
                        | Some current when obj.ReferenceEquals(current, k) -> kernel <- None
                        | _ -> ())

                    do! Eff.shield (Kernel.close k |> Eff.ignore)

                match outcome with
                | Ran.Interrupted _ -> return raise (OperationCanceledException turn)
                | Ran.Answered result ->
                    return
                        lock gate (fun () ->
                            {
                                Result = result
                                Finished = state.Finished
                                Reads = state.Reads
                            })
            finally
                lock gate (fun () -> running <- None)
        }

    /// Shuts the kernel down (the host stops).
    member _.Close() : Eff<'K, unit> =
        eff {
            let current =
                lock gate (fun () ->
                    let current = kernel
                    kernel <- None
                    current)

            match current with
            | Some k -> do! Eff.shield (Kernel.close k |> Eff.ignore)
            | None -> ()
        }

    override _.ToString() = $"kernel of {session}"
