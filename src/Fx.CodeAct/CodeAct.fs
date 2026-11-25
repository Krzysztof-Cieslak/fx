namespace Fx.CodeAct

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Kernel
open Fx.Envd
open Fx.Interpreters
open Fx.Prompt
open Fx.Sdk
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Variants

/// What the CodeAct strategy needs from the host (spec §3: the host's agent runtime). The composition
/// root (T129) builds it from the host's stores, profiles, environments and model catalog.
[<NoEquality; NoComparison>]
type CodeActConfig<'R, 'K> =
    {
        /// The session events (`SessionLog.typed` over the host's event store).
        Sessions: EventLog<Event>
        /// The context events (`ContextLog.typed` over the same store), where turns record theirs.
        Contexts: EventLog<ContextEvent>
        /// The step journal (`StepJournal.typed` over the same store): calls before they run, their
        /// host calls and output, kernel launches and cells' values (crash recovery, `recall`).
        Steps: EventLog<StepEvent>
        /// The host's blob store, for cells' values too large to keep in the journal; without one only
        /// values up to `values.inlineMaxBytes` are kept.
        Blobs: Fx.Store.Blobs option
        /// The variants of a profile (a turn runs under the profile bound at its start).
        Profile: string -> Eff<'R, Variants>
        /// A session's hands: the environment its kernel runs in (`IEnvs.Open`) and its interpreter.
        Hands: TurnStart -> Eff<'R, Result<Hands<'K>, string>>
        /// A model's catalog entry, if known: its caching shapes the request's cache breakpoints.
        Catalog: ModelRef -> ModelInfo option
    }

    override _.ToString() = "CodeActConfig"

/// What a model call came to, after the retry and empty-reply policies.
[<RequireQualifiedAccess>]
type private Called =
    | Reply of ChatResponse
    | Failed of reason: string

/// A turn the host stopped during, going on after people decided what happened to its call in doubt.
type private Resumed =
    {
        Halted: Halted
        Card: OutcomeUnknown
        Decided: Decided
    }

    override this.ToString() =
        $"resumed {this.Halted} ({this.Decided})"

/// One turn's state as it runs.
type private TurnState(settings: CodeActSettings) =
    member val Step = 0 with get, set
    member val Outcome: Result<TurnResult, string> option = None with get, set
    member val Tokens = 0L with get, set
    member val Guard = LoopGuard.empty with get, set
    member val Cuts = 0 with get, set
    member val Nudged = false with get, set
    /// Notes for the next call's suffix.
    member val Notes: Block list = [] with get, set
    member _.Settings = settings

