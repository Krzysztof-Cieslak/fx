namespace Fx.Host

open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Identity
open Fx.Identity.Host
open Fx.Sessions
open Fx.Sessions.Host

/// Sessions on a host, as embedded mode and the client protocol (T129b) use them: who is acting, and
/// creating sessions in the host's workspace.
[<RequireQualifiedAccess>]
module HostSessions =
    let private within (host: Host) (cancellation: CancellationToken) : HostEnv = Host.within host cancellation

    /// Signs in the person the host runs for, in process (embedded mode, spec §3: `local-os-user`), as
    /// the client `client`.
    let signInLocal
        (host: Host)
        (client: string)
        (cancellation: CancellationToken)
        : Task<Result<ClientSession, string>> =
        task {
            let request =
                SignIn.Provider(Fx.Ext.IdentityLocalOsUser.LocalOsUser.ProviderId, Json.Null)

            match!
                Eff.run
                    (within host cancellation)
                    (Authenticator.signIn host.Authenticator ConnectionFacts.inProcess client (Some request))
            with
            | Ok signedIn -> return Ok signedIn.Session
            | Error failure -> return Error $"signing in failed: {failure}"
        }

    /// The sessions the host knows, oldest first.
    let entries (host: Host) : SessionEntry list =
        host.Entries.Values |> Seq.sortBy (fun e -> e.Session) |> List.ofSeq

    let tryFind (host: Host) (session: string) : SessionEntry option =
        match host.Entries.TryGetValue session with
        | true, entry -> Some entry
        | _ -> None

    /// Creates a session in the host's workspace: its environment (created, and its components
    /// installed, if the workspace has none yet), its entry in the index, and its start under the
    /// profile of the host's resolution. Creating it again with the same id changes nothing.
    let start
        (host: Host)
        (by: Attribution)
        (session: string)
        (title: string option)
        : Eff<HostEnv, Result<SessionEntry, string>> =
        effResult {
            let! environment = host.EnsureEnvironment

            let entry =
                {
                    Session = session
                    Workspace = host.Options.Workspace
                    Environment = environment
                    Title = title
                }

            do! SessionIndex.add host.Index by entry
            host.Entries[session] <- entry
            let! profile = host.Profiles.Bind [] |> EffResult.mapError (String.concat "; ")

            match! SessionHost.start host.Sessions session (IdempotencyKey("start/" + session)) by profile.Id title with
            | Accepted _ -> return entry
            | Rejected rejection when rejection.Code = Rejections.Started -> return entry
            | Rejected rejection -> return! EffResult.error (string rejection)
            | Stopped -> return! EffResult.error "the session host stopped"
        }

    /// `start` as a task, with a caller's cancellation.
    let create
        (host: Host)
        (by: Attribution)
        (session: string)
        (title: string option)
        (cancellation: CancellationToken)
        : Task<Result<SessionEntry, string>> =
        Eff.run (within host cancellation) (start host by session title)

    /// Whether a session exists: the host indexed it, or its log has events (a fork's child).
    let exists (host: Host) (session: string) : Eff<HostEnv, bool> =
        match host.Entries.TryGetValue session with
        | true, _ -> Eff.ret true
        | _ ->
            Fx.Machines.Host.EventLog.head host.Sessions.Log (Session.stream session)
            |> Eff.map (fun head -> head > 0L)

    /// A session's state now (starting its actor if needed).
    let state (host: Host) (session: string) (cancellation: CancellationToken) : Task<SessionState> =
        task {
            let! snapshot =
                Eff.run (within host cancellation) (SessionHost.state host.Sessions session)

            return snapshot.State
        }

    /// Sends a command to a session.
    let send
        (host: Host)
        (session: string)
        (key: string)
        (by: Attribution)
        (command: Command)
        (cancellation: CancellationToken)
        : Task<Outcome> =
        Eff.run (within host cancellation) (SessionHost.send host.Sessions session (IdempotencyKey key) by command)

    /// The questions of open requests, by id (`agent.question`'s `question`, else the title).
    let questions (state: SessionState) (ids: string list) : string list =
        ids
        |> List.choose (fun id ->
            state.Requests
            |> List.tryFind (fun r -> r.Request.Id = id)
            |> Option.map (fun r ->
                match Json.tryField "question" r.Request.Payload with
                | Some(Json.String question) -> question
                | _ -> r.Request.Title))
