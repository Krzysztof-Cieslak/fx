namespace Fx.CodeAct

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Kernel
open Fx.Interpreters
open Fx.Prompt
open Fx.Sessions

// ---- The environment's side: jobs as processes (T143) ---------------------------------------------

/// How a background job starts (T143): a command line for the environment's shell, as `shell.run` runs
/// one, whose output and end are kept while the cells and turns of its session go on.
type JobLaunch =
    {
        /// The session it belongs to: a session sees its own jobs only.
        Session: string
        /// Its id in the session (`j1`), which the strategy gives.
        Id: string
        Command: string
        /// The working directory, absolute in the environment's syntax.
        Cwd: string
        Env: EnvVars
        /// Written to its standard input, which is then closed.
        Stdin: string option
    }

    override this.ToString() = $"job {this.Id} of {this.Session}"

/// Whether a job runs, and how it ended.
[<RequireQualifiedAccess>]
type JobState =
    | Running
    | Exited of code: int
    /// `jobs.kill` stopped it.
    | Killed
    /// Its output could not be read, or the host stopped while it ran.
    | Failed of reason: string

    override this.ToString() =
        match this with
        | Running -> "running"
        | Exited code -> $"exited ({code})"
        | Killed -> "killed"
        | Failed reason -> $"failed: {reason}"

/// A job as its environment reports it.
type JobInfo =
    {
        Id: string
        Command: string
        State: JobState
        /// The bytes of output it wrote so far.
        Bytes: int64
    }

    override this.ToString() = $"job {this.Id} ({this.State})"

/// A job's output from a byte offset: standard output and standard error together, as they arrived
/// (`jobs.output`).
type JobOutput =
    {
        Id: string
        /// The offset of the first byte shown: later than asked for when the bytes before it were not
        /// kept any more.
        From: int64
        /// The offset after the last byte shown: where to read on.
        Next: int64
        /// The bytes it wrote so far.
        Bytes: int64
        Text: string
        Running: bool
    }

    override this.ToString() =
        $"output of {this.Id} ({this.From}..{this.Next} of {this.Bytes})"

/// A job of a session.
type JobRef =
    {
        Session: string
        Id: string
    }

    override this.ToString() = $"{this.Id} of {this.Session}"

/// What to read of a job's output: from a byte offset (`None`: its last `MaxChars` bytes), at most
/// `MaxChars` characters.
type JobRead =
    {
        Job: JobRef
        From: int64 option
        MaxChars: int
    }

    override this.ToString() = $"read {this.Job}"

/// How long to wait for a job's end (`None`: until it ends).
type JobWait =
    {
        Job: JobRef
        TimeoutMs: int option
    }

    override this.ToString() = $"wait for {this.Job}"

/// A job that ended on its own (not by `jobs.kill`): what its session is told (`Command.NotifyJob`).
type JobNotice =
    {
        Session: string
        Job: string
        Note: string
    }

    override this.ToString() =
        $"job {this.Job} of {this.Session} ended"

/// The background jobs of a session's environment (T143): processes the hands start in the
/// environment's (sandboxed) shell and keep running across cells and turns. A capability of the hands,
/// like their guard: every call is data in and out, so a recording host journals what it answered and
/// replay answers from the journal without running a process (`Hands.Jobs`). `LiveJobs` is its
/// interpreter over the hands' shell; the composition root owns it and closes it when the host stops.
[<NoEquality; NoComparison>]
type Jobs<'K> =
    {
        /// Starts a job, or gives the job that already has its id.
        Start: JobLaunch -> Eff<'K, Result<JobInfo, string>>
        Info: JobRef -> Eff<'K, JobInfo option>
        /// A session's jobs, in the order they started.
        List: string -> Eff<'K, JobInfo list>
        Read: JobRead -> Eff<'K, JobOutput option>
        /// Waits until the job ends, or for at most the time given; then how it is.
        Wait: JobWait -> Eff<'K, JobInfo option>
        /// Stops the job and what it started, and waits (briefly) for its end.
        Kill: JobRef -> Eff<'K, JobInfo option>
    }

    override _.ToString() = "jobs"

