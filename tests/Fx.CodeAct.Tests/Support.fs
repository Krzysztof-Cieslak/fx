/// The CodeAct strategy on a real session host: an in-memory store of payloads (every event goes through
/// its JSON), a scripted model, and a kernel: the F# script kernel in process (deterministic; its language
/// is `ScriptKernel`'s), acting on a scratch workspace through the local filesystem and shell.
module Fx.CodeAct.Tests.Support

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd
open Fx.Envd.Live
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
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Sdk
open Fx.CodeAct

/// The host's side: the clock, traces, the model and the turn's scope.
type HostEnv =
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

    override env.ToString() = $"HostEnv({env.Scope})"

/// The hands: the workspace's files and shell.
type HandsEnv =
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

    /// Where output cut by the bindings goes (T201a): one store for every environment of this type.
    static member val Stored = Fx.Store.Fakes.InMemoryBlobs()

    interface Fx.Store.IBlobs with
        member _.Blobs = HandsEnv.Stored.Blobs

    /// Ids of handles (T201b): seeded, the same in every run.
    static member val SeededIds = Fx.Effects.Fakes.FakeIds(7UL, Fx.Effects.Fakes.FakeClock(Instant.unixEpoch).Clock)

    interface IIds with
        member _.Ids = HandsEnv.SeededIds.Ids

    interface IScopeWith<HandsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"HandsEnv({env.Scope})"

let alice = Attribution.create "user:alice" "test"
let g = Product.kernelGlobal

let usage =
    { Usage.zero with
        Input = 100
        Output = 10
    }

/// A model's replies, in order: each answers the next request.
[<RequireQualifiedAccess>]
type Reply =
    | Events of ChatEvent list
    /// Parts and a stop reason; tool calls get ids `call-<n>` in order.
    | Parts of Part list * StopReason
    /// A reply whose provider said its request had this many input tokens.
    | Measured of input: int * Reply

/// A scripted model: answers requests in order with its replies, records every request, and can hold
/// a request until the test lets it go (to steer or interrupt while a turn runs).
[<Sealed>]
type Model() =
    let gate = obj ()
    let requests = ResizeArray<ChatRequest>()
    let replies = Queue<Reply>()
    let holds = Dictionary<int, TaskCompletionSource>()
    let asked = Dictionary<int, TaskCompletionSource>()
    let mutable calls = 0

    let signal (n: int) =
        match asked.TryGetValue n with
        | true, s -> s
        | _ ->
            let s = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            asked[n] <- s
            s

    let rec events (reply: Reply) : ChatEvent list =
        match reply with
        | Reply.Events events -> events
        | Reply.Measured(input, reply) ->
            events reply
            |> List.map (fun event ->
                match event with
                | ChatEvent.UsageUpdated used -> ChatEvent.UsageUpdated { used with Input = input }
                | other -> other)
        | Reply.Parts(parts, stop) ->
            let parts =
                parts
                |> List.map (fun part ->
                    match part with
                    | Part.ToolCall call when call.Id = "" ->
                        calls <- calls + 1
                        Part.ToolCall { call with Id = $"call-{calls}" }
                    | part -> part)

            ChatEvents.ofResponse
                {
                    Id = None
                    Model = Some "model"
                    Parts = parts
                    Stop = stop
                    Usage = usage
                }

    let stream (request: ChatRequest) (cancellation: CancellationToken) : Task<ChatStream> =
        task {
            let n, hold, reply =
                lock gate (fun () ->
                    requests.Add request
                    let n = requests.Count
                    (signal n).TrySetResult() |> ignore

                    let hold =
                        match holds.TryGetValue n with
                        | true, h -> Some h.Task
                        | _ -> None

                    let reply = if replies.Count > 0 then Some(replies.Dequeue()) else None
                    n, hold, reply)

            match hold with
            | Some hold -> do! hold.WaitAsync cancellation
            | None -> ()

            match reply with
            | Some reply -> return lock gate (fun () -> ChatStream.ofEvents (events reply))
            | None ->
                return ChatStream.failed (ChatError.InvalidRequest $"the test's model has no reply for request {n}")
        }

    member _.Llm: Llm = { Stream = stream }

    member _.Add(reply: Reply) =
        lock gate (fun () -> replies.Enqueue reply)

    member this.AddAll(items: Reply list) =
        for reply in items do
            this.Add reply

    /// Holds request `n` (1-based) until the returned source is set.
    member _.Hold(n: int) : TaskCompletionSource =
        lock gate (fun () ->
            let h = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            holds[n] <- h
            h)

    /// Completes when request `n` has been made.
    member _.Asked(n: int) : Task = lock gate (fun () -> (signal n).Task)

    member _.Requests: ChatRequest list = lock gate (fun () -> List.ofSeq requests)

    override _.ToString() = "Model"

