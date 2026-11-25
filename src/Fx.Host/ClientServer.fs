namespace Fx.Host

open System
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Rpc
open Fx.Sdk
open Fx.Environments
open Fx.Identity
open Fx.Identity.Host
open Fx.Grants
open Fx.Grants.Host
open Fx.Sessions
open Fx.Sessions.Host

/// One client connection on the host: who signed in (`ClientConnection`, set by `initialize`) and the
/// request kinds it can answer (its `answers` member).
[<Sealed>]
type ClientState() =
    let gate = obj ()
    let mutable answers: string list = []

    member val Connection = ClientConnection()

    /// The capabilities the client offered in `initialize`.
    member _.Answers: string list = lock gate (fun () -> answers)

    member internal _.Offered(capabilities: string list) =
        lock gate (fun () -> answers <- capabilities)

    override this.ToString() = $"ClientState({this.Connection})"

/// The host's side of the client protocol (spec §17): the handlers of every method group over a
/// running host, and serving one connection (`serve`). Every handler asks who is calling with the
/// method's least role (`ClientProtocol.leastRole`, identity.md#roles); inputs are attributed to the
/// connection's principal and client.
[<RequireQualifiedAccess>]
module ClientServer =
    /// How the host introduces itself in `initialize`.
    let info: PeerInfo =
        {
            Name = Product.binaryName
            Version = Product.Version
        }

    let private caller (host: Host) (client: ClientState) (methodName: string) : Result<Attribution, ClientError> =
        IdentityServer.caller host.Authenticator (ClientProtocol.leastRole methodName) client.Connection
        |> Result.mapError ClientError.Forbidden

    /// A client's idempotency key, apart from the host's own keys and other clients': the client id
    /// (`Attribution.Via`, the same across reconnects with its token) and the key it sent.
    let clientKey (by: Attribution) (key: string) : IdempotencyKey =
        IdempotencyKey("client/" + by.Via + "/" + key)

    let private appended (outcome: Outcome) : Result<Committed, ClientError> =
        match outcome with
        | Accepted a -> Ok { After = a.After; Count = a.Count }
        | Rejected rejection -> Error(ClientError.Rejected(rejection.Code, rejection.Message))
        | Stopped -> Error(ClientError.Unavailable "the session's actor stopped: send it again")

    let private known (host: Host) (session: string) : Eff<HostEnv, Result<unit, ClientError>> =
        HostSessions.exists host session
        |> Eff.map (fun exists ->
            if exists then
                Ok()
            else
                Error(ClientError.UnknownSession session))

    /// Sends a client's command to a session, as `methodName` allows.
    let private send
        (host: Host)
        (client: ClientState)
        (methodName: string)
        (session: string)
        (key: string)
        (command: Command)
        : Eff<HostEnv, Result<Committed, ClientError>> =
        effResult {
            let! by = caller host client methodName
            do! known host session
            let! outcome = SessionHost.send host.Sessions session (clientKey by key) by command
            let! result = appended outcome
            return result
        }

    let private answerable (client: ClientState) (request: PendingRequest) : bool =
        request.Capabilities
        |> List.exists (fun capability -> List.contains capability client.Answers)

    let private sessionHandlers (host: Host) (client: ClientState) : Handler<HostEnv> list =
        [
            Handler.request ClientProtocol.create (fun _ p ->
                effResult {
                    let! by = caller host client ClientProtocol.create.Name
                    let! fresh = Ids.newId

                    let id = p.Session |> Option.defaultWith (fun () -> string fresh)

                    let! entry =
                        HostSessions.start host by id p.Title
                        |> EffResult.mapError ClientError.Unavailable

                    return entry
                })

            Handler.request ClientProtocol.list (fun _ () ->
                effResult {
                    let! _ = caller host client ClientProtocol.list.Name
                    return HostSessions.entries host
                })

            Handler.request ClientProtocol.get (fun _ p ->
                effResult {
                    let! _ = caller host client ClientProtocol.get.Name
                    do! known host p.Session
                    let! snapshot = SessionHost.state host.Sessions p.Session

                    return
                        {
                            Seq = snapshot.Seq
                            State = snapshot.State
                        }
                })

            Handler.request ClientProtocol.prompt (fun _ p ->
                send host client ClientProtocol.prompt.Name p.Session p.Key (Command.Prompt(p.Text, p.Mode)))

            // The control path: SessionHost.send routes it past the queue.
            Handler.request ClientProtocol.interrupt (fun _ p ->
                send host client ClientProtocol.interrupt.Name p.Session p.Key Command.Interrupt)

            Handler.request ClientProtocol.answer (fun _ p ->
                effResult {
                    let! _ = caller host client ClientProtocol.answer.Name
                    do! known host p.Session
                    let! snapshot = SessionHost.state host.Sessions p.Session

                    // Only clients that offered a capability of the request's kind answer it; a
                    // request that is not open is the session's to refuse (session.not-pending).
                    do!
                        match snapshot.State.Requests |> List.tryFind (fun r -> r.Request.Id = p.Request) with
                        | Some request when not (answerable client request) ->
                            Error(ClientError.CannotAnswer p.Request)
                        | _ -> Ok()

                    let! result =
                        send
                            host
                            client
                            ClientProtocol.answer.Name
                            p.Session
                            p.Key
                            (Command.Answer(p.Request, p.Answer))

                    return result
                })

            Handler.request ClientProtocol.fork (fun _ p ->
                effResult {
                    let! by = caller host client ClientProtocol.fork.Name
                    do! known host p.Session
                    let! fresh = Ids.newId
                    let child = p.Child |> Option.defaultWith (fun () -> string fresh)

                    // The child works where its parent does.
                    let index: Eff<HostEnv, unit> =
                        match HostSessions.tryFind host p.Session with
                        | Some parent ->
                            let entry =
                                { parent with
                                    Session = child
                                    Title = None
                                }

                            host.Entries[child] <- entry
                            SessionIndex.add host.Index by entry
                        | None -> Eff.unit

                    do! index

                    let! outcome =
                        SessionHost.send host.Sessions p.Session (clientKey by p.Key) by (Command.Fork(child, p.UpTo))

                    let! _ = appended outcome
                    return { Session = child }
                })

            Handler.request ClientProtocol.overrides (fun _ p ->
                effResult {
                    let! by = caller host client ClientProtocol.overrides.Name
                    do! known host p.Session

                    let changes =
                        (p.Set |> List.map (fun (slot, value) -> slot, Some value))
                        @ (p.Unset |> List.map (fun slot -> slot, None))

                    let! outcome =
                        SessionHost.setOverrides host.Sessions host.Profiles p.Session (clientKey by p.Key) by changes
                        |> EffResult.mapError ClientError.Invalid

                    let! result = appended outcome
                    return result
                })

            Handler.request ClientProtocol.close (fun _ p ->
                effResult {
                    let! result =
                        send host client ClientProtocol.close.Name p.Session p.Key Command.Close
                    // The session ended: its grants expire and its pending grant requests close.
                    do! GrantsService.endSession host.Grants p.Session |> Eff.map ignore
                    return result
                })

            Handler.request ClientProtocol.reopen (fun _ p ->
                send host client ClientProtocol.reopen.Name p.Session p.Key Command.Reopen)

            Handler.request ClientProtocol.offers (fun _ p ->
                effResult {
                    let! _ = caller host client ClientProtocol.offers.Name
                    do! known host p.Session
                    let! offers = SessionHost.offers host.Sessions p.Session client.Answers
                    return offers
                })

            Handler.request ClientProtocol.subscribe (fun context p ->
                effResult {
                    let! _ = caller host client ClientProtocol.subscribe.Name
                    do! known host p.Session

                    let! last =
                        SessionHost.subscribe host.Sessions p.Session p.After (fun item ->
                            Rpc.notify context.Peer ClientProtocol.event { Session = p.Session; Item = item }
                            |> Eff.map Result.isOk)

                    return last
                })

            Handler.request ClientProtocol.context (fun context p ->
                effResult {
                    let! _ = caller host client ClientProtocol.context.Name
                    do! known host p.Session

                    let recorded: Eff<HostEnv, int64> =
                        Fx.Machines.Host.EventLog.subscribe
                            (Fx.CodeAct.ContextLog.typed host.Stores.Log)
                            (Fx.CodeAct.ContextLog.stream p.Session)
                            p.After
                            (fun item ->
                                Rpc.notify
                                    context.Peer
                                    ClientProtocol.contextNotice
                                    { Session = p.Session; Item = item }
                                |> Eff.map Result.isOk)

                    // What the model writes, in the order it wrote it, until the subscription ends.
                    let streamed: Eff<HostEnv, int64> =
                        eff {
                            let pieces = System.Threading.Channels.Channel.CreateUnbounded<StreamingNotice>()

                            use _ =
                                host.Streams.Subscribe(
                                    p.Session,
                                    fun notice -> pieces.Writer.TryWrite notice |> ignore
                                )

                            let! token = Eff.cancellation

                            while true do
                                let! notice = Eff.ofTask (fun () -> pieces.Reader.ReadAsync(token).AsTask())
                                let! _ = Rpc.notify context.Peer ClientProtocol.streamingNotice notice
                                ()

                            return 0L
                        }

                    let! last = Eff.race [ recorded; streamed ]
                    return last
                })
        ]

    let private identityHandlers (host: Host) (client: ClientState) : Handler<HostEnv> list =
        let auth = host.Authenticator

        let current () =
            IdentityServer.session auth client.Connection
            |> Result.mapError ClientError.Forbidden

        [
            Handler.request ClientProtocol.whoami (fun _ () ->
                effResult {
                    let! _ = caller host client ClientProtocol.whoami.Name
                    let! session = current ()
                    return ClientSession.info None session
                })

            Handler.request ClientProtocol.sessions (fun _ () ->
                effResult {
                    let! _ = caller host client ClientProtocol.sessions.Name
                    let! active = SessionTokens.active auth.Tokens
                    return active |> List.map (ClientSession.info None)
                })

            Handler.request ClientProtocol.revoke (fun _ p ->
                effResult {
                    let! _ = caller host client ClientProtocol.revoke.Name
                    let! revoked = SessionTokens.revoke auth.Tokens p.Id
                    return revoked
                })

            Handler.request ClientProtocol.signOut (fun _ () ->
                effResult {
                    let! _ = caller host client ClientProtocol.signOut.Name
                    let! session = current ()
                    let! revoked = SessionTokens.revoke auth.Tokens session.Id
                    return revoked
                })
        ]

    /// An environment as clients see it.
    let summary (record: EnvRecord) : EnvSummary =
        {
            Id = record.Id
            Name = record.Spec.Name
            Provider = record.Spec.Provider
            Phase = string record.Phase
            Transport = record.Transport |> Option.map string
            Workspace = record.Spec.Workspace.Path
            Reason = record.Reason
            Warnings = Envs.warnings record
        }

    let private envHandlers (host: Host) (client: ClientState) : Handler<HostEnv> list =
        [
            Handler.request ClientProtocol.envList (fun _ () ->
                effResult {
                    let! _ = caller host client ClientProtocol.envList.Name
                    let! records = Envs.list
                    return records |> List.map summary
                })

            Handler.request ClientProtocol.envGet (fun _ p ->
                effResult {
                    let! _ = caller host client ClientProtocol.envGet.Name

                    match! Envs.get p.Id with
                    | Some record -> return summary record
                    | None -> return! EffResult.error (ClientError.UnknownEnvironment p.Id)
                })

            Handler.request ClientProtocol.envDestroy (fun _ p ->
                effResult {
                    let! _ = caller host client ClientProtocol.envDestroy.Name

                    let! record =
                        Envs.destroy p.Id
                        |> EffResult.mapError (fun e ->
                            match e with
                            | EnvError.UnknownEnvironment id -> ClientError.UnknownEnvironment id
                            | e -> ClientError.Unavailable(string e))

                    return summary record
                })
        ]

    /// `ext/<id>/<name>` for every method of the active extensions.
    let private extHandlers (host: Host) (client: ClientState) : Handler<HostEnv> list =
        [
            for id in Fx.Extensions.Registry.active host.Extensions.State do
                match host.Extensions.Loaded.TryFind id with
                | Some loaded ->
                    for m in loaded.Activation.Methods do
                        let name = ClientProtocol.extName id m.Name

                        if (Protocol.checkMethodName name).IsNone then
                            let definition = ClientProtocol.ext id m.Name

                            Handler.request definition (fun _ parameters ->
                                effResult {
                                    let! _ =
                                        IdentityServer.caller
                                            host.Authenticator
                                            (ClientProtocol.leastRole name)
                                            client.Connection
                                        |> Result.mapError ExtCallError.Forbidden

                                    let! result =
                                        Fx.Extensions.ExtensionHost.call host.Extensions id m.Name parameters
                                        |> EffResult.mapError ExtCallError.Failed

                                    return result
                                })
                | None -> ()
        ]

    /// Every handler of the protocol for one connection.
    let handlers (host: Host) (client: ClientState) : Handler<HostEnv> list =
        let person (_: RequestContext) : Result<Attribution, GrantError> =
            IdentityServer.caller host.Authenticator Role.Owner client.Connection
            |> Result.mapError (fun failure -> GrantError.NotAPerson(string failure))

        sessionHandlers host client
        @ identityHandlers host client
        @ envHandlers host client
        @ extHandlers host client
        @ GrantsProtocol.handlers host.Grants person

    /// The capabilities a client offered (`answers`), if it said.
    let answersOf (members: Json) : string list =
        match Json.tryField ClientProtocol.AnswersMember members with
        | Some(Json.Array items) ->
            items
            |> List.choose (fun item ->
                match item with
                | Json.String capability -> Some capability
                | _ -> None)
        | _ -> []

    /// The service of one connection: the client signs in in `initialize` (`IdentityServer`) with
    /// what the transport established (`facts`), and says what it can answer.
    let service (host: Host) (facts: ConnectionFacts) (client: ClientState) : Service<HostEnv> =
        let accept (remote: Fx.Rpc.Remote) : Eff<HostEnv, Result<(string * Json) list, RpcError>> =
            eff {
                match! IdentityServer.accept host.Authenticator facts client.Connection remote with
                | Error e -> return Error e
                | Ok members ->
                    client.Offered(answersOf remote.Members)
                    return Ok(members @ [ "clientProtocol", Json.String ClientProtocol.Version ])
            }

        Service.create info (handlers host client) |> Service.withAccept accept

    /// Serves one connection until it ends (or `cancellation`): `initialize` signs the client in, and
    /// revoking its session token ends the connection (identity.md).
    let serve (host: Host) (facts: ConnectionFacts) (transport: Transport) (cancellation: CancellationToken) : Task =
        task {
            let client = ClientState()

            match! Eff.run (Host.within host cancellation) (Rpc.accept (service host facts client) transport) with
            | Error _ -> ()
            | Ok peer ->
                let revocation =
                    match client.Connection.Session with
                    | Some session -> host.Authenticator.Tokens.Revocation session.Id
                    | None -> CancellationToken.None

                use _ = revocation.Register(fun () -> peer.CloseAsync() |> ignore)
                let! _ = peer.Completion
                ()
        }