/// The JSON of jobs, for the bridge (the model's view of them) and for the journal.
[<RequireQualifiedAccess>]
module JobCodecs =
    let state: Codec<JobState> =
        Codec.union "kind" {
            let! running = Codec.case0 "running" JobState.Running
            and! exited = Codec.case1 "exited" JobState.Exited (Field.required "code" Codec.int)
            and! killed = Codec.case0 "killed" JobState.Killed

            and! failed =
                Codec.case1 "failed" JobState.Failed (Field.required "reason" Codec.string)

            return
                fun s ->
                    match s with
                    | JobState.Running -> running
                    | JobState.Exited code -> exited code
                    | JobState.Killed -> killed
                    | JobState.Failed reason -> failed reason
        }

    /// A job as its environment reports it (the journal's).
    let info: Codec<JobInfo> =
        Codec.record {
            let! id = Codec.field "id" (fun (i: JobInfo) -> i.Id) Codec.string
            and! command = Codec.field "command" (fun i -> i.Command) Codec.string
            and! state = Codec.field "state" (fun i -> i.State) state
            and! bytes = Codec.field "bytes" (fun i -> i.Bytes) Codec.int64

            return
                ({
                    Id = id
                    Command = command
                    State = state
                    Bytes = bytes
                }
                : JobInfo)
        }

    let output: Codec<JobOutput> =
        Codec.record {
            let! id = Codec.field "id" (fun (o: JobOutput) -> o.Id) Codec.string

            and! from =
                Codec.field
                    "from"
                    (fun o -> o.From)
                    (Codec.int64
                     |> Codec.describe "The byte offset of the text: later than asked when earlier output was not kept.")

            and! next =
                Codec.field
                    "next"
                    (fun o -> o.Next)
                    (Codec.int64
                     |> Codec.describe "The byte offset after the text: pass it as `from` to read on.")

            and! bytes =
                Codec.field "bytes" (fun o -> o.Bytes) (Codec.int64 |> Codec.describe "The bytes it wrote so far.")

            and! text = Codec.field "text" (fun o -> o.Text) Codec.string
            and! running = Codec.field "running" (fun o -> o.Running) Codec.bool

            return
                ({
                    Id = id
                    From = from
                    Next = next
                    Bytes = bytes
                    Text = text
                    Running = running
                }
                : JobOutput)
        }
        |> Codec.describe "A job's output (standard output and error together, as they arrived) from a byte offset."
        |> Codec.named "JobOutput"

/// The output a job keeps: its last bytes (`retain`, up to twice that between compactions), and the
/// offset of the first byte kept. Not thread-safe: its job locks.
[<Sealed>]
type internal OutputLog(retain: int) =
    let mutable data: byte array = Array.zeroCreate 0
    let mutable length = 0
    let mutable first = 0L

    /// The offset of the first byte kept.
    member _.First = first

    /// The bytes written so far.
    member _.Total = first + int64 length

    member _.Append(bytes: byte array) =
        if bytes.Length > 0 then
            if length + bytes.Length > data.Length then
                let grown: byte array =
                    Array.zeroCreate (max (length + bytes.Length) (max 4096 (data.Length * 2)))

                Buffer.BlockCopy(data, 0, grown, 0, length)
                data <- grown

            Buffer.BlockCopy(bytes, 0, data, length, bytes.Length)
            length <- length + bytes.Length

            if length > 2 * retain then
                let drop = length - retain
                Buffer.BlockCopy(data, drop, data, 0, retain)
                length <- retain
                first <- first + int64 drop

    /// At most `count` bytes from `offset` (at least `First`).
    member _.Slice(offset: int64, count: int) : byte array =
        let start = int (max 0L (offset - first))
        let n = max 0 (min count (length - start))
        Array.sub data start n

/// Reading text from a job's bytes without cutting a character.
[<RequireQualifiedAccess>]
module internal JobText =
    let private utf8 = UTF8Encoding(false)

    let private sequenceLength (lead: byte) =
        if lead < 0x80uy then 1
        elif lead &&& 0xE0uy = 0xC0uy then 2
        elif lead &&& 0xF0uy = 0xE0uy then 3
        elif lead &&& 0xF8uy = 0xF0uy then 4
        else 1

    /// Continuation bytes at the start of `bytes` (a character begun before them).
    let skipped (bytes: byte array) : int =
        let mutable n = 0

        while n < bytes.Length && n < 3 && bytes[n] &&& 0xC0uy = 0x80uy do
            n <- n + 1

        n

    /// The bytes from `start` that hold at most `maxChars` characters; a character the bytes end in
    /// the middle of is left for later unless `final`.
    let span (bytes: byte array) (start: int) (maxChars: int) (final: bool) : int =
        let mutable i = start
        let mutable chars = 0
        let mutable going = true

        while going && i < bytes.Length do
            let n = sequenceLength bytes[i]
            let width = if n = 4 then 2 else 1

            if i + n > bytes.Length && not final then
                going <- false
            elif chars + width > maxChars then
                going <- false
            else
                i <- min bytes.Length (i + n)
                chars <- chars + width

        i - start

    let decode (bytes: byte array) (start: int) (count: int) : string = utf8.GetString(bytes, start, count)