/// Replies, as a model writes them.
[<RequireQualifiedAccess>]
module Say =
    let call (name: string) (arguments: Json) : Part =
        Part.ToolCall
            {
                Id = ""
                Name = name
                Arguments = Json.serialize arguments
                State = None
            }

    /// A reply with one cell.
    let cell (code: string) : Reply =
        Reply.Parts([ call "cell" (Json.Object [ "code", Json.String code ]) ], StopReason.ToolUse)

    /// A reply with one cell and the texts it uses (T140).
    let cellWith (code: string) (texts: (string * string) list) : Reply =
        let texts =
            Json.Object(texts |> List.map (fun (name, text) -> name, Json.String text))

        Reply.Parts([ call "cell" (Json.Object [ "code", Json.String code; "texts", texts ]) ], StopReason.ToolUse)

    /// A reply with one call of a tool.
    let tool (name: string) (arguments: (string * Json) list) : Reply =
        Reply.Parts([ call name (Json.Object arguments) ], StopReason.ToolUse)

    let text (text: string) : Reply =
        Reply.Parts([ Part.text text ], StopReason.EndTurn)

    let failed (error: ChatError) : Reply = Reply.Events [ ChatEvent.Failed error ]

    let empty: Reply = Reply.Parts([], StopReason.EndTurn)

    /// The reply, from a request of this many input tokens.
    let sized (input: int) (reply: Reply) : Reply = Reply.Measured(input, reply)

    /// A cell that ends the turn `done`.
    let finish (summary: string) : Reply =
        cell $"// intent: finish\nfinish {{\"kind\":\"done\",\"summary\":{Json.quote summary}}}"

/// An F# file of at least `size` characters with what ends or breaks a TypeScript template literal:
/// identifiers in double backticks, Markdown fences in comments, `${…}`, backslashes; and non-ASCII
/// text (T140).
let fsharpWithBackticks (size: int) : string =
    let sb = Text.StringBuilder()

    sb.Append("module Native.Posix\n\n/// The POSIX layer (`spec §18`): see ``Native.fs``.\n\n")
    |> ignore

    let mutable i = 0

    while sb.Length < size do
        i <- i + 1
        let n = string i

        sb
            .Append("/// ```fsharp\n")
            .Append("/// let ``case " + n + "`` = \"${x}\\n\" // é ✓\n")
            .Append("/// ```\n")
            .Append("let ``case " + n + " works`` () = \"a\\\\b ${y}\" + `" + n + "`\n\n")
        |> ignore

    sb.ToString()

/// A scratch directory under artifacts/.
let scratch (name: string) : string =
    let dir =
        Fx.Kernel.Tests.Repo.path
            [
                "artifacts"
                "codeact-tests"
                $"{Environment.ProcessId}-{name}-{Guid.NewGuid():N}"
            ]

    Directory.CreateDirectory dir |> ignore
    dir

