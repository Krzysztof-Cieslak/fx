namespace Fx.Envd

open Fx.Contracts
open Fx.Effects
open Fx.Pty
open Fx.Rpc
open Fx.Store

/// A file read: where the content (sent over the channel) starts, how long it is, and the file's size.
type ReadReply =
    {
        Offset: int64
        Length: int
        FileSize: int64
    }

    override this.ToString() =
        $"{this.Length} bytes at {this.Offset} of {this.FileSize}"

/// A process envd started.
type Started =
    {
        /// envd's id of the process: unique for envd's lifetime, valid on every connection.
        Id: string
        Pid: int
    }

    override this.ToString() = $"process {this.Id} (pid {this.Pid})"

/// What `exec/start` runs.
type StartSpec =
    {
        Program: Program
        Cwd: string option
        Env: EnvVars
        /// On a pseudo-terminal of this size; `None`: pipes.
        Terminal: PtySize option
    }

    override this.ToString() =
        match this.Terminal with
        | Some size -> $"{this.Program} on a {size} terminal"
        | None -> $"{this.Program}"

/// Where a process envd runs stands.
type ProcessStatus =
    {
        Id: string
        Pid: int
        Terminal: bool
        /// Set once it has exited.
        ExitCode: int option
        Out: LogStatus
        /// Empty for a terminal.
        Err: LogStatus
    }

    override this.ToString() =
        let state =
            match this.ExitCode with
            | Some code -> $"exited {code}"
            | None -> "running"

        $"process {this.Id} ({state}; out {this.Out}; err {this.Err})"

/// An expected failure of the `exec/*` methods.
[<RequireQualifiedAccess>]
type ExecError =
    | NotStarted of program: string * reason: string
    | InvalidSize of PtySize
    /// No process with this id (never started, or released).
    | UnknownProcess of id: string

    override this.ToString() =
        match this with
        | NotStarted(program, reason) -> $"could not start {program}: {reason}"
        | InvalidSize size -> $"invalid terminal size {size}"
        | UnknownProcess id -> $"no process {id}"

