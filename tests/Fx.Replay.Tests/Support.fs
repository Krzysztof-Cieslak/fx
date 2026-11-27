/// Recorded CodeAct sessions on a real session host: an in-memory store of payloads (every event goes
/// through its JSON), a scripted model, an in-memory workspace, a shell that runs the script kernel as
/// a process in this process (`ScriptProc`), and the strategy recorded (`Recording.codeact`).
module Fx.Replay.Tests.Support

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Envd
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
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
open Fx.Replay
open Fx.Aot.Scenarios.Kernel

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

    interface IScopeWith<HandsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"HandsEnv({env.Scope})"

/// What replay runs in: a real clock (for its stall timeout only).
type DriverEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<DriverEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = "DriverEnv"

/// How the composition root puts the recording interpreters into its environments.
let wraps: Wraps<HostEnv, HandsEnv> =
    {
        Host =
            fun taps env ->
                { env with
                    Clock = taps.Clock env.Clock
                    Llm = taps.Llm env.Llm
                }
        Hands =
            fun taps env ->
                { env with
                    Clock = taps.Clock env.Clock
                    FsRead = taps.FsRead env.FsRead
                    Fs = taps.Fs env.Fs
                    FsOps = taps.FsOps env.FsOps
                    Shell = taps.Shell env.Shell
                }
    }

let alice = Attribution.create "user:alice" "test"

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

/// A scripted model: answers requests in order, records them, and can hold a request until the test
/// lets it go (to steer or interrupt while a turn runs).
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

    member _.AddAll(items: Reply list) =
        lock gate (fun () ->
            for reply in items do
                replies.Enqueue reply)

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

    let cell (code: string) : Reply =
        Reply.Parts([ call "cell" (Json.Object [ "code", Json.String code ]) ], StopReason.ToolUse)

    let tool (name: string) (arguments: (string * Json) list) : Reply =
        Reply.Parts([ call name (Json.Object arguments) ], StopReason.ToolUse)

    let text (text: string) : Reply =
        Reply.Parts([ Part.text text ], StopReason.EndTurn)

    let failed (error: ChatError) : Reply = Reply.Events [ ChatEvent.Failed error ]

    /// A cell that ends the turn `done`.
    let finish (summary: string) : Reply =
        cell $"// intent: finish\nfinish {{\"kind\":\"done\",\"summary\":{Json.quote summary}}}"

    /// The reply, from a request of this many input tokens.
    let sized (input: int) (reply: Reply) : Reply = Reply.Measured(input, reply)

/// A blob store that remembers what was put in it, so a recording can be exported whole.
[<Sealed>]
type TrackedBlobs() =
    let inner = InMemoryBlobs()
    let kept = ConcurrentDictionary<string, byte array>(StringComparer.Ordinal)

    member val Blobs: Blobs =
        { inner.Blobs with
            Put =
                fun bytes cancellation ->
                    task {
                        let! blob = inner.Blobs.Put bytes cancellation
                        kept[string blob.Hash] <- Array.copy bytes
                        return blob
                    }
        }

    member _.All: (string * byte array) list =
        kept |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.sortBy fst |> List.ofSeq

    override _.ToString() = "TrackedBlobs"

let workspace = if OperatingSystem.IsWindows() then @"C:\work" else "/work"

let os =
    if OperatingSystem.IsWindows() then Os.Windows
    elif OperatingSystem.IsMacOS() then Os.MacOs
    else Os.Linux

/// The file `name` of the workspace.
let file (name: string) =
    if OperatingSystem.IsWindows() then
        workspace + "\\" + name
    else
        workspace + "/" + name

let kernelOptions =
    { KernelOptions.defaults with
        ExitGrace = TimeSpan.FromSeconds 1.0
    }

/// Commands the hands' shell runs: `ran <script>`, exit 0.
let ran: Fx.Effects.Command -> CancellationToken -> Task<Result<Output, ShellError>> =
    fun command _ ->
        let script =
            match command.Spec.Program with
            | Program.Script script -> script
            | Program.Exec(file, args) -> String.concat " " (file :: args)

        Task.FromResult(Ok(ScriptedShell.output 0 $"ran {script}\n" ""))

/// A host process: its session host and the recorded strategy.
type Process =
    {
        Host: SessionHost<HostEnv>
        Strategy: RecordedCodeAct<HostEnv, HandsEnv>
    }

    override _.ToString() = "Process"

