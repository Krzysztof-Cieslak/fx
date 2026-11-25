namespace Fx.Replay

open System
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

/// What `ChatStream.Next` gave: one pull of a model's response.
[<RequireQualifiedAccess>]
type Pull =
    | Event of ChatEvent
    | End
    | Cancelled
    | Threw of exceptionType: string

    override this.ToString() =
        match this with
        | Event event -> string event
        | End -> "end"
        | Cancelled -> "cancelled"
        | Threw name -> "threw " + name

[<RequireQualifiedAccess>]
module Pull =
    let codec: Codec<Pull> =
        Codec.union "kind" {
            let! event = Codec.case1 "event" Pull.Event (Field.required "event" ChatEvent.codec)
            and! ended = Codec.case0 "end" Pull.End
            and! cancelled = Codec.case0 "cancelled" Pull.Cancelled
            and! threw = Codec.case1 "threw" Pull.Threw (Field.required "type" Codec.string)

            return
                fun pull ->
                    match pull with
                    | Pull.Event e -> event e
                    | Pull.End -> ended
                    | Pull.Cancelled -> cancelled
                    | Pull.Threw name -> threw name
        }

/// What a secret's resolution gave, as journaled: never its value.
[<RequireQualifiedAccess>]
type Resolution =
    | Resolved
    | NotFound
    | Unavailable of reason: string

    override this.ToString() =
        match this with
        | Resolved -> "resolved"
        | NotFound -> "not found"
        | Unavailable reason -> "unavailable: " + reason