/// The environment protocol (spec §17): what the host calls on envd over Fx.Rpc. Bulk data goes over
/// binary channels that the *caller* opens and names in the request: envd accepts the channel first,
/// then writes (downloads, ending with `CloseOutput` before it answers) or reads it (uploads). A caller
/// whose call fails resets its channel.
///
/// The host is the JSON-RPC client. envd's `initialize` result carries `Members.platform`.
module EnvProtocol =
    /// The version of this protocol, exchanged in `initialize` as `Members.protocol`.
    [<Literal>]
    let Version = "1"

    /// `initialize` members.
    module Members =
        /// The environment protocol version (both directions).
        [<Literal>]
        let Protocol = "environmentProtocol"

        /// envd's platform (in the result).
        [<Literal>]
        let Platform = "platform"

    // ---- Params and results ---------------------------------------------------------------------

    /// The roots a request's paths resolve beneath (`EnvdServices.Beneath`, T113b); absent or empty:
    /// none, paths resolve as the OS resolves them.
    let private beneathField (get: 'T -> string list) =
        Codec.defaultField "beneath" get [] (Codec.list Codec.string)

    /// `{path, beneath?}`.
    let private pathParams: Codec<string * string list> =
        Codec.record {
            let! p = Codec.field "path" fst Codec.string
            and! roots = beneathField snd
            return p, roots
        }

    let private idParams: Codec<string> =
        Codec.record {
            let! i = Codec.field "id" id Codec.string
            return i
        }

    let private channelParams: Codec<uint32> =
        Codec.record {
            let! c = Codec.field "channel" id Rpc.channelId
            return c
        }

    /// `fs/stat`: `{paths, beneath?}` → `{entries: [{entry} | {error}]}`.
    let fsStat =
        Method.define
            "fs/stat"
            (Codec.record {
                let! paths = Codec.field "paths" fst (Codec.list Codec.string)
                and! roots = beneathField snd
                return paths, roots
            })
            (Codec.record {
                let! entries = Codec.field "entries" id (Codec.list EnvCodecs.entryResult)
                return entries
            })

    /// `fs/list`: `{path, beneath?}` → `{entries}`.
    let fsList =
        Method.define
            "fs/list"
            pathParams
            (Codec.record {
                let! entries = Codec.field "entries" id (Codec.list EnvCodecs.entry)
                return entries
            })
        |> Method.withErrors EnvCodecs.fsError

    /// `fs/read`: `{path, offset, count?, channel, beneath?}` → the reply; the content goes over the
    /// channel. Without `count`, the whole file from `offset`.
    let fsRead =
        Method.define
            "fs/read"
            (Codec.record {
                let! p =
                    Codec.field
                        "path"
                        (fun (p: string, _: int64, _: int option, _: uint32, _: string list) -> p)
                        Codec.string

                and! offset =
                    Codec.defaultField "offset" (fun (_, o, _, _, _) -> o) 0L (Codec.int64 |> Codec.atLeast 0L)

                and! count =
                    Codec.optionalField "count" (fun (_, _, c, _, _) -> c) (Codec.int |> Codec.atLeast 0)

                and! channel = Codec.field "channel" (fun (_, _, _, c, _) -> c) Rpc.channelId
                and! roots = beneathField (fun (_, _, _, _, r) -> r)
                return p, offset, count, channel, roots
            })
            (Codec.record {
                let! offset = Codec.field "offset" (fun (r: ReadReply) -> r.Offset) Codec.int64
                and! length = Codec.field "length" (fun r -> r.Length) Codec.int
                and! size = Codec.field "fileSize" (fun r -> r.FileSize) Codec.int64

                return
                    {
                        Offset = offset
                        Length = length
                        FileSize = size
                    }
            })
        |> Method.withErrors EnvCodecs.fsError

    /// `fs/write`: `{path, channel, atomic, beneath?}`; the content comes over the channel.
    let fsWrite =
        Method.define
            "fs/write"
            (Codec.record {
                let! p =
                    Codec.field "path" (fun (p: string, _: uint32, _: bool, _: string list) -> p) Codec.string

                and! channel = Codec.field "channel" (fun (_, c, _, _) -> c) Rpc.channelId
                and! atomic = Codec.defaultField "atomic" (fun (_, _, a, _) -> a) false Codec.bool
                and! roots = beneathField (fun (_, _, _, r) -> r)
                return p, channel, atomic, roots
            })
            Codec.unit
        |> Method.withErrors EnvCodecs.fsError

    let fsCreateDirectory =
        Method.define "fs/createDirectory" pathParams Codec.unit
        |> Method.withErrors EnvCodecs.fsError

    let fsDelete =
        Method.define "fs/delete" pathParams Codec.unit
        |> Method.withErrors EnvCodecs.fsError

    let fsMove =
        Method.define
            "fs/move"
            (Codec.record {
                let! source =
                    Codec.field "source" (fun (s: string, _: string, _: string list) -> s) Codec.string

                and! destination = Codec.field "destination" (fun (_, d, _) -> d) Codec.string
                and! roots = beneathField (fun (_, _, r) -> r)
                return source, destination, roots
            })
            Codec.unit
        |> Method.withErrors EnvCodecs.fsError

    let fsPatch =
        Method.define
            "fs/patch"
            (Codec.record {
                let! patches = Codec.field "patches" fst (Codec.list EnvCodecs.filePatch)
                and! roots = beneathField snd
                return patches, roots
            })
            (Codec.record {
                let! files = Codec.field "files" id (Codec.list EnvCodecs.patchedFile)
                return files
            })
        |> Method.withErrors EnvCodecs.patchError

    /// `fs/search`: `{root, …the query, beneath?}` → the result.
    let fsSearch =
        Method.define "fs/search" EnvCodecs.searchParams EnvCodecs.searchResult
        |> Method.withErrors EnvCodecs.searchError

    // ---- Watching -------------------------------------------------------------------------------

    /// A watch the host started: its id (chosen by the host, unique on its connection) and spec.
    let watchStart =
        Method.define
            "watch/start"
            (Codec.record {
                let! watch = Codec.field "watch" (fun (w: string, _: WatchSpec) -> w) Codec.string
                and! root = Codec.field "root" (fun (_, s) -> s.Root) Codec.string

                and! recursive =
                    Codec.defaultField "recursive" (fun (_, s) -> s.Recursive) true Codec.bool

                and! debounce =
                    Codec.defaultField
                        "debounceMs"
                        (fun (_, s) -> int s.Debounce.TotalMilliseconds)
                        100
                        (Codec.int |> Codec.between 0 60_000)

                return
                    watch,
                    {
                        Root = root
                        Recursive = recursive
                        Debounce = System.TimeSpan.FromMilliseconds(float debounce)
                    }
            })
            Codec.unit
        |> Method.withErrors EnvCodecs.fsError

    let watchStop =
        Method.define
            "watch/stop"
            (Codec.record {
                let! watch = Codec.field "watch" id Codec.string
                return watch
            })
            Codec.unit

    /// envd → host: a batch of changes of a watch; `closed` once the watch has ended.
    let watchChanged =
        Notification.define
            "watch/changed"
            (Codec.record {
                let! watch =
                    Codec.field "watch" (fun (w: string, _: WatchBatch, _: bool) -> w) Codec.string

                and! changes =
                    Codec.field "changes" (fun (_, b, _) -> b.Changes) (Codec.list EnvCodecs.fileChange)

                and! overflow =
                    Codec.defaultField "overflow" (fun (_, b, _) -> b.Overflow) false Codec.bool

                and! closed = Codec.defaultField "closed" (fun (_, _, c) -> c) false Codec.bool

                return
                    watch,
                    {
                        Changes = changes
                        Overflow = overflow
                    },
                    closed
            })

    // ---- Processes ------------------------------------------------------------------------------

    let execError: Codec<ExecError> =
        Codec.union "kind" {
            let! notStarted =
                Codec.case2
                    "notStarted"
                    (fun program reason -> ExecError.NotStarted(program, reason))
                    (Field.required "program" Codec.string)
                    (Field.required "reason" Codec.string)

            and! invalidSize =
                Codec.case1 "invalidSize" ExecError.InvalidSize (Field.required "size" EnvCodecs.ptySize)

            and! unknown =
                Codec.case1 "unknownProcess" ExecError.UnknownProcess (Field.required "id" Codec.string)

            return
                fun value ->
                    match value with
                    | ExecError.NotStarted(program, reason) -> notStarted program reason
                    | ExecError.InvalidSize size -> invalidSize size
                    | ExecError.UnknownProcess id -> unknown id
        }

    let private started: Codec<Started> =
        Codec.record {
            let! i = Codec.field "id" (fun (s: Started) -> s.Id) Codec.string
            and! pid = Codec.field "pid" (fun s -> s.Pid) Codec.int
            return { Id = i; Pid = pid }
        }

    let execStart =
        Method.define
            "exec/start"
            (Codec.record {
                let! program =
                    Codec.field "program" (fun (s: StartSpec) -> s.Program) EnvCodecs.program

                and! cwd = Codec.optionalField "cwd" (fun s -> s.Cwd) Codec.string
                and! env = Codec.defaultField "env" (fun s -> s.Env) (Inherit []) EnvCodecs.envVars

                and! terminal =
                    Codec.optionalField "terminal" (fun s -> s.Terminal) EnvCodecs.ptySize

                return
                    {
                        Program = program
                        Cwd = cwd
                        Env = env
                        Terminal = terminal
                    }
            })
            started
        |> Method.withErrors execError

    /// `exec/attach`: `{id, out, err, channel}`: streams output records (`OutputRecords`) from those
    /// offsets over the channel until both streams have ended.
    let execAttach =
        Method.define
            "exec/attach"
            (Codec.record {
                let! i =
                    Codec.field "id" (fun (i: string, _: int64, _: int64, _: uint32) -> i) Codec.string

                and! out =
                    Codec.defaultField "out" (fun (_, o, _, _) -> o) 0L (Codec.int64 |> Codec.atLeast 0L)

                and! err =
                    Codec.defaultField "err" (fun (_, _, e, _) -> e) 0L (Codec.int64 |> Codec.atLeast 0L)

                and! channel = Codec.field "channel" (fun (_, _, _, c) -> c) Rpc.channelId
                return i, out, err, channel
            })
            Codec.unit
        |> Method.withErrors execError

    /// `exec/write`: types or pipes input (base64, at most `maxInputChunk` bytes per call).
    let execWrite =
        Method.define
            "exec/write"
            (Codec.record {
                let! i = Codec.field "id" (fun (i: string, _: byte array) -> i) Codec.string
                and! data = Codec.field "data" snd EnvCodecs.bytes
                return i, data
            })
            Codec.unit
        |> Method.withErrors execError

    /// The largest input of one `exec/write`.
    let maxInputChunk = 64 * 1024

    let execCloseStdin =
        Method.define "exec/closeStdin" idParams Codec.unit
        |> Method.withErrors execError

    let execResize =
        Method.define
            "exec/resize"
            (Codec.record {
                let! i = Codec.field "id" (fun (i: string, _: PtySize) -> i) Codec.string
                and! cols = Codec.field "cols" (fun (_, s) -> s.Cols) Codec.int
                and! rows = Codec.field "rows" (fun (_, s) -> s.Rows) Codec.int
                return i, { Cols = cols; Rows = rows }
            })
            Codec.unit
        |> Method.withErrors execError

    /// `exec/kill`: kills the process and its descendants (the process group and tree on Linux and
    /// macOS, the job object of a terminal on Windows).
    let execKill =
        Method.define "exec/kill" idParams Codec.unit |> Method.withErrors execError

    /// `exec/wait`: the exit code, once the process has exited.
    let execWait =
        Method.define
            "exec/wait"
            idParams
            (Codec.record {
                let! code = Codec.field "exitCode" id Codec.int
                return code
            })
        |> Method.withErrors execError

    let processStatus: Codec<ProcessStatus> =
        Codec.record {
            let! i = Codec.field "id" (fun (s: ProcessStatus) -> s.Id) Codec.string
            and! pid = Codec.field "pid" (fun s -> s.Pid) Codec.int
            and! terminal = Codec.field "terminal" (fun s -> s.Terminal) Codec.bool
            and! code = Codec.optionalField "exitCode" (fun s -> s.ExitCode) Codec.int
            and! out = Codec.field "out" (fun s -> s.Out) EnvCodecs.logStatus
            and! err = Codec.field "err" (fun s -> s.Err) EnvCodecs.logStatus

            return
                {
                    Id = i
                    Pid = pid
                    Terminal = terminal
                    ExitCode = code
                    Out = out
                    Err = err
                }
        }

    let execStatus =
        Method.define "exec/status" idParams processStatus
        |> Method.withErrors execError

    let execList =
        Method.define
            "exec/list"
            Codec.unit
            (Codec.record {
                let! processes = Codec.field "processes" id (Codec.list processStatus)
                return processes
            })

    /// `exec/release`: forgets a process: kills it if it still runs, drops its output and deletes its
    /// spill files. Idempotent.
    let execRelease = Method.define "exec/release" idParams Codec.unit

    // ---- Blobs ----------------------------------------------------------------------------------

    /// `blobs/put`: `{channel}` → the reference; the content comes over the channel.
    let blobsPut = Method.define "blobs/put" channelParams EnvCodecs.blobRef

    let private rangeOf (offset: int64 option) (count: int option) : (int64 * int) option =
        match offset, count with
        | None, None -> None
        | o, c -> Some(Option.defaultValue 0L o, Option.defaultValue System.Int32.MaxValue c)

    /// `blobs/get`: `{hash, offset?, count?, channel}` → the number of bytes sent over the channel.
    /// Without a range, the whole blob, checked against its hash.
    let blobsGet =
        Method.define
            "blobs/get"
            (Codec.record {
                let! hash =
                    Codec.field "hash" (fun (h: BlobHash, _: (int64 * int) option, _: uint32) -> h) EnvCodecs.blobHash

                and! offset =
                    Codec.optionalField
                        "offset"
                        (fun (_, r, _) -> r |> Option.map fst)
                        (Codec.int64 |> Codec.atLeast 0L)

                and! count =
                    Codec.optionalField "count" (fun (_, r, _) -> r |> Option.map snd) (Codec.int |> Codec.atLeast 0)

                and! channel = Codec.field "channel" (fun (_, _, c) -> c) Rpc.channelId
                return hash, rangeOf offset count, channel
            })
            (Codec.record {
                let! size = Codec.field "size" id Codec.int64
                return size
            })
        |> Method.withErrors EnvCodecs.blobError

    let blobsStat =
        Method.define
            "blobs/stat"
            (Codec.record {
                let! hash = Codec.field "hash" id EnvCodecs.blobHash
                return hash
            })
            (Codec.record {
                let! blob = Codec.optionalField "blob" id EnvCodecs.blobRef
                return blob
            })

    // ---- Snapshots ------------------------------------------------------------------------------

    let snapshotTake =
        Method.define
            "snapshot/take"
            (Codec.record {
                let! message = Codec.defaultField "message" id "" Codec.string
                return message
            })
            EnvCodecs.snapshotRef
        |> Method.withErrors EnvCodecs.snapshotError

    let private pair: Codec<SnapshotRef * SnapshotRef> =
        Codec.record {
            let! from = Codec.field "from" fst EnvCodecs.snapshotRef
            and! until = Codec.field "to" snd EnvCodecs.snapshotRef
            return from, until
        }

    let snapshotDiff =
        Method.define
            "snapshot/diff"
            pair
            (Codec.record {
                let! changes = Codec.field "changes" id (Codec.list EnvCodecs.fileChange)
                return changes
            })
        |> Method.withErrors EnvCodecs.snapshotError

    /// `snapshot/patch`: `{from, to, paths?, channel}`; the unified diff (UTF-8) goes over the channel.
    let snapshotPatch =
        Method.define
            "snapshot/patch"
            (Codec.record {
                let! from =
                    Codec.field
                        "from"
                        (fun (f: SnapshotRef, _: SnapshotRef, _: string list option, _: uint32) -> f)
                        EnvCodecs.snapshotRef

                and! until = Codec.field "to" (fun (_, t, _, _) -> t) EnvCodecs.snapshotRef

                and! paths =
                    Codec.optionalField "paths" (fun (_, _, p, _) -> p) (Codec.list Codec.string)

                and! channel = Codec.field "channel" (fun (_, _, _, c) -> c) Rpc.channelId
                return from, until, paths, channel
            })
            (Codec.record {
                let! length = Codec.field "length" id Codec.int
                return length
            })
        |> Method.withErrors EnvCodecs.snapshotError

    let snapshotRestore =
        Method.define
            "snapshot/restore"
            (Codec.record {
                let! snapshot = Codec.field "to" fst EnvCodecs.snapshotRef
                and! paths = Codec.optionalField "paths" snd (Codec.list Codec.string)
                return snapshot, paths
            })
            Codec.unit
        |> Method.withErrors EnvCodecs.snapshotError
