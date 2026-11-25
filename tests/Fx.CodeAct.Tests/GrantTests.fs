/// The grants guard (T127b): on its own over the real grants service, and in turns on the real
/// session host (script kernel in process, local hands): both action surfaces asking, answered,
/// denied, interrupted, steered away, timed out, refused by policy, across a restart, and replayed.
module Fx.CodeAct.Tests.GrantTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Machines.Host
open Fx.Models
open Fx.Store
open Fx.Store.Fakes
open Fx.Kernel
open Fx.Prompt
open Fx.Sdk
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Grants
open Fx.Grants.Host
open Fx.CodeAct
open Fx.CodeAct.Tests.Support

/// What the grants service runs in.
type GrantsEnv =
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

    interface IScopeWith<GrantsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "GrantsEnv"

let private grantsEnv () : GrantsEnv =
    {
        Clock = SystemClock.clock
        Ids = SystemIds.create SystemClock.clock
        Trace = Trace.none
        Scope = Scope.root
    }

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

let private startGrants (env: GrantsEnv) (store: InMemoryEventLog<Payload>) (policy: PolicyRule list) =
    let settings =
        { GrantsSettings.create (GrantStoreLog.hostLocal store.Log) with
            Policy = policy
        }

    wait (Eff.run env (GrantsService.start settings))

let private shellExec = ActionName.create "shell.exec"

let private allow (scope: ScopeChoice) (pattern: string option) : GrantAnswer =
    {
        Effect = GrantEffect.Allow
        Scope = scope
        Resource = pattern
        Expires = None
        Reason = None
    }

let private deny: GrantAnswer =
    {
        Effect = GrantEffect.Deny
        Scope = ScopeChoice.Once
        Resource = None
        Expires = None
        Reason = Some "not now"
    }

let private denyRule (id: string) (pattern: string) : PolicyRule =
    {
        Id = id
        Action = shellExec
        Resource = pattern
        Effect = PolicyEffect.Deny
        Reason = "never in tests"
    }

let private status (service: GrantsService<GrantsEnv>) (id: RequestId) : RequestStatus =
    (GrantsService.snapshot service).State.Requests[id].Status

let private requestCount (service: GrantsService<GrantsEnv>) : int =
    (GrantsService.snapshot service).State.Requests.Count

let private pending (service: GrantsService<GrantsEnv>) : GrantRequest list =
    (GrantsService.pending service).Requests

/// Waits until `count` requests are pending, and gives them.
let private pendingAt (service: GrantsService<GrantsEnv>) (count: int) : Task<GrantRequest list> =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 30.0

        while (pending service).Length < count do
            if DateTime.UtcNow > deadline then
                failtest $"{count} requests did not become pending: {pending service}"

            do! Task.Delay 10

        return pending service
    }

let private answer (env: GrantsEnv) (service: GrantsService<GrantsEnv>) (request: GrantRequest) (answer: GrantAnswer) =
    match wait (Eff.run env (GrantsService.answer service alice request.Id answer)) with
    | Ok _ -> ()
    | Error e -> failtest $"the answer was refused: {e}"

let private withdrawn (service: GrantsService<GrantsEnv>) (id: RequestId) =
    match status service id with
    | RequestStatus.Closed { Why = CloseReason.Withdrawn _ } -> ()
    | other -> failtest $"{id} is not withdrawn: {other}"

// ---- The guard on its own ---------------------------------------------------------------------------

let private turn: TurnStart =
    {
        Session = "s1"
        Turn = 1
        Profile = "p1"
        Inputs =
            [
                {
                    Id = 2L
                    By = alice
                    At = 0L
                    Content = InputContent.Text "go"
                }
            ]
        Seq = 3L
    }

let private place = Workplace.create "/home/me/ws" Os.Linux

type private Bench =
    {
        Env: GrantsEnv
        Store: InMemoryEventLog<Payload>
        Service: GrantsService<GrantsEnv>
    }

    member this.Guard(settings: GrantSettings, ?rules: BindingRule list) =
        let setup =
            { GrantSetup.create (GrantsService.grants this.Service) (SessionLog.typed this.Store.Log) turn settings with
                Rules = defaultArg rules Permissions.core
            }

        GrantGuard.create<GrantsEnv> setup place

    member this.Call(settings: GrantSettings, call: HostCall, ?cancellation: CancellationToken) =
        let env =
            { this.Env with
                Scope =
                    { this.Env.Scope with
                        Cancellation = defaultArg cancellation CancellationToken.None
                    }
            }

        Eff.run env (this.Guard settings call)

    override _.ToString() = "Bench"