/// A job the environment runs, with the output it keeps.
[<Sealed>]
type internal LiveJob(launch: JobLaunch, retain: int) =
    let gate = obj ()
    let log = OutputLog(retain)

    let ended =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable state = JobState.Running
    let mutable killed = false

    member _.Launch = launch
    member val Proc: Proc option = None with get, set
    member val Drain: Task = Task.CompletedTask with get, set

    /// Completes once its end is known.
    member _.Ended: Task<unit> = ended.Task

    member _.Running = lock gate (fun () -> state = JobState.Running)

    member _.Append(bytes: byte array) = lock gate (fun () -> log.Append bytes)

    member _.Finish(how: JobState) =
        lock gate (fun () ->
            if state = JobState.Running then
                state <- how)

        ended.TrySetResult() |> ignore

    /// `jobs.kill` stops it: its end is not a notice.
    member _.MarkKilled() =
        lock gate (fun () ->
            if state = JobState.Running then
                killed <- true)

    member _.Killed = lock gate (fun () -> killed)

    member _.Info: JobInfo =
        lock gate (fun () ->
            ({
                Id = launch.Id
                Command = launch.Command
                State = state
                Bytes = log.Total
            }
            : JobInfo))

    member _.Read(from: int64 option, maxChars: int) : JobOutput =
        lock gate (fun () ->
            let maxChars = max 16 maxChars
            let total = log.Total

            let asked =
                match from with
                | Some offset -> offset
                | None -> total - int64 maxChars

            let start = min total (max log.First asked)
            let bytes = log.Slice(start, maxChars * 4)
            let skip = JobText.skipped bytes
            // A character cut by the end of what was written is complete only once the job ended.
            let final = state <> JobState.Running && start + int64 bytes.Length = total
            let count = JobText.span bytes skip maxChars final

            ({
                Id = launch.Id
                From = start + int64 skip
                Next = start + int64 (skip + count)
                Bytes = total
                Text = JobText.decode bytes skip count
                Running = state = JobState.Running
            }
            : JobOutput))

    /// Its last lines, for its notice.
    member this.Tail(lines: int, chars: int) : string =
        let shown = this.Read(None, 4096)

        let all =
            shown.Text.Replace("\r\n", "\n").Split('\n')
            |> Array.map (fun line ->
                // A progress bar rewrites its line: its last state.
                let last = line.Split('\r') |> Array.last
                last.TrimEnd())
            |> Array.filter (fun line -> line <> "")

        let kept = all |> Array.skip (max 0 (all.Length - lines))

        kept
        |> Array.map (fun line ->
            if line.Length > chars then
                line.Substring(0, chars - 1) + "…"
            else
                line)
        |> String.concat "\n"

/// What sessions are told about their jobs (`Command.NotifyJob`): a job that ended on its own, or one
/// lost because the host stopped. One notice per job: its key is the job's.
[<RequireQualifiedAccess>]
module JobNotices =
    /// The most lines of a job's output its notice quotes (its last).
    [<Literal>]
    let TailLines = 10

    /// The most characters of each line a notice quotes.
    [<Literal>]
    let LineChars = 300

    /// The key of a job's notice in its session's stream: every way of telling the session (the
    /// environment when the job ends, a host that starts after it was lost) uses it, so the session
    /// hears of a job's end once.
    let key (job: string) : IdempotencyKey = IdempotencyKey $"jobs/{job}/ended"

    /// Who tells the session.
    let by: Attribution = Attribution.system "jobs"

    let command (notice: JobNotice) : Command =
        Command.NotifyJob(notice.Job, notice.Note)

    let private firstLine (text: string) =
        let line =
            text.Split('\n')
            |> Array.map (fun l -> l.Trim())
            |> Array.tryFind (fun l -> l <> "")
            |> Option.defaultValue ""

        if line.Length > 200 then
            line.Substring(0, 199) + "…"
        else
            line

    /// The note of a job that ended on its own: how, and its last lines.
    let ended (job: string) (command: string) (state: JobState) (bytes: int64) (tail: string) : string =
        let what = "`" + firstLine command + "`"

        let how =
            match state with
            | JobState.Exited code -> $"{what} exited with code {code}"
            | JobState.Killed -> $"{what} was killed"
            | JobState.Failed reason -> $"{what} failed: {reason}"
            | JobState.Running -> $"{what} is running"

        // The first line says how it ended (clients show it alone).
        if bytes = 0L || tail = "" then
            how + "; it printed nothing."
        else
            how
            + $" after printing {bytes} bytes.\nIts last lines:\n{tail}\n"
            + $"Read more with `{Product.kernelGlobal}.jobs.output(\"{job}\", {{ from: 0 }})`."

    /// The note of a job lost because the host stopped while it ran.
    let lost: string =
        "lost: the host stopped while it ran, and its output is gone. Start it again if it is still needed."

    /// What a host that just started tells a session that waits for jobs: none of them runs any more
    /// (a host does not take over the jobs of the one before it).
    let lostAll (state: SessionState) : JobNotice list =
        state.AwaitingJobs
        |> List.map (fun job ->
            {
                Session = state.Id
                Job = job
                Note = lost
            })

[<RequireQualifiedAccess>]
module Jobs =
    /// The most bytes of a job's output kept (its last).
    [<Literal>]
    let RetainBytes = 1048576

    /// Hands without background jobs: every start fails.
    let none<'K> : Jobs<'K> =
        {
            Start = fun _ -> EffResult.error "this environment runs no background jobs"
            Info = fun _ -> Eff.ret None
            List = fun _ -> Eff.ret []
            Read = fun _ -> Eff.ret None
            Wait = fun _ -> Eff.ret None
            Kill = fun _ -> Eff.ret None
        }