[<RequireQualifiedAccess>]
module Resolution =
    let codec: Codec<Resolution> =
        Codec.union "kind" {
            let! resolved = Codec.case0 "resolved" Resolution.Resolved
            and! notFound = Codec.case0 "notFound" Resolution.NotFound

            and! unavailable =
                Codec.case1 "unavailable" Resolution.Unavailable (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | Resolution.Resolved -> resolved
                    | Resolution.NotFound -> notFound
                    | Resolution.Unavailable reason -> unavailable reason
        }

    let ofResult (result: Result<Secret, SecretError>) : Resolution =
        match result with
        | Ok _ -> Resolution.Resolved
        | Error(SecretNotFound _) -> Resolution.NotFound
        | Error(SecretUnavailable(_, reason)) -> Resolution.Unavailable reason

/// The operations the journal names (`Entry.Op`).
[<RequireQualifiedAccess>]
module Ops =
    [<Literal>]
    let Now = "Clock.Now"

    [<Literal>]
    let Monotonic = "Clock.Monotonic"

    [<Literal>]
    let Delay = "Clock.Delay"

    [<Literal>]
    let NewId = "Ids.NewId"

    [<Literal>]
    let RandomBytes = "Ids.RandomBytes"

    [<Literal>]
    let Stream = "Llm.Stream"

    [<Literal>]
    let Pulls = "Stream.Pulls"

    [<Literal>]
    let Run = "Shell.Run"

    [<Literal>]
    let Spawn = "Shell.Spawn"

    [<Literal>]
    let Write = "Proc.Write"

    [<Literal>]
    let CloseStdin = "Proc.CloseStdin"

    [<Literal>]
    let Read = "Proc.Read"

    [<Literal>]
    let Wait = "Proc.Wait"

    [<Literal>]
    let Kill = "Proc.Kill"

    [<Literal>]
    let ReadFile = "Fs.ReadFile"

    [<Literal>]
    let Stat = "Fs.Stat"

    [<Literal>]
    let List = "Fs.List"

    [<Literal>]
    let WriteFile = "Fs.WriteFile"

    [<Literal>]
    let CreateDirectory = "Fs.CreateDirectory"

    [<Literal>]
    let Delete = "Fs.Delete"

    [<Literal>]
    let Move = "Fs.Move"

    [<Literal>]
    let StatMany = "FsOps.StatMany"

    [<Literal>]
    let ReadRange = "FsOps.ReadRange"

    [<Literal>]
    let WriteAtomic = "FsOps.WriteAtomic"

    [<Literal>]
    let ApplyPatch = "FsOps.ApplyPatch"

    [<Literal>]
    let Search = "FsOps.Search"

    [<Literal>]
    let Take = "Snapshots.Take"

    [<Literal>]
    let Diff = "Snapshots.Diff"

    [<Literal>]
    let Patch = "Snapshots.Patch"

    [<Literal>]
    let Restore = "Snapshots.Restore"

    [<Literal>]
    let Resolve = "Secrets.Resolve"

    [<Literal>]
    let VariantRead = "Variants.Read"

    [<Literal>]
    let LogAppend = "Log.Append"

    [<Literal>]
    let LogRead = "Log.Read"

    [<Literal>]
    let LogHead = "Log.Head"

    [<Literal>]
    let LogWaitPast = "Log.WaitPast"

    [<Literal>]
    let LogFindCommit = "Log.FindCommit"

/// Inputs as the journal identifies them (digests), shared by recording and replay.
[<RequireQualifiedAccess>]
module Inputs =
    let none = ""
    let json (value: Json) = Digest.json value
    let many (values: Json list) = Digest.json (Json.Array values)
    let text (value: string) = Digest.json (Json.String value)
    let int (value: int) = Digest.json (Json.int value)
    let span (value: TimeSpan) = Digest.json (Json.int64 value.Ticks)
    let bytes (value: byte array) = Digest.bytes value

    let pathAndBytes (path: string) (content: byte array) =
        many [ Json.String path; Json.String(Digest.bytes content) ]

    let paths (paths: string list) = many (paths |> List.map Json.String)

    let maybePaths (paths: string list option) =
        match paths with
        | Some paths -> Json.Array(paths |> List.map Json.String)
        | None -> Json.Null

    let request (request: ChatRequest) =
        Digest.json (ChatRequest.codec.Encode request)

    let hostCall (call: HostCall) =
        Digest.json (KernelProtocol.Codecs.hostCall.Encode call)

    let meta (meta: EventMeta) =
        Json.Object
            [
                "at", Json.int64 meta.At.UnixMicroseconds
                "by", Fx.Store.Codecs.attribution.Encode meta.By
                "key", Json.String meta.Key.Value
                "cause",
                (match meta.Cause with
                 | Some cause -> Fx.Store.Codecs.eventRef.Encode cause
                 | None -> Json.Null)
            ]

    let append (stream: StreamId) (expected: int64) (eventMeta: EventMeta) (events: Payload list) =
        many
            [
                Json.String stream.Value
                Json.int64 expected
                meta eventMeta
                Json.Array(events |> List.map Fx.Store.Codecs.payload.Encode)
            ]

    let read (stream: StreamId) (after: int64) (max: int) =
        many [ Json.String stream.Value; Json.int64 after; Json.int max ]

    let variant (profile: string) (slot: SlotId) =
        many [ Json.String profile; Json.String slot.Value ]

/// Lanes derived from a call: a spawned process's, a model response's.
[<RequireQualifiedAccess>]
module Lanes =
    /// The lane of a process: one per spawn, named by the spawn's index in its shell's lane.
    let proc (shellLane: string) (spawnIndex: int64) = $"{shellLane}/proc/{spawnIndex}"

    /// The lane of a model response, named by the call's index in its model's lane.
    let stream (llmLane: string) (callIndex: int64) = $"{llmLane}/stream/{callIndex}"

/// Recording interpreters (docs/modules/replay.md): each wraps a real interpreter and journals every
/// call in a lane of a session's journal, with the digest of its inputs and its outcome. Secret values
/// are never journaled: processes' variables by name only, `Secrets` by reference and outcome.
[<RequireQualifiedAccess>]
module Record =
    let private unitJson (_: unit) = Json.Null

    let private fsResult (ok: Codec<'T>) = Values.result ok EnvCodecs.fsError

    let private encodeWith (codec: Codec<'T>) : 'T -> Json = codec.Encode

    let clock (journal: SessionJournal) (lane: string) (clock: Clock) : Clock =
        {
            Now = fun () -> journal.RunSync(lane, Ops.Now, Inputs.none, "", Values.instant.Encode, clock.Now)
            Monotonic =
                fun () -> journal.RunSync(lane, Ops.Monotonic, Inputs.none, "", Values.mono.Encode, clock.Monotonic)
            Delay =
                fun duration cancellation ->
                    journal.Run(
                        lane,
                        Ops.Delay,
                        Inputs.span duration,
                        $"{duration.TotalMilliseconds} ms",
                        unitJson,
                        fun () -> clock.Delay duration cancellation
                    )
        }

    let ids (journal: SessionJournal) (lane: string) (ids: Ids) : Ids =
        {
            NewId = fun () -> journal.RunSync(lane, Ops.NewId, Inputs.none, "", Values.id.Encode, ids.NewId)
            RandomBytes =
                fun count ->
                    journal.RunSync(
                        lane,
                        Ops.RandomBytes,
                        Inputs.int count,
                        string count,
                        Values.bytes.Encode,
                        fun () -> Array.copy (ids.RandomBytes count)
                    )
        }

    /// The pulls of a response, journaled together when it closes (`Stream.Pulls` in its own lane).
    let private chatStream (journal: SessionJournal) (lane: string) (stream: ChatStream) : ChatStream =
        let gate = obj ()
        let pulls = ResizeArray<Pull>()
        let mutable closed = false
        let call = journal.Start(lane, Ops.Pulls, Inputs.none, "")
        let add (pull: Pull) = lock gate (fun () -> pulls.Add pull)

        {
            Next =
                fun cancellation ->
                    task {
                        try
                            let! next = stream.Next cancellation

                            match next with
                            | Some event -> add (Pull.Event event)
                            | None -> add Pull.End

                            return next
                        with e ->
                            match e with
                            | :? OperationCanceledException -> add Pull.Cancelled
                            | e -> add (Pull.Threw(e.GetType().Name))

                            return Internal.rethrow e
                    }
            Close =
                fun () ->
                    task {
                        let first =
                            lock gate (fun () ->
                                let first = not closed
                                closed <- true
                                first)

                        try
                            do! stream.Close()
                        finally
                            if first then
                                let all = lock gate (fun () -> List.ofSeq pulls)
                                journal.Returned(call, Json.Array(all |> List.map Pull.codec.Encode))
                    }
        }

    /// Journals each request (its digest: requests are rendered from the logs, prompt.md) and each pull
    /// of each response. It sits above the model-egress hooks and the wire, so no credential reaches it.
    let llm (journal: SessionJournal) (lane: string) (llm: Llm) : Llm =
        {
            Stream =
                fun request cancellation ->
                    task {
                        do! journal.Due()
                        let summary = $"{request.Model} ({request.Messages.Length} messages)"
                        let call = journal.Start(lane, Ops.Stream, Inputs.request request, summary)

                        try
                            let! stream = llm.Stream request cancellation
                            journal.Returned(call, Json.Null)
                            return chatStream journal (Lanes.stream lane call.Index) stream
                        with e ->
                            journal.Failed(call, e)
                            return Internal.rethrow e
                    }
        }

    let private proc (journal: SessionJournal) (lane: string) (proc: Proc) : Proc =
        {
            Pid = proc.Pid
            Write =
                fun bytes cancellation ->
                    journal.RunActing(
                        lane,
                        Ops.Write,
                        Inputs.bytes bytes,
                        $"{bytes.Length} bytes",
                        unitJson,
                        fun () -> proc.Write bytes cancellation
                    )
            CloseStdin = fun () -> journal.RunActing(lane, Ops.CloseStdin, Inputs.none, "", unitJson, proc.CloseStdin)
            Read =
                fun cancellation ->
                    journal.Run(
                        lane,
                        Ops.Read,
                        Inputs.none,
                        "",
                        Values.chunk.Encode,
                        (fun () -> proc.Read cancellation)
                    )
            Wait =
                fun cancellation ->
                    journal.Run(lane, Ops.Wait, Inputs.none, "", Json.int, (fun () -> proc.Wait cancellation))
            Kill = fun () -> journal.RunActing(lane, Ops.Kill, Inputs.none, "", unitJson, proc.Kill)
        }

    /// Journals commands with their outputs, and spawned processes with everything written to them
    /// (digests) and read from them, each process in its own lane.
    let shell (journal: SessionJournal) (lane: string) (shell: Shell) : Shell =
        {
            Run =
                fun command cancellation ->
                    journal.Run(
                        lane,
                        Ops.Run,
                        Inputs.json (Values.commandInput command),
                        string command.Spec.Program,
                        encodeWith (Values.result Values.output Values.shellError),
                        fun () -> shell.Run command cancellation
                    )
            Spawn =
                fun spec cancellation ->
                    task {
                        do! journal.Due()

                        let call =
                            journal.Start(lane, Ops.Spawn, Inputs.json (Values.procInput spec), string spec.Program)

                        try
                            match! shell.Spawn spec cancellation with
                            | Ok started ->
                                journal.Returned(
                                    call,
                                    (Values.result Codec.int Values.shellError).Encode(Ok started.Pid)
                                )

                                return Ok(proc journal (Lanes.proc lane call.Index) started)
                            | Error e ->
                                journal.Returned(call, (Values.result Codec.int Values.shellError).Encode(Error e))
                                return Error e
                        with e ->
                            journal.Failed(call, e)
                            return Internal.rethrow e
                    }
        }

    let fsRead (journal: SessionJournal) (lane: string) (fs: FsRead) : FsRead =
        {
            ReadFile =
                fun path cancellation ->
                    journal.Run(
                        lane,
                        Ops.ReadFile,
                        Inputs.text path,
                        path,
                        encodeWith (fsResult Values.bytes),
                        fun () -> fs.ReadFile path cancellation
                    )
            Stat =
                fun path cancellation ->
                    journal.Run(
                        lane,
                        Ops.Stat,
                        Inputs.text path,
                        path,
                        encodeWith (fsResult EnvCodecs.entry),
                        fun () -> fs.Stat path cancellation
                    )
            List =
                fun path cancellation ->
                    journal.Run(
                        lane,
                        Ops.List,
                        Inputs.text path,
                        path,
                        encodeWith (fsResult (Codec.list EnvCodecs.entry)),
                        fun () -> fs.List path cancellation
                    )
        }

    let fs (journal: SessionJournal) (lane: string) (fs: Fs) : Fs =
        let unitResult = encodeWith (fsResult Codec.unit)

        {
            WriteFile =
                fun path content cancellation ->
                    journal.Run(
                        lane,
                        Ops.WriteFile,
                        Inputs.pathAndBytes path content,
                        $"{path} ({content.Length} bytes)",
                        unitResult,
                        fun () -> fs.WriteFile path content cancellation
                    )
            CreateDirectory =
                fun path cancellation ->
                    journal.Run(
                        lane,
                        Ops.CreateDirectory,
                        Inputs.text path,
                        path,
                        unitResult,
                        fun () -> fs.CreateDirectory path cancellation
                    )
            Delete =
                fun path cancellation ->
                    journal.Run(
                        lane,
                        Ops.Delete,
                        Inputs.text path,
                        path,
                        unitResult,
                        (fun () -> fs.Delete path cancellation)
                    )
            Move =
                fun source target cancellation ->
                    journal.Run(
                        lane,
                        Ops.Move,
                        Inputs.paths [ source; target ],
                        $"{source} -> {target}",
                        unitResult,
                        fun () -> fs.Move source target cancellation
                    )
        }

    /// envd's file operations. Watching is not recordable (no session uses it yet): a recorded
    /// `Watch` refuses with `InvalidPath`.
    let fsOps (journal: SessionJournal) (lane: string) (ops: FsOps) : FsOps =
        {
            StatMany =
                fun paths cancellation ->
                    journal.Run(
                        lane,
                        Ops.StatMany,
                        Inputs.paths paths,
                        $"{paths.Length} paths",
                        encodeWith (Codec.list (fsResult EnvCodecs.entry)),
                        fun () -> ops.StatMany paths cancellation
                    )
            ReadRange =
                fun path offset count cancellation ->
                    journal.Run(
                        lane,
                        Ops.ReadRange,
                        Inputs.many [ Json.String path; Json.int64 offset; Json.int count ],
                        $"{path} @{offset}+{count}",
                        encodeWith (fsResult Values.fileChunk),
                        fun () -> ops.ReadRange path offset count cancellation
                    )
            WriteAtomic =
                fun path content cancellation ->
                    journal.Run(
                        lane,
                        Ops.WriteAtomic,
                        Inputs.pathAndBytes path content,
                        $"{path} ({content.Length} bytes)",
                        encodeWith (fsResult Codec.unit),
                        fun () -> ops.WriteAtomic path content cancellation
                    )
            ApplyPatch =
                fun patches cancellation ->
                    journal.Run(
                        lane,
                        Ops.ApplyPatch,
                        Inputs.json (Json.Array(patches |> List.map EnvCodecs.filePatch.Encode)),
                        patches |> List.map (fun p -> p.Path) |> String.concat ", ",
                        encodeWith (Values.result (Codec.list EnvCodecs.patchedFile) EnvCodecs.patchError),
                        fun () -> ops.ApplyPatch patches cancellation
                    )
            Search =
                fun query cancellation ->
                    journal.Run(
                        lane,
                        Ops.Search,
                        Inputs.json (EnvCodecs.searchQuery.Encode query),
                        string query,
                        encodeWith (Values.result EnvCodecs.searchResult EnvCodecs.searchError),
                        fun () -> ops.Search query cancellation
                    )
            Watch =
                fun spec _ ->
                    Task.FromResult(Error(InvalidPath(spec.Root, "watching is not recorded (docs/modules/replay.md)")))
        }

    /// Terminals are not recordable (no session uses them yet): a recorded `Spawn` refuses.
    let terminal (_: Fx.Pty.Terminal) : Fx.Pty.Terminal =
        {
            Spawn =
                fun spec _ ->
                    Task.FromResult(
                        Error(
                            Fx.Pty.NotStarted(
                                string spec.Program,
                                "terminals are not recorded (docs/modules/replay.md)"
                            )
                        )
                    )
        }

    let snapshots (journal: SessionJournal) (lane: string) (snapshots: Snapshots) : Snapshots =
        let refResult = Values.result EnvCodecs.snapshotRef EnvCodecs.snapshotError

        {
            Take =
                fun message cancellation ->
                    journal.Run(
                        lane,
                        Ops.Take,
                        Inputs.text message,
                        message,
                        encodeWith refResult,
                        fun () -> snapshots.Take message cancellation
                    )
            Diff =
                fun before after cancellation ->
                    journal.Run(
                        lane,
                        Ops.Diff,
                        Inputs.many [ EnvCodecs.snapshotRef.Encode before; EnvCodecs.snapshotRef.Encode after ],
                        $"{before} -> {after}",
                        encodeWith (Values.result (Codec.list EnvCodecs.fileChange) EnvCodecs.snapshotError),
                        fun () -> snapshots.Diff before after cancellation
                    )
            Patch =
                fun before after paths cancellation ->
                    journal.Run(
                        lane,
                        Ops.Patch,
                        Inputs.many
                            [
                                EnvCodecs.snapshotRef.Encode before
                                EnvCodecs.snapshotRef.Encode after
                                Inputs.maybePaths paths
                            ],
                        $"{before} -> {after}",
                        encodeWith (Values.result Codec.string EnvCodecs.snapshotError),
                        fun () -> snapshots.Patch before after paths cancellation
                    )
            Restore =
                fun snapshot paths cancellation ->
                    journal.Run(
                        lane,
                        Ops.Restore,
                        Inputs.many [ EnvCodecs.snapshotRef.Encode snapshot; Inputs.maybePaths paths ],
                        string snapshot,
                        encodeWith (Values.result Codec.unit EnvCodecs.snapshotError),
                        fun () -> snapshots.Restore snapshot paths cancellation
                    )
        }

    /// Journals each resolution by its reference and outcome, never the secret's value.
    let secrets (journal: SessionJournal) (lane: string) (secrets: Secrets) : Secrets =
        {
            Resolve =
                fun reference cancellation ->
                    task {
                        do! journal.Due()

                        let call =
                            journal.Start(lane, Ops.Resolve, Inputs.text (string reference), string reference)

                        try
                            let! result = secrets.Resolve reference cancellation
                            journal.Returned(call, Resolution.codec.Encode(Resolution.ofResult result))
                            return result
                        with e ->
                            journal.Failed(call, e)
                            return Internal.rethrow e
                    }
        }

    /// Journals each slot's first read under a profile (one interpreter's values never change).
    let variants (journal: SessionJournal) (lane: string) (profile: string) (variants: Variants) : Variants =
        {
            Read =
                fun slot ->
                    let read = variants.Read slot

                    if journal.Once($"variant {profile} {slot.Value}") then
                        journal.Mark(
                            lane,
                            Ops.VariantRead,
                            Inputs.variant profile slot,
                            slot.Value,
                            (Codec.option Resolved.codec).Encode read
                        )

                    read
        }

    /// An event log as the strategy uses it (`encode`: its schema's): appends are journaled with the
    /// digest of the payloads they wrote (replay checks that it writes the same), reads with how much
    /// they saw (replay shows exactly that).
    let log (journal: SessionJournal) (lane: string) (encode: 'E -> Payload) (log: EventLog<'E>) : EventLog<'E> =
        {
            Append =
                fun stream expected meta events cancellation ->
                    journal.Run(
                        lane,
                        Ops.LogAppend,
                        Inputs.append stream expected meta (List.map encode events),
                        $"{stream} #{expected + 1L} {meta.Key.Value}",
                        encodeWith (Values.result Codec.int64 Values.appendError),
                        fun () -> log.Append stream expected meta events cancellation
                    )
            Read =
                fun stream after max cancellation ->
                    journal.Run(
                        lane,
                        Ops.LogRead,
                        Inputs.read stream after max,
                        $"{stream} after {after}",
                        (fun (page: Recorded<'E> list) -> Json.int page.Length),
                        fun () -> log.Read stream after max cancellation
                    )
            Head =
                fun stream cancellation ->
                    journal.Run(
                        lane,
                        Ops.LogHead,
                        Inputs.text stream.Value,
                        stream.Value,
                        Json.int64,
                        fun () -> log.Head stream cancellation
                    )
            WaitPast =
                fun stream after cancellation ->
                    journal.Run(
                        lane,
                        Ops.LogWaitPast,
                        Inputs.many [ Json.String stream.Value; Json.int64 after ],
                        $"{stream} past {after}",
                        Json.int64,
                        fun () -> log.WaitPast stream after cancellation
                    )
            FindCommit =
                fun stream key cancellation ->
                    journal.Run(
                        lane,
                        Ops.LogFindCommit,
                        Inputs.many [ Json.String stream.Value; Json.String key.Value ],
                        $"{stream} {key.Value}",
                        encodeWith (Codec.option Values.appended),
                        fun () -> log.FindCommit stream key cancellation
                    )
        }
