/// The client protocol end to end (spec §17): in process (embedded mode) and over the local listener
/// (a Unix socket or named pipe, and loopback TCP with the token file), against a host whose turns end
/// at their hands (no interpreter: no network). Sessions created, prompted, followed with
/// subscribe(afterSeq) and again after a reconnect and a restart; least roles; grants answered over the
/// protocol; revocation ending connections; environments; extensions' methods; the generated types.
module Fx.Host.Tests.ProtocolTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Contracts.TypeScript
open Fx.Effects
open Fx.Effects.Live
open Fx.Rpc
open Fx.Sdk
open Fx.Identity
open Fx.Identity.Host
open Fx.Grants
open Fx.Grants.Host
open Fx.Sessions
open Fx.Store
open Fx.Extensions
open Fx.Host
open Fx.Host.Tests.Support

type private Listener = Fx.Transport.Http.LocalServer.Listener

/// A client's environment: its clock and scope.
type ClientEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<ClientEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "ClientEnv"

let private clientEnv (cancellation: CancellationToken) : ClientEnv =
    {
        Clock = SystemClock.clock
        Scope = Scope.ofCancellation cancellation
    }

/// A client: its connection and the session events it received.
type Client =
    {
        Peer: Peer
        Events: ConcurrentQueue<SessionNotice>
        Lifetime: CancellationTokenSource
    }

    member this.Call(m: Method<'P, 'R, 'E>, value: 'P) : 'R =
        match waitFor 120.0 (Eff.run (clientEnv CancellationToken.None) (Rpc.call this.Peer m value)) with
        | Ok result -> result
        | Error e -> failtest $"{m.Name} failed: {e}"

    member this.Try(m: Method<'P, 'R, 'E>, value: 'P) : Result<'R, CallError<'E>> =
        waitFor 120.0 (Eff.run (clientEnv CancellationToken.None) (Rpc.call this.Peer m value))

    /// Subscribes to a session in the background until the client closes.
    member this.Subscribe(session: string, after: int64) : unit =
        Eff.run
            (clientEnv this.Lifetime.Token)
            (Rpc.call this.Peer ClientProtocol.subscribe { Session = session; After = after })
        |> ignore

    /// Waits for an event that matches, among those received.
    member this.WaitFor(matches: SessionNotice -> bool) : SessionNotice =
        let deadline = DateTime.UtcNow.AddSeconds 60.0
        let mutable found = None

        while found.IsNone do
            found <- this.Events |> Seq.tryFind matches

            if found.IsNone then
                if DateTime.UtcNow > deadline then
                    let seen = this.Events |> Seq.map string |> String.concat "\n"
                    failtest $"no such event; received:\n{seen}"

                Thread.Sleep 10

        found.Value

    member this.Close() =
        this.Lifetime.Cancel()
        (this.Peer.CloseAsync()).Wait(TimeSpan.FromSeconds 10.0) |> ignore

    override _.ToString() = "Client"

let private clientService (events: ConcurrentQueue<SessionNotice>) : Service<ClientEnv> =
    Service.create
        { Name = "tests"; Version = "0" }
        [
            Handler.notification ClientProtocol.event (fun _ event -> Eff.ret (events.Enqueue event))
        ]

/// What a client following a session's context log receives.
let private contextService
    (context: ConcurrentQueue<ContextNotice>)
    (streamed: ConcurrentQueue<StreamingNotice>)
    : Service<ClientEnv> =
    Service.create
        { Name = "tests"; Version = "0" }
        [
            Handler.notification ClientProtocol.contextNotice (fun _ notice -> Eff.ret (context.Enqueue notice))
            Handler.notification ClientProtocol.streamingNotice (fun _ notice -> Eff.ret (streamed.Enqueue notice))
        ]

let private until (what: string) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 60.0

    while not (condition ()) do
        if DateTime.UtcNow > deadline then
            failtest $"never: {what}"

        Thread.Sleep 10

let private answers = [ "confirm"; "choice"; "agent.question" ]

/// A client in the host's process, signed in as the OS user.
let private embedded (host: Host) : Client =
    let events = ConcurrentQueue<SessionNotice>()
    let lifetime = new CancellationTokenSource()

    match
        waitFor 60.0 (Eff.run (clientEnv lifetime.Token) (HostClient.embedded host (clientService events) answers))
    with
    | Ok peer ->
        {
            Peer = peer
            Events = events
            Lifetime = lifetime
        }
    | Error e -> failtest $"no connection: {e}"