/// The jobs of the hands' environments in a host process (T143): each starts with the shell of the
/// environment its call runs in (the hands' sandboxed one), keeps its last output
/// (`Jobs.RetainBytes`), and tells its session when it ends on its own (`notify`). Jobs outlive the
/// cells and turns that started them, not the host: `Close` kills them, and the next host reports them
/// lost. The composition root owns it; a recording host journals its answers (`Hands.Jobs`).
[<Sealed>]
type LiveJobs<'K when 'K :> IShell and 'K :> IClock and 'K :> IScopeWith<'K>>(notify: JobNotice -> Task<unit>) =
    let gate = obj ()
    let lifetime = new CancellationTokenSource()
    let jobs = Dictionary<string, LiveJob>(StringComparer.Ordinal)
    let order = ResizeArray<LiveJob>()
    let mutable closing = false
    let utf8 = UTF8Encoding(false)

    let keyOf (session: string) (id: string) = session + "\n" + id

    let find (job: JobRef) : LiveJob option =
        lock gate (fun () ->
            match jobs.TryGetValue(keyOf job.Session job.Id) with
            | true, found -> Some found
            | _ -> None)

    let ended (job: LiveJob) : Eff<'K, unit> =
        Eff.ofCancellableTask (fun cancellation -> job.Ended.WaitAsync cancellation)

    /// Feeds its input, reads its output until both streams end, waits for its exit; then tells the
    /// session, unless `jobs.kill` stopped it or the host is stopping.
    let drain (job: LiveJob) (proc: Proc) : Eff<'K, unit> =
        eff {
            let feed: Eff<'K, unit> =
                eff {
                    match job.Launch.Stdin with
                    | Some text when text <> "" -> do! Shell.write proc (utf8.GetBytes text)
                    | _ -> ()

                    do! Shell.closeStdin proc
                }

            let read: Eff<'K, unit> =
                eff {
                    let mutable reading = true

                    while reading do
                        match! Shell.read proc with
                        | Some(Stdout bytes)
                        | Some(Stderr bytes) -> job.Append bytes
                        | None -> reading <- false
                }

            let run: Eff<'K, int> =
                eff {
                    let! _ = Eff.both feed read
                    return! Shell.wait proc
                }

            let! outcome = Eff.catch run

            let how =
                match outcome with
                | Ok _ when job.Killed -> JobState.Killed
                | Ok code -> JobState.Exited code
                | Error(:? OperationCanceledException) -> JobState.Failed "the host stopped while it ran"
                | Error e -> JobState.Failed $"its output could not be read ({e.GetType().Name})"

            job.Finish how

            let tell = not job.Killed && not (lock gate (fun () -> closing))

            if tell then
                let info = job.Info

                let notice: JobNotice =
                    {
                        Session = job.Launch.Session
                        Job = job.Launch.Id
                        Note =
                            JobNotices.ended
                                job.Launch.Id
                                job.Launch.Command
                                info.State
                                info.Bytes
                                (job.Tail(JobNotices.TailLines, JobNotices.LineChars))
                    }

                let! _ = Eff.catch (Eff.ofTask (fun () -> notify notice))
                ()
        }

    let start (launch: JobLaunch) : Eff<'K, Result<JobInfo, string>> =
        fun env ->
            task {
                let existing, stopping =
                    lock gate (fun () ->
                        match jobs.TryGetValue(keyOf launch.Session launch.Id) with
                        | true, found -> Some found, closing
                        | _ -> None, closing)

                match existing with
                | Some job -> return Ok job.Info
                | None when stopping -> return Error "the host is stopping"
                | None ->
                    // The job lives as long as the host's jobs, not the call that started it.
                    let lives =
                        env.WithScope
                            { env.Scope with
                                Cancellation = lifetime.Token
                            }

                    let spec: ProcSpec =
                        {
                            Program = Program.Script launch.Command
                            Cwd = Some launch.Cwd
                            Env = launch.Env
                        }

                    match! Eff.run lives (Shell.spawn spec) with
                    | Error e -> return Error(string e)
                    | Ok proc ->
                        let job = LiveJob(launch, Jobs.RetainBytes)
                        job.Proc <- Some proc

                        let late =
                            lock gate (fun () ->
                                jobs[keyOf launch.Session launch.Id] <- job
                                order.Add job
                                closing)

                        job.Drain <- Eff.run lives (drain job proc)

                        if late then
                            do! proc.Kill()

                        return Ok job.Info
            }

    let wait (asked: JobWait) : Eff<'K, JobInfo option> =
        eff {
            match find asked.Job with
            | None -> return None
            | Some job ->
                match asked.TimeoutMs with
                | Some ms ->
                    let! _ = Eff.timeout (TimeSpan.FromMilliseconds(float ms)) (ended job)
                    ()
                | None -> do! ended job

                return Some job.Info
        }

    let kill (asked: JobRef) : Eff<'K, JobInfo option> =
        eff {
            match find asked with
            | None -> return None
            | Some job ->
                if job.Running then
                    job.MarkKilled()

                    match job.Proc with
                    | Some proc -> do! Shell.kill proc
                    | None -> ()

                    let! _ = Eff.timeout (TimeSpan.FromSeconds 10.0) (ended job)
                    ()

                return Some job.Info
        }

    /// The jobs, as the hands use them.
    member _.Jobs: Jobs<'K> =
        {
            Start = start
            Info = fun job -> fun _ -> Task.FromResult(find job |> Option.map _.Info)
            List =
                fun session ->
                    fun _ ->
                        Task.FromResult(
                            lock gate (fun () ->
                                order
                                |> Seq.filter (fun job -> job.Launch.Session = session)
                                |> Seq.map _.Info
                                |> List.ofSeq)
                        )
            Read =
                fun asked ->
                    fun _ ->
                        Task.FromResult(find asked.Job |> Option.map (fun job -> job.Read(asked.From, asked.MaxChars)))
            Wait = wait
            Kill = kill
        }

    /// Kills every job (the host stops): their sessions are not told; the next host reports them lost.
    member _.Close() : Task =
        task {
            let running =
                lock gate (fun () ->
                    closing <- true
                    List.ofSeq order)

            for job in running do
                match job.Proc with
                | Some proc when job.Running ->
                    try
                        do! proc.Kill()
                    with _ ->
                        ()
                | _ -> ()

            lifetime.Cancel()

            for job in running do
                try
                    do! job.Drain
                with _ ->
                    ()
        }

    override _.ToString() = "live jobs"

