/// The host's composition root (src/Fx.Host) on the JIT and as a native binary: `fx auth` on the file
/// credential store, the start (lock, config, the built-ins, the variant registry, the SQLite stores,
/// grants, service credentials, identity, environments, model access, sessions), signing in the OS
/// user in process, a headless run in an embedded environment, a restart that continues the session,
/// and the codecs of what `fx run --json` prints.
/// The interpreter is disabled in host config, so turns end at their hands: no network, no Bun.
module Fx.Aot.Scenarios.Host

open System
open System.Collections
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Sessions
open Fx.Auth
open Fx.Host
open Fx.Aot.Scenarios

let private temporary () =
    let dir =
        Path.Combine(Path.GetTempPath(), "host-scenario-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private remove (dir: string) =
    try
        for file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
            File.SetAttributes(file, FileAttributes.Normal)

        Directory.Delete(dir, true)
    with _ ->
        ()

let private variables () : (string * string) list =
    [
        for entry in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> do
            match entry.Key, entry.Value with
            | (:? string as name), (:? string as value) -> name, value
            | _ -> ()
    ]
    |> List.sortBy fst

/// A model that must never be called: the turns end before it.
let private noModel: Http = (InMemoryHttp(SystemClock.clock, (fun _ -> None))).Http

let private options (root: string) (explicit: (string * Json) list) : HostOptions =
    let workspace = Path.Combine(root, "workspace")
    Directory.CreateDirectory workspace |> ignore

    let facts: ProcessFacts =
        {
            Variables = variables ()
            CurrentDirectory = workspace
            Binary = Product.binaryName
            BinaryArgs = []
        }

    match Live.options facts workspace explicit with
    | Error e -> failwith e
    | Ok options ->
        let paths = HostPaths.under (Path.Combine(root, "data"))

        { options with
            Paths = paths
            Http = noModel
            Keyring = InMemoryKeyring().Keyring
            PrivateFiles = Fx.Keyring.LocalPrivateFiles.create paths.Credentials Product.Name
            HostVariables = InMemoryVariables.create [ "ANTHROPIC_API_KEY", "scenario-key" ]
            Prompts = false
        }

/// A login's flow that pastes a key.
let private paste: FlowRunner =
    fun flow _ ->
        match flow with
        | Fx.Sdk.AuthFlow.Paste _ -> Task.FromResult(Ok(Fx.Sdk.FlowResult.Pasted(SecretText "sk-scenario")))
        | other -> Task.FromResult(Error $"not a paste: {other}")

let private quiet: RunIo =
    {
        Show = ignore
        Answer = fun _ -> Task.FromResult None
        Decide = fun _ -> Task.FromResult None
        Interrupt = CancellationToken.None
    }

let private embedded () : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let say (line: string) = lines.Add(Show.escape line)
        let root = temporary ()

        try
            let data = Path.Combine(root, "data")
            Directory.CreateDirectory data |> ignore

            File.WriteAllText(
                Path.Combine(data, "config.json"),
                """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}, "variants": {"credentials.store": "file"}}"""
            )

            let opts =
                options root [ "model.role.main", Json.String "anthropic/claude-sonnet-5-5" ]

            for command in [ AuthCommand.Login("openai", None); AuthCommand.List; AuthCommand.Status ] do
                match! Commands.auth opts paste command with
                | Error errors -> say ($"auth {command} failed: " + String.concat "; " errors)
                | Ok(code, lines) ->
                    let shows = (String.concat " | " lines).Contains "sk-scenario"
                    say $"auth {command}: exit {code}, shows the key {shows}"

                    for line in lines do
                        say ("  " + line.Replace(root, "<root>"))

            match! Host.start opts with
            | Error errors -> say ("start failed: " + String.concat "; " errors)
            | Ok host ->
                let mutable session = ""

                try
                    say $"started: {host.Warnings.Length} warnings, lockfile {host.Lockfile.IsSome}"

                    say $"extensions: {host.Extensions.Loaded.Count} active, {host.Extensions.Report.Length} reported"

                    say $"registry has agent.interpreter: {host.Registry.Contains(HostSlots.interpreter.Id)}"

                    match!
                        Eff.run
                            (Host.within host CancellationToken.None)
                            (AuthService.resolve host.Auth Fx.Ext.ModelOpenAI.Provider.def)
                    with
                    | Ok(resolved, kind) ->
                        let names = resolved.Headers |> List.map fst |> String.concat ", "
                        say $"openai resolves with {kind}: headers {names}"
                    | Error e -> say $"openai does not resolve: {e}"

                    match! Commands.auth opts paste AuthCommand.Status with
                    | Ok _ -> say "fx auth ran beside the host"
                    | Error errors ->
                        let held = (String.concat "; " errors).Contains "a host is running"
                        say $"fx auth beside the host refused: {held}"

                    match! Host.start opts with
                    | Ok second ->
                        do! Host.stop second
                        say "a second host started"
                    | Error errors ->
                        let locked = (String.concat "; " errors).Contains "another host is running"
                        say $"a second host refused: {locked}"

                    match! HostSessions.signInLocal host "scenario" CancellationToken.None with
                    | Error e -> say $"sign-in failed: {e}"
                    | Ok signedIn ->
                        let roles = signedIn.Roles |> List.map string |> String.concat ", "
                        say $"signed in: {signedIn.Principal.Kind} via {signedIn.Principal.Provider}, roles {roles}"

                    match!
                        Headless.run
                            host
                            {
                                Prompt = "hello"
                                Session = None
                                Title = Some "scenario"
                            }
                            quiet
                            CancellationToken.None
                    with
                    | Error e -> say $"run failed: {e}"
                    | Ok outcome ->
                        session <- outcome.Session

                        let shown = { outcome with Session = "<id>" }

                        say $"run: {Json.serialize (Codec.encode Headless.outcomeCodec shown)}"
                        say $"exit code: {Headless.exitCode outcome}"
                finally
                    (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore

                // A new host on the same data directory continues the session.
                match! Host.start (options root []) with
                | Error errors -> say ("restart failed: " + String.concat "; " errors)
                | Ok host ->
                    try
                        let entries = HostSessions.entries host

                        say
                            $"restarted: {entries.Length} sessions, the same: {entries |> List.exists (fun e -> e.Session = session)}"

                        match!
                            Headless.run
                                host
                                {
                                    Prompt = "again"
                                    Session = Some session
                                    Title = None
                                }
                                quiet
                                CancellationToken.None
                        with
                        | Error e -> say $"second run failed: {e}"
                        | Ok outcome ->
                            let failed =
                                match outcome.Ending with
                                | TurnEnd.Failed reason -> reason.Contains "no interpreter"
                                | _ -> false

                            say $"second run: turn {outcome.Turn}, failed at its hands: {failed}"
                    finally
                        (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore
        finally
            remove root

        return List.ofSeq lines
    }

/// The pure parts: paths, config files, trust and the static list.
let private composition () : string list =
    [
        let config =
            match
                Json.parse
                    """{"variants": {"edit.guard": "require"}, "environments": {"default": {"provider": "local"}}, "colour": 1}"""
            with
            | Ok json -> json
            | Error e -> failwith (string e)

        match ConfigFile.ofJson Fx.Variants.Layer.Host "host.json" config with
        | Ok _ -> "config: accepted"
        | Error issues -> "config: " + Issue.render issues

        let root = if OperatingSystem.IsWindows() then @"C:\work" else "/work"

        let store =
            TrustStore.add false (Path.Combine(root, "app")) 1L TrustStore.empty
            |> Result.defaultWith failwith

        let below = TrustStore.isTrusted false (Path.Combine(root, "app", "src")) store
        let sibling = TrustStore.isTrusted false (Path.Combine(root, "app2")) store
        $"trust: below {below}, sibling {sibling}"
        "trust json: " + Json.serialize (Codec.encode TrustStore.codec store)

        "built-ins: " + String.concat ", " Builtins.ids

        for args in [ [ "login"; "openai"; "--method"; "api_key" ]; [ "status" ]; [ "bogus" ] ] do
            match AuthCommand.parse args with
            | Ok command -> "auth: " + string command
            | Error e -> "auth: " + e

        "core slots: " + string HostSlots.core.Length

        "index: "
        + Json.serialize (
            Codec.encode
                SessionIndex.event
                (IndexEvent.Created
                    {
                        Session = "s1"
                        Workspace = "/w"
                        Environment = "e1"
                        Title = None
                    })
        )

        for slot in HostSlots.all do
            $"slot {slot.Id} = {Json.serialize slot.Default} privileged {slot.Privileged}"
    ]

/// A client's environment.
type private ClientEnv =
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

/// The client protocol: in process (embedded mode), then over the local listener on loopback TCP with
/// the token file: sign-in, sessions created, prompted and followed with subscribe(afterSeq), the
/// typed errors, environments, the codecs' JSON.
let private protocol () : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let say (line: string) = lines.Add(Show.escape line)
        let root = temporary ()

        try
            let data = Path.Combine(root, "data")
            Directory.CreateDirectory data |> ignore

            File.WriteAllText(
                Path.Combine(data, "config.json"),
                """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}}"""
            )

            match! Host.start (options root [ "model.role.main", Json.String "anthropic/claude-sonnet-5-5" ]) with
            | Error errors -> say ("start failed: " + String.concat "; " errors)
            | Ok host ->
                use lifetime = new CancellationTokenSource()

                let env =
                    {
                        Clock = SystemClock.clock
                        Scope = Scope.ofCancellation lifetime.Token
                    }

                let events = Collections.Concurrent.ConcurrentQueue<SessionNotice>()

                let service =
                    Fx.Rpc.Service.create
                        { Name = "scenario"; Version = "0" }
                        [
                            Fx.Rpc.Handler.notification ClientProtocol.event (fun _ e -> Eff.ret (events.Enqueue e))
                        ]

                let call
                    (peer: Fx.Rpc.Peer)
                    (m: Fx.Rpc.Method<'P, 'R, 'E>)
                    (value: 'P)
                    : Task<Result<'R, Fx.Rpc.CallError<'E>>> =
                    Eff.run env (Fx.Rpc.Rpc.call peer m value)

                let ended () =
                    events
                    |> Seq.exists (fun e ->
                        match e.Item with
                        | Fx.Store.Feed.Next recorded ->
                            match recorded.Event with
                            | Event.TurnEnded _ -> true
                            | _ -> false
                        | Fx.Store.Feed.Reset _ -> false)

                try
                    match! Eff.run env (HostClient.embedded host service [ "agent.question" ]) with
                    | Error e -> say $"embedded connection failed: {e}"
                    | Ok peer ->
                        match! call peer ClientProtocol.whoami () with
                        | Ok me ->
                            let roles = me.Roles |> List.map string |> String.concat ", "

                            say
                                $"whoami: {me.Principal.Kind} via {me.Principal.Provider}, roles {roles}, token shown {me.Token.IsSome}"
                        | Error e -> say $"whoami failed: {e}"

                        match!
                            call
                                peer
                                ClientProtocol.create
                                {
                                    Session = Some "s1"
                                    Title = Some "scenario"
                                }
                        with
                        | Ok entry ->
                            let titled = entry.Title = Some "scenario"
                            say $"created: {entry.Session}, titled {titled}"
                        | Error e -> say $"create failed: {e}"

                        let subscription = call peer ClientProtocol.subscribe { Session = "s1"; After = 0L }

                        match!
                            call
                                peer
                                ClientProtocol.prompt
                                {
                                    Session = "s1"
                                    Key = "p1"
                                    Text = "hello"
                                    Mode = PromptMode.Steer
                                }
                        with
                        | Ok committed -> say $"prompted: {committed.Count > 0}"
                        | Error e -> say $"prompt failed: {e}"

                        let mutable waited = 0

                        while not (ended ()) && waited < 3000 do
                            do! Task.Delay 20
                            waited <- waited + 1

                        let first =
                            events
                            |> Seq.tryHead
                            |> Option.map (fun e ->
                                match e.Item with
                                | Fx.Store.Feed.Next recorded -> recorded.Seq
                                | Fx.Store.Feed.Reset snapshot -> snapshot.Seq)
                            |> Option.defaultValue 0L

                        say $"subscription: turn ended {ended ()}, first event #{first}"

                        match! call peer ClientProtocol.get { Session = "nope" } with
                        | Error(Fx.Rpc.CallError.Failed e) ->
                            say $"unknown session: {Json.serialize (Codec.encode ClientProtocol.clientError e)}"
                        | Ok _ -> say "unknown session: found"
                        | Error e -> say $"unknown session: {e}"

                        match! call peer ClientProtocol.envList () with
                        | Ok envs ->
                            for e in envs do
                                let transport = e.Transport |> Option.defaultValue "-"
                                say $"environment: {e.Provider} {e.Phase} {transport} warnings {e.Warnings.Length}"
                        | Error e -> say $"env/list failed: {e}"

                        lifetime.Cancel()

                        try
                            let! _ = subscription
                            ()
                        with _ ->
                            ()

                        let! _ = peer.CloseAsync()
                        ()

                    // Over loopback TCP, with the listener's token.
                    match! Serve.start host (Fx.Transport.Http.LocalServer.Loopback 0) with
                    | Error e -> say $"serve failed: {e}"
                    | Ok served ->
                        try
                            let! located = Eff.run host.Env (HostClient.locate host.Options.Paths)

                            match located with
                            | Error e -> say $"locate failed: {e}"
                            | Ok(listener, token) ->
                                say
                                    $"listener: {(Listening.info listener).Kind}, the one served {listener = served.Listener}"

                                let env = { env with Scope = Scope.root }

                                match!
                                    Eff.run env (HostClient.connect listener token service (HostClient.members []))
                                with
                                | Error e -> say $"remote connection failed: {e}"
                                | Ok peer ->
                                    match! Eff.run env (Fx.Rpc.Rpc.call peer ClientProtocol.list ()) with
                                    | Ok sessions ->
                                        let ids = sessions |> List.map _.Session |> String.concat ", "
                                        say $"remote list: {ids}"
                                    | Error e -> say $"remote list failed: {e}"

                                    let! _ = peer.CloseAsync()
                                    ()

                                match!
                                    Eff.run
                                        env
                                        (HostClient.connect
                                            listener
                                            "wrong-token-0123456789abcdefghijklmnopq"
                                            service
                                            [])
                                with
                                | Error e -> say $"wrong token: {e}"
                                | Ok _ -> say "wrong token: connected"
                        finally
                            (Serve.stop served).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                finally
                    (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore
        finally
            remove root

        return List.ofSeq lines
    }

/// The protocol's codecs: the JSON of its messages.
let private protocolCodecs () : string list =
    [
        let show (codec: Codec<'T>) (value: 'T) =
            let json = Codec.encode codec value
            let text = Json.serialize json
            let back = Codec.decode codec json = Ok value
            $"{text} (reads back {back})"

        show
            ClientProtocol.promptSession
            {
                Session = "s1"
                Key = "k1"
                Text = "fix it"
                Mode = PromptMode.Queue
            }

        show
            ClientProtocol.overrideSession
            {
                Session = "s1"
                Key = "k2"
                Set = [ "agent.actionSurface", Json.String "hybrid" ]
                Unset = [ "edit.guard" ]
            }

        show ClientProtocol.createSession { Session = None; Title = Some "t" }
        show ClientProtocol.committed { After = 4L; Count = 2 }

        show ClientProtocol.clientError (ClientError.Forbidden(Fx.Identity.IdentityFailure.NoRole "user:p:s"))

        show ClientProtocol.clientError (ClientError.Rejected("session.busy", "a turn is running"))
        show ClientProtocol.extCallError (ExtCallError.Failed(Fx.Sdk.MethodFailure.Crashed "IOException"))

        show
            ClientProtocol.envSummary
            {
                Id = "e1"
                Name = "default"
                Provider = "local"
                Phase = "ready"
                Transport = Some "in-process"
                Workspace = Some "/w"
                Reason = None
                Warnings = [ "unsandboxed" ]
            }

        for name in [ "session/get"; "session/prompt"; "identity/revoke"; "ext/x/y" ] do
            $"least role of {name}: {ClientProtocol.leastRole name}"
    ]

let private bounded (run: unit -> Task<string list>) () : Task<string list> =
    run().WaitAsync(TimeSpan.FromSeconds 240.0)

let scenarios: Scenario list =
    [
        Scenario.lines "host-composition" composition
        Scenario.task "host-embedded" (bounded embedded)
        Scenario.lines "host-protocol-codecs" protocolCodecs
        Scenario.task "host-protocol" (bounded protocol)
    ]
