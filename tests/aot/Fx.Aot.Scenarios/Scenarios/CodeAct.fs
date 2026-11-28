/// The CodeAct strategy (T127, docs/modules/codeact.md) under Native AOT: a session on the session host
/// whose turns the strategy runs, with a scripted model, the script kernel in process, and the
/// workspace in memory. A hybrid turn reads, edits, runs a command, runs cells and finishes with
/// evidence; a cells-only turn replies in text twice and ends asking the user. The output is the turns'
/// endings, the requests' tools, the receipts, the context log, and the replay of the logs into the
/// requests sent.
module Fx.Aot.Scenarios.CodeAct

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Envd
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Rpc
open Fx.Store
open Fx.Store.Fakes
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Kernel
open Fx.Prompt
open Fx.Sdk
open Fx.Variants.Fakes
open Fx.Models.Fakes
open Fx.CodeAct

type private HostEnv =
    {
        Clock: Clock
        Trace: Trace
        Llm: Llm
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITrace with
        member env.Trace = env.Trace

    interface ILlm with
        member env.Llm = env.Llm

    interface IScopeWith<HostEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "HostEnv"

type private HandsEnv =
    {
        Clock: Clock
        FsRead: FsRead
        Fs: Fs
        FsOps: FsOps
        Shell: Shell
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IFsOps with
        member env.FsOps = env.FsOps

    interface IShell with
        member env.Shell = env.Shell

    interface IScopeWith<HandsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "HandsEnv"

let private os =
    if OperatingSystem.IsWindows() then Os.Windows
    elif OperatingSystem.IsMacOS() then Os.MacOs
    else Os.Linux

let private workspace = if OperatingSystem.IsWindows() then @"C:\work" else "/work"

/// The script kernel in this process: a fresh one for each launch.
let private kernel: Interpreter<HandsEnv> =
    {
        Language = "script"
        Prompt =
            fun surface ->
                "Cells are scripts. Bindings: "
                + String.concat ", " (surface.Bindings |> List.map (fun b -> b.Path))
        Start =
            fun start ->
                fun env ->
                    task {
                        let script = Kernel.ScriptKernel.Kernel ignore
                        let frames = ConnectionLimits.frames KernelOptions.defaults.Limits
                        let client, server = InProcess.pair (1 <<< 20) frames frames

                        let serverEnv: Rpc.TestService.Env =
                            {
                                Clock = SystemClock.clock
                                Scope = Scope.root
                            }

                        Eff.run serverEnv (Rpc.accept script.Service server) |> ignore

                        let terminate () =
                            server.Abort()
                            script.Kill()
                            Task.FromResult()

                        match!
                            Eff.run env (Kernel.connect KernelOptions.defaults start.Host start.Setup terminate client)
                        with
                        | Ok k -> return Ok k
                        | Error e -> return Error(string e)
                    }
        Launches = None
    }

let private call (n: int) (name: string) (arguments: (string * Json) list) : ChatEvent list =
    ChatEvents.ofResponse
        {
            Id = None
            Model = Some "model"
            Parts =
                [
                    Part.ToolCall
                        {
                            Id = $"call-{n}"
                            Name = name
                            Arguments = Json.serialize (Json.Object arguments)
                            State = None
                        }
                ]
            Stop = StopReason.ToolUse
            Usage =
                { Usage.zero with
                    Input = 100
                    Output = 10
                }
        }

let private text (words: string) : ChatEvent list =
    ChatEvents.ofResponse
        {
            Id = None
            Model = Some "model"
            Parts = [ Part.text words ]
            Stop = StopReason.EndTurn
            Usage =
                { Usage.zero with
                    Input = 100
                    Output = 10
                }
        }

let private cell (n: int) (code: string) =
    call n "cell" [ "code", Json.String code ]

/// A reply from a request the provider counted `input` tokens of.
let private sized (input: int) (events: ChatEvent list) : ChatEvent list =
    events
    |> List.map (fun event ->
        match event with
        | ChatEvent.UsageUpdated usage -> ChatEvent.UsageUpdated { usage with Input = input }
        | other -> other)

/// Whether a request asked for a summary (compaction) rather than the turn's next step.
let private isSummary (request: ChatRequest) : bool =
    match request.Messages |> List.tryLast |> Option.bind (fun m -> List.tryLast m.Parts) with
    | Some(Part.Text text) -> text.Text.StartsWith("[Compaction:", StringComparison.Ordinal)
    | _ -> false

let private hash (request: ChatRequest) =
    let json = Json.serializeCanonical (ChatRequest.codec.Encode request)
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes json)).Substring(0, 16)