/// A recorded CodeAct session host.
type Fixture =
    {
        Env: HostEnv
        Store: InMemoryEventLog<Payload>
        Blobs: TrackedBlobs
        Fs: InMemoryFs
        Model: Model
        Variants: Variants
        /// The models' catalog the strategy (and replay) knows.
        Catalog: ModelRef -> ModelInfo option
        /// The host process running now (`restart` starts another on the same stores).
        mutable Process: Process
        Start: unit -> Process
    }

    member this.Host = this.Process.Host

    /// The recorded stores.
    member this.Recording: RecordedSession =
        {
            Log = this.Store.Log
            Blobs = this.Blobs.Blobs
        }

    interface IDisposable with
        member this.Dispose() =
            try
                (Eff.run this.Env (SessionHost.stop this.Host)).Wait(TimeSpan.FromSeconds 30.0)
                |> ignore
            with _ ->
                ()

            try
                (this.Process.Strategy.Close()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
            with _ ->
                ()

    override _.ToString() = "Fixture"

/// Variant selections every test starts from: a model, and no waiting between retries.
let baseSelections: (string * Json) list =
    [
        "model.role.main", Json.String "test/model"
        "codeact.policy.retryDelayMs", Json.int 0
        "codeact.journal.snapshots", Json.Bool false
    ]

/// A recorded fixture with these selections, commands, the variables commands get, a model (the scripted
/// one by default), what each turn's hands are given (a guard), and the models' catalog.
let createIn
    (catalog: ModelRef -> ModelInfo option)
    (selections: (string * Json) list)
    (commands: Fx.Effects.Command -> CancellationToken -> Task<Result<Output, ShellError>>)
    (commandEnv: EnvVars)
    (llm: Llm option)
    (guard: Variants -> EventLog<Event> -> TurnStart -> Hands<HandsEnv> -> Eff<HostEnv, Hands<HandsEnv>>)
    : Fixture =
    let fsClock = FakeClock(Instant.ofUnixMicroseconds 1_700_000_000_000_000L)

    let fs =
        InMemoryFs(fsClock.Clock, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())

    fs.AddDirectory workspace
    let model = Model()

    let env: HostEnv =
        {
            Clock = SystemClock.clock
            Trace = Trace.none
            Llm = llm |> Option.defaultValue model.Llm
            Scope = Scope.root
        }

    let hands: Hands<HandsEnv> =
        {
            Env =
                {
                    Clock = SystemClock.clock
                    FsRead = fs.FsRead
                    Fs = fs.Fs
                    FsOps = FsOpsOver.create fs.FsRead fs.Fs
                    Shell = ScriptProc.shell commands
                    Scope = Scope.root
                }
            Workspace = workspace
            Os = os
            CommandEnv = commandEnv
            Interpreter = ScriptProc.interpreter kernelOptions
            Guard = fun _ -> EffResult.ok []
            Snapshots = None
            Sandbox = None
            Variables = []
        }

    let overrides = selections |> List.map fst

    let chosen =
        (baseSelections |> List.filter (fun (k, _) -> not (List.contains k overrides)))
        @ selections

    let variants = FakeVariants.create (CodeActSlots.read @ RecordingSlots.all) chosen

    let store = InMemoryEventLog<Payload>()
    let blobs = TrackedBlobs()
    let snapshots = InMemorySnapshots()
    let sessions = SessionLog.typed store.Log

    let settings =
        { SessionSettings.defaults with
            Kinds = Questions.kinds
        }

    let start () : Process =
        let recorder = Recording.recorder store.Log blobs.Blobs SystemClock.clock

        let strategy =
            Recording.codeact
                recorder
                wraps
                {
                    Sessions = sessions
                    Contexts = ContextLog.typed store.Log
                    Steps = StepJournal.typed store.Log
                    Blobs = Some blobs.Blobs
                    Profile = fun _ -> Eff.ret variants
                    Hands = fun turn -> guard variants sessions turn hands |> Eff.map Ok
                    Catalog = catalog
                }

        let host =
            (Eff.run env (SessionHost.create sessions snapshots.Store strategy.Runner settings))
                .GetAwaiter()
                .GetResult()

        { Host = host; Strategy = strategy }

    {
        Env = env
        Store = store
        Blobs = blobs
        Fs = fs
        Model = model
        Variants = variants
        Catalog = catalog
        Process = start ()
        Start = start
    }

/// A recorded fixture with these selections, commands, the variables commands get, a model (the scripted
/// one by default), and what each turn's hands are given (a guard).
let createGuarded
    (selections: (string * Json) list)
    (commands: Fx.Effects.Command -> CancellationToken -> Task<Result<Output, ShellError>>)
    (commandEnv: EnvVars)
    (llm: Llm option)
    (guard: Variants -> EventLog<Event> -> TurnStart -> Hands<HandsEnv> -> Eff<HostEnv, Hands<HandsEnv>>)
    : Fixture =
    createIn (fun _ -> None) selections commands commandEnv llm guard

/// A recorded fixture with these selections, commands, the variables commands get, and a model (the
/// scripted one by default).
let createFull
    (selections: (string * Json) list)
    (commands: Fx.Effects.Command -> CancellationToken -> Task<Result<Output, ShellError>>)
    (commandEnv: EnvVars)
    (llm: Llm option)
    : Fixture =
    createGuarded selections commands commandEnv llm (fun _ _ _ hands -> Eff.ret hands)

/// A recorded fixture with these selections, commands and the variables commands get.
let createWith
    (selections: (string * Json) list)
    (commands: Fx.Effects.Command -> CancellationToken -> Task<Result<Output, ShellError>>)
    (commandEnv: EnvVars)
    : Fixture =
    createFull selections commands commandEnv None

let create (selections: (string * Json) list) : Fixture = createWith selections ran (Inherit [])

let run (f: Fixture) (effect: Eff<HostEnv, 'T>) : Task<'T> =
    (Eff.run f.Env effect).WaitAsync(TimeSpan.FromSeconds 60.0)

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

/// Waits until the session has the open request.
let opened (f: Fixture) (session: string) (id: string) : Task<PendingRequest> =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 60.0
        let mutable found = None

        while found.IsNone do
            let! snapshot = run f (SessionHost.state f.Host session)
            found <- snapshot.State.Requests |> List.tryFind (fun r -> r.Request.Id = id)

            if found.IsNone then
                if DateTime.UtcNow > deadline then
                    failtest $"no request {id}: {snapshot.State}"

                do! Task.Delay 10

        return found.Value
    }

/// Stops the host process as a crash would (turns stop where they are, kernels die) and starts another
/// on the same stores.
let restart (f: Fixture) : Task =
    task {
        do! run f (SessionHost.stop f.Host)
        do! f.Process.Strategy.Close().WaitAsync(TimeSpan.FromSeconds 60.0)
        f.Process <- f.Start()
    }

/// Closes the host (its journals end), as the composition root does when it stops.
let close (f: Fixture) : Task =
    task {
        do! run f (SessionHost.stop f.Host)
        do! f.Process.Strategy.Close().WaitAsync(TimeSpan.FromSeconds 60.0)
    }

let driver: DriverEnv =
    {
        Clock = SystemClock.clock
        Scope = Scope.root
    }

/// How replay starts the session's kernels (the script kernel) and how long it waits for progress.
let settings: ReplaySettings =
    {
        Interpreter = fun _ -> ScriptProc.interpreter kernelOptions
        Provider = fun id -> Error $"no interpreter provider {id} here"
        KernelOptions = kernelOptions
        Catalog = fun _ -> None
        StallAfter = TimeSpan.FromSeconds 10.0
    }

/// Replays a recorded session exactly, with these settings.
let replayWith (settings: ReplaySettings) (recording: RecordedSession) (session: string) : Task<ReplayReport> =
    (Eff.run driver (SessionReplay.run settings recording session)).WaitAsync(TimeSpan.FromSeconds 120.0)

/// Replays a recorded session exactly.
let replay (recording: RecordedSession) (session: string) : Task<ReplayReport> = replayWith settings recording session

/// A recording exported as JSON lines (every stream's events, then every blob), and read back: what
/// the golden recorded session is.
[<RequireQualifiedAccess>]
module Exported =
    let private recorded = Fx.Store.Codecs.recorded Fx.Store.Codecs.payload

    let write (store: InMemoryEventLog<Payload>) (blobs: TrackedBlobs) : string =
        let lines = ResizeArray<string>()

        for stream in store.Streams |> List.sortBy (fun s -> s.Value) do
            for event in store.Events stream do
                lines.Add(Json.serialize (Json.Object [ "event", recorded.Encode event ]))

        for hash, bytes in blobs.All do
            lines.Add(
                Json.serialize (
                    Json.Object
                        [
                            "blob", Json.String hash
                            "content", Json.String(Convert.ToBase64String bytes)
                        ]
                )
            )

        String.concat "\n" lines + "\n"

    /// The stores of an exported recording.
    let read (text: string) : RecordedSession =
        let store = InMemoryEventLog<Payload>()
        let blobs = InMemoryBlobs()
        let events = ResizeArray<Recorded<Payload>>()

        for line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
            match Json.parse line with
            | Ok(Json.Object [ "event", event ]) ->
                match recorded.Decode event with
                | Ok r -> events.Add r
                | Error issues -> failtest (Issue.renderAtMost 3 issues)
            | Ok(Json.Object [ "blob", _; "content", Json.String content ]) ->
                blobs.Blobs.Put (Convert.FromBase64String content) CancellationToken.None
                |> fun t -> t.GetAwaiter().GetResult() |> ignore
            | other -> failtest $"not a recording line: {other}"

        // One commit per run of events with one key, as they were appended.
        let commits =
            events
            |> Seq.fold
                (fun (acc: (Recorded<Payload> list) list) r ->
                    match acc with
                    | (first :: _ as current) :: rest when first.Stream = r.Stream && first.Meta.Key = r.Meta.Key ->
                        (current @ [ r ]) :: rest
                    | _ -> [ r ] :: acc)
                []
            |> List.rev

        for commit in commits do
            let first = List.head commit

            match
                (store.Log.Append
                    first.Stream
                    (first.Seq - 1L)
                    first.Meta
                    (commit |> List.map (fun r -> r.Event))
                    CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()
            with
            | Ok _ -> ()
            | Error e -> failtest $"the recording does not load: {e}"

        { Log = store.Log; Blobs = blobs.Blobs }
