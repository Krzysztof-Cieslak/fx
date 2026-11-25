/// The grants log (T123, docs/modules/grants.md): patterns and their specificity, the effective rules,
/// the machine's events and their JSON, and the service on the fake clock (requests, answers of every
/// scope, covered and timed-out requests, expiries, policy gates, a restart) with its client protocol
/// over an in-process JSON-RPC connection.
module Fx.Aot.Scenarios.Grants

open System
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host
open Fx.Rpc
open Fx.Store
open Fx.Sdk
open Fx.Grants
open Fx.Grants.Host

type private Env =
    {
        Clock: Clock
        Ids: Ids
        Trace: Trace
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

let private shellExec = ActionName.create "shell.exec"
let private fsWrite = ActionName.create "fs.write"

let private context =
    {
        Session = Some "s1"
        Run = None
        Repo = Some { Repo = "r1"; Path = "src/billing" }
    }

let private query (action: ActionName) (resource: string) : GrantQuery =
    {
        Action = action
        Resource = resource
        Context = context
    }

let private ask (key: string) (action: ActionName) (resource: string) : GrantAsk =
    {
        Key = key
        Action = action
        Resource = resource
        Context = context
        Requester =
            {
                Principal = "user:ann"
                Agent = Some "build"
                Extension = None
            }
        Summary = "Run " + resource
        Evidence = []
        Suggestions = []
        Scopes = []
        Expires = None
    }

let private allow (scope: ScopeChoice) : GrantAnswer =
    {
        Effect = GrantEffect.Allow
        Scope = scope
        Resource = None
        Expires = None
        Reason = None
    }

let private show (result: Result<'T, GrantError>) : string =
    match result with
    | Ok value -> "ok " + string value
    | Error e -> "error " + e.Code

/// Patterns, specificity and the effective rules, with no effects.
let rules =
    Scenario.lines "grants-rules" (fun () ->
        let matches =
            [
                ResourceKind.Path, "src/**", "src/a/b.fs"
                ResourceKind.Path, "src/*", "src/a/b.fs"
                ResourceKind.Path, "**/*.fs", "x/y/a.fs"
                ResourceKind.Path, "a?.fs", "ab.fs"
                ResourceKind.Command, "git *", "git log -n 3"
                ResourceKind.Command, "git **", "git log -n 3"
                ResourceKind.Name, "*.github.com:443", "api.github.com:443"
                ResourceKind.Name, "a\\*b", "a*b"
                ResourceKind.Path, "src/**", "SRC/a.fs"
            ]
            |> List.map (fun (kind, pattern, resource) ->
                string kind
                + " "
                + pattern
                + " ~ "
                + resource
                + ": "
                + string (Pattern.matches kind pattern resource)
                + "/"
                + string (Pattern.matchesIgnoringCase kind pattern resource)
                + " "
                + string (Pattern.specificity kind pattern))

        let invalid =
            [
                ResourceKind.Path, "a//b"
                ResourceKind.Path, "src**"
                ResourceKind.Name, "a**"
            ]
            |> List.map (fun (kind, pattern) ->
                match Pattern.validate kind pattern with
                | Ok() -> pattern + ": valid"
                | Error reason -> pattern + ": " + reason)

        let grant (id: string) (action: ActionName) (pattern: string) (effect: GrantEffect) (scope: GrantScope) =
            {
                Grant =
                    {
                        Id = GrantId id
                        Action = action
                        Resource = pattern
                        Effect = effect
                        Scope = scope
                        By = "user:ann"
                        At = 10L
                        Expires = None
                        Reason = None
                        Origin = GrantOrigin.Direct "tui"
                    }
                Status = GrantStatus.Active
            }

        let grants =
            [
                grant "g1" shellExec "git **" GrantEffect.Allow GrantScope.Host
                grant "g2" shellExec "git status" GrantEffect.Allow (GrantScope.Session "s1")
                grant "g3" shellExec "git push **" GrantEffect.Deny GrantScope.Host
                grant "g4" fsWrite "src/**" GrantEffect.Allow (GrantScope.Repo { Repo = "r1"; Path = Some "src" })
                grant "g5" fsWrite "secrets/**" GrantEffect.Deny GrantScope.Host
            ]

        let policy =
            [
                {
                    Id = "host/curl"
                    Action = shellExec
                    Resource = "curl **"
                    Effect = PolicyEffect.Ask
                    Reason = "network tools need a person"
                }
            ]

        let decisions =
            [
                query shellExec "git status"
                query shellExec "git log"
                query shellExec "git push origin main"
                query shellExec "curl example.com"
                query fsWrite "src/billing/a.fs"
                query fsWrite "Secrets/key"
                query fsWrite "src/../secrets/key"
                query (ActionName.create "x.unknown") "a"
            ]
            |> List.map (fun q -> string q + " -> " + string (Rules.evaluate Actions.builtIn policy 20L grants q))

        [ yield! matches; yield! invalid; yield! decisions ])

/// The machine's events and their canonical JSON, and every error code.
let log =
    Scenario.lines "grants-log" (fun () ->
        let command by at op : GrantCommand = { Command = op; By = by; At = at }

        let request id key resource at =
            match GrantLog.request Actions.builtIn (ask key shellExec resource) (RequestId id) at with
            | Ok made -> made
            | Error e -> invalidOp (string e)

        let commands =
            [
                command "user:ann" 1L (GrantOp.Request(request "q1" "k1" "git status" 1L))
                command "user:ann" 2L (GrantOp.Answer(RequestId "q1", allow ScopeChoice.Session, GrantId "g1"))
                command "user:ann" 3L (GrantOp.Request(request "q2" "k2" "make" 3L))
                command "system" 4L (GrantOp.Answer(RequestId "q2", allow ScopeChoice.Once, GrantId "g2"))
                command
                    "user:bob"
                    5L
                    (GrantOp.Answer(
                        RequestId "q2",
                        { allow ScopeChoice.Once with
                            Effect = GrantEffect.Deny
                        },
                        GrantId "g2"
                    ))
                command "user:bob" 6L (GrantOp.Revoke(GrantId "g1", Some "done"))
                command "user:ann" 7L (GrantOp.Request(request "q3" "k3" "ls" 7L))
                command "system" 8L (GrantOp.EndSession "s1")
            ]

        let state, lines =
            commands
            |> List.fold
                (fun (state, lines) c ->
                    match GrantLog.decide Actions.builtIn state c with
                    | Ok events ->
                        let next = List.fold GrantLog.evolve state events

                        next,
                        lines
                        @ (events |> List.map (fun e -> Json.serializeCanonical (Codecs.event.Encode e)))
                    | Error e -> state, lines @ [ "refused " + e.Code + ": " + e.Message ])
                (GrantLog.initial, [])

        let errors =
            [
                GrantError.UnknownAction "x.y"
                GrantError.NotPending(RequestId "q")
                GrantError.Conflict("grants.not-active", "m")
                GrantError.Unavailable "stopped"
            ]
            |> List.map (fun e -> e.Code + " " + Json.serializeCanonical (Codecs.error.Encode e))

        [
            yield! lines
            yield Json.serializeCanonical (Codecs.state.Encode state)
            yield
                "state round-trips: "
                + string (Codec.decodeText Codecs.state (Codec.encodeText Codecs.state state) = Ok state)
            yield! errors
        ])

/// The service on the fake clock and its client protocol. On a thread-pool thread: FakeClock.Advance
/// needs inline continuations, which the main thread of a musl Native AOT binary never gets.
let service =
    Scenario.task "grants-service" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Ids = FakeIds(11UL, clock.Clock).Ids
                        Trace = Trace.none
                        Scope = Scope.root
                    }

                let run (effect: Eff<Env, 'T>) : 'T = wait (Eff.run env effect)
                let ann = Attribution.create "user:ann" "tui"
                let out = Collections.Generic.List<string>()
                let store = InMemoryEventLog<Payload>()

                let gate: Contributed<Gate<GrantQuery>> =
                    {
                        Extension = "fx.policy"
                        Type = GrantPolicy.gates.Name
                        Id = "curl"
                        Value =
                            {
                                Check =
                                    fun q ->
                                        Eff.ret (
                                            if q.Resource.StartsWith("curl", StringComparison.Ordinal) then
                                                Decision.Ask "network tools need a person"
                                            else
                                                Decision.Allow
                                        )
                            }
                        Env =
                            {
                                BasicExtensionEnv.Extension = "fx.policy"
                                Clock = clock.Clock
                                Scope = Scope.root
                            }
                            :> IExtensionEnv
                    }

                let settings =
                    { GrantsSettings.create (GrantStoreLog.hostLocal store.Log) with
                        Gates = [ gate ]
                    }

                let service = run (GrantsService.start settings)
                let at = clock.Now.UnixMicroseconds

                let pending (a: GrantAsk) =
                    match run (GrantsService.request service a) with
                    | Ok(AskOutcome.Pending id) -> id
                    | other -> invalidOp (show other)

                let first = pending (ask "call-1" shellExec "npm test")
                let second = pending (ask "call-2" shellExec "npm run build")
                let covered = Eff.run env (GrantsService.await service second)

                out.Add(
                    "answer: "
                    + show (
                        run (
                            GrantsService.answer
                                service
                                ann
                                first
                                { allow ScopeChoice.Session with
                                    Resource = Some "npm **"
                                }
                        )
                    )
                )

                out.Add("covered: " + show (wait covered))

                out.Add(
                    "again: "
                    + show (run (GrantsService.request service (ask "call-3" shellExec "npm ci")))
                )

                out.Add("curl: " + show (run (GrantsService.check service (query shellExec "curl x"))))

                let timed =
                    pending
                        { ask "call-4" fsWrite "src/billing/a.fs" with
                            Expires = Some(at + 30_000_000L)
                        }

                let timing = Eff.run env (GrantsService.await service timed)
                clock.Advance(TimeSpan.FromSeconds 30.0)
                out.Add("timed out: " + show (wait timing))

                let expiring = pending (ask "call-5" fsWrite "src/billing/b.fs")

                out.Add(
                    "repo: "
                    + show (
                        run (
                            GrantsService.answer
                                service
                                ann
                                expiring
                                { allow (ScopeChoice.Repo(Some "src")) with
                                    Resource = Some "src/**"
                                    Expires = Some(clock.Now.UnixMicroseconds + 60_000_000L)
                                }
                        )
                    )
                )

                out.Add(
                    "before expiry: "
                    + show (run (GrantsService.check service (query fsWrite "src/c.fs")))
                )

                clock.Advance(TimeSpan.FromSeconds 60.0)

                out.Add(
                    "after expiry: "
                    + show (run (GrantsService.check service (query fsWrite "src/c.fs")))
                )

                let left = pending (ask "call-6" shellExec "make")
                let before = GrantsService.snapshot service
                run (GrantsService.stop service)
                let restarted = run (GrantsService.start settings)
                out.Add("restarted the same: " + string (GrantsService.snapshot restarted = before))

                // The client protocol, over an in-process connection.
                let limits = ConnectionLimits.frames ConnectionLimits.defaults
                let clientSide, serverSide = InProcess.pair 65536 limits limits
                let caller: GrantsProtocol.Caller = fun _ -> Ok(Attribution.create "user:bob" "web")

                let server =
                    Service.create { Name = "host"; Version = "0" } (GrantsProtocol.handlers restarted caller)

                let accepting = Eff.run env (Rpc.accept server serverSide)

                let peer =
                    match
                        wait (
                            Eff.run
                                env
                                (Rpc.connect (Service.create { Name = "tui"; Version = "0" } []) [] clientSide)
                        )
                    with
                    | Ok peer -> peer
                    | Error e -> invalidOp (string e)

                wait accepting |> ignore
                let call m p = wait (Eff.run env (Rpc.call peer m p))

                match call GrantsProtocol.pending () with
                | Ok listed -> out.Add("pending: " + String.Join(", ", listed.Requests |> List.map (fun r -> r.Key)))
                | Error e -> out.Add("pending: " + string e)

                match
                    call
                        GrantsProtocol.answer
                        {
                            Request = left
                            Answer = allow ScopeChoice.Once
                        }
                with
                | Ok resolution -> out.Add("answered over RPC: " + string resolution)
                | Error e -> out.Add("answer failed: " + string e)

                match
                    call
                        GrantsProtocol.answer
                        {
                            Request = RequestId "nope"
                            Answer = allow ScopeChoice.Once
                        }
                with
                | Error(CallError.Failed e) -> out.Add("typed error: " + e.Code)
                | other -> out.Add("unexpected: " + string other)

                match call GrantsProtocol.list { All = true } with
                | Ok listed ->
                    for entry in listed.Grants do
                        out.Add("grant: " + string entry)
                | Error e -> out.Add("list: " + string e)

                wait (Eff.run env (Rpc.close peer)) |> ignore
                let final = GrantsService.snapshot restarted
                run (GrantsService.stop restarted)

                [
                    yield!
                        store.Events(GrantMachine.stream "host")
                        |> List.map (fun r -> Json.serializeCanonical r.Event.Data + " " + string r.Meta)
                    yield! out
                    yield
                        "replayed: "
                        + string (
                            GrantLog.replay (
                                store.Events(GrantMachine.stream "host")
                                |> List.map (fun r ->
                                    match EventSchema.decode GrantMachine.schema r.Event with
                                    | Ok e -> e
                                    | Error reason -> invalidOp reason)
                            ) =
                                final.State
                        )
                ])
        ))

let scenarios: Scenario list = [ rules; log; service ]
