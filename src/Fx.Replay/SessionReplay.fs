namespace Fx.Replay

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Store
open Fx.Store.Fakes
open Fx.Envd
open Fx.Kernel
open Fx.Interpreters
open Fx.Sdk
open Fx.Sessions
open Fx.Sessions.Host
open Fx.CodeAct

/// A recorded session's stores: the host's event store (its session, context, step and journal streams)
/// and blob store. Replay only reads them.
[<NoEquality; NoComparison>]
type RecordedSession =
    {
        Log: EventLog<Payload>
        Blobs: Blobs
    }

    override _.ToString() = "RecordedSession"

/// What replay needs besides the recording: how the session's kernels start (the same interpreter the
/// host used, by the language its hands recorded), the model catalog, and how long replay may make no
/// progress before it reports a stall.
[<NoEquality; NoComparison>]
type ReplaySettings =
    {
        /// The interpreter of hands whose launches were not journaled (`HandsInfo.Interpreter` is
        /// `None`: not an interpreter from a provider).
        Interpreter: HandsInfo -> Interpreter<ReplayHandsEnv>
        /// The interpreter provider of an id and its runtime, as the host has them: a kernel whose
        /// launch was journaled (`LaunchInfo`) starts with them, from what the launch recorded.
        Provider: string -> Result<InterpreterProvider * RuntimeSpec, string>
        /// The options kernels start with (the host's).
        KernelOptions: KernelOptions
        Catalog: ModelRef -> ModelInfo option
        StallAfter: TimeSpan
    }

    override _.ToString() = "ReplaySettings"

/// What exact replay of a session found.
type ReplayReport =
    {
        Session: string
        /// Host processes that recorded it.
        Segments: int
        /// Turns replayed (a recovered turn counts again).
        Turns: int
        /// Journal entries replayed.
        Entries: int
        /// Model calls whose requests replay sent again, byte for byte (canonical JSON).
        Requests: int
        ContextEvents: int
        StepEvents: int
        /// The first difference; `None`: replay matched the recording.
        Divergence: Divergence option
        /// What replay matched last before it diverged (the journal entries it claimed and completed).
        Trace: string list
    }

    member this.Matched = this.Divergence.IsNone

    override this.ToString() =
        let head =
            $"session {this.Session}: {this.Segments} segments, {this.Turns} turns, {this.Entries} entries, {this.Requests} model requests, {this.ContextEvents} context events, {this.StepEvents} step events"

        match this.Divergence with
        | None -> head + "; replay matches the recording"
        | Some divergence ->
            head
            + "; replay diverged: "
            + string divergence
            + (if this.Trace.IsEmpty then
                   ""
               else
                   "\n  replay's last steps:\n    " + String.concat "\n    " this.Trace)

/// A segment of a session's journal as read back: its entries with their values (from the blob store
/// for those stored there), and whether the host closed it.
type JournalSegment =
    {
        Number: int
        Entries: (JournalEntry * Json option) list
        Ended: bool
    }

    override this.ToString() =
        $"segment {this.Number} ({this.Entries.Length} entries, ended: {this.Ended})"

