namespace Fx.Host

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Identity.Host
open Fx.Grants
open Fx.Grants.Host
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Prompt
open Fx.CodeAct

/// What a headless run (`fx run -p`) asks for.
type RunRequest =
    {
        Prompt: string
        /// Prompts this session instead of starting a new one (sessions persist across runs).
        Session: string option
        Title: string option
    }

    override this.ToString() =
        match this.Session with
        | Some session -> $"run in {session}"
        | None -> "run"

/// How a headless run talks to the person who started it (the CLI: standard error and the terminal).
[<NoEquality; NoComparison>]
type RunIo =
    {
        /// Shows a line of activity.
        Show: string -> unit
        /// Asks the person to answer a grant request (spec §13: only people answer); `None` when nobody
        /// can (no terminal), which denies it once.
        Answer: GrantRequest -> Task<GrantAnswer option>
        /// Asks the person what happened to a call that was running when the host stopped (a turn
        /// recovered at the host's start waits for it: codeact.md "Crash recovery"); `None` when nobody
        /// can (no terminal), which ends the run and leaves the decision open.
        Decide: OutcomeUnknown -> Task<OutcomeAnswer option>
        /// Cancelled when the person asks to interrupt the turn (Ctrl+C).
        Interrupt: CancellationToken
    }

    override _.ToString() = "RunIo"

/// How a headless run ended.
type RunOutcome =
    {
        Session: string
        Turn: int
        Ending: TurnEnd
        /// The questions of a `NeedInput` ending.
        Questions: string list
    }

    override this.ToString() =
        $"{this.Session} turn {this.Turn}: {this.Ending}"