// ---- The strategy's side: a session's jobs, their bindings and their journal --------------------

/// Options of `jobs.start`.
type JobOptions =
    {
        /// A name for people (the command by default).
        Name: string option
        /// The working directory, relative to the workspace (the workspace by default).
        Cwd: string option
        /// Text written to its standard input (which is then closed).
        Stdin: string option
    }

    override this.ToString() = $"JobOptions({this.Name}, {this.Cwd})"

/// A job as `jobs.start` gives it.
type Job =
    {
        Id: string
        Name: string
        Command: string
    }

    override this.ToString() = $"job {this.Id} ({this.Name})"

/// A job as `jobs.status`, `list`, `wait` and `kill` give it.
type JobStatus =
    {
        Id: string
        Name: string
        Command: string
        /// `running`, `exited`, `killed` or `failed`.
        State: string
        ExitCode: int option
        /// Why it failed.
        Reason: string option
        Bytes: int64
    }

    override this.ToString() = $"job {this.Id} {this.State}"

/// Options of `jobs.output`.
type OutputOptions =
    {
        From: int64 option
        MaxChars: int option
    }

    override this.ToString() =
        $"OutputOptions({this.From}, {this.MaxChars})"

/// Options of `jobs.wait`.
type WaitOptions =
    {
        TimeoutMs: int option
    }

    override this.ToString() = $"WaitOptions({this.TimeoutMs})"

/// The JSON of the `jobs` bindings: what the bridge declares to the model.
[<RequireQualifiedAccess>]
module JobBindingCodecs =
    let options: Codec<JobOptions> =
        Codec.record {
            let! name =
                Codec.optionalField
                    "name"
                    (fun (o: JobOptions) -> o.Name)
                    (Codec.string |> Codec.describe "A name for people; the command by default.")

            and! cwd =
                Codec.optionalField
                    "cwd"
                    (fun o -> o.Cwd)
                    (Codec.string
                     |> Codec.describe "Relative to the workspace; the workspace by default.")

            and! stdin =
                Codec.optionalField
                    "stdin"
                    (fun o -> o.Stdin)
                    (Codec.string |> Codec.describe "Written to the job's standard input.")

            return
                ({
                    Name = name
                    Cwd = cwd
                    Stdin = stdin
                }
                : JobOptions)
        }
        |> Codec.describe "How to start a background job."
        |> Codec.named "JobOptions"

    let job: Codec<Job> =
        Codec.record {
            let! id =
                Codec.field
                    "id"
                    (fun (j: Job) -> j.Id)
                    (Codec.string |> Codec.describe "Its id in the session: j1, j2, …")

            and! name = Codec.field "name" (fun j -> j.Name) Codec.string
            and! command = Codec.field "command" (fun j -> j.Command) Codec.string

            return
                ({
                    Id = id
                    Name = name
                    Command = command
                }
                : Job)
        }
        |> Codec.describe "A background job."
        |> Codec.named "Job"

    let status: Codec<JobStatus> =
        Codec.record {
            let! id = Codec.field "id" (fun (s: JobStatus) -> s.Id) Codec.string
            and! name = Codec.field "name" (fun s -> s.Name) Codec.string
            and! command = Codec.field "command" (fun s -> s.Command) Codec.string

            and! state =
                Codec.field
                    "state"
                    (fun s -> s.State)
                    (Codec.enum [ for s in [ "running"; "exited"; "killed"; "failed" ] -> s, s ])

            and! exitCode =
                Codec.optionalField "exitCode" (fun s -> s.ExitCode) (Codec.int |> Codec.describe "Once it exited.")

            and! reason =
                Codec.optionalField "reason" (fun s -> s.Reason) (Codec.string |> Codec.describe "Why it failed.")

            and! bytes =
                Codec.field
                    "bytes"
                    (fun s -> s.Bytes)
                    (Codec.int64 |> Codec.describe "The bytes of output it wrote so far.")

            return
                ({
                    Id = id
                    Name = name
                    Command = command
                    State = state
                    ExitCode = exitCode
                    Reason = reason
                    Bytes = bytes
                }
                : JobStatus)
        }
        |> Codec.describe "Whether a background job runs, or how it ended, and how much it printed."
        |> Codec.named "JobStatus"

    let outputOptions: Codec<OutputOptions> =
        Codec.record {
            let! from =
                Codec.optionalField
                    "from"
                    (fun (o: OutputOptions) -> o.From)
                    (Codec.int64
                     |> Codec.atLeast 0L
                     |> Codec.describe "A byte offset (a `next` you got); by default the last `maxChars`.")

            and! maxChars =
                Codec.optionalField
                    "maxChars"
                    (fun o -> o.MaxChars)
                    (Codec.int |> Codec.atLeast 1 |> Codec.describe "At most this many characters.")

            return ({ From = from; MaxChars = maxChars }: OutputOptions)
        }
        |> Codec.describe "Which part of a job's output to read."
        |> Codec.named "OutputOptions"

    let waitOptions: Codec<WaitOptions> =
        Codec.record {
            let! timeoutMs =
                Codec.optionalField
                    "timeoutMs"
                    (fun (o: WaitOptions) -> o.TimeoutMs)
                    (Codec.int
                     |> Codec.atLeast 1
                     |> Codec.describe "Gives up after this many milliseconds (and at the end of the cell's time).")

            return ({ TimeoutMs = timeoutMs }: WaitOptions)
        }
        |> Codec.describe "How long to wait for a job."
        |> Codec.named "WaitOptions"

