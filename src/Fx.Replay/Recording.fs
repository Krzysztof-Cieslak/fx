namespace Fx.Replay

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Store
open Fx.Envd
open Fx.Kernel
open Fx.Interpreters
open Fx.Sdk
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Variants
open Fx.CodeAct

/// Whether a session is recorded (`session.record`).
[<RequireQualifiedAccess>]
type RecordMode =
    | On
    | Off

    override this.ToString() =
        match this with
        | On -> "on"
        | Off -> "off"

/// The variant slots of recording; the host registers `all` with its registry (as `PromptSlots.all`).
[<RequireQualifiedAccess>]
module RecordingSlots =
    /// `session.record`: whether a session's capability calls are journaled for exact replay.
    let record: Slot<RecordMode> =
        Slot.enum
            "session.record"
            "Whether a session's capability calls (model responses, commands, files, kernels, the clock) are journaled so that it can be replayed exactly (fx replay): on or off. Read at the session's first turn in a host process."
            [ RecordMode.On, "on"; RecordMode.Off, "off" ]
            RecordMode.On

    let all: SlotInfo list = [ Slot.info record ]

/// The recording interpreters of the host's environment, for the composition root to put into its
/// environment type (`Wraps.Host`).
type HostTaps =
    {
        Clock: Clock -> Clock
        Llm: Llm -> Llm
    }

    override _.ToString() = "HostTaps"

/// The recording interpreters of the hands' environment (`Wraps.Hands`).
type HandsTaps =
    {
        Clock: Clock -> Clock
        Shell: Shell -> Shell
        FsRead: FsRead -> FsRead
        Fs: Fs -> Fs
        FsOps: FsOps -> FsOps
        /// Refuses: terminals are not recorded.
        Terminal: Fx.Pty.Terminal -> Fx.Pty.Terminal
    }

    override _.ToString() = "HandsTaps"

/// How the composition root puts recording interpreters into its environment types: every capability
/// the strategy's environments have (spec §4.2) must be replaced, or replay finds calls it has no
/// journal for. `{ env with Clock = taps.Clock env.Clock; … }`.
[<NoEquality; NoComparison>]
type Wraps<'R, 'K> =
    {
        Host: HostTaps -> 'R -> 'R
        Hands: HandsTaps -> 'K -> 'K
    }

    override _.ToString() = "Wraps"

/// What a kernel launch of an interpreter from a provider started from (its `InterpreterSession`), as
/// journaled before the launch: what replay needs to start the same kernel, the provider and its
/// runtime by id (never the values of the launcher's variables).
type LaunchInfo =
    {
        /// The interpreter provider's id.
        Provider: string
        Installed: InstalledComponent list
        AssetsRoot: string
        Target: TargetTriple
        Owned: OwnedDirs
        Workspace: string option
        ProjectTrusted: bool
        /// The launcher's variables, by name.
        HostEnvironment: string list
    }

    override this.ToString() =
        $"launch of {this.Provider} in {this.Owned.Root}"