let private bench (policy: PolicyRule list) : Bench =
    let env = grantsEnv ()
    let store = InMemoryEventLog<Payload>()

    {
        Env = env
        Store = store
        Service = startGrants env store policy
    }

let private shellCall (cell: string) (line: string) : HostCall =
    {
        CellId = Some cell
        Binding = "shell.run"
        Args = [ Value.Inline(Json.String line) ]
    }

let private deniedWith (result: Result<unit, HostCallError>) (part: string) =
    match result with
    | Error(HostCallError.Denied reason) -> Expect.stringContains reason part "why"
    | other -> failtest $"expected a denial, got {other}"

let private guardTests =
    testList
        "the guard"
        [
            testTask "listed commands and calls in the workspace pass without a request" {
                let b = bench []

                let! listed =
                    b.Call(GrantSettings.defaults, shellCall "t1-s1-1" "ls -la && cat a.txt")

                Expect.equal listed (Ok()) "listed"

                let! write =
                    b.Call(
                        GrantSettings.defaults,
                        {
                            CellId = Some "t1-s1-1"
                            Binding = "fs.write"
                            Args = [ Value.Inline(Json.String "src/a.fs"); Value.Inline(Json.String "x") ]
                        }
                    )

                Expect.equal write (Ok()) "a write in the workspace"
                Expect.equal (requestCount b.Service) 0 "nothing asked"
            }

            testTask "what no grant covers waits for a person, keyed by the turn, the call and the resource" {
                let b = bench []
                let call = shellCall "t1-s1-1" "mkdir made"
                let waiting = b.Call(GrantSettings.defaults, call)
                let! (requests: GrantRequest list) = pendingAt b.Service 1
                let request = requests.Head
                Expect.isFalse waiting.IsCompleted "the call waits"

                Expect.stringStarts request.Key "session/s1#3/t1-s1-1/shell.exec/" "the key"
                Expect.equal request.Action shellExec "the action"
                Expect.equal request.Resource "mkdir made" "the resource"
                Expect.equal request.Context.Session (Some "s1") "the session"
                Expect.equal request.Requester.Principal "user:alice" "on whose behalf"
                Expect.equal request.Requester.Agent (Some "main") "the agent"
                Expect.equal request.Summary "Run `mkdir made`" "the summary"
                Expect.equal request.Suggestions [ "mkdir **" ] "a broader pattern"

                Expect.contains
                    request.Evidence
                    ({
                        Label = "asked by"
                        Text = "session s1, turn 1, t1-s1-1"
                    }
                    : Fx.Grants.Evidence)
                    "where it comes from"

                answer b.Env b.Service request (allow ScopeChoice.Once None)
                let! result = waiting
                Expect.equal result (Ok()) "allowed"

                // The same call again is the same request, already answered.
                let! again = b.Call(GrantSettings.defaults, call)
                Expect.equal again (Ok()) "the same request"
                Expect.equal (requestCount b.Service) 1 "no new request"

                // Another cell asks anew; a denial refuses the call.
                let other = b.Call(GrantSettings.defaults, shellCall "t1-s1-2" "mkdir made")
                let! (requests: GrantRequest list) = pendingAt b.Service 1
                answer b.Env b.Service requests.Head deny
                let! refused = other
                deniedWith refused "shell.exec `mkdir made` was denied (denied by user:alice: not now)"
                deniedWith refused "Do not work around a denial"
            }

            testTask "a session grant with a broader pattern covers later calls" {
                let b = bench []
                let first = b.Call(GrantSettings.defaults, shellCall "t1-s1-1" "npm run build")
                let! (requests: GrantRequest list) = pendingAt b.Service 1
                answer b.Env b.Service requests.Head (allow ScopeChoice.Session (Some "npm run **"))
                let! result = first
                Expect.equal result (Ok()) "allowed"
                let! later = b.Call(GrantSettings.defaults, shellCall "t1-s2-1" "npm run test")
                Expect.equal later (Ok()) "covered"
                Expect.equal (requestCount b.Service) 1 "asked once"
            }

            testTask "policy and deny grants refuse at once, also listed commands" {
                let b = bench [ denyRule "test/no-rm" "rm **" ]
                let! rm = b.Call(GrantSettings.defaults, shellCall "t1-s1-1" "make && rm -rf build")
                deniedWith rm "deny by policy test/no-rm: never in tests"

                let draft: GrantDraft =
                    {
                        Action = shellExec
                        Resource = "cat **"
                        Effect = GrantEffect.Deny
                        Scope = GrantScope.Host
                        Expires = None
                        Reason = Some "no cat"
                    }

                match wait (Eff.run b.Env (GrantsService.grant b.Service alice draft)) with
                | Ok _ -> ()
                | Error e -> failtest $"{e}"

                let! cat = b.Call(GrantSettings.defaults, shellCall "t1-s1-2" "cat a.txt")
                deniedWith cat "no cat"
                Expect.equal (requestCount b.Service) 0 "nobody asked"
            }

            testTask "mode deny refuses what a person would decide; mode off checks nothing" {
                let b = bench [ denyRule "test/no-rm" "rm **" ]

                let! denied =
                    b.Call(
                        { GrantSettings.defaults with
                            Mode = GrantMode.Deny
                        },
                        shellCall "t1-s1-1" "mkdir made"
                    )

                deniedWith denied "nobody is asked here (codeact.grants.mode is deny): no grant applies"

                let! off =
                    b.Call(
                        { GrantSettings.defaults with
                            Mode = GrantMode.Off
                        },
                        shellCall "t1-s1-1" "rm -rf /"
                    )

                Expect.equal off (Ok()) "not checked"
                Expect.equal (requestCount b.Service) 0 "nobody asked"
            }

            testTask "a call's requests are made at once; the first denial withdraws the others" {
                let b = bench []

                let waiting =
                    b.Call(GrantSettings.defaults, shellCall "t1-s1-1" "mkdir a && mkdir b")

                let! (requests: GrantRequest list) = pendingAt b.Service 2

                Expect.equal (requests |> List.map (fun r -> r.Resource) |> List.sort) [ "mkdir a"; "mkdir b" ] "both"

                let second = requests |> List.find (fun r -> r.Resource = "mkdir b")
                let first = requests |> List.find (fun r -> r.Resource = "mkdir a")
                answer b.Env b.Service second deny
                let! refused = waiting
                deniedWith refused "shell.exec `mkdir b` was denied"
                withdrawn b.Service first.Id
            }

            testTask "an interrupted wait withdraws its requests" {
                let b = bench []
                let cancel = new CancellationTokenSource()

                let waiting =
                    b.Call(GrantSettings.defaults, shellCall "t1-s1-1" "mkdir made", cancel.Token)

                let! (requests: GrantRequest list) = pendingAt b.Service 1
                cancel.Cancel()

                let! outcome =
                    task {
                        try
                            let! _ = waiting
                            return "completed"
                        with :? OperationCanceledException ->
                            return "cancelled"
                    }

                Expect.equal outcome "cancelled" "the cancellation goes on"
                withdrawn b.Service requests.Head.Id
            }

            testTask "a request nobody answers in time refuses the call" {
                let b = bench []

                let! result =
                    b.Call(
                        { GrantSettings.defaults with
                            Timeout = Some(TimeSpan.FromMilliseconds 200.0)
                        },
                        shellCall "t1-s1-1" "mkdir made"
                    )

                deniedWith result "was not run: its grant request was not answered (timed out)"
            }

            testTask "network bindings ask net.connect; what cannot be named is refused" {
                let b = bench []

                let fetch =
                    Permissions.connects "net.fetch" (fun call ->
                        match call.Args with
                        | [ Value.Inline(Json.String url) ] -> Permissions.endpoint url |> Result.map List.singleton
                        | _ -> Error "net.fetch takes a URL")

                let setup =
                    { GrantSetup.create
                          (GrantsService.grants b.Service)
                          (SessionLog.typed b.Store.Log)
                          turn
                          GrantSettings.defaults with
                        Rules = fetch :: Permissions.core
                    }

                let guard = GrantGuard.create<GrantsEnv> setup place

                let waiting =
                    Eff.run
                        b.Env
                        (guard
                            {
                                CellId = Some "t1-s1-1"
                                Binding = "net.fetch"
                                Args = [ Value.Inline(Json.String "https://example.com/x") ]
                            })

                let! (requests: GrantRequest list) = pendingAt b.Service 1
                Expect.equal requests.Head.Action (ActionName.create "net.connect") "net.connect"
                Expect.equal requests.Head.Resource "example.com:443" "host and port"
                answer b.Env b.Service requests.Head (allow ScopeChoice.Once None)
                let! allowed = waiting
                Expect.equal allowed (Ok()) "allowed"

                let windowsGuard =
                    GrantGuard.create<GrantsEnv> setup (Workplace.create "C:\\ws" Os.Windows)

                let! share =
                    Eff.run
                        b.Env
                        (windowsGuard
                            {
                                CellId = Some "t1-s1-2"
                                Binding = "fs.write"
                                Args =
                                    [
                                        Value.Inline(Json.String "\\\\server\\share\\x")
                                        Value.Inline(Json.String "x")
                                    ]
                            })

                deniedWith share "fs.write was refused"
            }
        ]