let private within (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 60.0)

/// Runs one session's turn with these replies, selections and the models' catalog; gives the output
/// lines.
let private sessionIn
    (catalog: ModelRef -> ModelInfo option)
    (name: string)
    (selections: (string * Json) list)
    (replies: ChatEvent list list)
    : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let fsClock = FakeClock(Instant.ofUnixMicroseconds 1_700_000_000_000_000L)

        let fs =
            InMemoryFs(fsClock.Clock, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())

        fs.AddDirectory workspace

        fs.AddText(
            (if OperatingSystem.IsWindows() then
                 @"C:\work\notes.md"
             else
                 "/work/notes.md"),
            "# Notes\nfirst\n"
        )

        let shell =
            ScriptedShell(fun command ->
                match command.Spec.Program with
                | Program.Script script -> Some(Ok(ScriptedShell.output 0 $"ran {script}\n" ""))
                | Exec(file, _) -> Some(Error(NotStarted(file, "not scripted"))))

        let queue = Collections.Generic.Queue<ChatEvent list>(replies)

        let script =
            ScriptedLlm(fun _ -> lock queue (fun () -> if queue.Count > 0 then Some(queue.Dequeue()) else None))

        let env: HostEnv =
            {
                Clock = SystemClock.clock
                Trace = Trace.none
                Llm = script.Llm
                Scope = Scope.root
            }

        let hands: HandsEnv =
            {
                Clock = SystemClock.clock
                FsRead = fs.FsRead
                Fs = fs.Fs
                FsOps = FsOpsOver.create fs.FsRead fs.Fs
                Shell = shell.Shell
                Scope = Scope.root
            }

        let variants =
            FakeVariants.create CodeActSlots.read (("model.role.main", Json.String "test/model") :: selections)

        let store = InMemoryEventLog<Payload>()
        let sessions = SessionLog.typed store.Log
        let contexts = ContextLog.typed store.Log

        let codeact =
            CodeAct.create
                {
                    Sessions = sessions
                    Contexts = contexts
                    Steps = StepJournal.typed store.Log
                    Blobs = None
                    Profile = fun _ -> Eff.ret variants
                    Hands =
                        fun _ ->
                            Eff.ret (
                                Ok
                                    {
                                        Env = hands
                                        Workspace = workspace
                                        Os = os
                                        CommandEnv = Inherit []
                                        Interpreter = kernel
                                        Guard = fun _ -> EffResult.ok []
                                        Snapshots = None
                                        Sandbox = None
                                        Variables = []
                                        Jobs = Jobs.none
                                    }
                            )
                    Catalog = catalog
                }

        let settings =
            { SessionSettings.defaults with
                Kinds = Questions.kinds
            }

        let! host =
            Eff.run env (SessionHost.create sessions (InMemorySnapshots()).Store codeact.Runner settings)

        let alice = Attribution.create "user:alice" "scenario"

        let! _ =
            Eff.run env (SessionHost.start host "s1" (IdempotencyKey "start") alice "p1" None)
            |> within

        let! _ =
            Eff.run
                env
                (SessionHost.send host "s1" (IdempotencyKey "p1") alice (Command.Prompt("Do it", PromptMode.Steer)))
            |> within

        let events () =
            store.Events(Session.stream "s1")
            |> List.map (EventLog.decode SessionLog.schema)

        let mutable ending = None
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        while ending.IsNone && DateTime.UtcNow < deadline do
            ending <-
                events ()
                |> List.tryPick (fun r ->
                    match r.Event with
                    | Event.TurnEnded(1, ended, _) -> Some ended
                    | _ -> None)

            if ending.IsNone then
                do! Task.Delay 10

        let ended = ending |> Option.map string |> Option.defaultValue "did not end"

        lines.Add $"{name}: turn 1 {ended}"
        let all = script.Requests
        lines.Add $"requests: {all.Length}"

        // Summary requests (compaction): the request before each, and the instruction.
        for before, summary in List.pairwise all do
            if isSummary summary then
                let last = List.last summary.Messages

                let without =
                    { summary with
                        MaxOutputTokens = before.MaxOutputTokens
                        Messages =
                            (summary.Messages |> List.truncate (summary.Messages.Length - 1))
                            @ [
                                { last with
                                    Parts = last.Parts |> List.truncate (last.Parts.Length - 1)
                                }
                            ]
                    }

                lines.Add
                    $"summary request: {hash summary} is the call before it and the instruction: {hash without = hash before}"

        let requests = all |> List.filter (not << isSummary)

        lines.Add(
            "tools: "
            + (requests
               |> List.map (fun r -> r.Tools |> List.map (fun t -> t.Name) |> String.concat ",")
               |> List.distinct
               |> String.concat " | ")
        )

        let context =
            store.Events(ContextLog.stream "s1")
            |> List.map (EventLog.decode ContextLog.schema)

        for recorded in context do
            match recorded.Event with
            | ContextEvent.Returned receipt ->
                let shown = receipt.Pieces |> List.map Piece.render |> String.concat "\n"
                lines.Add(kv $"receipt {receipt.Step} {receipt.Name} error={receipt.IsError}" shown)
            | other -> lines.Add $"context {recorded.Seq}: {other}"

        // The logs replay into the requests sent.
        let! history = Eff.run env (ContextLog.load sessions contexts "s1") |> within

        let cfg =
            PromptSettings.config PromptSettings.defaults [] Exclusions.none ModelCaps.minimal.Caching

        let replies =
            history.Items
            |> List.indexed
            |> List.choose (fun (i, item) ->
                match item with
                | LogItem.Context(_, ContextEvent.Replied reply) -> Some(i, reply)
                | _ -> None)

        for (index, reply), sent in List.zip replies requests do
            let rendered =
                Render.render cfg (Transcript.ofItems (List.take index history.Items)) Live.none

            let replayed =
                Render.request
                    cfg
                    { sent with
                        System = []
                        Messages = []
                        Cache = { sent.Cache with Key = None }
                    }
                    { rendered with
                        Prompt =
                            { rendered.Prompt with
                                Suffix = reply.Shown
                            }
                    }

            lines.Add $"request {reply.Step}: {hash sent} replays: {hash replayed = hash sent}"

        let! state = Eff.run env (SessionHost.state host "s1") |> within

        for request in state.State.Requests do
            lines.Add(kv $"open {request.Request.Id} ({request.Request.Kind})" (Json.serialize request.Request.Payload))

        do! codeact.Close().WaitAsync(TimeSpan.FromSeconds 60.0)
        do! Eff.run env (SessionHost.stop host) |> within
        return List.ofSeq lines
    }