[<RequireQualifiedAccess>]
module LaunchInfo =
    let private pairs: Codec<(string * string) list> =
        Codec.list (Codec.tuple2 Codec.string Codec.string)

    let private installed: Codec<InstalledComponent> =
        Codec.record {
            let! id = Codec.field "id" (fun (c: InstalledComponent) -> c.Id) Codec.string
            and! version = Codec.field "version" (fun c -> c.Version) SemVer.codec
            and! dir = Codec.field "dir" (fun c -> c.Dir) Codec.string
            and! bin = Codec.field "bin" (fun c -> c.Bin) pairs
            and! env = Codec.field "env" (fun c -> c.Env) pairs

            return
                {
                    Id = id
                    Version = version
                    Dir = dir
                    Bin = bin
                    Env = env
                }
        }

    let private owned: Codec<OwnedDirs> =
        Codec.record {
            let! root = Codec.field "root" (fun (o: OwnedDirs) -> o.Root) Codec.string
            and! cwd = Codec.field "cwd" (fun o -> o.Cwd) Codec.string
            and! home = Codec.field "home" (fun o -> o.Home) Codec.string
            and! config = Codec.field "config" (fun o -> o.Config) Codec.string
            and! cache = Codec.field "cache" (fun o -> o.Cache) Codec.string
            and! temp = Codec.field "temp" (fun o -> o.Temp) Codec.string

            return
                {
                    Root = root
                    Cwd = cwd
                    Home = home
                    Config = config
                    Cache = cache
                    Temp = temp
                }
        }

    let codec: Codec<LaunchInfo> =
        Codec.record {
            let! provider =
                Codec.field "provider" (fun (l: LaunchInfo) -> l.Provider) Codec.string

            and! components =
                Codec.field "installed" (fun l -> l.Installed) (Codec.list installed)

            and! assets = Codec.field "assetsRoot" (fun l -> l.AssetsRoot) Codec.string
            and! target = Codec.field "target" (fun l -> l.Target) TargetTriple.codec
            and! dirs = Codec.field "owned" (fun l -> l.Owned) owned
            and! workspace = Codec.optionalField "workspace" (fun l -> l.Workspace) Codec.string
            and! trusted = Codec.field "projectTrusted" (fun l -> l.ProjectTrusted) Codec.bool

            and! names =
                Codec.field "hostEnvironment" (fun l -> l.HostEnvironment) (Codec.list Codec.string)

            return
                {
                    Provider = provider
                    Installed = components
                    AssetsRoot = assets
                    Target = target
                    Owned = dirs
                    Workspace = workspace
                    ProjectTrusted = trusted
                    HostEnvironment = names
                }
        }

    let ofSession (session: InterpreterSession) : LaunchInfo =
        {
            Provider = session.Provider.Id
            Installed = session.Installed
            AssetsRoot = session.AssetsRoot
            Target = session.Launch.Target
            Owned = session.Launch.Owned
            Workspace = session.Launch.Workspace
            ProjectTrusted = session.Launch.ProjectTrusted
            HostEnvironment = session.Launch.HostEnvironment |> List.map fst
        }

    /// The session to start the same kernel with, from the provider and its runtime: the launcher's
    /// variables have empty values (replay starts no process).
    let session (provider: InterpreterProvider) (runtime: RuntimeSpec) (info: LaunchInfo) : InterpreterSession =
        {
            Provider = provider
            Runtime = runtime
            Installed = info.Installed
            AssetsRoot = info.AssetsRoot
            Launch =
                {
                    Target = info.Target
                    Owned = info.Owned
                    Workspace = info.Workspace
                    ProjectTrusted = info.ProjectTrusted
                    HostEnvironment = info.HostEnvironment |> List.map (fun name -> name, "")
                }
        }

/// What a session's hands were, as journaled: what replay needs to build the same hands (never the
/// values of the variables commands get).
type HandsInfo =
    {
        Workspace: string
        Os: Os
        /// `inherit` (`+name` set, `-name` removed) or `exactly`, by name.
        CommandEnv: string * string list
        Language: string
        Snapshots: bool
        /// The interpreter provider whose launches are journaled (`LaunchInfo`), for an interpreter
        /// from a provider.
        Interpreter: string option
        /// The sandbox's policy, when a driver confined the hands' processes: the prompt describes
        /// it (`Instructions.sandbox`), so replay renders the same.
        Sandbox: SandboxPolicy option
        /// The variables chosen for the commands (T138), by name and why one is missing: the prompt
        /// names them (`Instructions.variables`). Never values.
        Variables: HandsVariable list
    }

    override this.ToString() =
        $"hands ({this.Workspace}, {this.Os}, {this.Language})"