/// `fx run -p`: one prompt, run headless to the end of its turn in the host's workspace (embedded
/// mode, spec §19.5). The person is signed in in process; grant requests and decisions about calls in
/// doubt are put to them through `RunIo.Answer` and `RunIo.Decide`; Ctrl+C interrupts the turn on the
/// control path.
[<RequireQualifiedAccess>]
module Headless =
    /// The client name a headless run signs in as.
    [<Literal>]
    let Client = "cli"

    /// `{"session", "turn", "ending", "questions"}`: what `--json` prints.
    let outcomeCodec: Codec<RunOutcome> =
        Codec.record {
            let! session = Codec.field "session" (fun (o: RunOutcome) -> o.Session) Codec.string
            and! turn = Codec.field "turn" (fun (o: RunOutcome) -> o.Turn) Codec.int
            and! ending = Codec.field "ending" (fun (o: RunOutcome) -> o.Ending) Codecs.turnEnd

            and! questions =
                Codec.field "questions" (fun (o: RunOutcome) -> o.Questions) (Codec.list Codec.string)

            return
                {
                    Session = session
                    Turn = turn
                    Ending = ending
                    Questions = questions
                }
        }

    /// Puts each grant request of the run to the person, until cancelled: requests of its session (a
    /// turn's host calls, T127b) and requests of no session (the components the run installs), those
    /// pending when the run starts (left by a host that stopped while it asked, and asked again now
    /// with the same key) first. One the person cannot answer (no terminal) is withdrawn by the host,
    /// saying so and how to allow it beforehand: nobody answered it, so it is no person's answer, and
    /// the next run asks again.
    let private answering
        (host: Host)
        (by: Attribution)
        (io: RunIo)
        (session: string option ref)
        (cancellation: CancellationToken)
        : Task =
        let env = Host.within host cancellation

        let pending = GrantsService.pending host.Grants

        let ours (request: GrantRequest) =
            match request.Context.Session with
            | None -> true
            | Some asking -> session.Value = Some asking

        let put (request: GrantRequest) : Eff<HostEnv, unit> =
            eff {
                match! Eff.ofTask (fun () -> io.Answer request) with
                | Some answer ->
                    match! GrantsService.answer host.Grants by request.Id answer with
                    | Ok _ -> ()
                    | Error(GrantError.NotPending _) -> ()
                    | Error e -> io.Show $"  ✗ the answer to {request.Summary} was refused: {e}"
                | None ->
                    io.Show(GrantPrompts.unasked request)

                    match! GrantsService.withdraw host.Grants request.Id GrantPrompts.Unanswered with
                    | Ok _ -> ()
                    | Error(GrantError.NotPending _) -> ()
                    | Error e -> io.Show $"  ✗ {request.Summary} could not be withdrawn: {e}"
            }

        let handle (notice: GrantNotice) : Eff<HostEnv, bool> =
            eff {
                match notice.Event with
                | GrantEvent.Requested request when ours request -> do! put request
                | _ -> ()

                return true
            }

        task {
            try
                do!
                    Eff.run
                        env
                        (eff {
                            for request in pending.Requests do
                                if ours request then
                                    do! put request

                            let! _ = GrantsService.subscribe host.Grants pending.Seq handle
                            ()
                        })
            with _ ->
                // Cancelled, or the host stopped (its stores closed): nothing more to answer.
                ()
        }

    /// Puts each decision about a call in doubt that the session's turn waits for (open at `from`, or
    /// opened later) to the person, until cancelled; `stuck` gets the question nobody can answer.
    let private deciding
        (host: Host)
        (session: string)
        (by: Attribution)
        (io: RunIo)
        (from: SessionState)
        (stuck: string -> unit)
        (cancellation: CancellationToken)
        : Task =
        let env = Host.within host cancellation
        let asked = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

        let decide (request: RequestSpec) : Eff<HostEnv, unit> =
            eff {
                if request.Kind = Recovery.KindId && asked.Add request.Id then
                    match Codec.decode Recovery.payload request.Payload with
                    | Error _ -> ()
                    | Ok card ->
                        match! Eff.ofTask (fun () -> io.Decide card) with
                        | None -> stuck card.Question
                        | Some answer ->
                            let! token = Eff.cancellation

                            let! outcome =
                                Eff.ofTask (fun () ->
                                    HostSessions.send
                                        host
                                        session
                                        $"{session}/decide/{request.Id}"
                                        by
                                        (Command.Answer(request.Id, Codec.encode Recovery.answer answer))
                                        token)

                            match outcome with
                            | Rejected rejection -> io.Show $"  ✗ the decision was refused: {rejection}"
                            | Accepted _
                            | Stopped -> ()
            }

        task {
            try
                do!
                    Eff.run
                        env
                        (eff {
                            for pending in from.Requests do
                                do! decide pending.Request

                            let! _ =
                                SessionHost.subscribe host.Sessions session from.Seq (fun feed ->
                                    match feed with
                                    | Feed.Next recorded ->
                                        match recorded.Event with
                                        | Event.RequestOpened pending ->
                                            decide pending.Request |> Eff.map (fun () -> true)
                                        | _ -> Eff.ret true
                                    | Feed.Reset _ -> Eff.ret true)

                            ()
                        })
            with _ ->
                // Cancelled, or the host stopped: nothing more to decide.
                ()
        }

    /// Shows what the session's turns do, until cancelled.
    let private following (host: Host) (session: string) (io: RunIo) (cancellation: CancellationToken) : Task =
        let env = Host.within host cancellation

        let contexts = ContextLog.typed host.Stores.Log

        task {
            try
                let! from = Eff.run env (EventLog.head contexts (ContextLog.stream session))

                let! _ =
                    Eff.run
                        env
                        (EventLog.subscribe contexts (ContextLog.stream session) from (fun recorded ->
                            for line in Activity.ofContext recorded.Event do
                                io.Show line

                            Eff.ret true))

                ()
            with _ ->
                // Cancelled, or the host stopped: nothing more to show.
                ()
        }

    /// Waits for the first turn that ends after a sequence number of the session's log.
    let private ended (host: Host) (session: string) (after: int64) : Eff<HostEnv, (int * TurnEnd) option> =
        eff {
            let mutable result = None

            let! _ =
                SessionHost.subscribe host.Sessions session after (fun feed ->
                    match feed with
                    | Feed.Next recorded ->
                        match recorded.Event with
                        | Event.TurnEnded(turn, ending, _) ->
                            result <- Some(turn, ending)
                            Eff.ret false
                        | _ -> Eff.ret true
                    | Feed.Reset _ -> Eff.ret true)

            return result
        }

    /// Whether a new session's turns could call their model: one is configured for the main role, and
    /// its provider's credentials resolve (spec §11.3). Checked before the session's environment
    /// installs anything, so a run that cannot call its model fails at once, saying what the turn
    /// would have said, rather than after a download.
    let modelReady (host: Host) : Eff<HostEnv, Result<unit, string>> =
        eff {
            let variants = Fx.Variants.Variants.ofResolution host.Resolution

            match
                Setup.value (Fx.Variants.Slots.modelRole "main") variants
                |> Option.map Fx.Models.ModelRef.tryParse
            with
            | None -> return Error "no model is configured for the main role (model.role.main)"
            | Some(Error reason) -> return Error $"model.role.main is not a model: {reason}"
            | Some(Ok model) ->
                match Setup.providers host.Extensions |> List.tryFind (fun p -> p.Id = model.Provider) with
                // Model access says so at the call.
                | None -> return Ok()
                | Some provider ->
                    match! Fx.Auth.AuthService.resolve host.Auth provider with
                    | Ok _ -> return Ok()
                    | Error e ->
                        let failure = Fx.Models.ChatError.Unavailable(provider.Id + ": " + string e)
                        return Error $"the model call would fail: {failure}"
        }

    /// Runs one prompt to the end of its turn.
    let run
        (host: Host)
        (request: RunRequest)
        (io: RunIo)
        (cancellation: CancellationToken)
        : Task<Result<RunOutcome, string>> =
        task {
            match! HostSessions.signInLocal host Client cancellation with
            | Error e -> return Error e
            | Ok signedIn ->
                let by = ClientSession.attribution signedIn
                use watching = CancellationTokenSource.CreateLinkedTokenSource cancellation
                // The run's session, once known: its turns' requests are the run's to answer.
                let running = ref request.Session
                let answers = answering host by io running watching.Token

                try
                    let! session =
                        match request.Session with
                        | Some id ->
                            match HostSessions.tryFind host id with
                            | Some entry -> Task.FromResult(Ok entry)
                            | None -> Task.FromResult(Error $"no session {id} on this host")
                        | None ->
                            task {
                                // Before the session's environment installs components (a download).
                                match! Eff.run (Host.within host cancellation) (modelReady host) with
                                | Error e -> return Error e
                                | Ok() ->
                                    let id = string (host.Options.Ids.NewId())
                                    io.Show $"session {id}"
                                    return! HostSessions.create host by id request.Title cancellation
                            }

                    match session with
                    | Error e -> return Error e
                    | Ok entry ->
                        let id = entry.Session
                        running.Value <- Some id

                        // The session's environment and its sandbox, once, and what a person must know
                        // about it: e.g. that the sandbox the host asked for cannot run (spec §9.1,
                        // §19.5). The sandbox is opt-in (T137): off by default, quietly.
                        let! environment =
                            (Fx.Environments.EnvsHost.envs host.Environments).Get entry.Environment cancellation

                        match environment with
                        | Some record ->
                            let sandbox =
                                match record.Sandbox with
                                | Some sandbox -> " · sandbox: " + Fx.Environments.Envs.sandboxLabel sandbox
                                | None -> ""

                            io.Show $"env: {record.Spec.Name} ({record.Spec.Provider}){sandbox}"

                            for warning in Fx.Environments.Envs.warnings record do
                                io.Show $"warning: {warning}"
                        | None -> ()

                        // Auto approvals, once: what runs without asking, and with what rights (T134).
                        let! approvals = Eff.run (Host.within host cancellation) (HostApprovals.view host id)

                        match HostApprovals.notice approvals with
                        | Some line -> io.Show line
                        | None -> ()

                        let follow = following host id io watching.Token
                        let! before = HostSessions.state host id cancellation
                        let key = $"{id}/prompt/{before.Seq}"

                        match!
                            HostSessions.send
                                host
                                id
                                key
                                by
                                (Command.Prompt(request.Prompt, PromptMode.Steer))
                                cancellation
                        with
                        | Rejected rejection -> return Error $"the prompt was refused: {rejection}"
                        | Stopped -> return Error "the session host stopped"
                        | Accepted appended ->
                            // A decision nobody here can make ends the wait for the turn.
                            use waiting = CancellationTokenSource.CreateLinkedTokenSource cancellation
                            let undecided = ref None

                            let stuck (question: string) =
                                undecided.Value <- Some question
                                waiting.Cancel()

                            let decisions = deciding host id by io before stuck watching.Token

                            use _ =
                                io.Interrupt.Register(fun () ->
                                    HostSessions.send
                                        host
                                        id
                                        $"{id}/interrupt/{appended.First}"
                                        by
                                        Command.Interrupt
                                        CancellationToken.None
                                    |> ignore)

                            let env = Host.within host waiting.Token

                            let! ended = Eff.run env (Eff.catch (ended host id appended.After))
                            watching.Cancel()
                            do! follow
                            do! decisions

                            match ended with
                            | Error(:? OperationCanceledException) when undecided.Value.IsSome ->
                                return
                                    Error(
                                        $"the session's turn waits for a decision about a call that was running when the host stopped: {undecided.Value.Value} Decide it from a terminal."
                                    )
                            | Error(:? OperationCanceledException) when cancellation.IsCancellationRequested ->
                                return Error "cancelled before the turn ended"
                            | Error _
                            | Ok None -> return Error "the host stopped before the turn ended"
                            | Ok(Some(turn, ending)) ->
                                let! state = HostSessions.state host id cancellation

                                let questions =
                                    match ending with
                                    | TurnEnd.Completed(TurnResult.NeedInput ids) -> HostSessions.questions state ids
                                    | _ -> []

                                return
                                    Ok
                                        {
                                            Session = id
                                            Turn = turn
                                            Ending = ending
                                            Questions = questions
                                        }
                finally
                    watching.Cancel()
                    answers.Wait(TimeSpan.FromSeconds 10.0) |> ignore
        }

    /// The exit code of a run: 0 done or waiting, 3 needs input, 130 interrupted, 1 otherwise.
    let exitCode (outcome: RunOutcome) : int =
        match outcome.Ending with
        | TurnEnd.Completed(TurnResult.Done _)
        | TurnEnd.Completed(TurnResult.Waiting _) -> 0
        | TurnEnd.Completed(TurnResult.NeedInput _) -> 3
        | TurnEnd.Interrupted -> 130
        | TurnEnd.Failed _
        | TurnEnd.Abandoned _ -> 1