/// Runs one session's turn with these replies and selections, without a catalog.
let private session = sessionIn (fun _ -> None)

let private hybrid () : Task<string list> =
    session
        "hybrid"
        [ "agent.actionSurface", Json.String "hybrid" ]
        [
            call 1 "read" [ "path", Json.String "notes.md" ]
            call
                2
                "edit"
                [
                    "path", Json.String "notes.md"
                    "old", Json.String "first"
                    "new", Json.String "first\nsecond"
                ]
            call 3 "bash" [ "command", Json.String "make test" ]
            cell 4 "// intent: read it back\ncall n = fs.read [\"notes.md\"]\nn"
            cell
                5
                "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Added a line\",\"evidence\":[{\"kind\":\"command\",\"summary\":\"make test passed\"}]}"
        ]

let private textOnly () : Task<string list> =
    session "text-only" [] [ text "Which notes?"; text "Which notes should I change?" ]

/// Crash recovery (T128): a cell whose `deploy` hangs is in flight when the host process stops; a new
/// host recovers the turn, which pauses on a decision; restarting again opens nothing new; the answer
/// (`rerun`) runs the cell again and the turn goes on to finish; a later turn reads the value an
/// earlier cell returned back from the journal. The output is the decision card, the journal, the
/// receipts, the session's events and the replay of the logs into the requests sent.
let private recovery () : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let fsClock = FakeClock(Instant.ofUnixMicroseconds 1_700_000_000_000_000L)

        let fs =
            InMemoryFs(fsClock.Clock, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())

        fs.AddDirectory workspace

        let deploying =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let deploys = ref 0

        let run (command: Fx.Effects.Command) (token: CancellationToken) =
            task {
                let first =
                    lock deploying (fun () ->
                        deploys.Value <- deploys.Value + 1
                        deploys.Value = 1)

                if first then
                    deploying.TrySetResult() |> ignore
                    do! Task.Delay(Threading.Timeout.Infinite, token)

                let script =
                    match command.Spec.Program with
                    | Program.Script script -> script
                    | Exec(file, _) -> file

                return Ok(ScriptedShell.output 0 $"ran {script}\n" "")
            }

        // `shell.run` spawns its commands (T142): each is a process whose run its kill cancels.
        let shell: Shell =
            {
                Run = run
                Spawn = fun spec _ -> Task.FromResult(Ok(Fx.Aot.Scenarios.Kernel.ScriptProc.command run spec))
            }

        let queue =
            Collections.Generic.Queue<ChatEvent list>(
                [
                    cell
                        1
                        "// intent: deploy\ncall w = fs.write [\"site.txt\", \"v2\"]\ncall r = shell.run [\"deploy\"]\nr"
                    cell 2 "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Deployed\"}"
                    cell 3 "// intent: read it back\ncall v = recall [\"call-1\"]\nv"
                    cell 4 "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Read\"}"
                ]
            )

        let script =
            ScriptedLlm(fun _ -> lock queue (fun () -> if queue.Count > 0 then Some(queue.Dequeue()) else None))

        let env: HostEnv =
            {
                Clock = SystemClock.clock
                Trace = Trace.none
                Llm = script.Llm
                Scope = Scope.root
            }

        let hands: HandsEnv =
            {
                Clock = SystemClock.clock
                FsRead = fs.FsRead
                Fs = fs.Fs
                FsOps = FsOpsOver.create fs.FsRead fs.Fs
                Shell = shell
                Scope = Scope.root
            }

        let variants =
            FakeVariants.create CodeActSlots.read [ "model.role.main", Json.String "test/model" ]

        let store = InMemoryEventLog<Payload>()
        let blobs = InMemoryBlobs()
        let snapshots = InMemorySnapshots()
        let sessions = SessionLog.typed store.Log
        let contexts = ContextLog.typed store.Log

        let settings =
            { SessionSettings.defaults with
                Kinds = Questions.kinds
            }

        let start () =
            task {
                let codeact =
                    CodeAct.create
                        {
                            Sessions = sessions
                            Contexts = contexts
                            Steps = StepJournal.typed store.Log
                            Blobs = Some blobs.Blobs
                            Profile = fun _ -> Eff.ret variants
                            Hands =
                                fun _ ->
                                    Eff.ret (
                                        Ok
                                            {
                                                Env = hands
                                                Workspace = workspace
                                                Os = os
                                                CommandEnv = Inherit []
                                                Interpreter = kernel
                                                Guard = fun _ -> EffResult.ok []
                                                Snapshots = None
                                                Sandbox = None
                                                Variables = []
                                                Jobs = Jobs.none
                                            }
                                    )
                            Catalog = fun _ -> None
                        }

                let! host =
                    Eff.run env (SessionHost.create sessions snapshots.Store codeact.Runner settings)

                return codeact, host
            }

        let alice = Attribution.create "user:alice" "scenario"

        let send host key command =
            Eff.run env (SessionHost.send host "s1" (IdempotencyKey key) alice command)
            |> within

        let events () =
            store.Events(Session.stream "s1")
            |> List.map (EventLog.decode SessionLog.schema)

        let waitFor (what: string) (condition: unit -> bool) =
            task {
                let deadline = DateTime.UtcNow.AddSeconds 60.0

                while not (condition ()) && DateTime.UtcNow < deadline do
                    do! Task.Delay 10

                if not (condition ()) then
                    lines.Add $"timed out: {what}"
            }

        let ended (turn: int) () =
            events ()
            |> List.exists (fun r ->
                match r.Event with
                | Event.TurnEnded(n, _, _) -> n = turn
                | _ -> false)

        let stopHost (codeact: CodeAct<HostEnv, HandsEnv>) host =
            task {
                do! Eff.run env (SessionHost.stop host) |> within
                do! codeact.Close().WaitAsync(TimeSpan.FromSeconds 60.0)
            }

        let! codeact, host = start ()

        let! _ =
            Eff.run env (SessionHost.start host "s1" (IdempotencyKey "start") alice "p1" None)
            |> within

        let! _ = send host "p1" (Command.Prompt("Deploy", PromptMode.Steer))
        do! deploying.Task.WaitAsync(TimeSpan.FromSeconds 60.0)
        do! stopHost codeact host

        let requestOf (host: SessionHost<HostEnv>) =
            task {
                let mutable found = None
                let deadline = DateTime.UtcNow.AddSeconds 60.0

                while found.IsNone && DateTime.UtcNow < deadline do
                    let! state = Eff.run env (SessionHost.state host "s1") |> within
                    found <- state.State.Requests |> List.tryHead

                    if found.IsNone then
                        do! Task.Delay 10

                return found
            }

        // Recovery: twice, as after two crashes in a row.
        let! codeact, host = start ()
        let! first = requestOf host
        do! stopHost codeact host
        let! codeact, host = start ()
        let! second = requestOf host

        match first, second with
        | Some first, Some second ->
            lines.Add(
                kv $"decision {first.Request.Id} ({first.Request.Kind})" (Json.serializeCanonical first.Request.Payload)
            )

            lines.Add $"recovered again: the same request {first.Seq = second.Seq}"
        | _ -> lines.Add "no decision"

        let opened =
            events ()
            |> List.filter (fun r ->
                match r.Event with
                | Event.RequestOpened _ -> true
                | _ -> false)

        lines.Add $"decisions opened: {opened.Length}; model calls before the answer: {script.Requests.Length}"

        let! answered =
            send
                host
                "a1"
                (Command.Answer(
                    "unknown-t1-s1-1",
                    RequestKind.answer
                        Recovery.kind
                        {
                            Option = "rerun"
                            Note = Some "try again"
                        }
                ))

        lines.Add $"answered: {answered}"
        do! waitFor "turn 1" (ended 1)
        let! _ = send host "p2" (Command.Prompt("Read it back", PromptMode.Steer))
        do! waitFor "turn 2" (ended 2)
        lines.Add $"deploy ran {deploys.Value} times"

        for recorded in events () do
            lines.Add $"session {recorded.Seq}: {recorded.Event}"

        for recorded in
            store.Events(StepJournal.stream "s1")
            |> List.map (EventLog.decode StepJournal.schema) do
            lines.Add(kv $"journal {recorded.Seq}" (Json.serializeCanonical (StepCodecs.event.Encode recorded.Event)))

        let context =
            store.Events(ContextLog.stream "s1")
            |> List.map (EventLog.decode ContextLog.schema)

        for recorded in context do
            match recorded.Event with
            | ContextEvent.Returned receipt ->
                let shown = receipt.Pieces |> List.map Piece.render |> String.concat "\n"
                lines.Add(kv $"receipt {receipt.Turn}.{receipt.Step} {receipt.Name} error={receipt.IsError}" shown)
            | other -> lines.Add $"context {recorded.Seq}: {other}"

        // The logs replay into the requests sent.
        let! history = Eff.run env (ContextLog.load sessions contexts "s1") |> within

        let cfg =
            PromptSettings.config PromptSettings.defaults [] Exclusions.none ModelCaps.minimal.Caching

        let replies =
            history.Items
            |> List.indexed
            |> List.choose (fun (i, item) ->
                match item with
                | LogItem.Context(_, ContextEvent.Replied reply) -> Some(i, reply)
                | _ -> None)

        for (index, reply), sent in List.zip replies script.Requests do
            let rendered =
                Render.render cfg (Transcript.ofItems (List.take index history.Items)) Live.none

            let replayed =
                Render.request
                    cfg
                    { sent with
                        System = []
                        Messages = []
                        Cache = { sent.Cache with Key = None }
                    }
                    { rendered with
                        Prompt =
                            { rendered.Prompt with
                                Suffix = reply.Shown
                            }
                    }

            lines.Add $"request {reply.Turn}.{reply.Step}: replays: {hash replayed = hash sent}"

        do! stopHost codeact host
        return List.ofSeq lines
    }

