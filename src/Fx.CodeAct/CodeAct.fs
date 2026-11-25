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

    /// Runs one action and gives its receipt, and the result `finish` accepted in it.
    let execute
        (hands: Hands<'K>)
        (kernel: SessionKernel<'K>)
        (settings: CodeActSettings)
        (limits: KernelLimits)
        (cellId: string)
        (action: ToolAction)
        : Eff<'R, Executed * TurnResult option> =
        eff {
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
                let! acquired = Eff.provide hands.Env (kernel.Acquire(hands, limits))

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

    let turn (context: TurnContext<'R>) (hands: Hands<'K>) (kernel: SessionKernel<'K>) (settings: CodeActSettings) =
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

                            for index, toolCall in List.indexed calls do
                                let! executed, finished =
                                    if state.Outcome.IsSome then
                                        Eff.ret (
                                            {
                                                Pieces = Receipts.skipped
                                                IsError = true
                                            },
                                            None
                                        )
                                    else
                                        match Tools.decode surface toolCall with
                                        | Error message ->
                                            Eff.ret (
                                                {
                                                    Pieces = Receipts.failed message
                                                    IsError = true
                                                },
                                                None
                                            )
                                        | Ok action ->
                                            execute
                                                hands
                                                kernel
                                                settings
                                                limits
                                                $"t{start.Turn}-s{step}-{index + 1}"
                                                action

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
                let kernel = kernels.GetOrAdd(start.Session, (fun id -> SessionKernel<'K> id))

                envs[start.Session] <- hands.Env
                let! held = kernel.Enter()
                use _ = held
                return! turn context hands kernel settings
        }

    /// The turn runner sessions run their turns with.
    member _.Runner: TurnRunner<'R> = TurnRunner.create run

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
