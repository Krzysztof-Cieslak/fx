namespace Fx.Tui.Live

open System
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

    /// The client's side of the connection: notifications become messages.
    let service (inbox: Inbox<Msg>) : Service<TuiEnv> =
        Service.create
            info
            [
                Handler.notification ClientProtocol.event (fun _ notice ->
                    Eff.ret (inbox.Post(Msg.Session notice.Item)))
                Handler.notification ClientProtocol.contextNotice (fun _ notice ->
                    Eff.ret (inbox.Post(Msg.Context notice.Item)))
                Handler.notification ClientProtocol.streamingNotice (fun _ notice ->
                    Eff.ret (inbox.Post(Msg.Streaming(notice.Turn, notice.Fresh, notice.Text, notice.Tool))))
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

    /// Starts the session and follows it until cancelled.
    let follow (peer: Peer) (inbox: Inbox<Msg>) (choice: SessionChoice) : Eff<TuiEnv, unit> =
        let sessionPart: Eff<TuiEnv, unit> =
            eff {
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

        Eff.all [ grants peer inbox; sessionPart ] |> Eff.ignore

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
    let run (peer: Peer) (inbox: Inbox<Msg>) (choice: SessionChoice) (start: Model) : Eff<TuiEnv, Model> =
        eff {
            let final = ref start

            let loop: Eff<TuiEnv, unit> =
                Runtime.run inbox (interpret peer inbox) start
                |> Eff.map (fun model -> final.Value <- model)

            let following: Eff<TuiEnv, unit> =
                eff {
                    do! follow peer inbox choice
                    // The subscriptions ended (the host stopped): the TUI stays until the person quits.
                    do! untilCancelled
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

            do! Eff.race [ loop; following; ended ]
            return final.Value
        }
