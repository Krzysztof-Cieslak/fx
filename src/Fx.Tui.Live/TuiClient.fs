namespace Fx.Tui.Live

open System
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Sessions
open Fx.Grants.Host
open Fx.Host
open Fx.Tui

/// The TUI's environment: its clock, its terminal and its scope.
type TuiEnv =
    {
        Clock: Clock
        Tty: Tty
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITty with
        member env.Tty = env.Tty

    interface IScopeWith<TuiEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "TuiEnv"

/// Which session the TUI shows.
[<RequireQualifiedAccess>]
type SessionChoice =
    /// A new one, with a title if given.
    | New of title: string option
    /// This one.
    | Resume of id: string
    /// The newest session of the workspace, or a new one if it has none.
    | Latest of workspace: string

    override this.ToString() =
        match this with
        | New _ -> "new"
        | Resume id -> "resume " + id
        | Latest workspace -> "latest in " + workspace

/// Which session the TUI follows now: notices of any other (one it left may still send a few) are
/// dropped before they reach the model.
[<Sealed>]
type Following() =
    let mutable session = ""

    member _.Session
        with get () = Volatile.Read &session
        and set (value: string) = Volatile.Write(&session, value)

    member this.Shows(id: string) = id = this.Session

    override this.ToString() = "following " + this.Session

/// Sessions the person asked to switch to, taken by the loop that follows them.
[<Sealed>]
type private Switches() =
    let gate = obj ()

    let mutable waiting =
        TaskCompletionSource<SessionChoice>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Post(choice: SessionChoice) =
        lock gate (fun () -> waiting.TrySetResult choice |> ignore)

    /// The next switch asked for (after the last one taken).
    member _.Next: Task<SessionChoice> = lock gate (fun () -> waiting.Task)

    member _.Take() =
        lock gate (fun () ->
            if waiting.Task.IsCompleted then
                waiting <- TaskCompletionSource<SessionChoice>(TaskCreationOptions.RunContinuationsAsynchronously))

    override _.ToString() = "Switches"

/// The TUI as a client of the client protocol (spec §17): it follows the session (`session/subscribe`
/// and `session/context`), the grants log (`grants/pending`, `grants/subscribe`) and the session's
/// environment (`env/get`), and sends what the person does as commands. The same in process
/// (embedded mode) and over the local listener (`fx attach`).
[<RequireQualifiedAccess>]
module TuiClient =
    /// How the TUI introduces itself in `initialize`.
    let info: PeerInfo =
        {
            Name = Product.binaryName + "-tui"
            Version = Product.Version
        }

    /// The client's side of the connection: notifications of the session it follows become messages.
    let service (inbox: Inbox<Msg>) (following: Following) : Service<TuiEnv> =
        Service.create
            info
            [
                Handler.notification ClientProtocol.event (fun _ notice ->
                    Eff.ret (
                        if following.Shows notice.Session then
                            inbox.Post(Msg.Session notice.Item)
                    ))
                Handler.notification ClientProtocol.contextNotice (fun _ notice ->
                    Eff.ret (
                        if following.Shows notice.Session then
                            inbox.Post(Msg.Context notice.Item)
                    ))
                Handler.notification ClientProtocol.streamingNotice (fun _ notice ->
                    Eff.ret (
                        if following.Shows notice.Session then
                            inbox.Post(Msg.Streaming(notice.Turn, notice.Fresh, notice.Text, notice.Tool))
                    ))
                Handler.notification GrantsProtocol.event (fun _ notice -> Eff.ret (inbox.Post(Msg.Grant notice)))
            ]

    let private env (environment: EnvSummary) : EnvInfo =
        {
            Id = environment.Id
            Name = environment.Name
            Provider = environment.Provider
            Phase = environment.Phase
            Warnings = environment.Warnings
            Sandbox = environment.Sandbox
            Branch = environment.Branch
        }

    let private approvalsOf (view: ApprovalsView) : ApprovalsInfo =
        {
            Mode = view.Mode
            Configured = view.Configured
            Sandbox = view.Sandbox
            Reason = view.Reason
        }

    let private infoOf (entry: SessionEntry) : SessionInfo =
        {
            Id = entry.Session
            Title = entry.Title
            Environment = entry.Environment
            Workspace = entry.Workspace
        }

    /// The session to show: created (with its environment, which may first ask for grants), or
    /// found.
    let private session (peer: Peer) (choice: SessionChoice) : Eff<TuiEnv, Result<SessionEntry, string>> =
        eff {
            let create (title: string option) =
                Rpc.call peer ClientProtocol.create { Session = None; Title = title }
                |> Eff.map (Result.mapError string)

            match choice with
            | SessionChoice.New title -> return! create title
            | SessionChoice.Resume id ->
                match! Rpc.call peer ClientProtocol.list () with
                | Error e -> return Error(string e)
                | Ok entries ->
                    match entries |> List.tryFind (fun e -> e.Session = id) with
                    | Some entry -> return Ok entry
                    | None -> return Error $"no session {id} on this host"
            | SessionChoice.Latest workspace ->
                match! Rpc.call peer ClientProtocol.list () with
                | Error e -> return Error(string e)
                | Ok entries ->
                    match entries |> List.filter (fun e -> e.Workspace = workspace) |> List.tryLast with
                    | Some entry -> return Ok entry
                    | None -> return! create None
        }

    /// Follows the grants log: the pending requests, then every event after them.
    let private grants (peer: Peer) (inbox: Inbox<Msg>) : Eff<TuiEnv, unit> =
        eff {
            match! Rpc.call peer GrantsProtocol.pending () with
            | Error e -> inbox.Post(Msg.Sent("grants/pending", Error(string e)))
            | Ok pending ->
                inbox.Post(Msg.GrantsPending pending)

                match! Rpc.call peer GrantsProtocol.subscribe { After = pending.Seq } with
                | Error e -> inbox.Post(Msg.Sent("grants/subscribe", Error(string e)))
                | Ok _ -> ()
        }

    /// Starts the session and follows it until cancelled (or its subscriptions end).
    let private follow
        (peer: Peer)
        (inbox: Inbox<Msg>)
        (following: Following)
        (choice: SessionChoice)
        : Eff<TuiEnv, unit> =
        eff {
            following.Session <- ""

            inbox.Post(
                Msg.Starting(
                    match choice with
                    | SessionChoice.Resume id -> "resuming " + id
                    | _ -> "creating the session and its environment"
                )
            )

            match! session peer choice with
            | Error reason -> inbox.Post(Msg.StartFailed reason)
            | Ok entry ->
                following.Session <- entry.Session
                inbox.Post(Msg.Started(infoOf entry))

                match! Rpc.call peer ClientProtocol.envGet { Id = entry.Environment } with
                | Ok summary -> inbox.Post(Msg.Env(env summary))
                | Error _ -> ()

                let subscribe: Eff<TuiEnv, unit> =
                    eff {
                        match! Rpc.call peer ClientProtocol.subscribe { Session = entry.Session; After = 0L } with
                        | Error e -> inbox.Post(Msg.Disconnected(string e))
                        | Ok _ -> ()
                    }

                let context: Eff<TuiEnv, unit> =
                    eff {
                        match! Rpc.call peer ClientProtocol.context { Session = entry.Session; After = 0L } with
                        | Error e -> inbox.Post(Msg.Disconnected(string e))
                        | Ok _ -> ()
                    }

                let! _ = Eff.all [ subscribe; context ]
                ()
        }

    let private sent (inbox: Inbox<Msg>) (what: string) (result: Result<'T, CallError<ClientError>>) =
        match result with
        | Ok _ -> inbox.Post(Msg.Sent(what, Ok()))
        | Error e -> inbox.Post(Msg.Sent(what, Error(string e)))

    /// Sends one command and posts what came of it.
    let interpret (peer: Peer) (inbox: Inbox<Msg>) (cmd: Cmd) : Eff<TuiEnv, unit> =
        eff {
            match cmd with
            | Cmd.Prompt(session, key, text, mode) ->
                let! result =
                    Rpc.call
                        peer
                        ClientProtocol.prompt
                        {
                            Session = session
                            Key = key
                            Text = text
                            Mode = mode
                        }

                sent inbox (string mode) result
            | Cmd.Interrupt(session, key) ->
                let! result = Rpc.call peer ClientProtocol.interrupt { Session = session; Key = key }
                sent inbox "interrupt" result
            | Cmd.Answer(session, key, request, answer) ->
                match!
                    Rpc.call
                        peer
                        ClientProtocol.answer
                        {
                            Session = session
                            Key = key
                            Request = request
                            Answer = answer
                        }
                with
                | Ok _ -> ()
                | Error e -> inbox.Post(Msg.AnswerFailed(request, string e))
            | Cmd.AnswerGrant(request, answer) ->
                match! Rpc.call peer GrantsProtocol.answer { Request = request; Answer = answer } with
                | Ok _ -> ()
                | Error e -> inbox.Post(Msg.AnswerFailed("grant/" + request.Value, string e))
            | Cmd.Override(session, key, set) ->
                let! result =
                    Rpc.call
                        peer
                        ClientProtocol.overrides
                        {
                            Session = session
                            Key = key
                            Set = set
                            Unset = []
                        }

                sent inbox "override" result
            | Cmd.ListSessions ->
                match! Rpc.call peer ClientProtocol.list () with
                | Ok entries -> inbox.Post(Msg.Sessions(entries |> List.map infoOf))
                | Error e -> inbox.Post(Msg.Sent("sessions", Error(string e)))
            | Cmd.Approvals session ->
                match! Rpc.call peer ClientProtocol.approvals { Session = session } with
                | Ok view -> inbox.Post(Msg.Approvals(approvalsOf view, false))
                | Error _ -> ()
            | Cmd.SetApprovals(session, auto) ->
                match! Rpc.call peer ClientProtocol.setApprovals { Session = session; Auto = auto } with
                | Ok view -> inbox.Post(Msg.Approvals(approvalsOf view, true))
                | Error(CallError.Failed(ClientError.Invalid reasons)) ->
                    inbox.Post(Msg.Sent("approvals", Error(String.concat "; " reasons)))
                | Error e -> inbox.Post(Msg.Sent("approvals", Error(string e)))
            // Switching sessions is the loop's (`run`).
            | Cmd.NewSession
            | Cmd.OpenSession _
            | Cmd.Redraw
            | Cmd.Quit -> ()
        }

    /// Waits until the scope is cancelled.
    let private untilCancelled: Eff<TuiEnv, unit> =
        fun env ->
            let waiting =
                Threading.Tasks.TaskCompletionSource(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)

            let registration =
                env.Scope.Cancellation.Register(fun () -> waiting.TrySetCanceled env.Scope.Cancellation |> ignore)

            task {
                use _ = registration
                do! waiting.Task
            }

    /// Runs the TUI on a connection until the person quits: the loop, the session's start and
    /// subscriptions, and the end of the connection. Gives the last model.
    let run
        (peer: Peer)
        (inbox: Inbox<Msg>)
        (following: Following)
        (choice: SessionChoice)
        (start: Model)
        : Eff<TuiEnv, Model> =
        eff {
            let final = ref start
            let switches = Switches()

            let handle (cmd: Cmd) : Eff<TuiEnv, unit> =
                match cmd with
                | Cmd.NewSession ->
                    switches.Post(SessionChoice.New None)
                    Eff.ret ()
                | Cmd.OpenSession id ->
                    switches.Post(SessionChoice.Resume id)
                    Eff.ret ()
                | other -> interpret peer inbox other

            let loop: Eff<TuiEnv, unit> =
                Runtime.run inbox handle start |> Eff.map (fun model -> final.Value <- model)

            // The next switch the person asks for.
            let switched: Eff<TuiEnv, SessionChoice option> =
                fun env ->
                    task {
                        // Eff.race waits for every branch: this one ends when the scope does.
                        let! choice = switches.Next.WaitAsync env.Scope.Cancellation
                        switches.Take()
                        return Some choice
                    }

            // One session at a time: a switch ends following the one shown and starts the next.
            let following: Eff<TuiEnv, unit> =
                eff {
                    let mutable current = choice
                    let mutable going = true

                    while going do
                        let shown: Eff<TuiEnv, SessionChoice option> =
                            eff {
                                do! follow peer inbox following current
                                // The subscriptions ended (the host stopped): the TUI stays until the
                                // person quits or switches.
                                do! untilCancelled
                                return None
                            }

                        match! Eff.race [ shown; switched ] with
                        | Some next -> current <- next
                        | None -> going <- false
                }

            // The connection's end, or the scope's.
            let ended: Eff<TuiEnv, unit> =
                fun env ->
                    task {
                        let cancelled =
                            Threading.Tasks.TaskCompletionSource(
                                Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                            )

                        use _ =
                            env.Scope.Cancellation.Register(fun () -> cancelled.TrySetResult() |> ignore)

                        let! first =
                            Threading.Tasks.Task.WhenAny(peer.Completion :> Threading.Tasks.Task, cancelled.Task)

                        if first <> cancelled.Task then
                            inbox.Post(Msg.Disconnected(string peer.Completion.Result))
                            do! untilCancelled env
                    }

            do! Eff.race [ loop; Eff.all [ grants peer inbox; following ] |> Eff.ignore; ended ]
            return final.Value
        }
