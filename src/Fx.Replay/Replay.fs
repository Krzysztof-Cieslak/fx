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
open Fx.Variants

/// The host's environment of a replayed turn (spec §4.2 `ReplayEnv`): the clock and the model answer
/// from the journal.
type ReplayTurnEnv =
    {
        Clock: Clock
        Llm: Llm
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ILlm with
        member env.Llm = env.Llm

    interface IScopeWith<ReplayTurnEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"ReplayTurnEnv({env.Scope})"

/// The hands' environment in replay: files, commands, processes and the clock answer from the journal.
type ReplayHandsEnv =
    {
        Clock: Clock
        FsRead: FsRead
        Fs: Fs
        FsOps: FsOps
        Shell: Shell
        /// Where cut output goes (T201a): in memory, since a blob's reference depends only on its
        /// content, so replay stores what the recording stored without touching the world.
        Blobs: Blobs
        Ids: Ids
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

    interface IBlobs with
        member env.Blobs = env.Blobs

    interface IIds with
        member env.Ids = env.Ids

    interface IScopeWith<ReplayHandsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"ReplayHandsEnv({env.Scope})"

/// Replay interpreters (docs/modules/replay.md): each answers from a segment of the journal, checking
/// that every call matches a recorded one (operation and input digest), without touching the world.
[<RequireQualifiedAccess>]
module Replay =
    let private unitOf (_: Json) = ()

    let private fsResult (ok: Codec<'T>) = Values.result ok EnvCodecs.fsError

    let private answer (segment: ReplaySegment) lane op input summary (codec: Codec<'T>) cancellation : Task<'T> =
        segment.Answer(segment.Claim(lane, op, input, summary), Gate.Immediate, Decode.using codec, cancellation)

    let clock (segment: ReplaySegment) (lane: string) : Clock =
        {
            Now =
                fun () -> segment.AnswerSync(segment.Claim(lane, Ops.Now, Inputs.none, ""), Decode.using Values.instant)
            Monotonic =
                fun () ->
                    segment.AnswerSync(segment.Claim(lane, Ops.Monotonic, Inputs.none, ""), Decode.using Values.mono)
            // A delay the recording does not have is a timer that never fired there (whether a timeout
            // starts one can depend on timing): it waits for its cancellation.
            Delay =
                fun duration cancellation ->
                    match segment.Find(lane, Ops.Delay, Inputs.span duration) with
                    | Found.Entry item -> segment.Answer(Some item, Gate.Ordered, unitOf, cancellation)
                    | Found.Stopped -> Task.FromException<unit>(ReplayDivergedException "replay already diverged")
                    | Found.Missing _
                    | Found.Beyond -> segment.Answer(None, Gate.Ordered, unitOf, cancellation)
        }

    let ids (segment: ReplaySegment) (lane: string) : Ids =
        {
            NewId =
                fun () -> segment.AnswerSync(segment.Claim(lane, Ops.NewId, Inputs.none, ""), Decode.using Values.id)
            RandomBytes =
                fun count ->
                    segment.AnswerSync(
                        segment.Claim(lane, Ops.RandomBytes, Inputs.int count, string count),
                        Decode.using Values.bytes
                    )
        }

    let private chatStream (segment: ReplaySegment) (lane: string) : ChatStream =
        let gate = obj ()
        let pulls = segment.Claim(lane, Ops.Pulls, Inputs.none, "")

        let recorded =
            match pulls with
            | Some item ->
                match item.Value with
                | Some(Json.Array items) -> items |> List.map (Decode.using Pull.codec)
                | _ -> []
            | None -> []

        let mutable remaining = recorded
        let mutable closed = false

        {
            Next =
                fun cancellation ->
                    task {
                        match pulls with
                        | None ->
                            do! segment.UntilCancelled cancellation
                            return raise (OperationCanceledException cancellation)
                        | Some _ ->
                            let next =
                                lock gate (fun () ->
                                    match remaining with
                                    | pull :: rest ->
                                        remaining <- rest
                                        Some pull
                                    | [] -> None)

                            match next with
                            | Some(Pull.Event event) -> return Some event
                            | Some Pull.End -> return None
                            | Some(Pull.Threw name) -> return raise (ReplayedFailure name)
                            | Some Pull.Cancelled ->
                                do! segment.UntilCancelled cancellation
                                return raise (OperationCanceledException cancellation)
                            | None ->
                                return
                                    raise (
                                        segment.Diverge(
                                            lane,
                                            $"{recorded.Length} pulls",
                                            "another pull",
                                            "replay read more of a model's response than was recorded"
                                        )
                                    )
                    }
            Close =
                fun () ->
                    let first =
                        lock gate (fun () ->
                            let first = not closed
                            closed <- true
                            first)

                    match pulls with
                    | Some item when first -> segment.Complete item
                    | _ -> ()

                    Task.FromResult()
        }

    /// Answers each request with the recorded response, after checking that the request is the one
    /// recorded (its digest: the same canonical JSON).
    let llm (segment: ReplaySegment) (lane: string) : Llm =
        {
            Stream =
                fun request cancellation ->
                    task {
                        let summary = $"{request.Model} ({request.Messages.Length} messages)"
                        let item = segment.Claim(lane, Ops.Stream, Inputs.request request, summary)
                        let! () = segment.Answer(item, Gate.Immediate, unitOf, cancellation)
                        return chatStream segment (Lanes.stream lane item.Value.Entry.Index)
                    }
        }

    let private proc (segment: ReplaySegment) (lane: string) (pid: int) : Proc =
        {
            Pid = pid
            Write =
                fun bytes cancellation ->
                    answer segment lane Ops.Write (Inputs.bytes bytes) $"{bytes.Length} bytes" Codec.unit cancellation
            CloseStdin = fun () -> answer segment lane Ops.CloseStdin Inputs.none "" Codec.unit CancellationToken.None
            Read =
                fun cancellation ->
                    segment.Answer(
                        segment.Claim(lane, Ops.Read, Inputs.none, ""),
                        Gate.Ordered,
                        Decode.using Values.chunk,
                        cancellation
                    )
            Wait =
                fun cancellation ->
                    segment.Answer(
                        segment.Claim(lane, Ops.Wait, Inputs.none, ""),
                        Gate.Ordered,
                        Decode.using Codec.int,
                        cancellation
                    )
            Kill = fun () -> answer segment lane Ops.Kill Inputs.none "" Codec.unit CancellationToken.None
        }

    /// Commands answer with their recorded outputs; spawned processes give back what they wrote, in
    /// the order recorded, once what was written to them before has been written again.
    let shell (segment: ReplaySegment) (lane: string) : Shell =
        {
            Run =
                fun command cancellation ->
                    answer
                        segment
                        lane
                        Ops.Run
                        (Inputs.json (Values.commandInput command))
                        (string command.Spec.Program)
                        (Values.result Values.output Values.shellError)
                        cancellation
            Spawn =
                fun spec cancellation ->
                    task {
                        let item =
                            segment.Claim(lane, Ops.Spawn, Inputs.json (Values.procInput spec), string spec.Program)

                        let! started =
                            segment.Answer(
                                item,
                                Gate.Immediate,
                                Decode.using (Values.result Codec.int Values.shellError),
                                cancellation
                            )

                        return started |> Result.map (proc segment (Lanes.proc lane item.Value.Entry.Index))
                    }
        }

    let fsRead (segment: ReplaySegment) (lane: string) : FsRead =
        {
            ReadFile = fun path c -> answer segment lane Ops.ReadFile (Inputs.text path) path (fsResult Values.bytes) c
            Stat = fun path c -> answer segment lane Ops.Stat (Inputs.text path) path (fsResult EnvCodecs.entry) c
            List =
                fun path c ->
                    answer segment lane Ops.List (Inputs.text path) path (fsResult (Codec.list EnvCodecs.entry)) c
        }

    let fs (segment: ReplaySegment) (lane: string) : Fs =
        let unitResult = fsResult Codec.unit

        {
            WriteFile =
                fun path content c ->
                    answer
                        segment
                        lane
                        Ops.WriteFile
                        (Inputs.pathAndBytes path content)
                        $"{path} ({content.Length} bytes)"
                        unitResult
                        c
            CreateDirectory = fun path c -> answer segment lane Ops.CreateDirectory (Inputs.text path) path unitResult c
            Delete = fun path c -> answer segment lane Ops.Delete (Inputs.text path) path unitResult c
            Move =
                fun source target c ->
                    answer segment lane Ops.Move (Inputs.paths [ source; target ]) $"{source} -> {target}" unitResult c
        }

    let fsOps (segment: ReplaySegment) (lane: string) : FsOps =
        {
            StatMany =
                fun paths c ->
                    answer
                        segment
                        lane
                        Ops.StatMany
                        (Inputs.paths paths)
                        $"{paths.Length} paths"
                        (Codec.list (fsResult EnvCodecs.entry))
                        c
            ReadRange =
                fun path offset count c ->
                    answer
                        segment
                        lane
                        Ops.ReadRange
                        (Inputs.many [ Json.String path; Json.int64 offset; Json.int count ])
                        $"{path} @{offset}+{count}"
                        (fsResult Values.fileChunk)
                        c
            WriteAtomic =
                fun path content c ->
                    answer
                        segment
                        lane
                        Ops.WriteAtomic
                        (Inputs.pathAndBytes path content)
                        $"{path} ({content.Length} bytes)"
                        (fsResult Codec.unit)
                        c
            ApplyPatch =
                fun patches c ->
                    answer
                        segment
                        lane
                        Ops.ApplyPatch
                        (Inputs.json (Json.Array(patches |> List.map EnvCodecs.filePatch.Encode)))
                        (patches |> List.map (fun p -> p.Path) |> String.concat ", ")
                        (Values.result (Codec.list EnvCodecs.patchedFile) EnvCodecs.patchError)
                        c
            Search =
                fun query c ->
                    answer
                        segment
                        lane
                        Ops.Search
                        (Inputs.json (EnvCodecs.searchQuery.Encode query))
                        (string query)
                        (Values.result EnvCodecs.searchResult EnvCodecs.searchError)
                        c
            Watch =
                fun spec _ ->
                    Task.FromResult(Error(InvalidPath(spec.Root, "watching is not recorded (docs/modules/replay.md)")))
        }

    let snapshots (segment: ReplaySegment) (lane: string) : Snapshots =
        {
            Take =
                fun message c ->
                    answer
                        segment
                        lane
                        Ops.Take
                        (Inputs.text message)
                        message
                        (Values.result EnvCodecs.snapshotRef EnvCodecs.snapshotError)
                        c
            Diff =
                fun before after c ->
                    answer
                        segment
                        lane
                        Ops.Diff
                        (Inputs.many [ EnvCodecs.snapshotRef.Encode before; EnvCodecs.snapshotRef.Encode after ])
                        $"{before} -> {after}"
                        (Values.result (Codec.list EnvCodecs.fileChange) EnvCodecs.snapshotError)
                        c
            Patch =
                fun before after paths c ->
                    answer
                        segment
                        lane
                        Ops.Patch
                        (Inputs.many
                            [
                                EnvCodecs.snapshotRef.Encode before
                                EnvCodecs.snapshotRef.Encode after
                                Inputs.maybePaths paths
                            ])
                        $"{before} -> {after}"
                        (Values.result Codec.string EnvCodecs.snapshotError)
                        c
            Restore =
                fun snapshot paths c ->
                    answer
                        segment
                        lane
                        Ops.Restore
                        (Inputs.many [ EnvCodecs.snapshotRef.Encode snapshot; Inputs.maybePaths paths ])
                        (string snapshot)
                        (Values.result Codec.unit EnvCodecs.snapshotError)
                        c
        }

    /// Resolutions answer as recorded; a resolved secret's value is empty (it was never journaled, and
    /// nothing replayed sends it anywhere).
    let secrets (segment: ReplaySegment) (lane: string) : Secrets =
        {
            Resolve =
                fun reference cancellation ->
                    task {
                        let! resolution =
                            answer
                                segment
                                lane
                                Ops.Resolve
                                (Inputs.text (string reference))
                                (string reference)
                                Resolution.codec
                                cancellation

                        match resolution with
                        | Resolution.Resolved -> return Ok(Secret(reference, ""))
                        | Resolution.NotFound -> return Error(SecretNotFound reference)
                        | Resolution.Unavailable reason -> return Error(SecretUnavailable(reference, reason))
                    }
        }

    /// The recorded reads of a profile (each slot's first read in the segment; `cache` keeps them for
    /// the segment's later reads).
    let variants
        (segment: ReplaySegment)
        (lane: string)
        (cache: ConcurrentDictionary<string, Resolved option>)
        (profile: string)
        : Variants =
        {
            Read =
                fun slot ->
                    let key = profile + " " + slot.Value

                    cache.GetOrAdd(
                        key,
                        fun _ ->
                            segment.AnswerSync(
                                segment.Claim(lane, Ops.VariantRead, Inputs.variant profile slot, slot.Value),
                                Decode.using (Codec.option Resolved.codec)
                            )
                    )
        }

    /// The store as the replayed strategy sees it: the recorded session's own streams (`outputs`) are
    /// written again into `written` and checked against what was recorded; every read shows exactly
    /// what the recorded read saw (waiting for replay to have written it); other streams are read from
    /// the recording.
    ///
    /// Each call does its part (the write, the wait and the read) before its entry is done, and
    /// whatever its caller's cancellation says by then: the recorded call completed, and what was
    /// recorded after it (a turn's cancellation, which ends the host calls of its cell) may happen as
    /// soon as the entry is done. Otherwise an append could be lost in replay that the recording made.
    let store
        (segment: ReplaySegment)
        (lane: string)
        (recorded: EventLog<Payload>)
        (written: EventLog<Payload>)
        (outputs: StreamId list)
        (describeAppend: StreamId -> EventMeta -> Payload list -> string)
        : EventLog<Payload> =
        let isOutput (stream: StreamId) = List.contains stream outputs

        let source (stream: StreamId) =
            if isOutput stream then written else recorded

        /// Waits until replay wrote `head` events to an output stream (or the segment stops).
        let reached (stream: StreamId) (head: int64) : Task<unit> =
            task {
                if isOutput stream && head > 0L then
                    let! _ = written.WaitPast stream (head - 1L) segment.Stop
                    ()
            }

        let claimed (op: string) (input: string) (summary: string) = segment.Claim(lane, op, input, summary)

        {
            Append =
                fun stream expected meta events cancellation ->
                    task {
                        let summary = $"{stream} #{expected + 1L} {meta.Key.Value}"
                        let input = Inputs.append stream expected meta events

                        let item =
                            match segment.Find(lane, Ops.LogAppend, input) with
                            | Found.Entry item -> Some item
                            | Found.Beyond -> None
                            | Found.Stopped -> raise (ReplayDivergedException "replay already diverged")
                            | Found.Missing _ ->
                                raise (
                                    segment.Diverge(
                                        lane,
                                        describeAppend stream meta events,
                                        summary
                                        + " "
                                        + Json.serialize (Json.Array(events |> List.map (fun e -> e.Data))),
                                        "replay wrote something else"
                                    )
                                )

                        return!
                            segment.AnswerThen(
                                item,
                                Gate.Immediate,
                                Decode.using (Values.result Codec.int64 Values.appendError),
                                cancellation,
                                fun recordedResult ->
                                    task {
                                        let! result = written.Append stream expected meta events CancellationToken.None

                                        if result <> recordedResult then
                                            return
                                                raise (
                                                    segment.Diverge(
                                                        lane,
                                                        $"{summary} -> {recordedResult}",
                                                        $"{summary} -> {result}",
                                                        "an append ended differently"
                                                    )
                                                )
                                        else
                                            return result
                                    }
                            )
                    }
            Read =
                fun stream after max cancellation ->
                    segment.AnswerThen(
                        claimed Ops.LogRead (Inputs.read stream after max) $"{stream} after {after}",
                        Gate.Immediate,
                        Decode.using Codec.int,
                        cancellation,
                        fun count ->
                            task {
                                if count = 0 then
                                    return []
                                else
                                    do! reached stream (after + int64 count)
                                    return! (source stream).Read stream after count CancellationToken.None
                            }
                    )
            Head =
                fun stream cancellation ->
                    segment.AnswerThen(
                        claimed Ops.LogHead (Inputs.text stream.Value) stream.Value,
                        Gate.Immediate,
                        Decode.using Codec.int64,
                        cancellation,
                        fun head ->
                            task {
                                do! reached stream head
                                return head
                            }
                    )
            WaitPast =
                fun stream after cancellation ->
                    segment.AnswerThen(
                        claimed
                            Ops.LogWaitPast
                            (Inputs.many [ Json.String stream.Value; Json.int64 after ])
                            $"{stream} past {after}",
                        Gate.Immediate,
                        Decode.using Codec.int64,
                        cancellation,
                        fun head ->
                            task {
                                do! reached stream head
                                return head
                            }
                    )
            FindCommit =
                fun stream key cancellation ->
                    answer
                        segment
                        lane
                        Ops.LogFindCommit
                        (Inputs.many [ Json.String stream.Value; Json.String key.Value ])
                        $"{stream} {key.Value}"
                        (Codec.option Values.appended)
                        cancellation
        }

    /// A call whose answer depends on when it was made: it completes in the order recorded.
    let private ordered<'K, 'T when 'K :> IScope>
        (segment: ReplaySegment, lane: string, op: string, input: string, summary: string, codec: Codec<'T>)
        : Eff<'K, 'T> =
        fun env ->
            segment.Answer(
                segment.Claim(lane, op, input, summary),
                Gate.Ordered,
                Decode.using codec,
                env.Scope.Cancellation
            )

    /// The hands' background jobs (T143) answer as recorded: no process starts. A job's state and
    /// output depend on when they were asked, so those answers come in the order recorded.
    let jobs<'K when 'K :> IScope> (segment: ReplaySegment) (lane: string) : Fx.CodeAct.Jobs<'K> =
        {
            Start =
                fun launch ->
                    fun env ->
                        answer
                            segment
                            lane
                            Ops.JobStart
                            (Inputs.json (Values.jobLaunch launch))
                            $"{launch.Id}: {launch.Command}"
                            Values.jobStarted
                            env.Scope.Cancellation
            Info =
                fun job -> ordered (segment, lane, Ops.JobInfo, Inputs.json (Values.jobRef job), job.Id, Values.jobInfo)
            List = fun session -> ordered (segment, lane, Ops.JobList, Inputs.text session, "", Values.jobInfos)
            Read =
                fun read ->
                    ordered (
                        segment,
                        lane,
                        Ops.JobRead,
                        Inputs.json (Values.jobRead read),
                        read.Job.Id,
                        Values.jobOutput
                    )
            Wait =
                fun wait ->
                    ordered (segment, lane, Ops.JobWait, Inputs.json (Values.jobWait wait), wait.Job.Id, Values.jobInfo)
            Kill =
                fun job -> ordered (segment, lane, Ops.JobKill, Inputs.json (Values.jobRef job), job.Id, Values.jobInfo)
        }