/// The script kernel in this process, over the in-process transport as `wrap` makes the host see it: a
/// fresh one for each launch.
let scriptKernelOver (wrap: Transport -> Transport) (options: KernelOptions) : Interpreter<HandsEnv> =
    {
        Language = "script"
        Prompt =
            fun surface ->
                let names = surface.Bindings |> List.map (fun b -> $"{surface.Global}.{b.Path}")
                "Cells are scripts. Bindings: " + String.concat ", " names
        Start =
            fun start ->
                fun env ->
                    task {
                        let kernel = Fx.Aot.Scenarios.Kernel.ScriptKernel.Kernel ignore
                        let frames = ConnectionLimits.frames options.Limits
                        let clientEnd, serverEnd = InProcess.pair (1 <<< 20) frames frames

                        let serverEnv: Fx.Aot.Scenarios.Rpc.TestService.Env =
                            {
                                Clock = SystemClock.clock
                                Scope = Scope.root
                            }

                        Eff.run serverEnv (Rpc.accept kernel.Service serverEnd) |> ignore

                        let terminate () =
                            serverEnd.Abort()
                            kernel.Kill()
                            Task.FromResult()

                        match!
                            Eff.run env (Kernel.connect options start.Host start.Setup terminate (wrap clientEnd))
                        with
                        | Ok k -> return Ok k
                        | Error e ->
                            let! _ = terminate ()
                            return Error(string e)
                    }
        Launches = None
    }

/// The script kernel in this process, over the in-process transport: a fresh one for each launch.
let scriptKernel (options: KernelOptions) : Interpreter<HandsEnv> = scriptKernelOver id options

/// What a host process holds: the session host, the strategy's kernels, and how to close them.
type Process =
    {
        Host: SessionHost<HostEnv>
        /// The kernel of a session, if it has one.
        Kernel: string -> Kernel option
        /// Closes the strategy's kernels.
        Close: unit -> Task
    }

    override _.ToString() = "Process"