/// A client over a listener with a bearer and `initialize` members.
let private remote (listener: Listener) (bearer: string) (members: (string * Json) list) : Result<Client, string> =
    let events = ConcurrentQueue<SessionNotice>()
    let lifetime = new CancellationTokenSource()

    waitFor
        60.0
        (Eff.run (clientEnv lifetime.Token) (HostClient.connect listener bearer (clientService events) members))
    |> Result.map (fun peer ->
        {
            Peer = peer
            Events = events
            Lifetime = lifetime
        })

let private withoutKernel (place: Place) =
    Directory.CreateDirectory place.Data |> ignore

    File.WriteAllText(
        Path.Combine(place.Data, "config.json"),
        """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}}"""
    )

let private host (name: string) (extensions: Candidate list) : Place * Host =
    let p = place name
    withoutKernel p

    let opts =
        { options p (Model()) [ "model.role.main", Json.String modelId ] with
            Extensions = extensions
        }

    p, start opts

let private isTurnEnded (event: SessionNotice) =
    match event.Item with
    | Feed.Next recorded ->
        match recorded.Event with
        | Event.TurnEnded _ -> true
        | _ -> false
    | Feed.Reset _ -> false

let private seqOf (event: SessionNotice) =
    match event.Item with
    | Feed.Next recorded -> recorded.Seq
    | Feed.Reset snapshot -> snapshot.Seq

/// A provider that signs anyone in as a viewer: for least roles.
let private viewerProvider: Extension =
    let providerId = "test-viewer"
    let id = "tests.identity.viewer"

    let provider: IdentityProvider =
        {
            Id = providerId
            Display = "Viewers"
            Login = None
            Verify =
                fun _ ->
                    Eff.ret (
                        Principal.create PrincipalKind.User providerId "someone" "Someone" Map.empty
                        |> Result.mapError AuthError.Unavailable
                    )
            Refresh = None
            Roles = fun _ -> Eff.ret [ Role.Viewer ]
        }

    {
        Manifest =
            { Manifest.create id "1.0.0" Runtimes.Native with
                Contributions = [ Declare.contribution IdentityProviders.contributionType providerId ]
            }
        Activate =
            Eff.ret (
                Activation.create [ Contribution.create IdentityProviders.contributionType providerId provider ] []
            )
    }

/// An extension with a method: `ext/tests.echo/echo`.
let private echoExtension: Extension =
    let echo =
        ExtMethod.define "echo" Codec.json Codec.json
        |> ExtMethod.describe "Returns what it is given."

    {
        Manifest =
            { Manifest.create "tests.echo" "1.0.0" Runtimes.Native with
                Rpc = { Methods = [ Declare.method echo ] }
            }
        Activate =
            Eff.ret (
                Activation.create
                    []
                    [
                        ExtMethod.handle echo (fun json -> EffResult.ok (Json.Object [ "echo", json ]))
                    ]
            )
    }