/// Exact replay of a recorded CodeAct session (spec §15; docs/modules/replay.md): every turn runs again
/// on the CodeAct strategy with an environment that answers from the journal (`ReplayTurnEnv`,
/// `ReplayHandsEnv`), so nothing touches the world (no model, no kernel process, no files, no clock),
/// and what it writes is checked against the recording: every call's inputs, the model requests, the
/// context and step logs, the turns' outcomes.
[<RequireQualifiedAccess>]
module SessionReplay =
    let private eventOf (recorded: Recorded<Payload>) =
        Json.Object
            [
                "seq", Json.int64 recorded.Seq
                "at", Json.int64 recorded.Meta.At.UnixMicroseconds
                "by", Fx.Store.Codecs.attribution.Encode recorded.Meta.By
                "key", Json.String recorded.Meta.Key.Value
                "cause",
                (match recorded.Meta.Cause with
                 | Some cause -> Fx.Store.Codecs.eventRef.Encode cause
                 | None -> Json.Null)
                "payload", Fx.Store.Codecs.payload.Encode recorded.Event
            ]

    let private readAll (log: EventLog<'E>) (stream: StreamId) (cancellation: CancellationToken) =
        task {
            let all = ResizeArray<Recorded<'E>>()
            let mutable after = 0L
            let mutable reading = true

            while reading do
                let! page = log.Read stream after EventLog.PageSize cancellation

                match List.tryLast page with
                | Some last ->
                    all.AddRange page
                    after <- last.Seq
                | None -> reading <- false

            return List.ofSeq all
        }

    /// The segments of a session's journal, with values read back from the blob store.
    let segments
        (recording: RecordedSession)
        (session: string)
        (cancellation: CancellationToken)
        : Task<JournalSegment list> =
        task {
            let! events =
                readAll (JournalLog.typed recording.Log) (JournalLog.stream session) cancellation

            let segments = Dictionary<int, ResizeArray<JournalEntry> * bool ref>()
            let order = ResizeArray<int>()

            let segmentOf (n: int) =
                match segments.TryGetValue n with
                | true, s -> s
                | _ ->
                    let s = ResizeArray<JournalEntry>(), ref false
                    segments[n] <- s
                    order.Add n
                    s

            for recorded in events do
                match recorded.Event with
                | JournalEvent.Began n -> segmentOf n |> ignore
                | JournalEvent.Recorded(n, entries) ->
                    let list = fst (segmentOf n)

                    for entry in entries do
                        list.Add entry
                | JournalEvent.Ended n -> (snd (segmentOf n)).Value <- true

            let loaded = ResizeArray<JournalSegment>()

            for n in order do
                let entries, ended = segments[n]
                let withValues = ResizeArray<JournalEntry * Json option>()

                for entry in entries do
                    match entry.Outcome with
                    | Outcome.Returned(Stored.Inline json) -> withValues.Add(entry, Some json)
                    | Outcome.Returned(Stored.Blob blob) ->
                        match! recording.Blobs.Get blob.Hash cancellation with
                        | Ok bytes ->
                            match Json.parse (Text.Encoding.UTF8.GetString bytes) with
                            | Ok json -> withValues.Add(entry, Some json)
                            | Error _ -> withValues.Add(entry, None)
                        | Error _ -> withValues.Add(entry, None)
                    | _ -> withValues.Add(entry, None)

                loaded.Add
                    {
                        Number = n
                        Entries = List.ofSeq withValues
                        Ended = ended.Value
                    }

            return List.ofSeq loaded
        }

    /// Blobs replay writes go to a fresh store; reads fall back to the recording's.
    let private layered (fresh: Blobs) (recorded: Blobs) (puts: ConcurrentQueue<BlobRef>) : Blobs =
        {
            Put =
                fun bytes cancellation ->
                    task {
                        let! blob = fresh.Put bytes cancellation
                        puts.Enqueue blob
                        return blob
                    }
            Get =
                fun hash cancellation ->
                    task {
                        match! fresh.Get hash cancellation with
                        | Ok bytes -> return Ok bytes
                        | Error _ -> return! recorded.Get hash cancellation
                    }
            Read =
                fun hash offset count cancellation ->
                    task {
                        match! fresh.Read hash offset count cancellation with
                        | Ok bytes -> return Ok bytes
                        | Error _ -> return! recorded.Read hash offset count cancellation
                    }
            Stat =
                fun hash cancellation ->
                    task {
                        match! fresh.Stat hash cancellation with
                        | Some blob -> return Some blob
                        | None -> return! recorded.Stat hash cancellation
                    }
            Create = fresh.Create
        }

    /// Waits for a task, as an effect its scope can give up on (a timeout's race cancels it).
    let private within<'E when 'E :> IScope> (work: Task) : Eff<'E, unit> =
        Eff.ofCancellableTask (fun cancellation -> Internal.awaited (work.WaitAsync cancellation))

    /// Completes when either task does.
    let private either (a: Task) (b: Task) : Task<unit> =
        let done' =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        a.ContinueWith(fun (_: Task) -> done'.TrySetResult() |> ignore) |> ignore
        b.ContinueWith(fun (_: Task) -> done'.TrySetResult() |> ignore) |> ignore
        done'.Task

    /// Waits for `work` while replay makes progress; reports a stall otherwise.
    let private waitFor<'E when 'E :> IClock and 'E :> IScopeWith<'E>>
        (settings: ReplaySettings)
        (segment: ReplaySegment)
        (what: string)
        (work: Task)
        : Eff<'E, unit> =
        eff {
            let mutable waiting = true

            while waiting do
                if work.IsCompleted || segment.Divergence.IsSome then
                    waiting <- false
                else
                    let before = segment.Progress
                    let progressed = segment.Progressed

                    let! _ = Eff.timeout settings.StallAfter (within (either work progressed))

                    if not work.IsCompleted && segment.Progress = before && segment.Divergence.IsNone then
                        let expected =
                            match segment.Waiting with
                            | Some entry -> string entry.Entry
                            | None -> "nothing"

                        segment.Diverge(
                            "replay",
                            expected,
                            "waiting for " + what,
                            $"replay made no progress for {settings.StallAfter.TotalSeconds} s: it never made the call the recording has next"
                        )
                        |> ignore

                        waiting <- false
        }

    /// Completes once every entry of the segment completed.
    let private finished (segment: ReplaySegment) : Task =
        let done' = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let rec check () =
            let next = segment.Progressed

            if segment.Finished || segment.Stop.IsCancellationRequested then
                done'.TrySetResult() |> ignore
            else
                next.ContinueWith(fun (_: Task) -> check ()) |> ignore

        check ()
        done'.Task

    let private markerOf (entry: ReplayEntry) =
        match entry.Entry.Lane, entry.Entry.Op with
        | Markers.Turns, (Markers.Began | Markers.Cancelled) -> true
        | Markers.Host, Markers.Closing -> true
        | _ -> false

    let private exceptionName (e: exn) =
        match e with
        | :? ReplayedFailure as replayed -> replayed.ExceptionType
        | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
            aggregate.InnerExceptions[0].GetType().Name
        | e -> e.GetType().Name

    /// Where an exception was thrown: the top of its stack, for the report.
    let private where (e: exn) =
        match e.StackTrace with
        | null -> ""
        | stack ->
            let frames =
                stack.Split('\n')
                |> Array.map (fun line -> line.Trim())
                |> Array.filter (fun line -> line <> "")
                |> Array.truncate 6

            "\n    " + String.Join("\n    ", frames)

    /// Checks how a replayed turn ended against `Turn.Ended`.
    let private verifyEnd
        (segment: ReplaySegment)
        (input: string)
        (label: string)
        (outcome: Result<Result<TurnResult, string> option, exn>)
        =
        match outcome with
        | Error(:? ReplayDivergedException) -> ()
        | _ ->
            match segment.Find(Markers.Turns, Markers.Ended, input) with
            | Found.Entry item ->
                let recorded = item.Entry.Outcome

                let matches, actual =
                    match recorded, outcome, item.Value with
                    | Outcome.Returned _, Ok result, Some value ->
                        let replayed = Markers.result.Encode result
                        Json.serializeCanonical replayed = Json.serializeCanonical value, Json.serialize replayed
                    | Outcome.Threw name, Error e, _ -> exceptionName e = name, "threw " + exceptionName e
                    | Outcome.Cancelled, Error(:? OperationCanceledException), _ -> true, "cancelled"
                    | Outcome.Pending, Error(:? OperationCanceledException), _ ->
                        true, "still running when the host closed"
                    | _, Ok result, _ -> false, Json.serialize (Markers.result.Encode result)
                    | _, Error e, _ -> false, "threw " + exceptionName e + where e

                if matches then
                    segment.Complete item
                else
                    segment.Diverge(Markers.Turns, string item.Entry, actual, $"{label} ended differently")
                    |> ignore
            | _ -> ()

    /// Replays one segment on a fresh strategy instance.
    let private replaySegment<'E when 'E :> IClock and 'E :> IScopeWith<'E>>
        (settings: ReplaySettings)
        (recording: RecordedSession)
        (written: InMemoryEventLog<Payload>)
        (blobs: Blobs)
        (session: string)
        (loaded: JournalSegment)
        : Eff<'E, ReplaySegment * int> =
        eff {
            let segment = ReplaySegment(loaded.Number, loaded.Entries, loaded.Ended)

            let cache =
                ConcurrentDictionary<string, Fx.Variants.Resolved option>(StringComparer.Ordinal)

            let outputs = [ ContextLog.stream session; StepJournal.stream session ]

            let describeAppend (stream: StreamId) (meta: EventMeta) (_: Payload list) =
                let recorded =
                    (recording.Log.Read stream 0L Int32.MaxValue CancellationToken.None).GetAwaiter().GetResult()
                    |> List.tryFind (fun r -> r.Meta.Key = meta.Key)

                match recorded with
                | Some r -> $"{stream} #{r.Seq} {r.Meta.Key.Value} {Json.serialize (eventOf r)}"
                | None -> $"{stream}: no event with the key {meta.Key.Value}"

            let store =
                Replay.store segment Markers.Store recording.Log written.Log outputs describeAppend

            let handsEnv: ReplayHandsEnv =
                {
                    Clock = Replay.clock segment Markers.Hands
                    FsRead = Replay.fsRead segment Markers.Hands
                    Fs = Replay.fs segment Markers.Hands
                    FsOps = Replay.fsOps segment Markers.Hands
                    Shell = Replay.shell segment Markers.Hands
                    Blobs = Fx.Store.Fakes.InMemoryBlobs().Blobs
                    Scope = Scope.ofCancellation segment.Stop
                }

            let blobsUsed =
                segment.Entries
                |> List.tryPick (fun e ->
                    if e.Entry.Lane = Markers.Strategy && e.Entry.Op = Markers.Config then
                        segment.ClaimMarker e
                        segment.Complete e

                        match e.Value with
                        | Some(Json.Object members) ->
                            members
                            |> List.tryPick (fun (name, value) ->
                                match name, value with
                                | "blobs", Json.Bool used -> Some used
                                | _ -> None)
                        | _ -> None
                    else
                        None)
                |> Option.defaultValue true

            // An interpreter from a provider: each launch starts from what its recording journaled.
            let launched (provider: InterpreterProvider) : Interpreter<ReplayHandsEnv> =
                {
                    Language = provider.Language
                    Prompt = Fx.CodeAct.Interpreter.prompt provider
                    Start =
                        fun start ->
                            fun env ->
                                task {
                                    let! info =
                                        segment.Answer(
                                            segment.Claim(
                                                Markers.Hands,
                                                Markers.Launch,
                                                Inputs.int start.Launch,
                                                $"launch {start.Launch}"
                                            ),
                                            Gate.Immediate,
                                            Decode.using LaunchInfo.codec,
                                            env.Scope.Cancellation
                                        )

                                    match settings.Provider info.Provider with
                                    | Error e -> return Error e
                                    | Ok(provider, runtime) ->
                                        let session = LaunchInfo.session provider runtime info

                                        let interpreter =
                                            Fx.CodeAct.Interpreter.ofProvider settings.KernelOptions (fun _ -> session)

                                        return! interpreter.Start start env
                                }
                    Launches = None
                }

            let interpreterOf (info: HandsInfo) : Result<Interpreter<ReplayHandsEnv>, string> =
                match info.Interpreter with
                | None -> Ok(settings.Interpreter info)
                | Some id -> settings.Provider id |> Result.map (fst >> launched)

            let config: CodeActConfig<ReplayTurnEnv, ReplayHandsEnv> =
                {
                    Sessions = SessionLog.typed store
                    Contexts = ContextLog.typed store
                    Steps = StepJournal.typed store
                    Blobs = if blobsUsed then Some blobs else None
                    Profile =
                        fun profile ->
                            fun env ->
                                task {
                                    do!
                                        segment.Answer(
                                            segment.Claim(
                                                Markers.Variants,
                                                Markers.Profile,
                                                Inputs.text profile,
                                                profile
                                            ),
                                            Gate.Immediate,
                                            ignore,
                                            env.Scope.Cancellation
                                        )

                                    return Replay.variants segment Markers.Variants cache profile
                                }
                    Hands =
                        fun start ->
                            fun env ->
                                task {
                                    let! info =
                                        segment.Answer(
                                            segment.Claim(
                                                Markers.HandsOpen,
                                                Markers.OpenHands,
                                                Markers.turnOf start false,
                                                string start
                                            ),
                                            Gate.Immediate,
                                            Decode.using Markers.handsResult,
                                            env.Scope.Cancellation
                                        )

                                    return
                                        info
                                        |> Result.bind (fun info ->
                                            interpreterOf info |> Result.map (fun interpreter -> info, interpreter))
                                        |> Result.map (fun (info, interpreter) ->
                                            {
                                                Env = handsEnv
                                                Workspace = info.Workspace
                                                Os = info.Os
                                                CommandEnv = HandsInfo.commandEnv info
                                                Interpreter = interpreter
                                                Guard =
                                                    fun call ->
                                                        fun env ->
                                                            segment.Answer(
                                                                segment.Claim(
                                                                    Markers.Hands,
                                                                    Markers.Guard,
                                                                    Inputs.hostCall call,
                                                                    call.Binding
                                                                ),
                                                                // It ends when people answered (grants).
                                                                Gate.Ordered,
                                                                Decode.using Markers.guardResult,
                                                                env.Scope.Cancellation
                                                            )
                                                Snapshots =
                                                    if info.Snapshots then
                                                        Some(Replay.snapshots segment Markers.Hands)
                                                    else
                                                        None
                                                Sandbox = info.Sandbox
                                                Variables = info.Variables
                                                BlobRoot = info.BlobRoot
                                                Jobs = Replay.jobs segment Markers.Hands
                                            })
                                }
                    Catalog = settings.Catalog
                }

            let codeact = CodeAct.create config

            let contextOf (start: TurnStart) (lane: string) : TurnContext<ReplayTurnEnv> =
                {
                    Turn = start
                    Boundary =
                        fun step ->
                            fun env ->
                                segment.Answer(
                                    segment.Claim(lane, Markers.Boundary, Inputs.int step, $"step {step}"),
                                    Gate.Ordered,
                                    Decode.using (Codec.list Codecs.input),
                                    env.Scope.Cancellation
                                )
                    Open =
                        fun request ->
                            fun env ->
                                segment.Answer(
                                    segment.Claim(
                                        lane,
                                        Markers.Open,
                                        Inputs.json (Codecs.requestSpec.Encode request),
                                        request.Id
                                    ),
                                    Gate.Ordered,
                                    Decode.using Markers.openResult,
                                    env.Scope.Cancellation
                                )
                    Await =
                        fun request after ->
                            fun env ->
                                segment.Answer(
                                    segment.Claim(
                                        lane,
                                        Markers.Await,
                                        Inputs.many [ Json.String request; Json.int64 after ],
                                        request
                                    ),
                                    Gate.Ordered,
                                    Decode.using Codecs.resolution,
                                    env.Scope.Cancellation
                                )
                }

            let running = Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal)
            let work = ResizeArray<Task>()
            let turns = ref 0

            let startTurn (marker: ReplayEntry) =
                let json = marker.Value |> Option.defaultValue Json.Null

                let start, recover =
                    match json with
                    | Json.Object members ->
                        let find name =
                            members |> List.tryPick (fun (n, v) -> if n = name then Some v else None)

                        match find "start", find "mode" with
                        | Some start, Some(Json.String mode) ->
                            Decode.using Codecs.turnStart start, mode = Markers.mode true
                        | _ -> raise (ReplayDivergedException "a turn's start does not decode")
                    | _ -> raise (ReplayDivergedException "a turn's start does not decode")

                let cancel = CancellationTokenSource.CreateLinkedTokenSource segment.Stop
                running[marker.Entry.Input] <- cancel
                turns.Value <- turns.Value + 1
                let lane = Markers.turn start.Turn

                let env: ReplayTurnEnv =
                    {
                        Clock = Replay.clock segment lane
                        Llm = Replay.llm segment lane
                        Scope = Scope.ofCancellation cancel.Token
                    }

                let context = contextOf start lane
                let label = $"turn {start.Turn} ({Markers.mode recover})"

                // Turns start at the root flow, as recorded turns did.
                let run: Task<Result<TurnResult, string> option> =
                    Flow.root (fun () ->
                        if recover then
                            Eff.run env (codeact.Runner.Recover context)
                        else
                            Eff.run env (codeact.Runner.Run context |> Eff.map Some))

                let finished =
                    run.ContinueWith(fun (ran: Task<Result<TurnResult, string> option>) ->
                        let outcome =
                            if ran.IsCompletedSuccessfully then
                                Ok ran.Result
                            elif ran.IsCanceled then
                                Error(OperationCanceledException() :> exn)
                            else
                                match ran.Exception with
                                | null -> Error(Exception "unknown")
                                | aggregate when aggregate.InnerExceptions.Count = 1 ->
                                    Error aggregate.InnerExceptions[0]
                                | aggregate -> Error(aggregate :> exn)

                        Flow.root (fun () -> verifyEnd segment marker.Entry.Input label outcome))

                work.Add finished

            let close () =
                let closing =
                    (Flow.root (fun () -> codeact.Close()))
                        .ContinueWith(fun (_: Task) ->
                            Flow.root (fun () ->
                                match segment.Find(Markers.Host, Markers.Closed, Inputs.none) with
                                | Found.Entry item -> segment.Complete item
                                | _ -> ()))

                work.Add closing

            let markers = segment.Entries |> List.filter markerOf

            for marker in markers do
                if segment.Divergence.IsNone then
                    do! waitFor settings segment $"{marker.Entry}" (segment.Before marker)

                    if segment.Divergence.IsNone then
                        segment.ClaimMarker marker
                        segment.Complete marker

                        match marker.Entry.Op with
                        | Markers.Began -> startTurn marker
                        | Markers.Cancelled ->
                            match running.TryGetValue marker.Entry.Input with
                            | true, cancel -> cancel.Cancel()
                            | _ -> ()
                        | _ -> close ()

            // Everything the segment recorded happens, then the segment stops: what is still waiting
            // (a host that stopped without closing) gives up.
            if segment.Divergence.IsNone then
                do! waitFor settings segment "the segment's last calls" (finished segment)

            segment.End()

            for cancel in running.Values do
                cancel.Cancel()

            for task in List.ofSeq work do
                let! _ = Eff.timeout settings.StallAfter (within task)

                ()

            if not loaded.Ended then
                // The host stopped without closing: its kernels died with it.
                let! _ =
                    Eff.timeout settings.StallAfter (within (Flow.root (fun () -> codeact.Close())))

                ()

            return segment, turns.Value
        }

    /// Compares the streams replay wrote with the recorded ones, event by event.
    let private compare
        (session: string)
        (stream: StreamId)
        (recorded: Recorded<Payload> list)
        (replayed: Recorded<Payload> list)
        =
        let rec first (a: Recorded<Payload> list) (b: Recorded<Payload> list) =
            match a, b with
            | [], [] -> None
            | r :: _, [] ->
                Some
                    {
                        Segment = 0
                        Lane = string stream
                        Expected = Json.serialize (eventOf r)
                        Actual = "nothing"
                        Reason = "replay did not write this event"
                    }
            | [], p :: _ ->
                Some
                    {
                        Segment = 0
                        Lane = string stream
                        Expected = "nothing"
                        Actual = Json.serialize (eventOf p)
                        Reason = "replay wrote an event the recording does not have"
                    }
            | r :: rs, p :: ps ->
                let a' = Json.serializeCanonical (eventOf r)
                let b' = Json.serializeCanonical (eventOf p)

                if a' = b' then
                    first rs ps
                else
                    Some
                        {
                            Segment = 0
                            Lane = string stream
                            Expected = a'
                            Actual = b'
                            Reason = "replay wrote a different event"
                        }

        first recorded replayed
        |> Option.map (fun d -> { d with Lane = $"{session}: {d.Lane}" })

    /// Replays a recorded session exactly and reports the first difference, if any.
    let run<'E when 'E :> IClock and 'E :> IScopeWith<'E>>
        (settings: ReplaySettings)
        (recording: RecordedSession)
        (session: string)
        : Eff<'E, ReplayReport> =
        eff {
            let! cancellation = Eff.cancellation
            let! segments = Eff.ofTask (fun () -> segments recording session cancellation)
            let written = InMemoryEventLog<Payload>()
            let puts = ConcurrentQueue<BlobRef>()
            let blobs = layered (InMemoryBlobs()).Blobs recording.Blobs puts
            let mutable divergence = None
            let mutable turns = 0
            let mutable entries = 0
            let mutable requests = 0
            let mutable trace = []

            if segments.IsEmpty then
                divergence <-
                    Some
                        {
                            Segment = 0
                            Lane = "journal"
                            Expected = $"a journal for {session}"
                            Actual = "none"
                            Reason = "the session was not recorded (session.record, docs/modules/replay.md)"
                        }

            for loaded in segments do
                if divergence.IsNone then
                    let! segment, replayed =
                        replaySegment settings recording written blobs session loaded

                    turns <- turns + replayed
                    entries <- entries + loaded.Entries.Length

                    requests <-
                        requests
                        + (loaded.Entries |> List.filter (fun (e, _) -> e.Op = Ops.Stream) |> List.length)

                    divergence <- segment.Divergence

                    if divergence.IsSome then
                        let all = segment.Trace
                        trace <- all |> List.skip (max 0 (all.Length - 60))

            let contexts = ContextLog.stream session
            let steps = StepJournal.stream session

            let! recordedContext =
                Eff.ofTask (fun () -> readAll recording.Log contexts cancellation)

            let! recordedSteps = Eff.ofTask (fun () -> readAll recording.Log steps cancellation)

            if divergence.IsNone then
                divergence <- compare session contexts recordedContext (written.Events contexts)

            if divergence.IsNone then
                divergence <- compare session steps recordedSteps (written.Events steps)

            if divergence.IsNone then
                for blob in puts do
                    if divergence.IsNone then
                        let! found = Eff.ofTask (fun () -> recording.Blobs.Stat blob.Hash cancellation)

                        if found.IsNone then
                            divergence <-
                                Some
                                    {
                                        Segment = 0
                                        Lane = "blobs"
                                        Expected = "the blobs the recording kept"
                                        Actual = string blob
                                        Reason = "replay kept a value the recording did not"
                                    }

            // Turns the session log has that no journal covers (recorded with session.record off).
            if divergence.IsNone then
                let! events =
                    Eff.ofTask (fun () -> readAll recording.Log (Session.stream session) cancellation)

                let started =
                    events
                    |> List.choose (fun r ->
                        match EventSchema.decode SessionLog.schema r.Event with
                        | Ok(Event.TurnStarted(turn, _, _, _)) -> Some turn
                        | _ -> None)

                let began =
                    segments
                    |> List.collect (fun s -> s.Entries)
                    |> List.filter (fun (e, _) -> e.Lane = Markers.Turns && e.Op = Markers.Began)
                    |> List.length

                if began < started.Length then
                    divergence <-
                        Some
                            {
                                Segment = 0
                                Lane = "journal"
                                Expected = $"{started.Length} turns"
                                Actual = $"{began} recorded"
                                Reason = "some of the session's turns were not recorded"
                            }

            return
                {
                    Session = session
                    Segments = segments.Length
                    Turns = turns
                    Entries = entries
                    Requests = requests
                    ContextEvents = recordedContext.Length
                    StepEvents = recordedSteps.Length
                    Divergence = divergence
                    Trace = trace
                }
        }