// ---- Turns ------------------------------------------------------------------------------------------

let private os =
    if OperatingSystem.IsWindows() then Os.Windows
    elif OperatingSystem.IsMacOS() then Os.MacOs
    else Os.Linux

/// A CodeAct session host whose hands pass the grants guard, over one store with the grants log.
type private Granted =
    {
        F: Fixture
        GrantsEnv: GrantsEnv
        Service: GrantsService<GrantsEnv>
    }

    member this.Pending(count: int) = pendingAt this.Service count

    member this.Answer(request: GrantRequest, answer': GrantAnswer) =
        answer this.GrantsEnv this.Service request answer'

    interface IDisposable with
        member this.Dispose() =
            (this.F :> IDisposable).Dispose()
            wait (Eff.run this.GrantsEnv (GrantsService.stop this.Service))

    override _.ToString() = "Granted"

/// A host process over a fixture's stores whose hands pass the grants guard.
let private processOn
    (env: HostEnv)
    (store: InMemoryEventLog<Payload>)
    (blobs: InMemoryBlobs)
    (variants: Variants)
    (hands: Hands<HandsEnv>)
    (grants: Grants)
    : unit -> Process =
    let sessions = SessionLog.typed store.Log
    let snapshots = InMemorySnapshots()

    let settings =
        { SessionSettings.defaults with
            Kinds = Questions.kinds
        }

    fun () ->
        let codeact =
            CodeAct.create
                {
                    Sessions = sessions
                    Contexts = ContextLog.typed store.Log
                    Steps = StepJournal.typed store.Log
                    Blobs = Some blobs.Blobs
                    Profile = fun _ -> Eff.ret variants
                    Hands =
                        fun turn ->
                            eff {
                                let! settings = GrantSettings.ofVariants variants
                                return Ok(GrantGuard.attach (GrantSetup.create grants sessions turn settings) hands)
                            }
                    Catalog = fun _ -> None
                }

        let host =
            wait (Eff.run env (SessionHost.create sessions snapshots.Store codeact.Runner settings))

        {
            Host = host
            Kernel = codeact.Kernel
            Close = fun () -> codeact.Close()
        }

let private granted (name: string) (selections: (string * Json) list) (policy: PolicyRule list) : Granted =
    let root = scratch name
    let workspace = Path.Combine(root, "workspace")
    Directory.CreateDirectory workspace |> ignore
    let model = Model()
    let cleanup = ResizeArray<unit -> Task>()
    let hands = localHands (fun _ -> scriptKernel kernelOptions) root workspace cleanup

    let env: HostEnv =
        {
            Clock = SystemClock.clock
            Trace = Trace.none
            Llm = model.Llm
            Scope = Scope.root
        }

    let overrides = selections |> List.map fst

    let chosen =
        (baseSelections |> List.filter (fun (k, _) -> not (List.contains k overrides)))
        @ selections

    let variants = FakeVariants.create CodeActSlots.read chosen
    let store = InMemoryEventLog<Payload>()
    let blobs = InMemoryBlobs()
    let genv = grantsEnv ()
    let service = startGrants genv store policy
    let start = processOn env store blobs variants hands (GrantsService.grants service)

    {
        F =
            {
                Env = env
                Store = store
                Blobs = blobs
                Process = start ()
                Start = start
                Model = model
                Root = root
                Workspace = workspace
                Variants = variants
                Cleanup = cleanup
            }
        GrantsEnv = genv
        Service = service
    }

let private withGrants
    (name: string)
    (selections: (string * Json) list)
    (policy: PolicyRule list)
    (body: Granted -> Task<unit>)
    : Task =
    task {
        use g = granted name selections policy
        do! body(g).WaitAsync(TimeSpan.FromSeconds 120.0)
    }

let private hybrid = [ "agent.actionSurface", Json.String "hybrid" ]

let private kickoff (f: Fixture) (text: string) : Task =
    task {
        do! start f "s1"
        do! prompt f "s1" "p1" text
    }

let private cellRun (line: string) =
    Say.cell $"// intent: run\ncall r = shell.run [{Json.quote line}]\nr"

let private turnTests =
    testList
        "turns"
        [
            testTask "cells: a command waits for a person; answered once, it runs" {
                do!
                    withGrants "grant-cell" [] [] (fun g ->
                        task {
                            g.F.Model.AddAll [ cellRun "mkdir made"; Say.finish "Made it" ]
                            do! kickoff g.F "Make a directory"
                            let! (requests: GrantRequest list) = g.Pending 1
                            let request = requests.Head
                            Expect.equal request.Resource "mkdir made" "the command"
                            Expect.equal request.Context.Session (Some "s1") "the session"
                            Expect.stringStarts request.Key "session/s1#" "keyed by the turn"
                            Expect.isFalse (Directory.Exists(g.F.File "made")) "not before the answer"
                            g.Answer(request, allow ScopeChoice.Once None)
                            let! ending = ended g.F "s1" 1
                            Expect.equal (completed ending) (TurnResult.Done("Made it", [], None)) "done"
                            Expect.isTrue (Directory.Exists(g.F.File "made")) "it ran"
                            Expect.isFalse (receipts g.F "s1").Head.IsError "a receipt without an error"
                        })
            }

            testTask "cells: a denied command fails denied in the cell, and the receipt says so" {
                do!
                    withGrants "grant-cell-denied" [] [] (fun g ->
                        task {
                            g.F.Model.AddAll [ cellRun "mkdir made"; Say.finish "Could not" ]
                            do! kickoff g.F "Make a directory"
                            let! (requests: GrantRequest list) = g.Pending 1
                            g.Answer(requests.Head, deny)
                            let! ending = ended g.F "s1" 1
                            completed ending |> ignore
                            Expect.isFalse (Directory.Exists(g.F.File "made")) "nothing ran"
                            let receipt = (receipts g.F "s1").Head
                            Expect.isTrue receipt.IsError "an error"
                            Expect.stringContains (text receipt) "denied" "the error's code"

                            Expect.stringContains
                                (text receipt)
                                "shell.exec `mkdir made` was denied (denied by user:alice: not now)"
                                "why"

                            Expect.stringContains (requestText g.F.Model.Requests[1]) "was denied" "the model sees it"
                        })
            }

            testTask
                "hybrid: bash asks, a session grant covers the next one; read in the workspace and listed commands ask nothing" {
                do!
                    withGrants "grant-hybrid" hybrid [] (fun g ->
                        task {
                            g.F.Write("notes.md", "notes\n")

                            g.F.Model.AddAll
                                [
                                    Say.tool "bash" [ "command", Json.String "mkdir one" ]
                                    Say.tool "bash" [ "command", Json.String "mkdir two" ]
                                    Say.tool "read" [ "path", Json.String "notes.md" ]
                                    Say.tool "bash" [ "command", Json.String "echo hello" ]
                                    Say.finish "Both made"
                                ]

                            do! kickoff g.F "Make two directories"
                            let! (requests: GrantRequest list) = g.Pending 1
                            Expect.stringContains requests.Head.Key "/t1-s1-1/shell.exec/" "keyed by the tool call"
                            g.Answer(requests.Head, allow ScopeChoice.Session (Some "mkdir **"))
                            let! ending = ended g.F "s1" 1
                            Expect.equal (completed ending) (TurnResult.Done("Both made", [], None)) "done"
                            Expect.isTrue (Directory.Exists(g.F.File "one")) "one"
                            Expect.isTrue (Directory.Exists(g.F.File "two")) "two"

                            let asked =
                                (GrantsService.snapshot g.Service).State.Requests
                                |> Map.toList
                                |> List.map (fun (_, e) -> string e)

                            Expect.equal (requestCount g.Service) 1 $"asked once: {asked}"
                            let all = receipts g.F "s1"
                            Expect.stringContains (text all[3]) "hello" "the listed command ran"

                            for receipt in all do
                                Expect.isFalse receipt.IsError (text receipt)
                        })
            }

            testTask "hybrid: a policy denial is refused at once and its receipt says why" {
                do!
                    withGrants "grant-policy" hybrid [ denyRule "test/no-mkdir" "mkdir **" ] (fun g ->
                        task {
                            g.F.Model.AddAll
                                [
                                    Say.tool "bash" [ "command", Json.String "mkdir made" ]
                                    Say.finish "Refused"
                                ]

                            do! kickoff g.F "Make a directory"
                            let! ending = ended g.F "s1" 1
                            completed ending |> ignore
                            let receipt = (receipts g.F "s1").Head
                            Expect.isTrue receipt.IsError "an error"

                            Expect.stringContains
                                (text receipt)
                                "Refused: shell.exec `mkdir made` was denied (deny by policy test/no-mkdir"
                                "why"

                            Expect.equal (requestCount g.Service) 0 "nobody asked"
                            Expect.isFalse (Directory.Exists(g.F.File "made")) "nothing ran"
                        })
            }

            testTask "a write outside the workspace asks fs.write for the canonical path" {
                do!
                    withGrants "grant-outside" [] [] (fun g ->
                        task {
                            let outside = Path.Combine(g.F.Root, "outside.txt")

                            g.F.Model.AddAll
                                [
                                    Say.cell $"// intent: write\ncall w = fs.write [{Json.quote outside}, \"x\"]\nw"
                                    Say.finish "Done"
                                ]

                            do! kickoff g.F "Write outside"
                            let! (requests: GrantRequest list) = g.Pending 1
                            Expect.equal requests.Head.Action (ActionName.create "fs.write") "fs.write"
                            Expect.equal requests.Head.Resource (WorkspacePath.normalize outside) "the canonical path"
                            g.Answer(requests.Head, deny)
                            let! _ = ended g.F "s1" 1
                            Expect.isFalse (File.Exists outside) "nothing written"
                        })
            }

            testTask "an interrupt while a cell or a tool waits withdraws the request" {
                for surface, reply in
                    [
                        "cells-only", cellRun "mkdir made"
                        "hybrid", Say.tool "bash" [ "command", Json.String "mkdir made" ]
                    ] do
                    do!
                        withGrants
                            $"grant-interrupt-{surface}"
                            [ "agent.actionSurface", Json.String surface ]
                            []
                            (fun g ->
                                task {
                                    g.F.Model.AddAll [ reply ]
                                    do! kickoff g.F "Make a directory"
                                    let! (requests: GrantRequest list) = g.Pending 1
                                    let! _ = send g.F "s1" "stop" Command.Interrupt
                                    let! ending = ended g.F "s1" 1
                                    Expect.equal ending TurnEnd.Interrupted surface

                                    let deadline = DateTime.UtcNow.AddSeconds 30.0

                                    while status g.Service requests.Head.Id = RequestStatus.Pending
                                          && DateTime.UtcNow < deadline do
                                        do! Task.Delay 10

                                    withdrawn g.Service requests.Head.Id
                                    Expect.isFalse (Directory.Exists(g.F.File "made")) "nothing ran"
                                })
            }

            testTask "a steer while a call waits withdraws the request; the model reads the steer next" {
                do!
                    withGrants "grant-steer" hybrid [] (fun g ->
                        task {
                            g.F.Model.AddAll
                                [
                                    Say.tool "bash" [ "command", Json.String "mkdir made" ]
                                    Say.finish "Left it"
                                ]

                            do! kickoff g.F "Make a directory"
                            let! (requests: GrantRequest list) = g.Pending 1
                            do! prompt g.F "s1" "p2" "Do not create it after all"
                            let! ending = ended g.F "s1" 1
                            Expect.equal (completed ending) (TurnResult.Done("Left it", [], None)) "done"
                            withdrawn g.Service requests.Head.Id
                            let receipt = (receipts g.F "s1").Head
                            Expect.stringContains (text receipt) "a message from the user arrived" "why"
                            let next = requestText g.F.Model.Requests[1]
                            Expect.stringContains next "Do not create it after all" "the steer"
                            Expect.isFalse (Directory.Exists(g.F.File "made")) "nothing ran"
                        })
            }

            testTask "a request nobody answers in time refuses the call" {
                do!
                    withGrants "grant-timeout" (hybrid @ [ "codeact.grants.timeoutMs", Json.int 200 ]) [] (fun g ->
                        task {
                            g.F.Model.AddAll
                                [
                                    Say.tool "bash" [ "command", Json.String "mkdir made" ]
                                    Say.finish "Timed out"
                                ]

                            do! kickoff g.F "Make a directory"
                            let! _ = ended g.F "s1" 1
                            let receipt = (receipts g.F "s1").Head
                            Expect.stringContains (text receipt) "not answered (timed out)" "why"
                        })
            }

            testTask "mode deny refuses without asking" {
                do!
                    withGrants "grant-mode-deny" (hybrid @ [ "codeact.grants.mode", Json.String "deny" ]) [] (fun g ->
                        task {
                            g.F.Model.AddAll
                                [
                                    Say.tool "bash" [ "command", Json.String "mkdir made" ]
                                    Say.finish "Refused"
                                ]

                            do! kickoff g.F "Make a directory"
                            let! _ = ended g.F "s1" 1
                            let receipt = (receipts g.F "s1").Head
                            Expect.stringContains (text receipt) "nobody is asked here" "why"
                            Expect.equal (requestCount g.Service) 0 "nobody asked"
                        })
            }

            testTask "a host that stops while a call waits: withdrawn on a clean stop, pending after a crash" {
                // A stop cancels the turn: the request is withdrawn; recovery decides about the turn
                // (the call in doubt), and nothing asks the grants log again by itself.
                do!
                    withGrants "grant-restart" hybrid [] (fun g ->
                        task {
                            g.F.Model.AddAll [ Say.tool "bash" [ "command", Json.String "mkdir made" ] ]
                            do! kickoff g.F "Make a directory"
                            let! (requests: GrantRequest list) = g.Pending 1
                            do! restart g.F
                            withdrawn g.Service requests.Head.Id
                            let! _ = state g.F "s1"
                            do! Task.Delay 200
                            Expect.equal (pending g.Service) [] "nothing asks again"
                            Expect.equal (requestCount g.Service) 1 "one request in the log"
                            Expect.isFalse (Directory.Exists(g.F.File "made")) "nothing ran"
                        })

                // A crash: the host goes before it can withdraw (here, the grants log stops first), and the
                // request stays pending in the store for whoever reads it next.
                do!
                    withGrants "grant-crash" hybrid [] (fun g ->
                        task {
                            g.F.Model.AddAll [ Say.tool "bash" [ "command", Json.String "mkdir made" ] ]
                            do! kickoff g.F "Make a directory"
                            let! (requests: GrantRequest list) = g.Pending 1
                            do! Eff.run g.GrantsEnv (GrantsService.stop g.Service)
                            do! run g.F (SessionHost.stop g.F.Host)
                            do! g.F.Process.Close()
                            let again = startGrants g.GrantsEnv g.F.Store []

                            try
                                Expect.equal
                                    (pending again |> List.map (fun r -> r.Id))
                                    [ requests.Head.Id ]
                                    "still pending"
                            finally
                                wait (Eff.run g.GrantsEnv (GrantsService.stop again))
                        })
            }
        ]

// ---- Replay -----------------------------------------------------------------------------------------

/// `mkdir made` printing nothing: PowerShell's `mkdir` lists what it made (paths and times).
let private quietMkdir =
    if OperatingSystem.IsWindows() then
        "mkdir made > $null"
    else
        "mkdir made"

/// A hybrid turn that asks twice (allowed, then denied), and finishes.
let private scenario (g: Granted) : Task<unit> =
    task {
        g.F.Model.AddAll
            [
                Say.tool "bash" [ "command", Json.String quietMkdir ]
                Say.cell "// intent: hook\ncall w = fs.write [\".git/hooks/pre-commit\", \"x\"]\nw"
                Say.finish "Made, not hooked"
            ]

        do! kickoff g.F "Make a directory and a hook"
        let! (first: GrantRequest list) = g.Pending 1
        g.Answer(first.Head, allow ScopeChoice.Once None)

        let deadline = DateTime.UtcNow.AddSeconds 30.0

        while requestCount g.Service < 2 && DateTime.UtcNow < deadline do
            do! Task.Delay 10

        let! (second: GrantRequest list) = g.Pending 1
        g.Answer(second.Head, deny)
        let! ending = ended g.F "s1" 1
        completed ending |> ignore
    }

let private canonical (request: ChatRequest) =
    Json.serializeCanonical (ChatRequest.codec.Encode request)

let private replayTests =
    testList
        "replay"
        [
            testTask "the same inputs give the same requests, context log, session events and grant requests" {
                let once (name: string) =
                    task {
                        use g = granted name hybrid []
                        do! scenario g

                        let context =
                            g.F.Store.Events(ContextLog.stream "s1")
                            |> List.map (fun r -> Json.serializeCanonical r.Event.Data)

                        let asked =
                            (GrantsService.snapshot g.Service).State.Requests
                            |> Map.toList
                            |> List.map (fun (_, (entry: RequestEntry)) ->
                                let r = entry.Request

                                { r with Id = RequestId "-"; At = 0L }
                                |> Fx.Grants.Codecs.request.Encode
                                |> Json.serializeCanonical)
                            |> List.sort

                        let sessionEvents = events g.F "s1" |> List.map (fun r -> string r.Event)
                        return context, g.F.Model.Requests |> List.map canonical, sessionEvents, asked
                    }

                let! first = once "grant-replay-1"
                let! second = once "grant-replay-2"
                let context1, requests1, events1, (asked1: string list) = first
                let context2, requests2, events2, asked2 = second
                Expect.equal asked1.Length 2 "two requests"
                Expect.equal context2 context1 "the context log"
                Expect.equal requests2 requests1 "the requests"
                Expect.equal events2 events1 "the session's events"
                Expect.equal asked2 asked1 "the grant requests"
            }

            testTask "the recorded logs, denials included, render to the requests that were sent" {
                let g = granted "grant-render" hybrid []

                try
                    do! scenario g
                    let requests = g.F.Model.Requests

                    let! history =
                        run g.F (ContextLog.load (SessionLog.typed g.F.Store.Log) (ContextLog.typed g.F.Store.Log) "s1")

                    let! settings = run g.F (CodeActSettings.ofVariants g.F.Variants)

                    let replies =
                        history.Items
                        |> List.indexed
                        |> List.choose (fun (i, item) ->
                            match item with
                            | LogItem.Context(_, ContextEvent.Replied reply) -> Some(i, reply)
                            | _ -> None)

                    Expect.equal replies.Length requests.Length "a reply per request"
                    Expect.stringContains (requestText requests[2]) "was denied" "the denial was sent"

                    for (index, reply), sent in List.zip replies requests do
                        let transcript = Transcript.ofItems (history.Items |> List.take index)

                        let cfg =
                            PromptSettings.config settings.Prompt [] Exclusions.none ModelCaps.minimal.Caching

                        let rendered = Render.render cfg transcript Live.none

                        let rendered =
                            { rendered with
                                Prompt =
                                    { rendered.Prompt with
                                        Suffix = reply.Shown
                                    }
                            }

                        let template =
                            { sent with
                                System = []
                                Messages = []
                                Cache = { sent.Cache with Key = None }
                            }

                        Expect.equal
                            (canonical (Render.request cfg template rendered))
                            (canonical sent)
                            $"request {reply.Step} replays"
                finally
                    (g :> IDisposable).Dispose()
            }
        ]

[<Tests>]
let tests = testList "CodeAct grants" [ guardTests; turnTests; replayTests ]