let private fileListener () : Listener =
    if OperatingSystem.IsWindows() then
        Fx.Transport.Http.LocalServer.NamedPipe(Product.Name + "-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    else
        Fx.Transport.Http.LocalServer.UnixSocket(
            "/tmp/"
            + Product.Name
            + "-"
            + Guid.NewGuid().ToString("N").Substring(0, 8)
            + ".sock"
        )

[<Tests>]
let tests =
    testList
        "Client protocol"
        [
            testCase
                "in process: create, prompt, follow with subscribe, reconnect after a sequence number, and a restart"
            <| fun () ->
                let p, h = host "protocol" []
                let mutable last = 0L

                try
                    let c = embedded h
                    let me = c.Call(ClientProtocol.whoami, ())
                    Expect.equal me.Roles [ Role.Owner ] "the OS user owns the host"
                    Expect.equal me.Principal.Provider "local-os-user" "provider"
                    Expect.isNone me.Token "whoami never shows a token"

                    let entry =
                        c.Call(
                            ClientProtocol.create,
                            {
                                Session = Some "s1"
                                Title = Some "first"
                            }
                        )

                    Expect.equal entry.Session "s1" "the client's id"
                    Expect.equal entry.Workspace p.Workspace "the host's workspace"

                    Expect.equal
                        (c.Call(
                            ClientProtocol.create,
                            {
                                Session = Some "s1"
                                Title = Some "first"
                            }
                        ))
                        entry
                        "creating again is harmless"

                    Expect.equal (c.Call(ClientProtocol.list, ()) |> List.map _.Session) [ "s1" ] "listed"

                    c.Subscribe("s1", 0L)

                    let prompt =
                        {
                            Session = "s1"
                            Key = "p1"
                            Text = "Hello"
                            Mode = PromptMode.Steer
                        }

                    let committed = c.Call(ClientProtocol.prompt, prompt)
                    Expect.isGreaterThan committed.Count 0 "events appended"
                    Expect.equal (c.Call(ClientProtocol.prompt, prompt)) committed "the same key: the first outcome"

                    let ended = c.WaitFor isTurnEnded
                    last <- seqOf ended
                    let seqs = c.Events |> Seq.map seqOf |> List.ofSeq
                    Expect.equal seqs.Head 1L "replayed from the first event"
                    Expect.equal seqs (List.sort seqs |> List.distinct) "in order, once each"

                    let view = c.Call(ClientProtocol.get, { Session = "s1" })
                    Expect.equal view.State.Turns 1 "one turn"
                    Expect.isGreaterThanOrEqual view.Seq last "the head"

                    // Environments: the workspace's, embedded, with its warning.
                    match c.Call(ClientProtocol.envList, ()) with
                    | [ env ] ->
                        Expect.equal env.Id entry.Environment "the session's"
                        Expect.equal env.Transport (Some "in-process") "embedded"
                        // Unsandboxed only where no sandbox driver confines processes (Windows, or a
                        // machine without bubblewrap): the warning says so.
                        let sandbox =
                            (wait ((Fx.Environments.EnvsHost.envs h.Environments).Get env.Id CancellationToken.None))
                            |> Option.bind _.Sandbox

                        let confined = sandbox |> Option.exists _.Confines

                        Expect.equal env.Warnings.IsEmpty confined "a warning exactly when unsandboxed"
                        Expect.equal env.Sandbox (sandbox |> Option.map _.Driver) "the driver"
                        Expect.isSome env.Sandbox "ready: its driver is known"
                        Expect.equal (c.Call(ClientProtocol.envGet, { Id = env.Id })) env "get"
                    | other -> failtest $"{other}"

                    match c.Try(ClientProtocol.get, { Session = "nope" }) with
                    | Error(CallError.Failed(ClientError.UnknownSession "nope")) -> ()
                    | other -> failtest $"{other}"

                    // A second client reconnects after what it saw: only what is new arrives.
                    let d = embedded h
                    d.Subscribe("s1", last)

                    d.Call(
                        ClientProtocol.prompt,
                        { prompt with
                            Key = "p2"
                            Text = "Again"
                        }
                    )
                    |> ignore

                    let again = d.WaitFor isTurnEnded
                    Expect.isTrue (d.Events |> Seq.forall (fun e -> seqOf e > last)) "nothing before afterSeq"
                    last <- seqOf again
                    c.Close()
                    d.Close()
                finally
                    stop h

                // The host restarts: the session is there, and a subscriber picks up where it was.
                let h = start (options p (Model()) [])

                try
                    let c = embedded h
                    Expect.equal (c.Call(ClientProtocol.list, ()) |> List.map _.Session) [ "s1" ] "listed again"
                    Expect.equal (c.Call(ClientProtocol.get, { Session = "s1" })).State.Turns 2 "two turns"
                    c.Subscribe("s1", last)

                    c.Call(
                        ClientProtocol.prompt,
                        {
                            Session = "s1"
                            Key = "p3"
                            Text = "After the restart"
                            Mode = PromptMode.Steer
                        }
                    )
                    |> ignore

                    c.WaitFor isTurnEnded |> ignore
                    Expect.isTrue (c.Events |> Seq.forall (fun e -> seqOf e > last)) "only new events"
                    c.Close()
                finally
                    stop h

            testCase "session/context: the context log after a sequence number, then live, and what the model writes"
            <| fun () ->
                let _, h = host "context" []

                try
                    let owner = embedded h

                    owner.Call(ClientProtocol.create, { Session = Some "s1"; Title = None })
                    |> ignore

                    let log = Fx.CodeAct.ContextLog.typed h.Stores.Log
                    let stream = Fx.CodeAct.ContextLog.stream "s1"

                    let receipt (n: int) : Fx.Prompt.ContextEvent =
                        Fx.Prompt.ContextEvent.Returned
                            {
                                Turn = 1
                                Step = n
                                Call = Some $"toolu_{n}"
                                Name = "bash"
                                Pieces = [ Fx.Prompt.Piece.Text $"exit {n}" ]
                                IsError = false
                            }

                    let append (n: int) =
                        let meta: Fx.Machines.Host.EventMeta =
                            {
                                At = Instant(int64 n)
                                By = Host.system
                                Key = Fx.Machines.IdempotencyKey $"tests/{n}"
                                Cause = Some(Fx.Machines.EventRef.create (Session.stream "s1") 1L)
                            }

                        match
                            waitFor
                                30.0
                                (Eff.run
                                    h.Env
                                    (Fx.Machines.Host.EventLog.append log stream (int64 (n - 1)) meta [ receipt n ]))
                        with
                        | Ok _ -> ()
                        | Error e -> failtest $"{e}"

                    append 1
                    append 2
                    let context = ConcurrentQueue<ContextNotice>()
                    let streamed = ConcurrentQueue<StreamingNotice>()
                    let lifetime = new CancellationTokenSource()

                    let peer =
                        match
                            waitFor
                                60.0
                                (Eff.run
                                    (clientEnv lifetime.Token)
                                    (HostClient.embedded h (contextService context streamed) answers))
                        with
                        | Ok peer -> peer
                        | Error e -> failtest $"{e}"

                    Eff.run
                        (clientEnv lifetime.Token)
                        (Rpc.call peer ClientProtocol.context { Session = "s1"; After = 1L })
                    |> ignore

                    until "the second receipt" (fun () -> context.Count = 1)
                    append 3
                    until "the third receipt, live" (fun () -> context.Count = 2)

                    Expect.equal
                        (context |> Seq.map (fun n -> n.Item.Seq, n.Item.Event) |> List.ofSeq)
                        [ 2L, receipt 2; 3L, receipt 3 ]
                        "after #1, in order, with their envelopes"

                    // What a running turn's model writes reaches the session's followers.
                    h.Streams.Publish
                        {
                            Session = "s1"
                            Turn = 1
                            Fresh = true
                            Text = "Reading"
                            Tool = None
                        }

                    h.Streams.Publish
                        {
                            Session = "other"
                            Turn = 1
                            Fresh = true
                            Text = "not this one"
                            Tool = None
                        }

                    until "the streamed text" (fun () -> streamed.Count = 1)
                    Expect.equal (streamed |> Seq.map _.Text |> List.ofSeq) [ "Reading" ] "this session's only"
                    lifetime.Cancel()
                    peer.CloseAsync().Wait(TimeSpan.FromSeconds 10.0) |> ignore
                    owner.Close()
                finally
                    stop h

            testCase "least roles: a viewer reads but cannot drive sessions or answer grants"
            <| fun () ->
                let _, h =
                    host "roles" [ Candidate.create Source.Builtin (Package.Native viewerProvider) ]

                try
                    let owner = embedded h

                    owner.Call(ClientProtocol.create, { Session = Some "s1"; Title = None })
                    |> ignore

                    let events = ConcurrentQueue<SessionNotice>()
                    let frames = ConnectionLimits.frames ConnectionLimits.defaults
                    let clientEnd, serverEnd = InProcess.pair (1 <<< 20) frames frames

                    ClientServer.serve h ConnectionFacts.inProcess serverEnd CancellationToken.None
                    |> ignore

                    let members = IdentityClient.members (SignIn.Provider("test-viewer", Json.Null))

                    let peer =
                        match
                            waitFor
                                60.0
                                (Eff.run
                                    (clientEnv CancellationToken.None)
                                    (Rpc.connect (clientService events) members clientEnd))
                        with
                        | Ok peer -> peer
                        | Error e -> failtest $"{e}"

                    let viewer =
                        {
                            Peer = peer
                            Events = events
                            Lifetime = new CancellationTokenSource()
                        }

                    Expect.equal (viewer.Call(ClientProtocol.list, ()) |> List.length) 1 "reads"

                    match
                        viewer.Try(
                            ClientProtocol.prompt,
                            {
                                Session = "s1"
                                Key = "k"
                                Text = "hi"
                                Mode = PromptMode.Steer
                            }
                        )
                    with
                    | Error(CallError.Failed(ClientError.Forbidden(IdentityFailure.NoRole _))) -> ()
                    | other -> failtest $"prompt: {other}"

                    match viewer.Try(ClientProtocol.sessions, ()) with
                    | Error(CallError.Failed(ClientError.Forbidden _)) -> ()
                    | other -> failtest $"identity/sessions: {other}"

                    let ask: GrantAsk =
                        {
                            Key = "tests/1"
                            Action = ActionName.create "shell.exec"
                            Resource = "make deploy"
                            Context =
                                {
                                    Session = Some "s1"
                                    Run = None
                                    Repo = None
                                }
                            Requester =
                                {
                                    Principal = "system"
                                    Agent = None
                                    Extension = None
                                }
                            Summary = "Run make deploy"
                            Evidence = []
                            Suggestions = []
                            Scopes = []
                            Expires = None
                        }

                    let id =
                        match waitFor 30.0 (Eff.run h.Env (GrantsService.request h.Grants ask)) with
                        | Ok(AskOutcome.Pending id) -> id
                        | other -> failtest $"{other}"

                    let once: GrantAnswer =
                        {
                            Effect = GrantEffect.Allow
                            Scope = ScopeChoice.Once
                            Resource = None
                            Expires = None
                            Reason = None
                        }

                    match viewer.Try(GrantsProtocol.answer, { Request = id; Answer = once }) with
                    | Error(CallError.Failed(GrantError.NotAPerson _)) -> ()
                    | other -> failtest $"grants/answer by a viewer: {other}"

                    // The owner answers over the protocol: a person, attributed.
                    let pending = owner.Call(GrantsProtocol.pending, ())
                    Expect.equal (pending.Requests |> List.map _.Id) [ id ] "pending"

                    match owner.Call(GrantsProtocol.answer, { Request = id; Answer = once }) with
                    | Fx.Grants.Resolution.Allowed _ -> ()
                    | other -> failtest $"{other}"

                    viewer.Close()
                    owner.Close()
                finally
                    stop h

            testCase
                "over the local listener: the token file, a session token as the bearer, revocation ends connections"
            <| fun () ->
                let p, h =
                    host "listener" [ Candidate.create Source.Builtin (Package.Native echoExtension) ]

                try
                    for listener in [ fileListener (); Fx.Transport.Http.LocalServer.Loopback 0 ] do
                        let served =
                            match waitFor 60.0 (Serve.start h listener) with
                            | Ok served -> served
                            | Error e -> failtest e

                        try
                            let found, token =
                                match waitFor 30.0 (Eff.run h.Env (HostClient.locate h.Options.Paths)) with
                                | Ok found -> found
                                | Error e -> failtest e

                            Expect.equal found served.Listener $"{listener}: listener.json names it"

                            let a =
                                match remote found token (HostClient.members answers) with
                                | Ok client -> client
                                | Error e -> failtest $"{listener}: {e}"

                            let info =
                                match IdentityClient.session a.Peer with
                                | Ok info -> info
                                | Error issues -> failtest (Issue.render issues)

                            Expect.equal info.Roles [ Role.Owner ] $"{listener}: the OS user"
                            Expect.isSome info.Token "a token was issued"

                            match
                                remote found "not-the-token-0123456789abcdefghijklmnopq" (HostClient.members answers)
                            with
                            | Error e -> Expect.stringContains e "401" $"{listener}: a wrong token"
                            | Ok c ->
                                c.Close()
                                failtest "connected with a wrong token"

                            // The session token is a bearer: the session resumes without a sign-in.
                            let b =
                                match remote found info.Token.Value [ ClientProtocol.AnswersMember, Json.Array [] ] with
                                | Ok client -> client
                                | Error e -> failtest $"{listener}: {e}"

                            Expect.equal (b.Call(ClientProtocol.whoami, ())).Id info.Id "the same session"

                            let hi = Json.Object [ "text", Json.String "hi" ]

                            Expect.equal
                                (a.Call(ClientProtocol.ext "tests.echo" "echo", hi))
                                (Json.Object [ "echo", hi ])
                                "an extension's method"

                            // Revoking the token ends every connection it opened.
                            Expect.isTrue (a.Call(ClientProtocol.revoke, { Id = info.Id })) "revoked"

                            for client in [ a; b ] do
                                waitFor 30.0 client.Peer.Completion |> ignore

                            match remote found info.Token.Value [] with
                            | Error e -> Expect.stringContains e "401" "the revoked token is refused"
                            | Ok c ->
                                c.Close()
                                failtest "connected with a revoked token"
                        finally
                            (Serve.stop served).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                finally
                    stop h

                Expect.isTrue (File.Exists(Path.Combine(p.Data, "listener.token"))) "the token file"

            testCase "with the bun-ts kernel: a client answers the first install's grant request, then a turn finishes"
            <| fun () ->
                match waitFor 600.0 (Fx.Kernel.Tests.PinnedBun.executable ()) with
                | Ok _ -> ()
                | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
                    failtest $"Bun is not available: {reason}"
                | Error reason -> skiptest $"Bun is not available: {reason}"

                let p = place "protocol-live"
                p.Write("notes.md", "# Notes\n")
                let model = Model()

                model.AddAll
                    [
                        $"// intent: read the notes\nconst notes = await {g}.fs.read(\"notes.md\")\nnotes.text"
                        $"// intent: finish\nawait {g}.finish({{ kind: \"done\", summary: \"Read the notes\" }})"
                    ]

                let h = start (options p model [ "model.role.main", Json.String modelId ])

                try
                    let c = embedded h

                    // Creating the session creates the workspace's environment, which installs Bun: the
                    // first install is a grant request, answered here, over the protocol, by a person.
                    let creating =
                        Eff.run
                            (clientEnv CancellationToken.None)
                            (Rpc.call c.Peer ClientProtocol.create { Session = Some "s1"; Title = None })

                    let deadline = DateTime.UtcNow.AddSeconds 120.0
                    let mutable answered = false

                    while not answered do
                        let pending = c.Call(GrantsProtocol.pending, ())

                        match pending.Requests with
                        | request :: _ ->
                            Expect.equal request.Action.Value "component.install" "the install"

                            let always: GrantAnswer =
                                {
                                    Effect = GrantEffect.Allow
                                    Scope = ScopeChoice.Host
                                    Resource = None
                                    Expires = None
                                    Reason = None
                                }

                            c.Call(
                                GrantsProtocol.answer,
                                {
                                    Request = request.Id
                                    Answer = always
                                }
                            )
                            |> ignore

                            answered <- true
                        | [] when creating.IsCompleted -> failtest $"created without asking: {creating.Result}"
                        | [] when DateTime.UtcNow > deadline -> failtest "no grant request"
                        | [] -> Thread.Sleep 20

                    match waitFor 300.0 creating with
                    | Ok entry -> Expect.equal entry.Session "s1" "created"
                    | Error e -> failtest $"{e}"

                    c.Subscribe("s1", 0L)

                    c.Call(
                        ClientProtocol.prompt,
                        {
                            Session = "s1"
                            Key = "p1"
                            Text = "Read the notes"
                            Mode = PromptMode.Steer
                        }
                    )
                    |> ignore

                    let ended = c.WaitFor isTurnEnded

                    match ended.Item with
                    | Feed.Next recorded ->
                        match recorded.Event with
                        | Event.TurnEnded(1, TurnEnd.Completed(TurnResult.Done("Read the notes", _, _)), _) -> ()
                        | other -> failtest $"{other}"
                    | other -> failtest $"{other}"

                    c.Close()
                finally
                    stop h

            test "the client protocol's types are generated from the codecs" {
                let file: DeclarationFile =
                    {
                        Layout = Layout.Module
                        Types = ClientProtocol.schemas
                        Members = []
                    }

                Fx.Kernel.Tests.Repo.expectGenerated
                    [ "tests"; "Fx.Host.Tests"; "golden"; "client.d.ts" ]
                    (DeclarationFile.render file)
            }

            test "every method has a least role, and only reads are a viewer's" {
                let viewers =
                    [
                        ClientProtocol.list.Name
                        ClientProtocol.get.Name
                        ClientProtocol.offers.Name
                        ClientProtocol.subscribe.Name
                        ClientProtocol.context.Name
                        ClientProtocol.whoami.Name
                        ClientProtocol.signOut.Name
                        ClientProtocol.envList.Name
                        ClientProtocol.envGet.Name
                    ]

                for name in viewers do
                    Expect.equal (ClientProtocol.leastRole name) Role.Viewer name

                for name in
                    [
                        ClientProtocol.create.Name
                        ClientProtocol.prompt.Name
                        ClientProtocol.interrupt.Name
                        ClientProtocol.answer.Name
                        ClientProtocol.fork.Name
                        ClientProtocol.overrides.Name
                        ClientProtocol.close.Name
                        ClientProtocol.reopen.Name
                        ClientProtocol.extName "x" "y"
                    ] do
                    Expect.equal (ClientProtocol.leastRole name) Role.Collaborator name

                for name in
                    [
                        ClientProtocol.sessions.Name
                        ClientProtocol.revoke.Name
                        ClientProtocol.envDestroy.Name
                    ] do
                    Expect.equal (ClientProtocol.leastRole name) Role.Owner name
            }
        ]