[<RequireQualifiedAccess>]
module HandsInfo =
    let private os: Codec<Os> =
        Codec.enum [ Os.Linux, "linux"; Os.MacOs, "macos"; Os.Windows, "windows" ]

    let codec: Codec<HandsInfo> =
        Codec.record {
            let! workspace =
                Codec.field "workspace" (fun (h: HandsInfo) -> h.Workspace) Codec.string

            and! hostOs = Codec.field "os" (fun h -> h.Os) os
            and! mode = Codec.field "envMode" (fun h -> fst h.CommandEnv) Codec.string

            and! names =
                Codec.field "envNames" (fun h -> snd h.CommandEnv) (Codec.list Codec.string)

            and! language = Codec.field "language" (fun h -> h.Language) Codec.string
            and! snapshots = Codec.field "snapshots" (fun h -> h.Snapshots) Codec.bool

            and! interpreter =
                Codec.optionalField "interpreter" (fun h -> h.Interpreter) Codec.string

            and! sandbox = Codec.optionalField "sandbox" (fun h -> h.Sandbox) SandboxPolicy.codec

            // Added with chosen variables (T138): absent when none, so earlier journals read the same.
            and! variables =
                let variable: Codec<HandsVariable> =
                    Codec.record {
                        let! name = Codec.field "name" (fun (v: HandsVariable) -> v.Name) Codec.string
                        and! missing = Codec.optionalField "missing" (fun v -> v.Missing) Codec.string
                        return { Name = name; Missing = missing }
                    }

                let variables = Codec.list variable

                Codec.fieldOf
                    { Field.withDefault "variables" [] variables with
                        Encode =
                            fun value ->
                                if List.isEmpty value then
                                    None
                                else
                                    Some(variables.Encode value)
                    }
                    (fun h -> h.Variables)

            return
                {
                    Workspace = workspace
                    Os = hostOs
                    CommandEnv = mode, names
                    Language = language
                    Snapshots = snapshots
                    Interpreter = interpreter
                    Sandbox = sandbox
                    Variables = variables
                }
        }

    let ofHands (hands: Hands<'K>) : HandsInfo =
        let env =
            match hands.CommandEnv with
            | Inherit changes ->
                "inherit",
                changes
                |> List.map (fun (name, value) -> (if value.IsSome then "+" else "-") + name)
            | Exactly vars -> "exactly", vars |> List.map fst

        {
            Workspace = hands.Workspace
            Os = hands.Os
            CommandEnv = env
            Language = hands.Interpreter.Language
            Snapshots = hands.Snapshots.IsSome
            Interpreter =
                hands.Interpreter.Launches
                |> Option.map (fun session -> (session 1).Provider.Id)
            Sandbox = hands.Sandbox
            Variables = hands.Variables
        }

    /// The variables commands get, by name, with empty values (replay runs no command).
    let commandEnv (info: HandsInfo) : EnvVars =
        match info.CommandEnv with
        | "exactly", names -> Exactly(names |> List.map (fun name -> name, ""))
        | _, names ->
            Inherit(
                names
                |> List.map (fun name ->
                    if name.StartsWith "+" then name.Substring 1, Some ""
                    elif name.StartsWith "-" then name.Substring 1, None
                    else name, Some "")
            )

/// The lanes and markers of a session's journal (docs/modules/replay.md).
[<RequireQualifiedAccess>]
module Markers =
    /// Turns: `Turn.Began`, `Turn.Cancelled`, `Turn.Ended`.
    [<Literal>]
    let Turns = "turns"

    /// The host: `Host.Closing`, `Host.Closed`.
    [<Literal>]
    let Host = "host"

    /// The hands of the session: their environment's capabilities, `Hands.Guard` and snapshots; each
    /// process they spawn has a lane of its own.
    [<Literal>]
    let Hands = "hands"

    /// `Hands`: the hands each turn got.
    [<Literal>]
    let HandsOpen = "hands.open"

    /// `Profile`, `Variants.Read`.
    [<Literal>]
    let Variants = "variants"

    /// The event store as the strategy uses it.
    [<Literal>]
    let Store = "store"

    /// `Strategy`: how the strategy was configured (whether it keeps large values in the blob store).
    [<Literal>]
    let Strategy = "strategy"

    [<Literal>]
    let Config = "Strategy.Config"

    [<Literal>]
    let Began = "Turn.Began"

    [<Literal>]
    let Cancelled = "Turn.Cancelled"

    [<Literal>]
    let Ended = "Turn.Ended"

    [<Literal>]
    let Closing = "Host.Closing"

    [<Literal>]
    let Closed = "Host.Closed"

    [<Literal>]
    let Boundary = "Turn.Boundary"

    [<Literal>]
    let Open = "Turn.Open"

    [<Literal>]
    let Await = "Turn.Await"

    [<Literal>]
    let OpenHands = "Hands"

    [<Literal>]
    let Profile = "Profile"

    [<Literal>]
    let Guard = "Hands.Guard"

    /// What a kernel launch starts from (`LaunchInfo`), before the launch.
    [<Literal>]
    let Launch = "Kernel.Launch"

    /// The lane of a turn: its host environment's clock and model, and its calls to the session.
    let turn (n: int) = $"turn/{n}"

    /// `Run` or `Recover`.
    let mode (recover: bool) = if recover then "recover" else "run"

    /// A turn's start, as `Turn.Began` journals it.
    let began (start: TurnStart) (recover: bool) : Json =
        Json.Object [ "start", Codecs.turnStart.Encode start; "mode", Json.String(mode recover) ]

    let turnOf (start: TurnStart) (recover: bool) =
        Inputs.many [ Json.int start.Turn; Json.String(mode recover) ]

    /// What `Run` or `Recover` returned.
    let result: Codec<Result<TurnResult, string> option> =
        Codec.option (Values.result Codecs.turnResult Codec.string)

    let openResult: Codec<Result<int64, Rejection>> =
        Values.result Codec.int64 Values.rejection

    /// What the guard let run that no grant allowed: `null` when nothing, as journals wrote the
    /// guard's `Ok` before approvals were kept (so they replay as they are).
    let guardApprovals: Codec<Fx.Prompt.Approval list> =
        Codec.option (Codec.list Fx.Prompt.ContextCodecs.approval)
        |> Codec.map (Option.defaultValue []) (fun approvals ->
            match approvals with
            | [] -> None
            | approvals -> Some approvals)

    let guardResult: Codec<Result<Fx.Prompt.Approval list, HostCallError>> =
        Values.result guardApprovals KernelProtocol.Codecs.hostCallError

    let handsResult: Codec<Result<HandsInfo, string>> =
        Values.result HandsInfo.codec Codec.string

/// One session's strategy in a recording host: its CodeAct instance (wrapped so every capability call is
/// journaled, or plain when the session is not recorded) and its journal.
[<NoEquality; NoComparison>]
type private Recorded<'R, 'K
    when 'R :> IClock
    and 'R :> IScopeWith<'R>
    and 'R :> ILlm
    and 'K :> IShell
    and 'K :> IFs
    and 'K :> IFsOps
    and 'K :> IClock
    and 'K :> IScopeWith<'K>> =
    {
        Instance: CodeAct<'R, 'K>
        Journal: SessionJournal option
        /// The host's environment of the latest turn (unwrapped): what the session's own calls (its
        /// profile, its hands) run in, with the caller's scope.
        Outer: 'R option ref
    }

/// The CodeAct strategy with every session's capability calls journaled (docs/modules/replay.md): the
/// composition root (T129) builds it instead of `CodeAct.create`, and runs sessions with its `Runner`.
[<Sealed>]
type RecordedCodeAct<'R, 'K
    when 'R :> IClock
    and 'R :> IScopeWith<'R>
    and 'R :> ILlm
    and 'K :> IShell
    and 'K :> IFs
    and 'K :> IFsOps
    and 'K :> IClock
    and 'K :> IScopeWith<'K>>(recorder: Recorder, wraps: Wraps<'R, 'K>, config: CodeActConfig<'R, 'K>) =
    let sessions =
        ConcurrentDictionary<string, Recorded<'R, 'K>>(StringComparer.Ordinal)

    let outerOf (cell: 'R option ref) (env: 'R) : 'R =
        match cell.Value with
        | Some outer -> outer.WithScope env.Scope
        | None -> env

    let wrapHands (journal: SessionJournal) (hands: Hands<'K>) : Hands<'K> =
        let lane = Markers.Hands

        let taps: HandsTaps =
            {
                Clock = Record.clock journal lane
                Shell = Record.shell journal lane
                FsRead = Record.fsRead journal lane
                Fs = Record.fs journal lane
                FsOps = Record.fsOps journal lane
                Terminal = Record.terminal
            }

        let original = hands.Env

        let interpreter = hands.Interpreter

        { hands with
            Env = wraps.Hands taps hands.Env
            Interpreter =
                match interpreter.Launches with
                | None -> interpreter
                | Some session ->
                    { interpreter with
                        Start =
                            fun start ->
                                fun env ->
                                    task {
                                        let! _ =
                                            journal.Run(
                                                lane,
                                                Markers.Launch,
                                                Inputs.int start.Launch,
                                                $"launch {start.Launch}",
                                                LaunchInfo.codec.Encode,
                                                fun () -> Task.FromResult(LaunchInfo.ofSession (session start.Launch))
                                            )

                                        return! interpreter.Start start env
                                    }
                    }
            Snapshots = hands.Snapshots |> Option.map (Record.snapshots journal lane)
            // Journaled at their level, with what they answered (T143): their processes run on the
            // hands' own (unrecorded) shell, and replay answers from these entries without one.
            Jobs =
                let inner (env: 'K) = original.WithScope env.Scope
                let jobs = hands.Jobs

                {
                    Start =
                        fun launch ->
                            fun env ->
                                journal.RunActing(
                                    lane,
                                    Ops.JobStart,
                                    Inputs.json (Values.jobLaunch launch),
                                    $"{launch.Id}: {launch.Command}",
                                    Values.jobStarted.Encode,
                                    fun () -> jobs.Start launch (inner env)
                                )
                    Info =
                        fun job ->
                            fun env ->
                                journal.Run(
                                    lane,
                                    Ops.JobInfo,
                                    Inputs.json (Values.jobRef job),
                                    job.Id,
                                    Values.jobInfo.Encode,
                                    fun () -> jobs.Info job (inner env)
                                )
                    List =
                        fun session ->
                            fun env ->
                                journal.Run(
                                    lane,
                                    Ops.JobList,
                                    Inputs.text session,
                                    "",
                                    Values.jobInfos.Encode,
                                    fun () -> jobs.List session (inner env)
                                )
                    Read =
                        fun read ->
                            fun env ->
                                journal.Run(
                                    lane,
                                    Ops.JobRead,
                                    Inputs.json (Values.jobRead read),
                                    read.Job.Id,
                                    Values.jobOutput.Encode,
                                    fun () -> jobs.Read read (inner env)
                                )
                    Wait =
                        fun wait ->
                            fun env ->
                                journal.Run(
                                    lane,
                                    Ops.JobWait,
                                    Inputs.json (Values.jobWait wait),
                                    wait.Job.Id,
                                    Values.jobInfo.Encode,
                                    fun () -> jobs.Wait wait (inner env)
                                )
                    Kill =
                        fun job ->
                            fun env ->
                                journal.RunActing(
                                    lane,
                                    Ops.JobKill,
                                    Inputs.json (Values.jobRef job),
                                    job.Id,
                                    Values.jobInfo.Encode,
                                    fun () -> jobs.Kill job (inner env)
                                )
                }
            Guard =
                fun call ->
                    fun env ->
                        journal.Run(
                            lane,
                            Markers.Guard,
                            Inputs.hostCall call,
                            call.Binding,
                            Markers.guardResult.Encode,
                            fun () -> hands.Guard call (original.WithScope env.Scope)
                        )
        }

    let recordedConfig (journal: SessionJournal) (outer: 'R option ref) : CodeActConfig<'R, 'K> =
        let log (schema: EventSchema<'E>) (typed: EventLog<'E>) =
            Record.log journal Markers.Store (EventSchema.encode schema) typed

        { config with
            Sessions = log SessionLog.schema config.Sessions
            Contexts = log ContextLog.schema config.Contexts
            Steps = log StepJournal.schema config.Steps
            Profile =
                fun profile ->
                    fun env ->
                        task {
                            let! variants =
                                journal.Run(
                                    Markers.Variants,
                                    Markers.Profile,
                                    Inputs.text profile,
                                    profile,
                                    (fun _ -> Json.Null),
                                    fun () -> config.Profile profile (outerOf outer env)
                                )

                            return Record.variants journal Markers.Variants profile variants
                        }
            Hands =
                fun start ->
                    fun env ->
                        task {
                            let! hands =
                                journal.Run(
                                    Markers.HandsOpen,
                                    Markers.OpenHands,
                                    Markers.turnOf start false,
                                    string start,
                                    (fun (result: Result<Hands<'K>, string>) ->
                                        Markers.handsResult.Encode(result |> Result.map HandsInfo.ofHands)),
                                    fun () -> config.Hands start (outerOf outer env)
                                )

                            return hands |> Result.map (wrapHands journal)
                        }
        }

    let context (journal: SessionJournal) (lane: string) (context: TurnContext<'R>) (original: 'R) : TurnContext<'R> =
        let outer (env: 'R) = original.WithScope env.Scope

        {
            Turn = context.Turn
            Boundary =
                fun step ->
                    fun env ->
                        journal.Run(
                            lane,
                            Markers.Boundary,
                            Inputs.int step,
                            $"step {step}",
                            (Codec.list Codecs.input).Encode,
                            fun () -> context.Boundary step (outer env)
                        )
            Open =
                fun request ->
                    fun env ->
                        journal.Run(
                            lane,
                            Markers.Open,
                            Inputs.json (Codecs.requestSpec.Encode request),
                            request.Id,
                            Markers.openResult.Encode,
                            fun () -> context.Open request (outer env)
                        )
            Await =
                fun request after ->
                    fun env ->
                        journal.Run(
                            lane,
                            Markers.Await,
                            Inputs.many [ Json.String request; Json.int64 after ],
                            request,
                            Codecs.resolution.Encode,
                            fun () -> context.Await request after (outer env)
                        )
        }

    /// Whether the session is recorded: `session.record` in the profile of its first turn here (its
    /// default when the host did not register the slot).
    let decide (start: TurnStart) (env: 'R) : Task<bool> =
        task {
            let! variants = config.Profile start.Profile env

            match variants.Read RecordingSlots.record.Id with
            | Some read ->
                match RecordingSlots.record.Codec.Decode read.Value with
                | Ok mode -> return mode = RecordMode.On
                | Error _ -> return true
            | None -> return RecordingSlots.record.Default = RecordMode.On
        }

    let sessionOf (start: TurnStart) (env: 'R) : Task<Recorded<'R, 'K>> =
        task {
            match sessions.TryGetValue start.Session with
            | true, existing -> return existing
            | _ ->
                let! record = decide start env
                let outer = ref None

                let created =
                    if record then
                        let journal = recorder.Session start.Session

                        journal.Mark(
                            Markers.Strategy,
                            Markers.Config,
                            Inputs.none,
                            "",
                            Json.Object [ "blobs", Json.Bool config.Blobs.IsSome ]
                        )

                        {
                            Instance = CodeAct.create (recordedConfig journal outer)
                            Journal = Some journal
                            Outer = outer
                        }
                    else
                        {
                            Instance = CodeAct.create config
                            Journal = None
                            Outer = outer
                        }

                return sessions.GetOrAdd(start.Session, created)
        }

    /// Runs a turn (or recovers one) with its journal: the start, the host's environment and the
    /// session's side wrapped, a cancellation journaled before it reaches the turn, the end, a flush.
    let turn (recover: bool) (context': TurnContext<'R>) (env: 'R) : Task<Result<TurnResult, string> option> =
        task {
            let start = context'.Turn
            let! session = sessionOf start env
            session.Outer.Value <- Some env

            match session.Journal with
            | None ->
                if recover then
                    return! session.Instance.Runner.Recover context' env
                else
                    let! result = session.Instance.Runner.Run context' env
                    return Some result
            | Some journal ->
                let lane = Markers.turn start.Turn
                let input = Markers.turnOf start recover
                journal.Mark(Markers.Turns, Markers.Began, input, string start, Markers.began start recover)
                use inner = new CancellationTokenSource()

                // Journaled before the turn sees it: replay cancels the turn when it reaches this point.
                let registration =
                    env.Scope.Cancellation.Register(fun () ->
                        journal.Mark(Markers.Turns, Markers.Cancelled, input, string start, Json.Null)
                        inner.Cancel())

                let scoped =
                    env.WithScope
                        { env.Scope with
                            Cancellation = inner.Token
                        }

                let taps: HostTaps =
                    {
                        Clock = Record.clock journal lane
                        Llm = Record.llm journal lane
                    }

                let wrapped = wraps.Host taps scoped
                let recorded = context journal lane context' scoped
                let call = journal.Start(Markers.Turns, Markers.Ended, input, string start)
                let mutable failure = None
                let mutable result = None

                try
                    if recover then
                        let! recovered = session.Instance.Runner.Recover recorded wrapped
                        result <- recovered
                    else
                        let! ran = session.Instance.Runner.Run recorded wrapped
                        result <- Some ran
                with e ->
                    failure <- Some e

                registration.Dispose()

                match failure with
                | Some e -> journal.Failed(call, e)
                | None -> journal.Returned(call, Markers.result.Encode result)

                do! journal.Flush()

                match failure with
                | Some e -> return Internal.rethrow e
                | None -> return result
        }

    /// The turn runner sessions run their turns with.
    member _.Runner: TurnRunner<'R> =
        {
            // Turns start at the root flow, whatever started them, as they do in replay.
            Run =
                fun context' ->
                    fun env ->
                        task {
                            let! result = Fx.Effects.Flow.root (fun () -> turn false context' env)
                            return result.Value
                        }
            Recover = fun context' -> fun env -> Fx.Effects.Flow.root (fun () -> turn true context' env)
        }

    /// The kernel of a session, if it has one.
    member _.Kernel(session: string) : Kernel option =
        match sessions.TryGetValue session with
        | true, recorded -> recorded.Instance.Kernel session
        | _ -> None

    /// Closes every session's kernel (the host stops) and ends the journals' segments.
    member _.Close() : Task =
        Fx.Effects.Flow.root (fun () ->
            task {
                for entry in List.ofSeq sessions do
                    match entry.Value.Journal with
                    | Some journal ->
                        journal.Mark(Markers.Host, Markers.Closing, Inputs.none, "", Json.Null)
                        do! entry.Value.Instance.Close()
                        journal.Mark(Markers.Host, Markers.Closed, Inputs.none, "", Json.Null)
                    | None -> do! entry.Value.Instance.Close()

                do! recorder.Close()
            })

    override _.ToString() = "RecordedCodeAct"

/// Recording (docs/modules/replay.md).
[<RequireQualifiedAccess>]
module Recording =
    /// A recorder that keeps journals in the host's event store (`journal/<session>`) and blob store.
    let recorder (log: EventLog<Payload>) (blobs: Blobs) (clock: Clock) : Recorder =
        Recorder(log, blobs, clock, RecorderSettings.defaults)

    /// The CodeAct strategy over `config`, with every recorded session's calls journaled.
    let codeact<'R, 'K
        when 'R :> IClock
        and 'R :> IScopeWith<'R>
        and 'R :> ILlm
        and 'K :> IShell
        and 'K :> IFs
        and 'K :> IFsOps
        and 'K :> IClock
        and 'K :> IScopeWith<'K>>
        (recorder: Recorder)
        (wraps: Wraps<'R, 'K>)
        (config: CodeActConfig<'R, 'K>)
        : RecordedCodeAct<'R, 'K> =
        RecordedCodeAct(recorder, wraps, config)
