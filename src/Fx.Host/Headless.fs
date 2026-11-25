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
/// mode, spec §19.5). The person is signed in in process; grant requests are put to them through
/// `RunIo.Answer`; Ctrl+C interrupts the turn on the control path.
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

    /// Puts each grant request that opens to the person, until cancelled.
    let private answering (host: Host) (by: Attribution) (io: RunIo) (cancellation: CancellationToken) : Task =
        let env = Host.within host cancellation

        let from = (GrantsService.snapshot host.Grants).Seq

        let handle (notice: GrantNotice) : Eff<HostEnv, bool> =
            eff {
                match notice.Event with
                | GrantEvent.Requested request ->
                    let! answer = Eff.ofTask (fun () -> io.Answer request)

                    let answer =
                        answer
                        |> Option.defaultValue
                            {
                                Effect = GrantEffect.Deny
                                Scope = ScopeChoice.Once
                                Resource = None
                                Expires = None
                                Reason = Some "nobody could answer (not run from a terminal)"
                            }

                    match! GrantsService.answer host.Grants by request.Id answer with
                    | Ok _ -> ()
                    | Error e -> io.Show $"  ✗ the answer to {request.Summary} was refused: {e}"

                    return true
                | _ -> return true
            }

        task {
            try
                let! _ = Eff.run env (GrantsService.subscribe host.Grants from handle)
                ()
            with _ ->
                // Cancelled, or the host stopped (its stores closed): nothing more to answer.
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
                let answers = answering host by io watching.Token

                try
                    let! session =
                        match request.Session with
                        | Some id ->
                            match HostSessions.tryFind host id with
                            | Some entry -> Task.FromResult(Ok entry)
                            | None -> Task.FromResult(Error $"no session {id} on this host")
                        | None ->
                            task {
                                let id = string (host.Options.Ids.NewId())
                                io.Show $"session {id}"
                                return! HostSessions.create host by id request.Title cancellation
                            }

                    match session with
                    | Error e -> return Error e
                    | Ok entry ->
                        let id = entry.Session
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

                            let env = Host.within host cancellation

                            let! ended = Eff.run env (Eff.catch (ended host id appended.After))
                            watching.Cancel()
                            do! follow

                            match ended with
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