/// A session's background jobs as the strategy knows them (T143): their ids, numbered on from the step
/// journal, and their starts and ends, journaled there. It learns of an end only from data a recording
/// journals (what a cell's `jobs.*` call answered, a job's notice among a turn's inputs, the journal
/// after the host restarted), never from a process's timing, so a recorded session replays exactly.
[<Sealed>]
type SessionJobs<'K>(session: string, writer: JournalWriter) =
    let gate = obj ()
    let mutable prepared = false
    let mutable last = 0
    let known = Dictionary<string, Fx.CodeAct.JobStarted>(StringComparer.Ordinal)
    let ended = Dictionary<string, JobEnd>(StringComparer.Ordinal)
    // Jobs started before this process and not ended: lost with the host that ran them.
    let mutable orphans: Fx.CodeAct.JobStarted list = []
    let mutable hands: Jobs<'K> = Jobs.none

    let number (id: string) =
        match Int32.TryParse(id.Substring(min 1 id.Length)) with
        | true, n when id.StartsWith 'j' -> n
        | _ -> 0

    member _.Session = session

    /// The running turn's hands' jobs: a kernel outlives the turn that started it.
    member _.Hands
        with get () = lock gate (fun () -> hands)
        and set (value: Jobs<'K>) = lock gate (fun () -> hands <- value)

    /// Reads the session's journal, once in this host process: the last job's number, the jobs
    /// started and those ended.
    member _.Prepare<'E when 'E :> IScope>() : Eff<'E, unit> =
        eff {
            if not (lock gate (fun () -> prepared)) then
                let! events = StepJournal.load writer.Log session

                lock gate (fun () ->
                    if not prepared then
                        prepared <- true

                        for recorded in events do
                            match recorded.Event with
                            | StepEvent.JobStarted started ->
                                known[started.Job] <- started
                                last <- max last (number started.Job)
                            | StepEvent.JobEnded e -> ended[e.Job] <- e.How
                            | _ -> ()

                        orphans <-
                            known.Values
                            |> Seq.filter (fun started -> not (ended.ContainsKey started.Job))
                            |> Seq.sortBy (fun started -> number started.Job)
                            |> List.ofSeq)
        }

    /// Journals a job's end, the first time the strategy learns it.
    member _.End<'E when 'E :> IClock and 'E :> IScope>(job: string, how: JobEnd, bytes: int64 option) : Eff<'E, unit> =
        eff {
            let first =
                lock gate (fun () ->
                    if known.ContainsKey job && not (ended.ContainsKey job) then
                        ended[job] <- how
                        true
                    else
                        false)

            if first then
                do! writer.Append($"jobs/{job}/ended", StepEvent.JobEnded { Job = job; How = how; Bytes = bytes })
        }

    /// What a cell's call answered about a job: journals its end if it ended.
    member this.Observe<'E when 'E :> IClock and 'E :> IScope>(info: JobInfo) : Eff<'E, unit> =
        match info.State with
        | JobState.Running -> Eff.unit
        | JobState.Exited code -> this.End(info.Id, JobEnd.Exited code, Some info.Bytes)
        | JobState.Killed -> this.End(info.Id, JobEnd.Killed, Some info.Bytes)
        | JobState.Failed reason -> this.End(info.Id, JobEnd.Failed reason, Some info.Bytes)

    /// The inputs a turn got (at its start or a step's boundary): a job's notice says it ended.
    member this.Delivered<'E when 'E :> IClock and 'E :> IScope>(inputs: Input list) : Eff<'E, unit> =
        eff {
            for input in inputs do
                match input.Content with
                | InputContent.JobFinished(job, note) -> do! this.End(job, JobEnd.Reported note, None)
                | _ -> ()
        }

    /// Jobs started before this host process that never ended (once): each was lost with the host that
    /// ran it, unless its session was told it ended (`items`: the session's log). The notes for the
    /// model's next call, for the jobs it was not told about.
    member this.Orphans<'E when 'E :> IClock and 'E :> IScope>(items: LogItem list) : Eff<'E, Block list> =
        eff {
            let todo =
                lock gate (fun () ->
                    let found = orphans
                    orphans <- []
                    found)

            let untold = ResizeArray<Fx.CodeAct.JobStarted>()

            for started in todo do
                let notice =
                    items
                    |> List.tryPick (fun item ->
                        match item with
                        | LogItem.Session(_, Event.JobNotified input) ->
                            match input.Content with
                            | InputContent.JobFinished(job, note) when job = started.Job -> Some note
                            | _ -> None
                        | _ -> None)

                match notice with
                | Some note -> do! this.End(started.Job, JobEnd.Reported note, None)
                | None ->
                    do! this.End(started.Job, JobEnd.Lost, None)
                    untold.Add started

            if untold.Count = 0 then
                return []
            else
                let jobs =
                    untold
                    |> Seq.map (fun started -> $"{started.Job} (`{started.Name}`)")
                    |> String.concat ", "

                return
                    [
                        {
                            Id = "note:jobs-lost"
                            Version = None
                            Pieces =
                                [
                                    Piece.Text
                                        $"[Note: background jobs were lost when the host stopped, with their output: {jobs}. Start them again if they are still needed.]"
                                ]
                        }
                    ]
        }

    /// A new job's start, journaled before its process starts.
    member _.Begin<'E when 'E :> IClock and 'E :> IScope>
        (name: string option, command: string, cwd: string option, call: CallRef option)
        : Eff<'E, Fx.CodeAct.JobStarted> =
        eff {
            let started: Fx.CodeAct.JobStarted =
                lock gate (fun () ->
                    last <- last + 1
                    let id = $"j{last}"

                    let started: Fx.CodeAct.JobStarted =
                        {
                            Job = id
                            Name =
                                match name with
                                | Some name when not (String.IsNullOrWhiteSpace name) -> name
                                | _ -> command
                            Command = command
                            Cwd = cwd
                            Call = call
                        }

                    known[id] <- started
                    started)

            do! writer.Append($"jobs/{started.Job}/started", StepEvent.JobStarted started)
            return started
        }

    /// A job's start, if the session started it.
    member _.Started(id: string) : Fx.CodeAct.JobStarted option =
        lock gate (fun () ->
            match known.TryGetValue id with
            | true, started -> Some started
            | _ -> None)

    /// How a job ended, if the strategy knows.
    member _.Ended(id: string) : JobEnd option =
        lock gate (fun () ->
            match ended.TryGetValue id with
            | true, how -> Some how
            | _ -> None)

    /// Why a turn cannot wait for a job (`Ensure.jobs`): it is not one of the session's, or it ended.
    member this.Check(id: string) : string option =
        match this.Started id, this.Ended id with
        | None, _ -> Some $"there is no background job {Json.quote id} in this session"
        | Some _, Some how ->
            Some $"job {id} ended already ({how}): wait only for jobs that run; the message of its end says how it went"
        | Some _, None -> None

    override _.ToString() = $"jobs of {session}"

/// The `jobs` bindings of the host bridge (spec §6.5, T143): background jobs of the session, in the
/// workspace's shell. Built per session from its `SessionJobs`; `jobs.start` is granted as `shell.run`
/// is (`Permissions.jobsStart`), and `jobs.wait` shares the cell's timeout (`CellCommands`).
[<RequireQualifiedAccess>]
module JobBindings =
    /// The group's description, for the bridge's declarations.
    let groups: (string * string) list =
        [
            "jobs",
            "Background jobs: commands that run while your cells and turns go on. When one ends on its own you get a message with its exit code and last lines."
        ]

    let private failure (code: string) (message: string) =
        HostCallError.Failed(code, message, None)

    let private statusOf (jobs: SessionJobs<'K>) (info: JobInfo) : JobStatus =
        let state, code, reason =
            match info.State with
            | JobState.Running -> "running", None, None
            | JobState.Exited code -> "exited", Some code, None
            | JobState.Killed -> "killed", None, None
            | JobState.Failed reason -> "failed", None, Some reason

        ({
            Id = info.Id
            Name =
                jobs.Started info.Id
                |> Option.map (fun started -> started.Name)
                |> Option.defaultValue info.Id
            Command = info.Command
            State = state
            ExitCode = code
            Reason = reason
            Bytes = info.Bytes
        }
        : JobStatus)

    /// Why a job is not there: none has the id, or it ran before the host restarted.
    let private missing (jobs: SessionJobs<'K>) (id: string) : HostCallError =
        match jobs.Started id, jobs.Ended id with
        | Some _, Some JobEnd.Lost ->
            failure "lost" $"job {id} was lost: the host stopped while it ran, and its output is gone"
        | Some _, _ -> failure "gone" $"job {id} ran before the host restarted: its output is gone"
        | None, _ -> failure "notFound" $"there is no background job {Json.quote id} in this session"

    let private jobArg = Arg.required "id" "The job's id (j1, …)." Codec.string

    /// The bindings, with the turn's settings.
    let all<'K when 'K :> IClock and 'K :> IScope>
        (jobs: SessionJobs<'K>)
        (writer: JournalWriter)
        (settings: CoreSettings)
        : HostBinding<'K> list =
        let reference (id: string) : JobRef = { Session = jobs.Session; Id = id }

        /// What a call answered about a job: its end journaled, then its status.
        let answered (id: string) (found: JobInfo option) : Eff<'K, Result<JobStatus, HostCallError>> =
            eff {
                match found with
                | None -> return Error(missing jobs id)
                | Some info ->
                    do! jobs.Observe info
                    return Ok(statusOf jobs info)
            }

        [
            HostBinding.create2
                "jobs.start"
                "Starts a command line in the background with the workspace's shell (as shell.run runs it) and returns at once; the job runs while your cells and turns go on. To wait for it longer than a cell, end the turn with finish({ kind: \"waiting\", jobs: [id], note })."
                (Arg.required "command" "The command line." (Codec.string |> Codec.nonEmpty),
                 Arg.optional "options" "" JobBindingCodecs.options)
                JobBindingCodecs.job
                (fun (command, options) ->
                    eff {
                        let options =
                            options
                            |> Option.defaultValue (
                                {
                                    Name = None
                                    Cwd = None
                                    Stdin = None
                                }
                                : JobOptions
                            )

                        let call = writer.Running |> Option.map (fun action -> action.Call)
                        let! started = jobs.Begin(options.Name, command, options.Cwd, call)

                        let launch: JobLaunch =
                            {
                                Session = jobs.Session
                                Id = started.Job
                                Command = command
                                Cwd =
                                    options.Cwd
                                    |> Option.map (CoreBindings.resolve settings)
                                    |> Option.defaultValue settings.Workspace
                                Env = settings.CommandEnv
                                Stdin = options.Stdin
                            }

                        match! jobs.Hands.Start launch with
                        | Error reason ->
                            do! jobs.End(started.Job, JobEnd.Failed reason, None)
                            return Error(failure "notStarted" $"could not start the job: {reason}")
                        | Ok info ->
                            // The action's receipt names it, for people (`Receipt.Jobs`).
                            match writer.Running with
                            | Some action ->
                                action.Started
                                    {
                                        Id = started.Job
                                        Name = started.Name
                                        Command = command
                                    }
                            | None -> ()

                            do! jobs.Observe info

                            return
                                Ok(
                                    {
                                        Id = started.Job
                                        Name = started.Name
                                        Command = command
                                    }
                                    : Job
                                )
                    })

            HostBinding.create0
                "jobs.list"
                "The session's background jobs that this host started, oldest first."
                (Codec.list JobBindingCodecs.status)
                (fun () ->
                    eff {
                        let! found = jobs.Hands.List jobs.Session

                        for info in found do
                            do! jobs.Observe info

                        return Ok(found |> List.map (statusOf jobs))
                    })

            HostBinding.create1
                "jobs.status"
                "Whether a job runs, or how it ended."
                jobArg
                JobBindingCodecs.status
                (fun id ->
                    eff {
                        let! found = jobs.Hands.Info(reference id)
                        return! answered id found
                    })

            HostBinding.create2
                "jobs.output"
                "A job's output (standard output and error together, as they arrived): by default its last characters; from a byte offset with `from`. Pass `next` as `from` to read on."
                (jobArg, Arg.optional "options" "" JobBindingCodecs.outputOptions)
                JobCodecs.output
                (fun (id, options) ->
                    eff {
                        let read: JobRead =
                            {
                                Job = reference id
                                From = options |> Option.bind (fun o -> o.From)
                                MaxChars =
                                    options
                                    |> Option.bind (fun o -> o.MaxChars)
                                    |> Option.defaultValue settings.OutputChars
                            }

                        match! jobs.Hands.Read read with
                        | Some output -> return Ok output
                        | None -> return Error(missing jobs id)
                    })

            HostBinding.create2
                "jobs.wait"
                "Waits until a job ends or `timeoutMs` passes, and gives its status. The wait counts against the cell's timeout as a command does; to wait longer, end the turn waiting for the job."
                (jobArg, Arg.optional "options" "" JobBindingCodecs.waitOptions)
                JobBindingCodecs.status
                (fun (id, options) ->
                    eff {
                        let asked: JobWait =
                            {
                                Job = reference id
                                TimeoutMs = options |> Option.bind (fun o -> o.TimeoutMs)
                            }

                        let! found = jobs.Hands.Wait asked

                        return! answered id found
                    })

            HostBinding.create1
                "jobs.kill"
                "Stops a job and what it started; gives its status."
                jobArg
                JobBindingCodecs.status
                (fun id ->
                    eff {
                        let! found = jobs.Hands.Kill(reference id)
                        return! answered id found
                    })
        ]