/// Compaction (T206): a reply whose prompt reached the threshold of a 20000-token window is summarized
/// beside its cell (`Eff.both`) and recorded before the next call; a cell asks to compact
/// (`history.compact`); the summary requests are the calls before them plus the instruction.
let private compaction () : Task<string list> =
    sessionIn
        (fun reference -> Some(ModelInfo.create reference { Context = 20000; Output = 4096 }))
        "compaction"
        []
        [
            cell 1 "// intent: one\nprint one"
            sized 17000 (cell 2 "// intent: two\ncall w = fs.write [\"count.txt\", \"2\"]\nprint two")
            text "Summary: counted to two in count.txt."
            cell 3 "// intent: compact\ncall r = history.compact [\"the count\"]\nr"
            text "Summary: counted to two."
            cell 4 "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Counted\"}"
        ]

/// A process for a background job: it writes these chunks, then exits with 3; or, `held`, it writes
/// them and runs until it is killed (exit -1). No OS process.
let private jobProc (chunks: byte array list) (held: bool) : Proc =
    let gate = obj ()
    let mutable left = chunks

    let killed =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    {
        Pid = 7
        Write = fun _ _ -> Task.FromResult()
        CloseStdin = fun () -> Task.FromResult()
        Read =
            fun cancellation ->
                task {
                    let next =
                        lock gate (fun () ->
                            match left with
                            | chunk :: rest ->
                                left <- rest
                                Some chunk
                            | [] -> None)

                    match next with
                    | Some chunk -> return Some(Stdout chunk)
                    | None ->
                        if held then
                            do! killed.Task.WaitAsync(cancellation)

                        return None
                }
        Wait =
            fun cancellation ->
                task {
                    if held then
                        do! killed.Task.WaitAsync(cancellation)
                        return -1
                    else
                        return 3
                }
        Kill =
            fun () ->
                killed.TrySetResult() |> ignore
                Task.FromResult()
    }