/// The CodeAct strategy (spec §4.4–4.5, §6.9): a turn runner that, step by step, takes steers at the
/// boundary, renders the prompt from the log, calls the model, records its reply, runs the actions it
/// asked for (cells, and on the hybrid surface direct tools) and records their receipts, until a cell's
/// `finish` is accepted or a policy ends the turn. Keeps one kernel per session across turns.
[<Sealed>]
type CodeAct<'R, 'K
    when 'R :> IClock
    and 'R :> IScopeWith<'R>
    and 'R :> ILlm
    and 'K :> IShell
    and 'K :> IFs
    and 'K :> IFsOps
    and 'K :> IClock
    and 'K :> IScopeWith<'K>>(config: CodeActConfig<'R, 'K>) =
    let kernels =
        ConcurrentDictionary<string, SessionKernel<'K>>(StringComparer.Ordinal)

    let envs = ConcurrentDictionary<string, 'K>(StringComparer.Ordinal)

    let writers = ConcurrentDictionary<string, JournalWriter>(StringComparer.Ordinal)

    let writerOf (session: string) : JournalWriter =
        writers.GetOrAdd(session, (fun id -> JournalWriter(config.Steps, id)))

    let journalOf (session: string) : Eff<'E, Recorded<StepEvent> list> when 'E :> IScope =
        StepJournal.load config.Steps session

    /// A host call's arguments or result, as the journal keeps it.
    let shown (value: Value) : string =
        match value with
        | Value.Inline json -> Json.serialize json
        | other -> string other

    /// What the step journal sees of a session's kernel: the running action's host calls (not the
    /// reads) and output.
    let watchOf (writer: JournalWriter) : Watch<'K> =
        {
            Calling =
                fun call ->
                    match writer.Running with
                    | Some action when
                        not (List.contains call.Binding StepJournal.readOnly)
                        && (call.CellId.IsNone || call.CellId = action.Cell)
                        ->
                        let token, index = writer.BeginHostCall action

                        let args =
                            Json.serialize (
                                Json.Array(
                                    call.Args
                                    |> List.map (fun arg ->
                                        match arg with
                                        | Value.Inline json -> json
                                        | other -> Json.String(string other))
                                )
                            )

                        writer.Append(
                            $"{action.Key}/host/{index}",
                            StepEvent.HostCallStarted
                                {
                                    Call = action.Call
                                    Index = index
                                    Binding = call.Binding
                                    Args = StepJournal.bound StepJournal.ArgsChars args
                                }
                        )
                        |> Eff.map (fun () -> Some token)
                    | _ -> Eff.ret None
            Called =
                fun token result ->
                    match writer.EndHostCall token with
                    | Some(action, index) ->
                        let failed, summary =
                            match result with
                            | Ok value -> false, shown value
                            | Error e -> true, string e

                        writer.Append(
                            $"{action.Key}/host/{index}/ended",
                            StepEvent.HostCallEnded
                                {
                                    Call = action.Call
                                    Index = index
                                    Failed = failed
                                    Summary = StepJournal.bound StepJournal.ArgsChars summary
                                }
                        )
                    | None -> Eff.unit
            Output =
                fun output ->
                    match writer.Running with
                    | Some action when not output.Late && (output.CellId.IsNone || output.CellId = action.Cell) ->
                        match action.NextPrinted output.Text.Length with
                        | Some(n, take) ->
                            let text =
                                if take >= output.Text.Length then
                                    output.Text
                                else
                                    StepJournal.bound (take + 1) output.Text

                            writer.Append(
                                $"{action.Key}/output/{n}",
                                StepEvent.Printed { Call = action.Call; Text = text }
                            )
                        | None -> Eff.unit
                    | _ -> Eff.unit
        }

    /// The kernel of a session, with its journal: launches numbered on from the journal's, the values
    /// kept for `recall` restored into new kernels, and the `recall` binding.
    let kernelOf (session: string) : SessionKernel<'K> =
        kernels.GetOrAdd(
            session,
            fun id ->
                let writer = writerOf id

                let journal: KernelJournal<'K> =
                    {
                        LastLaunch = journalOf id |> Eff.map StepJournal.lastLaunch
                        Launching = fun launch -> writer.Append($"kernel/{launch}", StepEvent.KernelLaunched launch)
                        Restore =
                            fun limit kernel ->
                                eff {
                                    let! journal = journalOf id
                                    return! Recall.restore config.Blobs limit journal kernel
                                }
                    }

                SessionKernel<'K>(id, watchOf writer, journal, [ Recall.binding (journalOf id) config.Blobs ])
        )

    /// The cell id of a call's attempt in its kernel.
    let cellIdOf (call: CallRef) : string =
        if call.Attempt <= 1 then
            call.Name
        else
            $"{call.Name}-{call.Attempt}"

    let takeSnapshot
        (snapshots: Snapshots)
        (message: string)
        : Eff<'E, Result<SnapshotRef, SnapshotError>> when 'E :> IScope =
        fun env -> snapshots.Take message env.Scope.Cancellation

    let diffSnapshots
        (snapshots: Snapshots)
        (before: SnapshotRef)
        (after: SnapshotRef)
        : Eff<'E, Result<FileChange list, SnapshotError>> when 'E :> IScope =
        fun env -> snapshots.Diff before after env.Scope.Cancellation

    let withToken (env: 'K) (token: Threading.CancellationToken) : 'K =
        env.WithScope { env.Scope with Cancellation = token }

    let isEmpty (response: ChatResponse) =
        response.Parts
        |> List.forall (fun part ->
            match part with
            | Part.Text t -> String.IsNullOrWhiteSpace t.Text
            | Part.Reasoning _
            | Part.CacheBreakpoint _ -> true
            | _ -> false)

    let textOf (parts: Part list) =
        parts
        |> List.choose (fun part ->
            match part with
            | Part.Text t when not (String.IsNullOrWhiteSpace t.Text) -> Some(t.Text.Trim())
            | Part.Refusal text -> Some text
            | _ -> None)
        |> String.concat "\n\n"

    let callsOf (parts: Part list) =
        parts
        |> List.choose (fun part ->
            match part with
            | Part.ToolCall call -> Some call
            | _ -> None)

    let delay (settings: CodeActSettings) (attempt: int) (error: ChatError option) =
        match error |> Option.bind ChatError.retryAfter with
        | Some wait -> min wait (TimeSpan.FromMinutes 5.0)
        | None ->
            let factor = Math.Pow(2.0, float (max 0 (attempt - 1)))
            TimeSpan.FromMilliseconds(min 60000.0 (settings.RetryDelay.TotalMilliseconds * factor))

    /// Calls the model: retryable errors and empty replies are tried again (with backoff), context
    /// overflow and other errors end the call with a reason.
    let call (settings: CodeActSettings) (request: ChatRequest) : Eff<'R, Called> =
        eff {
            let mutable result = None
            let mutable failures = 0
            let mutable empties = 0

            while result.IsNone do
                let! stream = Llm.stream request
                let! state = ChatStream.fold stream

                match ChatAggregate.result state with
                | Ok response when isEmpty response ->
                    empties <- empties + 1

                    if empties > settings.EmptyRetries then
                        result <- Some(Called.Failed $"the model gave {empties} empty replies in a row")
                    else
                        do! Clock.sleep (delay settings empties None)
                | Ok response -> result <- Some(Called.Reply response)
                | Error failure when ChatError.needsCompaction failure.Error ->
                    result <-
                        Some(
                            Called.Failed
                                $"the conversation does not fit the model's context window and needs compaction: {failure.Error}"
                        )
                | Error failure when ChatError.isRetryable failure.Error && failures < settings.ModelRetries ->
                    failures <- failures + 1
                    do! Clock.sleep (delay settings failures (Some failure.Error))
                | Error failure -> result <- Some(Called.Failed $"the model call failed: {failure.Error}")

            return result.Value
        }

    /// Opens a question for the user and ends the turn waiting for its answer.
    let ask
        (context: TurnContext<'R>)
        (step: int)
        (reason: string)
        (text: string)
        : Eff<'R, Result<TurnResult, string>> =
        eff {
            let id = $"q-{context.Turn.Turn}-{step}"
            let question = if String.IsNullOrWhiteSpace text then "(no text)" else text

            match! context.Open(Questions.request id { Question = question; Reason = reason }) with
            | Ok _ -> return Ok(TurnResult.NeedInput [ id ])
            | Error rejection -> return Error $"the agent's question could not be asked: {rejection.Message}"
        }

    /// Runs one action and gives its receipt, and the result `finish` accepted in it. A cell's value
    /// is kept in the journal for `recall`.
    let execute
        (hands: Hands<'K>)
        (kernel: SessionKernel<'K>)
        (settings: CodeActSettings)
        (limits: KernelLimits)
        (journaled: RunningAction)
        (action: ToolAction)
        : Eff<'R, Executed * TurnResult option> =
        eff {
            let cellId = cellIdOf journaled.Call
            let! token = Eff.cancellation
            let env = withToken hands.Env token
            let call = kernel.Call hands.Guard

            match action with
            | ToolAction.Read input ->
                let! executed = Eff.provide env (Hybrid.read call settings.ReadLines input)
                return executed, None
            | ToolAction.Edit input ->
                let! executed = Eff.provide env (Hybrid.edit call input)
                return executed, None
            | ToolAction.Bash input ->
                let! executed = Eff.provide env (Hybrid.bash call settings.ReceiptChars input)
                return executed, None
            | ToolAction.Cell code ->
                let intent = Cell.intent code

                let! acquired =
                    Eff.provide hands.Env (kernel.Acquire(hands, limits, settings.RecallRestore))

                match acquired with
                | Error reason ->
                    return
                        {
                            Pieces = Receipts.failed $"The kernel did not start: {reason}"
                            IsError = true
                        },
                        None
                | Ok(running, note) ->
                    let cell: CellRun =
                        {
                            CellId = cellId
                            Code = code
                            Intent = intent
                            TimeoutMs = Some settings.CellTimeoutMs
                            Check = settings.TypeCheck && Kernel.supports running "cell/check"
                        }

                    let! record = Eff.provide env (kernel.Run(running, cell, token))

                    match record.Result with
                    | Ok result ->
                        match result.Outcome with
                        | CellOutcome.Completed(Some info) when not (Kernel.exited running).IsCompleted ->
                            let! kept =
                                Eff.provide
                                    env
                                    (Recall.keep
                                        config.Blobs
                                        settings.InlineBytes
                                        settings.RecallMaxBytes
                                        running
                                        journaled.Call
                                        cellId
                                        info)

                            do! (writerOf kernel.Session).Append($"{journaled.Key}/value", kept)
                        | _ -> ()

                        return
                            {
                                Pieces = Receipts.cell settings.ReceiptChars note intent record.Reads result
                                IsError = Receipts.isError result.Outcome
                            },
                            record.Finished
                    | Error failure ->
                        return
                            {
                                Pieces = Receipts.kernelFailed note failure kernel.Kernel.IsNone
                                IsError = true
                            },
                            None
        }

    /// Journals a call's start (before anything it does), runs it as the session's running action, and
    /// gives its receipt and the result `finish` accepted in it. The receipt, recorded by the caller,
    /// is the call's end.
    let runAction
        (prefix: string)
        (hands: Hands<'K>)
        (kernel: SessionKernel<'K>)
        (settings: CodeActSettings)
        (limits: KernelLimits)
        (call: CallRef)
        (toolCall: ToolCall)
        (action: ToolAction)
        (snapshot: SnapshotRef option)
        : Eff<'R, Executed * TurnResult option> =
        eff {
            let writer = writerOf kernel.Session
            let key = $"{prefix}/step/{call.Step}/call/{call.Index}/attempt/{call.Attempt}"

            let cell, intent =
                match action with
                | ToolAction.Cell code -> Some(cellIdOf call), Cell.intent code
                | _ -> None, None

            do!
                writer.Append(
                    key + "/started",
                    StepEvent.CallStarted
                        {
                            Call = call
                            Tool = toolCall.Name
                            Cell = cell
                            Intent = intent
                            Fingerprint = StepJournal.fingerprint toolCall.Name toolCall.Arguments
                            Snapshot = snapshot
                        }
                )

            let journaled = RunningAction(call, key, cell)
            writer.SetRunning(Some journaled)

            try
                return! execute hands kernel settings limits journaled action
            finally
                writer.SetRunning None
        }

    /// The workspace before a step's actions, if the hands have snapshots and they are on.
    let snapshotOf (hands: Hands<'K>) (settings: CodeActSettings) (message: string) : Eff<'R, SnapshotRef option> =
        eff {
            match hands.Snapshots with
            | Some snapshots when settings.Snapshots ->
                match! takeSnapshot snapshots message with
                | Ok snapshot -> return Some snapshot
                | Error _ -> return None
            | _ -> return None
        }

    let turn
        (context: TurnContext<'R>)
        (hands: Hands<'K>)
        (kernel: SessionKernel<'K>)
        (settings: CodeActSettings)
        (resume: Resumed option)
        =
        eff {
            let start = context.Turn
            let session = start.Session

            match ContentExclusions.compile settings.Exclusions, settings.Model |> Option.map ModelRef.tryParse with
            | Error issues, _ -> return Error("the content exclusions do not compile:\n" + Issue.renderAtMost 10 issues)
            | _, None -> return Error "no model is configured for the main role (model.role.main)"
            | _, Some(Error reason) -> return Error $"model.role.main is not a model: {reason}"
            | Ok exclusions, Some(Ok model) ->
                let core =
                    { CoreSettings.defaults hands.Workspace hands.Os with
                        CommandEnv = hands.CommandEnv
                        OutputChars = settings.OutputChars
                        Exclusions = exclusions
                    }

                let limits: KernelLimits =
                    {
                        OutputChars = settings.OutputChars
                        Preview = settings.PreviewLimits
                        InlineBytes = settings.InlineBytes
                    }

                let caching =
                    config.Catalog model
                    |> Option.map (fun info -> info.Caps.Caching)
                    |> Option.defaultValue ModelCaps.minimal.Caching

                kernel.Begin(core, [ Ensure.summary ])
                let interpreter = hands.Interpreter.Prompt(kernel.Surface core)
                let started = EventRef.create (Session.stream session) start.Seq
                let state = TurnState settings

                let record (what: string) (event: ContextEvent) =
                    ContextLog.record
                        config.Sessions
                        config.Contexts
                        session
                        $"{started}/{what}"
                        (ContextEvent.withhold exclusions event)

                let prefix = string started

                // A turn the host stopped during goes on where it stopped: the call in doubt gets its
                // receipt from what people decided (or its new result, if they chose to run it again),
                // the calls after it are answered as not run, and the next step calls the model.
                match resume with
                | None -> ()
                | Some resumed ->
                    let halted = resumed.Halted
                    let call = halted.Started.Call
                    let! history = ContextLog.load config.Sessions config.Contexts session

                    let surface =
                        Instructions.recordedSurface (Transcript.ofItems history.Items).Current
                        |> Option.defaultValue settings.Surface

                    kernel.Begin(core, Ensure.all settings.Evidence history.State)

                    let! executed, finished =
                        match resumed.Decided with
                        | Decided.Rerun _ ->
                            match Tools.decode surface halted.Call with
                            | Error message ->
                                Eff.ret (
                                    {
                                        Pieces = Recovery.rerun resumed.Decided :: Receipts.failed message
                                        IsError = true
                                    },
                                    None
                                )
                            | Ok action ->
                                eff {
                                    let again = { call with Attempt = call.Attempt + 1 }
                                    let! snapshot = snapshotOf hands settings $"{prefix} step {call.Step} again"

                                    let! executed, finished =
                                        runAction prefix hands kernel settings limits again halted.Call action snapshot

                                    return
                                        { executed with
                                            Pieces = Recovery.rerun resumed.Decided :: executed.Pieces
                                        },
                                        finished
                                }
                        | decided ->
                            let pieces, isError = Recovery.receipt settings.ReceiptChars resumed.Card decided
                            Eff.ret ({ Pieces = pieces; IsError = isError }, None)

                    do!
                        record
                            $"step/{halted.Step}/receipt/{call.Index}"
                            (ContextEvent.Returned
                                {
                                    Turn = start.Turn
                                    Step = halted.Step
                                    Call = Some halted.Call.Id
                                    Name = halted.Call.Name
                                    Pieces = executed.Pieces
                                    IsError = executed.IsError
                                })

                    match finished with
                    | Some result -> state.Outcome <- Some(Ok result)
                    | None -> ()

                    for index, notRun in halted.NotRun do
                        do!
                            record
                                $"step/{halted.Step}/receipt/{index}"
                                (ContextEvent.Returned
                                    {
                                        Turn = start.Turn
                                        Step = halted.Step
                                        Call = Some notRun.Id
                                        Name = notRun.Name
                                        Pieces =
                                            if state.Outcome.IsSome then
                                                Receipts.skipped
                                            else
                                                Recovery.notRun
                                        IsError = true
                                    })

                    state.Step <- halted.Step

                while state.Outcome.IsNone do
                    state.Step <- state.Step + 1
                    let step = state.Step
                    let! _ = context.Boundary step
                    let! history = ContextLog.load config.Sessions config.Contexts session
                    let transcript = Transcript.ofItems history.Items

                    let surface =
                        Instructions.recordedSurface transcript.Current
                        |> Option.defaultValue settings.Surface

                    let sections = Instructions.sections surface interpreter

                    let! transcript =
                        match Transcript.reconcile sections transcript with
                        | None -> Eff.ret transcript
                        | Some event ->
                            eff {
                                do! record $"step/{step}/frame" event
                                let! history = ContextLog.load config.Sessions config.Contexts session
                                return Transcript.ofItems history.Items
                            }

                    kernel.Begin(core, Ensure.all settings.Evidence history.State)

                    let overBudget =
                        step > settings.MaxSteps
                        || (settings.MaxTokens |> Option.exists (fun limit -> state.Tokens >= int64 limit))

                    if overBudget then
                        let used =
                            if step > settings.MaxSteps then
                                $"its {settings.MaxSteps} steps"
                            else
                                $"{state.Tokens} tokens"

                        let! asked =
                            ask
                                context
                                step
                                "budget"
                                $"The turn used {used}. Reply to let the agent go on, or say what to do instead."

                        state.Outcome <- Some asked
                    else
                        let cfg = PromptSettings.config settings.Prompt sections exclusions caching
                        let! now = Clock.now

                        let live =
                            { Live.none with
                                Now = Some now.UnixMicroseconds
                                Notes = state.Notes
                            }

                        state.Notes <- []
                        let rendered = Render.render cfg transcript live

                        let template =
                            { ChatRequest.create model [] with
                                Tools = Tools.specs surface hands.Interpreter.Language
                                MaxOutputTokens = settings.MaxOutputTokens
                            }

                        let request = Render.request cfg template rendered

                        match! call settings request with
                        | Called.Failed reason -> state.Outcome <- Some(Error reason)
                        | Called.Reply response ->
                            state.Tokens <- state.Tokens + int64 response.Usage.Input + int64 response.Usage.Output

                            do!
                                record
                                    $"step/{step}/reply"
                                    (ContextEvent.Replied
                                        {
                                            Turn = start.Turn
                                            Step = step
                                            Shown = rendered.Prompt.Suffix
                                            Parts = response.Parts
                                        })

                            let calls = callsOf response.Parts
                            let cut = response.Stop = StopReason.MaxTokens
                            let mutable stop = None
                            let mutable snapshot = None

                            for index, toolCall in List.indexed calls do
                                let decoded =
                                    if state.Outcome.IsSome then
                                        None
                                    else
                                        Some(Tools.decode surface toolCall)

                                // The workspace before the step's first action.
                                match decoded, snapshot with
                                | Some(Ok _), None ->
                                    let! before = snapshotOf hands settings $"{prefix} step {step}"
                                    snapshot <- Some before
                                | _ -> ()

                                let! executed, finished =
                                    match decoded with
                                    | None ->
                                        Eff.ret (
                                            {
                                                Pieces = Receipts.skipped
                                                IsError = true
                                            },
                                            None
                                        )
                                    | Some(Error message) ->
                                        Eff.ret (
                                            {
                                                Pieces = Receipts.failed message
                                                IsError = true
                                            },
                                            None
                                        )
                                    | Some(Ok action) ->
                                        let call: CallRef =
                                            {
                                                Turn = start.Turn
                                                Step = step
                                                Index = index + 1
                                                Id = toolCall.Id
                                                Attempt = 1
                                            }

                                        runAction
                                            prefix
                                            hands
                                            kernel
                                            settings
                                            limits
                                            call
                                            toolCall
                                            action
                                            (snapshot |> Option.flatten)

                                do!
                                    record
                                        $"step/{step}/receipt/{index + 1}"
                                        (ContextEvent.Returned
                                            {
                                                Turn = start.Turn
                                                Step = step
                                                Call = Some toolCall.Id
                                                Name = toolCall.Name
                                                Pieces = executed.Pieces
                                                IsError = executed.IsError
                                            })

                                match finished with
                                | Some result -> state.Outcome <- Some(Ok result)
                                | None ->
                                    let print = LoopGuard.fingerprint toolCall.Name toolCall.Arguments executed.Pieces

                                    let guard, verdict = LoopGuard.observe settings.LoopRepeats print state.Guard
                                    state.Guard <- guard

                                    match verdict with
                                    | LoopVerdict.Stop count when stop.IsNone -> stop <- Some(toolCall.Name, count)
                                    | LoopVerdict.Warn count ->
                                        state.Notes <- state.Notes @ [ Instructions.loopWarning count ]
                                    | _ -> ()

                            if state.Outcome.IsNone then
                                if cut then
                                    if settings.LengthCut = LengthPolicy.Fail then
                                        state.Outcome <- Some(Error "the model's reply was cut at the output limit")
                                    else
                                        state.Cuts <- state.Cuts + 1

                                        if state.Cuts > settings.LengthRetries then
                                            state.Outcome <-
                                                Some(
                                                    Error
                                                        $"the model's replies were cut at the output limit {state.Cuts} times in a row"
                                                )
                                        else
                                            state.Notes <- state.Notes @ [ Instructions.lengthCut ]
                                else
                                    state.Cuts <- 0

                            if state.Outcome.IsNone then
                                match stop with
                                | Some(tool, count) ->
                                    let! asked =
                                        ask
                                            context
                                            step
                                            "loop"
                                            $"The agent did the same thing ({tool}) {count} times with the same result and stopped. How should it go on?"

                                    state.Outcome <- Some asked
                                | None ->
                                    match response.Stop with
                                    | StopReason.ContextWindow ->
                                        state.Outcome <-
                                            Some(
                                                Error
                                                    "the context window filled up during the model's reply: the conversation needs compaction"
                                            )
                                    | StopReason.Refusal
                                    | StopReason.ContentFilter when calls.IsEmpty ->
                                        let text = textOf response.Parts

                                        state.Outcome <- Some(Error $"the model refused ({response.Stop}): {text}")
                                    | StopReason.Pause when calls.IsEmpty -> ()
                                    | _ when calls.IsEmpty && not cut ->
                                        let text = textOf response.Parts

                                        match settings.TextOnly with
                                        | TextOnlyPolicy.Nudge when not state.Nudged ->
                                            state.Nudged <- true
                                            state.Notes <- state.Notes @ [ Instructions.nudge ]
                                        | TextOnlyPolicy.Done when
                                            (Ensure.all settings.Evidence history.State
                                             |> List.collect (fun check -> check (TurnResult.Done(text, [], None))))
                                                .IsEmpty
                                            ->
                                            state.Outcome <- Some(Ok(TurnResult.Done(text, [], None)))
                                        | _ ->
                                            let! asked = ask context step "reply" text
                                            state.Outcome <- Some asked
                                    | _ ->
                                        let left = settings.MaxSteps - step

                                        if left = 3 then
                                            state.Notes <- state.Notes @ [ Instructions.budgetWarning left ]

                return state.Outcome.Value
        }

    let run (context: TurnContext<'R>) : Eff<'R, Result<TurnResult, string>> =
        eff {
            let start = context.Turn
            let! variants = config.Profile start.Profile
            let! settings = CodeActSettings.ofVariants variants

            match! config.Hands start with
            | Error reason -> return Error $"the session's environment is not available: {reason}"
            | Ok hands ->
                let kernel = kernelOf start.Session

                envs[start.Session] <- hands.Env
                let! held = kernel.Enter()
                use _ = held
                return! turn context hands kernel settings None
        }

    /// The workspace's changes since a snapshot, or why they are not known.
    let changesSince
        (hands: Result<Hands<'K>, string>)
        (before: SnapshotRef option)
        : Eff<'R, Result<FileChange list, string>> =
        eff {
            match hands, before with
            | Error reason, _ -> return Error $"the environment is not available: {reason}"
            | Ok hands, _ when hands.Snapshots.IsNone -> return Error "the environment has no workspace snapshots"
            | Ok _, None ->
                return Error "no snapshot of the workspace was recorded before the step (codeact.journal.snapshots)"
            | Ok hands, Some before ->
                match hands.Snapshots with
                | None -> return Error "the environment has no workspace snapshots"
                | Some snapshots ->
                    match! takeSnapshot snapshots "after the host stopped" with
                    | Error e -> return Error(string e)
                    | Ok after ->
                        match! diffSnapshots snapshots before after with
                        | Ok changes -> return Ok changes
                        | Error e -> return Error(string e)
        }

    /// A turn the host stopped during (spec §5, crash recovery): if a call started and has no receipt,
    /// the turn pauses on a decision with the evidence the logs and the workspace give, and goes on
    /// with what people decide; otherwise nothing is in doubt, and the session abandons it.
    let recover (context: TurnContext<'R>) : Eff<'R, Result<TurnResult, string> option> =
        eff {
            let start = context.Turn
            let session = start.Session
            let! journal = journalOf session
            let! history = ContextLog.load config.Sessions config.Contexts session

            match Recovery.find (ContextLog.stream session) start.Turn history.Items journal with
            | None -> return None
            | Some halted ->
                let! variants = config.Profile start.Profile
                let! settings = CodeActSettings.ofVariants variants
                let! hands = config.Hands start
                let! changes = changesSince hands halted.Started.Snapshot

                let card =
                    Recovery.card halted (Recovery.evidence halted.Started.Call journal changes)

                let request = Recovery.request halted card

                match! context.Open request with
                | Error rejection ->
                    return Some(Error $"the decision about the call in doubt could not be opened: {rejection.Message}")
                | Ok opened ->
                    let! resolution = context.Await request.Id opened

                    let resumed =
                        {
                            Halted = halted
                            Card = card
                            Decided = Recovery.decided resolution
                        }

                    match hands with
                    | Error reason -> return Some(Error $"the session's environment is not available: {reason}")
                    | Ok hands ->
                        let kernel = kernelOf session
                        envs[session] <- hands.Env
                        let! held = kernel.Enter()
                        use _ = held
                        let! result = turn context hands kernel settings (Some resumed)
                        return Some result
        }

    /// The turn runner sessions run their turns with.
    member _.Runner: TurnRunner<'R> = { Run = run; Recover = recover }

    /// The kernel of a session, if it has one.
    member _.Kernel(session: string) : Kernel option =
        match kernels.TryGetValue session with
        | true, kernel -> kernel.Kernel
        | _ -> None

    /// Closes every session's kernel (the host stops).
    member _.Close() : Task =
        task {
            for entry in kernels do
                match envs.TryGetValue entry.Key with
                | true, env -> do! Eff.run env (entry.Value.Close())
                | _ -> ()
        }

    override _.ToString() = "CodeAct"

[<RequireQualifiedAccess>]
module CodeAct =
    /// The CodeAct strategy over a configuration.
    let create<'R, 'K
        when 'R :> IClock
        and 'R :> IScopeWith<'R>
        and 'R :> ILlm
        and 'K :> IShell
        and 'K :> IFs
        and 'K :> IFsOps
        and 'K :> IClock
        and 'K :> IScopeWith<'K>>
        (config: CodeActConfig<'R, 'K>)
        : CodeAct<'R, 'K> =
        CodeAct config