/// A CodeAct session host on fakes and a scratch workspace.
type Fixture =
    {
        Env: HostEnv
        Store: InMemoryEventLog<Payload>
        Blobs: InMemoryBlobs
        /// The host process running now (`restart` starts another on the same stores).
        mutable Process: Process
        /// Starts a host process on the fixture's stores.
        Start: unit -> Process
        Model: Model
        Root: string
        Workspace: string
        Variants: Variants
        /// What disposing does after closing the kernels (stopping environments), in order.
        Cleanup: ResizeArray<unit -> Task>
    }

    member this.Host = this.Process.Host

    /// The kernel of a session, if it has one.
    member this.Kernel(session: string) = this.Process.Kernel session

    member this.File(name: string) = Path.Combine(this.Workspace, name)

    member this.Write(name: string, text: string) =
        let path = this.File name
        Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
        File.WriteAllText(path, text)

    member this.Read(name: string) = File.ReadAllText(this.File name)

    interface IDisposable with
        member this.Dispose() =
            // Kernels close before their environments stop.
            try
                (this.Process.Close()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
            with _ ->
                ()

            for clean in this.Cleanup do
                try
                    (clean ()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
                with _ ->
                    ()

            (Eff.run this.Env (SessionHost.stop this.Host)).Wait(TimeSpan.FromSeconds 30.0)
            |> ignore

            try
                Directory.Delete(this.Root, true)
            with _ ->
                ()

    override _.ToString() = "Fixture"

let private os =
    if OperatingSystem.IsWindows() then Os.Windows
    elif OperatingSystem.IsMacOS() then Os.MacOs
    else Os.Linux

/// Variant selections every test starts from: a model, and no waiting between retries.
let baseSelections: (string * Json) list =
    [
        "model.role.main", Json.String "test/model"
        "codeact.policy.retryDelayMs", Json.int 0
        // Workspace snapshots (git) only where a test asks for them.
        "codeact.journal.snapshots", Json.Bool false
    ]

/// A fixture with these variant selections (on top of `baseSelections`) and the interpreter `make` gives
/// for the fixture's root directory.
let createOn<'K
    when 'K :> IShell
    and 'K :> IFs
    and 'K :> IFsOps
    and 'K :> IBlobs
    and 'K :> IIds
    and 'K :> IClock
    and 'K :> IScopeWith<'K>>
    (name: string)
    (selections: (string * Json) list)
    (handsOf: string -> string -> ResizeArray<unit -> Task> -> Hands<'K>)
    (llm: Llm option)
    (catalog: ModelRef -> ModelInfo option)
    : Fixture =
    let root = scratch name
    let workspace = Path.Combine(root, "workspace")
    Directory.CreateDirectory workspace |> ignore
    let clock = SystemClock.clock
    let model = Model()
    let cleanup = ResizeArray<unit -> Task>()
    let hands = handsOf root workspace cleanup

    let env: HostEnv =
        {
            Clock = clock
            Trace = Trace.none
            Llm = llm |> Option.defaultValue model.Llm
            Scope = Scope.root
        }

    let overrides = selections |> List.map fst

    let chosen =
        (baseSelections |> List.filter (fun (k, _) -> not (List.contains k overrides)))
        @ selections

    let variants = FakeVariants.create CodeActSlots.read chosen
    let store = InMemoryEventLog<Payload>()
    let blobs = InMemoryBlobs()
    let snapshots = InMemorySnapshots()
    let sessions = SessionLog.typed store.Log

    let settings =
        { SessionSettings.defaults with
            Kinds = Questions.kinds
        }

    let start () : Process =
        // The process's background jobs: they tell the session host when they end, and die with it.
        let host: SessionHost<HostEnv> option ref = ref None

        let notify (notice: JobNotice) : Task<unit> =
            task {
                match host.Value with
                | Some host ->
                    let! _ =
                        Eff.run
                            env
                            (SessionHost.send
                                host
                                notice.Session
                                (JobNotices.key notice.Job)
                                JobNotices.by
                                (JobNotices.command notice))

                    ()
                | None -> ()
            }

        let jobs = LiveJobs<'K>(notify)

        let codeact =
            CodeAct.create
                {
                    Sessions = sessions
                    Contexts = ContextLog.typed store.Log
                    Steps = StepJournal.typed store.Log
                    Blobs = Some blobs.Blobs
                    Profile = fun _ -> Eff.ret variants
                    Hands = fun _ -> Eff.ret (Ok { hands with Jobs = jobs.Jobs })
                    Catalog = catalog
                }

        let started =
            (Eff.run env (SessionHost.create sessions snapshots.Store codeact.Runner settings)).GetAwaiter().GetResult()

        host.Value <- Some started

        {
            Host = started
            Kernel = codeact.Kernel
            Close =
                fun () ->
                    task {
                        do! codeact.Close()
                        do! jobs.Close()
                    }
                    :> Task
        }

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

/// Hands on the local filesystem and shell, with the interpreter `make` gives for the fixture's root.
let localHands
    (make: string -> Interpreter<HandsEnv>)
    (root: string)
    (workspace: string)
    (_: ResizeArray<unit -> Task>)
    : Hands<HandsEnv> =
    let clock = SystemClock.clock

    {
        Env =
            {
                Clock = clock
                FsRead = LocalFs.read
                Fs = LocalFs.write
                FsOps = LocalFsOps.create clock (SystemIds.create clock)
                Shell = LocalShell.create (LocalShell.defaultShell ()) workspace
                Scope = Scope.root
            }
        Workspace = workspace
        Os = os
        CommandEnv = Inherit []
        Interpreter = make root
        Guard = fun _ -> EffResult.ok []
        BlobRoot = None
        Snapshots =
            Some(
                GitSnapshots.create
                    (LocalShell.create (LocalShell.defaultShell ()) workspace)
                    LocalFs.write
                    {
                        Git = "git"
                        GitDir = Path.Combine(root, "shadow.git")
                        WorkTree = workspace
                        Exclude = []
                    }
            )
        Sandbox = None
        Variables = []
        // Each host process gives its own (`createOn`).
        Jobs = Jobs.none
    }

/// A fixture on local hands, with the interpreter `make` gives for its root.
let createOver
    (name: string)
    (selections: (string * Json) list)
    (make: string -> Interpreter<HandsEnv>)
    (llm: Llm option)
    (catalog: ModelRef -> ModelInfo option)
    : Fixture =
    createOn name selections (localHands make) llm catalog

/// A fixture with the scripted model, no catalog, and the interpreter `make` gives for its root.
let createWith (name: string) (selections: (string * Json) list) (make: string -> Interpreter<HandsEnv>) : Fixture =
    createOver name selections make None (fun _ -> None)

let kernelOptions =
    { KernelOptions.defaults with
        ExitGrace = TimeSpan.FromSeconds 1.0
    }

/// A fixture with the script kernel.
let create (name: string) (selections: (string * Json) list) : Fixture =
    createWith name selections (fun _ -> scriptKernel kernelOptions)

let run (f: Fixture) (effect: Eff<HostEnv, 'T>) : Task<'T> =
    (Eff.run f.Env effect).WaitAsync(TimeSpan.FromSeconds 60.0)

/// Stops the host process as a crash would (turns stop where they are, kernels die) and starts another
/// on the same stores. Its sessions' actors start on first use.
let restart (f: Fixture) : Task =
    task {
        do! run f (SessionHost.stop f.Host)
        do! f.Process.Close().WaitAsync(TimeSpan.FromSeconds 60.0)
        f.Process <- f.Start()
    }

let send (f: Fixture) (session: string) (key: string) (command: Command) : Task<Fx.Machines.Outcome> =
    run f (SessionHost.send f.Host session (IdempotencyKey key) alice command)

/// Starts a session.
let start (f: Fixture) (session: string) : Task =
    task {
        let! outcome =
            run f (SessionHost.start f.Host session (IdempotencyKey(session + "/start")) alice "p1" None)

        match outcome with
        | Accepted _ -> ()
        | other -> failtest $"the session did not start: {other}"
    }

let prompt (f: Fixture) (session: string) (key: string) (text: string) : Task =
    task {
        match! send f session key (Command.Prompt(text, PromptMode.Steer)) with
        | Accepted _ -> ()
        | other -> failtest $"the prompt was not taken: {other}"
    }

/// The session's events, decoded.
let events (f: Fixture) (session: string) : Recorded<Event> list =
    f.Store.Events(Session.stream session)
    |> List.map (EventLog.decode SessionLog.schema)

/// The session's context events, decoded.
let contextEvents (f: Fixture) (session: string) : Recorded<ContextEvent> list =
    f.Store.Events(ContextLog.stream session)
    |> List.map (EventLog.decode ContextLog.schema)

/// Waits until turn `n` ended, and gives how.
let ended (f: Fixture) (session: string) (n: int) : Task<TurnEnd> =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 60.0
        let mutable result = None

        while result.IsNone do
            result <-
                events f session
                |> List.tryPick (fun r ->
                    match r.Event with
                    | Event.TurnEnded(turn, ending, _) when turn = n -> Some ending
                    | _ -> None)

            if result.IsNone then
                if DateTime.UtcNow > deadline then
                    let lines = events f session |> List.map (fun r -> string r.Event)
                    failtest ($"turn {n} did not end:\n" + String.concat "\n" lines)

                do! Task.Delay 10

        return result.Value
    }

/// The session's state.
let state (f: Fixture) (session: string) : Task<SessionState> =
    task {
        let! snapshot = run f (SessionHost.state f.Host session)
        return snapshot.State
    }

/// The receipts recorded, in order.
let receipts (f: Fixture) (session: string) : Receipt list =
    contextEvents f session
    |> List.choose (fun r ->
        match r.Event with
        | ContextEvent.Returned receipt -> Some receipt
        | _ -> None)

/// A receipt's text, as the model reads it.
let text (receipt: Receipt) : string =
    receipt.Pieces |> List.map Piece.render |> String.concat "\n"

/// Everything a request says to the model, as text (system parts, messages and tool results).
let requestText (request: ChatRequest) : string =
    let partText (part: Part) =
        match part with
        | Part.Text t -> t.Text
        | Part.ToolResult r ->
            r.Content
            |> List.map (fun c ->
                match c with
                | ToolContent.Text t -> t
                | ToolContent.Media _ -> "[media]")
            |> String.concat "\n"
        | Part.ToolCall c -> c.Name + " " + c.Arguments
        | Part.Refusal t -> t
        | _ -> ""

    (request.System @ (request.Messages |> List.collect (fun m -> m.Parts)))
    |> List.map partText
    |> String.concat "\n"

/// The result of a turn that completed.
let completed (ending: TurnEnd) : TurnResult =
    match ending with
    | TurnEnd.Completed result -> result
    | other -> failtest $"the turn did not complete: {other}"

/// The reason of a turn that failed.
let failedWith (ending: TurnEnd) : string =
    match ending with
    | TurnEnd.Failed reason -> reason
    | other -> failtest $"the turn did not fail: {other}"

/// Runs a test on a fixture, and disposes it.
let within (name: string) (selections: (string * Json) list) (body: Fixture -> Task<unit>) : unit -> Task =
    fun () ->
        task {
            use f = create name selections
            do! body(f).WaitAsync(TimeSpan.FromSeconds 120.0)
        }

/// The session's step journal, decoded.
let journal (f: Fixture) (session: string) : Recorded<StepEvent> list =
    f.Store.Events(StepJournal.stream session)
    |> List.map (EventLog.decode StepJournal.schema)

/// Waits until a condition holds.
let until (what: string) (condition: unit -> bool) : Task =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        while not (condition ()) do
            if DateTime.UtcNow > deadline then
                failtest $"timed out waiting: {what}"

            do! Task.Delay 10
    }

/// Waits until a cell of the session printed this text (its output reached the host). A cell that ends
/// `spin <text>` has the kernel blocked by then: an interrupt can only kill it.
let printed (f: Fixture) (session: string) (text: string) : Task =
    until $"a cell printed {text}" (fun () ->
        journal f session
        |> List.exists (fun r ->
            match r.Event with
            | StepEvent.Printed p -> p.Text.Contains text
            | _ -> false))

/// Waits until the session has the open request.
let opened (f: Fixture) (session: string) (id: string) : Task<PendingRequest> =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 60.0
        let mutable found = None

        while found.IsNone do
            let! state = state f session
            found <- state.Requests |> List.tryFind (fun r -> r.Request.Id = id)

            if found.IsNone then
                if DateTime.UtcNow > deadline then
                    failtest $"no request {id}: {state}"

                do! Task.Delay 10

        return found.Value
    }

let private canonicalRequest (request: ChatRequest) =
    Json.serializeCanonical (ChatRequest.codec.Encode request)

/// Whether a request asked for a summary (compaction) rather than the turn's next step.
let isSummary (request: ChatRequest) : bool =
    match request.Messages |> List.tryLast |> Option.bind (fun m -> List.tryLast m.Parts) with
    | Some(Part.Text text) -> text.Text.StartsWith "[Compaction:"
    | _ -> false

/// Replay: the session's recorded logs render, through the pure render alone, to exactly the requests
/// the model was sent (with the suffix each reply recorded it saw); summary requests aside.
let expectReplays (f: Fixture) (session: string) : Task<unit> =
    task {
        let requests = f.Model.Requests |> List.filter (not << isSummary)

        let! history =
            run f (ContextLog.load (SessionLog.typed f.Store.Log) (ContextLog.typed f.Store.Log) session)

        let! settings = run f (CodeActSettings.ofVariants f.Variants)

        let replies =
            history.Items
            |> List.indexed
            |> List.choose (fun (i, item) ->
                match item with
                | LogItem.Context(_, ContextEvent.Replied reply) -> Some(i, reply)
                | _ -> None)

        Expect.equal replies.Length requests.Length "a reply per request"

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
                (canonicalRequest (Render.request cfg template rendered))
                (canonicalRequest sent)
                $"request of turn {reply.Turn} step {reply.Step} replays"

        let collapsed =
            history.Items
            |> List.exists (fun item ->
                match item with
                | LogItem.Context(_, ContextEvent.Collapsed _) -> true
                | _ -> false)

        // Append-only: every request is the one before it plus what happened since (a collapse is the
        // documented rewrite).
        if not collapsed then
            for a, b in List.pairwise requests do
                Expect.equal (b.Messages |> List.truncate a.Messages.Length) a.Messages "append-only"
    }