/// Background jobs (T143): the hands' live jobs over a shell whose processes are scripted (multi-byte
/// text cut between writes, one that runs until killed), the notice of a job that ended, its output
/// from offsets, the bindings' JSON and the step journal's job events.
let private jobs () : Task<string list> =
    task {
        let utf8 = UTF8Encoding(false)
        let bytes = utf8.GetBytes "café ✓ built\nline two\n"
        let told = Collections.Concurrent.ConcurrentQueue<JobNotice>()

        let live =
            LiveJobs<HandsEnv>(fun notice ->
                told.Enqueue notice
                Task.FromResult())

        let fs = InMemoryFs(SystemClock.clock, false)

        let env: HandsEnv =
            {
                Clock = SystemClock.clock
                FsRead = fs.FsRead
                Fs = fs.Fs
                FsOps = Fx.Envd.FsOpsOver.create fs.FsRead fs.Fs
                Shell =
                    {
                        Run = fun _ _ -> Task.FromResult(Error(NotStarted("run", "no commands here")))
                        Spawn =
                            fun spec _ ->
                                match spec.Program with
                                | Program.Script "serve" ->
                                    Task.FromResult(Ok(jobProc [ utf8.GetBytes "listening\n" ] true))
                                | _ -> Task.FromResult(Ok(jobProc [ bytes[0..3]; bytes[4..] ] false))
                    }
                Scope = Scope.root
            }

        let launch (id: string) (command: string) : JobLaunch =
            {
                Session = "s1"
                Id = id
                Command = command
                Cwd = workspace
                Env = Inherit []
                Stdin = None
            }

        let reference (id: string) : JobRef = { Session = "s1"; Id = id }
        let run (effect: Eff<HandsEnv, 'T>) = Eff.run env effect
        let lines = ResizeArray<string>()

        let! started = run (live.Jobs.Start(launch "j1" "make"))
        lines.Add $"started: {Result.isOk started}"

        let! ended =
            run (
                live.Jobs.Wait
                    {
                        Job = reference "j1"
                        TimeoutMs = None
                    }
            )

        lines.Add $"ended: {ended |> Option.map string}"

        for from in [ Some 0L; Some 4L; None ] do
            let! (output: JobOutput option) =
                run (
                    live.Jobs.Read
                        {
                            Job = reference "j1"
                            From = from
                            MaxChars = 16
                        }
                )

            match output with
            | Some output -> lines.Add(Json.serialize (JobCodecs.output.Encode output))
            | None -> lines.Add "no output"

        let! _ = run (live.Jobs.Start(launch "j2" "serve"))
        let! killed = run (live.Jobs.Kill(reference "j2"))
        lines.Add $"killed: {killed |> Option.map string}"
        let! all = run (live.Jobs.List "s1")

        for info in all do
            lines.Add(Json.serialize (JobCodecs.info.Encode info))

        // Closing waits for every job's end: the notices are all told by then.
        do! live.Close()

        for notice in told do
            lines.Add $"notice {notice.Job}: {notice.Note}"

        let status: JobStatus =
            {
                Id = "j1"
                Name = "build"
                Command = "make"
                State = "exited"
                ExitCode = Some 3
                Reason = None
                Bytes = 22L
            }

        lines.Add(Json.serialize (JobBindingCodecs.status.Encode status))

        for event in
            [
                StepEvent.JobStarted
                    {
                        Job = "j1"
                        Name = "build"
                        Command = "make"
                        Cwd = None
                        Call = None
                    }
                StepEvent.JobEnded
                    {
                        Job = "j1"
                        How = JobEnd.Exited 3
                        Bytes = Some 22L
                    }
                StepEvent.JobEnded
                    {
                        Job = "j2"
                        How = JobEnd.Reported "lost"
                        Bytes = None
                    }
            ] do
            let json = StepCodecs.event.Encode event

            match StepCodecs.event.Decode json with
            | Ok back when back = event -> lines.Add(Json.serialize json)
            | other -> lines.Add $"did not read back: {other}"

        return List.ofSeq lines
    }

let scenarios: Scenario list =
    [
        Scenario.task "codeact-jobs" jobs
        Scenario.task "codeact-hybrid" hybrid
        Scenario.task "codeact-compaction" compaction
        Scenario.task "codeact-text-only" textOnly
        Scenario.task "codeact-recovery" recovery
    ]
