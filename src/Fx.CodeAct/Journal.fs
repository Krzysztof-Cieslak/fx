namespace Fx.CodeAct

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Envd

/// A tool call of a turn's reply, and which attempt at running it (a person may choose to run a call
/// again after the host stopped during it).
type CallRef =
    {
        Turn: int
        Step: int
        /// The call's place in its reply, from 1.
        Index: int
        /// The model's id of the tool call.
        Id: string
        /// 1 for the first run.
        Attempt: int
    }

    /// The call's name in the turn, `t<turn>-s<step>-<index>`: a cell's id in its kernel.
    member this.Name = $"t{this.Turn}-s{this.Step}-{this.Index}"

    override this.ToString() =
        $"call {this.Id} ({this.Name}, attempt {this.Attempt})"

/// A call about to run, journaled before anything it does (spec §5, crash recovery).
type CallStarted =
    {
        Call: CallRef
        /// The tool: `cell`, `read`, `edit`, `bash`.
        Tool: string
        /// The cell's id in its kernel (cells only).
        Cell: string option
        /// The cell's `// intent:` line.
        Intent: string option
        /// SHA-256 (hex) of the tool's name and its arguments, canonical when they are JSON.
        Fingerprint: string
        /// The workspace when the step's actions began, if it was recorded.
        Snapshot: SnapshotRef option
    }

    override this.ToString() =
        let cell = this.Cell |> Option.defaultValue "-"
        $"started {this.Call} {this.Tool} {cell}"

/// A host call of the running action, journaled before it runs (bindings that only read are not).
type HostCallStarted =
    {
        Call: CallRef
        /// Its place among the action's journaled host calls, from 1.
        Index: int
        Binding: string
        /// Its arguments, bounded.
        Args: string
    }

    override this.ToString() =
        $"{this.Call} host call {this.Index} {this.Binding}"

/// How a journaled host call ended.
type HostCallEnded =
    {
        Call: CallRef
        Index: int
        Failed: bool
        /// One line: what it returned, or its error.
        Summary: string
    }

    override this.ToString() =
        $"{this.Call} host call {this.Index} ended (failed {this.Failed})"

/// Output the running cell printed, as the kernel streamed it (bounded per call).
type Printed =
    {
        Call: CallRef
        Text: string
    }

    override this.ToString() =
        $"{this.Call} printed {this.Text.Length} chars"

/// A kept value's JSON: in the journal, or in the host's blob store when it is large.
[<RequireQualifiedAccess>]
type KeptJson =
    | Inline of Json
    /// Its canonical JSON, UTF-8, in the host's blob store.
    | Stored of BlobRef

    override this.ToString() =
        match this with
        | Inline _ -> "inline"
        | Stored blob -> $"stored {blob}"

/// A cell's completion value, kept for `recall` (spec §5, §6.5: `fx.recall`, `Out`).
type KeptValue =
    {
        Call: CallRef
        /// The cell's id: the value was `Out[cell]`.
        Cell: string
        Value: KeptJson
        /// The value's type in the kernel's language.
        Type: string
    }

    override this.ToString() = $"kept {this.Cell} ({this.Type})"

/// A cell's completion value that could not be kept, and why.
type LostValue =
    {
        Call: CallRef
        Cell: string
        Type: string
        Reason: string
    }

    override this.ToString() =
        $"lost {this.Cell} ({this.Type}): {this.Reason}"

/// How a kernel of the session ended (spec §6.5: a kernel lives for the session unless it is killed or
/// the host restarts). The host learns it when a cell finds the kernel gone, when it kills the kernel,
/// or when it starts a kernel after the host restarted.
[<RequireQualifiedAccess>]
type KernelEnd =
    /// Its process ended without the host asking (it crashed, or something outside killed it): its
    /// exit code if it gave one, and the end of its standard error.
    | Exited of code: int option * stderr: string
    /// It never ran: its launch failed (why, for people).
    | NotStarted of reason: string
    /// The host killed it: an interrupted cell did not stop within the grace period.
    | Killed
    /// The host stopped while it ran (a host does not take over the kernels of the one before it).
    | HostStopped

    override this.ToString() =
        match this with
        | Exited(Some code, _) -> $"exited ({code})"
        | Exited(None, _) -> "exited"
        | NotStarted _ -> "not started"
        | Killed -> "killed"
        | HostStopped -> "host stopped"

/// A kernel of the session ended.
type KernelEnded =
    {
        /// The launch that ended.
        Launch: int
        How: KernelEnd
    }

    override this.ToString() =
        $"kernel launch {this.Launch} {this.How}"

/// What the step journal records (spec §5: step results journaled): facts kept forever, in the
/// stream `steps/<session>`. A call's start is journaled before it runs; its end is its receipt in the
/// context log (`Returned`), so a call with a start and no receipt was in flight when the host stopped.
[<RequireQualifiedAccess>]
type StepEvent =
    /// A kernel of the session is about to start, with its launch's number (each launch gets fresh
    /// owned directories, also after the host restarted).
    | KernelLaunched of launch: int
    | CallStarted of CallStarted
    | HostCallStarted of HostCallStarted
    | HostCallEnded of HostCallEnded
    | Printed of Printed
    | ValueKept of KeptValue
    | ValueLost of LostValue
    /// A kernel ended, and how: journaled before the launch that replaces it (T135).
    | KernelEnded of KernelEnded

    override this.ToString() =
        match this with
        | KernelLaunched launch -> $"kernel launch {launch}"
        | CallStarted started -> string started
        | HostCallStarted started -> string started
        | HostCallEnded ended -> string ended
        | Printed printed -> string printed
        | ValueKept kept -> string kept
        | ValueLost lost -> string lost
        | KernelEnded ended -> string ended

/// The JSON of the step journal: version 1 of what `steps/<session>` keeps (never change an old
/// event's JSON without a new version and an upcaster; `tests/Fx.CodeAct.Tests/golden/step-events-v1.jsonl`
/// is what version 1 wrote).
[<RequireQualifiedAccess>]
module StepCodecs =
    let private count (low: int) : Codec<int> = Codec.int |> Codec.atLeast low

    let callRef: Codec<CallRef> =
        Codec.record {
            let! turn = Codec.field "turn" (fun (c: CallRef) -> c.Turn) (count 1)
            and! step = Codec.field "step" (fun c -> c.Step) (count 1)
            and! index = Codec.field "index" (fun c -> c.Index) (count 1)
            and! id = Codec.field "id" (fun c -> c.Id) Codec.string
            and! attempt = Codec.defaultField "attempt" (fun c -> c.Attempt) 1 (count 1)

            return
                {
                    Turn = turn
                    Step = step
                    Index = index
                    Id = id
                    Attempt = attempt
                }
        }
        |> Codec.named "CallRef"

    let callStarted: Codec<CallStarted> =
        Codec.record {
            let! call = Codec.field "call" (fun (c: CallStarted) -> c.Call) callRef
            and! tool = Codec.field "tool" (fun c -> c.Tool) Codec.string
            and! cell = Codec.optionalField "cell" (fun c -> c.Cell) Codec.string
            and! intent = Codec.optionalField "intent" (fun c -> c.Intent) Codec.string
            and! fingerprint = Codec.field "fingerprint" (fun c -> c.Fingerprint) Codec.string

            and! snapshot =
                Codec.optionalField "snapshot" (fun c -> c.Snapshot) EnvCodecs.snapshotRef

            return
                {
                    Call = call
                    Tool = tool
                    Cell = cell
                    Intent = intent
                    Fingerprint = fingerprint
                    Snapshot = snapshot
                }
        }
        |> Codec.named "CallStarted"

    let hostCallStarted: Codec<HostCallStarted> =
        Codec.record {
            let! call = Codec.field "call" (fun (c: HostCallStarted) -> c.Call) callRef
            and! index = Codec.field "index" (fun c -> c.Index) (count 1)
            and! binding = Codec.field "binding" (fun c -> c.Binding) Codec.string
            and! args = Codec.field "args" (fun c -> c.Args) Codec.string

            return
                {
                    Call = call
                    Index = index
                    Binding = binding
                    Args = args
                }
        }
        |> Codec.named "HostCallStarted"

    let hostCallEnded: Codec<HostCallEnded> =
        Codec.record {
            let! call = Codec.field "call" (fun (c: HostCallEnded) -> c.Call) callRef
            and! index = Codec.field "index" (fun c -> c.Index) (count 1)
            and! failed = Codec.defaultField "failed" (fun c -> c.Failed) false Codec.bool
            and! summary = Codec.field "summary" (fun c -> c.Summary) Codec.string

            return
                {
                    Call = call
                    Index = index
                    Failed = failed
                    Summary = summary
                }
        }
        |> Codec.named "HostCallEnded"

    let printed: Codec<Printed> =
        Codec.record {
            let! call = Codec.field "call" (fun (p: Printed) -> p.Call) callRef
            and! text = Codec.field "text" (fun p -> p.Text) Codec.string
            return { Call = call; Text = text }
        }
        |> Codec.named "Printed"

    let keptJson: Codec<KeptJson> =
        Codec.union "kind" {
            let! inline' =
                Codec.case1 "inline" KeptJson.Inline (Field.required "json" Codec.json)

            and! stored =
                Codec.case1 "stored" KeptJson.Stored (Field.required "blob" Fx.Store.Codecs.blobRef)

            return
                fun k ->
                    match k with
                    | KeptJson.Inline json -> inline' json
                    | KeptJson.Stored blob -> stored blob
        }
        |> Codec.named "KeptJson"

    let keptValue: Codec<KeptValue> =
        Codec.record {
            let! call = Codec.field "call" (fun (k: KeptValue) -> k.Call) callRef
            and! cell = Codec.field "cell" (fun k -> k.Cell) Codec.string
            and! value = Codec.field "value" (fun k -> k.Value) keptJson
            and! typeName = Codec.field "type" (fun k -> k.Type) Codec.string

            return
                {
                    Call = call
                    Cell = cell
                    Value = value
                    Type = typeName
                }
        }
        |> Codec.named "KeptValue"

    let lostValue: Codec<LostValue> =
        Codec.record {
            let! call = Codec.field "call" (fun (l: LostValue) -> l.Call) callRef
            and! cell = Codec.field "cell" (fun l -> l.Cell) Codec.string
            and! typeName = Codec.field "type" (fun l -> l.Type) Codec.string
            and! reason = Codec.field "reason" (fun l -> l.Reason) Codec.string

            return
                {
                    Call = call
                    Cell = cell
                    Type = typeName
                    Reason = reason
                }
        }
        |> Codec.named "LostValue"

    let kernelEnd: Codec<KernelEnd> =
        Codec.union "kind" {
            let! exited =
                Codec.case2
                    "exited"
                    (fun code stderr -> KernelEnd.Exited(code, stderr))
                    (Field.optional "code" Codec.int)
                    (Field.withDefault "stderr" "" Codec.string)

            and! notStarted =
                Codec.case1 "notStarted" KernelEnd.NotStarted (Field.required "reason" Codec.string)

            and! killed = Codec.case0 "killed" KernelEnd.Killed
            and! stopped = Codec.case0 "hostStopped" KernelEnd.HostStopped

            return
                fun e ->
                    match e with
                    | KernelEnd.Exited(code, stderr) -> exited code stderr
                    | KernelEnd.NotStarted reason -> notStarted reason
                    | KernelEnd.Killed -> killed
                    | KernelEnd.HostStopped -> stopped
        }
        |> Codec.named "KernelEnd"

    let kernelEnded: Codec<KernelEnded> =
        Codec.record {
            let! launch = Codec.field "launch" (fun (e: KernelEnded) -> e.Launch) (count 1)
            and! how = Codec.field "how" (fun e -> e.How) kernelEnd
            return { Launch = launch; How = how }
        }
        |> Codec.named "KernelEnded"

    let event: Codec<StepEvent> =
        Codec.union "kind" {
            let! launched =
                Codec.case1 "kernelLaunched" StepEvent.KernelLaunched (Field.required "launch" (count 1))

            and! started = Codec.caseOf "callStarted" StepEvent.CallStarted callStarted

            and! hostStarted =
                Codec.caseOf "hostCallStarted" StepEvent.HostCallStarted hostCallStarted

            and! hostEnded = Codec.caseOf "hostCallEnded" StepEvent.HostCallEnded hostCallEnded
            and! printed' = Codec.caseOf "printed" StepEvent.Printed printed
            and! kept = Codec.caseOf "valueKept" StepEvent.ValueKept keptValue
            and! lost = Codec.caseOf "valueLost" StepEvent.ValueLost lostValue
            and! ended = Codec.caseOf "kernelEnded" StepEvent.KernelEnded kernelEnded

            return
                fun e ->
                    match e with
                    | StepEvent.KernelLaunched launch -> launched launch
                    | StepEvent.CallStarted s -> started s
                    | StepEvent.HostCallStarted s -> hostStarted s
                    | StepEvent.HostCallEnded s -> hostEnded s
                    | StepEvent.Printed p -> printed' p
                    | StepEvent.ValueKept k -> kept k
                    | StepEvent.ValueLost l -> lost l
                    | StepEvent.KernelEnded e -> ended e
        }
        |> Codec.describe
            "What a session's step journal records: kernel launches and how kernels ended, calls before they run, their host calls and output, and cells' values for recall."
        |> Codec.named "StepEvent"

/// Where the strategy journals the steps of a session's turns (spec §5, crash recovery;
/// docs/modules/codeact.md, "Crash recovery"): the stream `steps/<session>` of the host's event store,
/// beside the session's and the context's.
[<RequireQualifiedAccess>]
module StepJournal =
    [<Literal>]
    let StreamKind = "steps"

    /// The most characters of a host call's arguments or result kept in the journal.
    [<Literal>]
    let ArgsChars = 400

    /// The step journal of a session.
    let stream (session: string) : StreamId = StreamId.create StreamKind session

    /// Version 1 of the journal's events.
    let schema: EventSchema<StepEvent> = EventSchema.create StepCodecs.event

    /// The step journal's events of a store's log.
    let typed (log: EventLog<Payload>) : EventLog<StepEvent> = EventLog.typed schema log

    /// A session's journal, in order.
    let load<'R when 'R :> IScope> (log: EventLog<StepEvent>) (session: string) : Eff<'R, Recorded<StepEvent> list> =
        EventLog.readAll log (stream session) 0L

    /// The fingerprint of a call: the tool and its arguments (canonical when they are JSON), hashed.
    let fingerprint (tool: string) (arguments: string) : string =
        let args =
            match Json.parse arguments with
            | Ok json -> Json.serializeCanonical json
            | Error _ -> arguments

        let text =
            Json.serializeCanonical (Json.Object [ "tool", Json.String tool; "arguments", Json.String args ])

        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))

    /// Text cut to at most `limit` characters (never half a surrogate pair), with `…` where it was cut.
    let bound (limit: int) (text: string) : string =
        if text.Length <= limit then
            text
        else
            let cut =
                if Char.IsHighSurrogate text[limit - 2] then
                    limit - 2
                else
                    limit - 1

            text.Substring(0, cut) + "…"

    /// The most output entries journaled per call.
    [<Literal>]
    let MaxPrinted = 64

    /// The most characters of output journaled per call.
    [<Literal>]
    let MaxPrintedChars = 16000

    /// The bindings whose calls only read: not journaled (their calls change nothing to be in doubt about).
    let readOnly: string list = [ "fs.read"; "fs.list"; "fs.stat"; "recall" ]

    /// The last kernel launch a journal records (0: none), and how it ended if the journal says.
    let lastKernel (events: Recorded<StepEvent> list) : int * KernelEnd option =
        events
        |> List.fold
            (fun (last, ended) recorded ->
                match recorded.Event with
                | StepEvent.KernelLaunched launch when launch > last -> launch, None
                | StepEvent.KernelEnded e when e.Launch = last -> last, Some e.How
                | _ -> last, ended)
            (0, None)

    /// The last kernel launch a journal records (0: none).
    let lastLaunch (events: Recorded<StepEvent> list) : int = fst (lastKernel events)

/// The action that runs now in a session, which its host calls and output are attributed to.
[<Sealed>]
type RunningAction(call: CallRef, key: string, cell: string option) =
    let gate = obj ()
    let mutable hostCalls = 0
    let mutable printed = 0
    let mutable printedChars = 0

    member _.Call = call
    /// The prefix of the keys of its journal entries.
    member _.Key = key
    /// The cell's id in its kernel (cells only).
    member _.Cell = cell

    /// The number of its next host call, from 1.
    member _.NextHostCall() =
        lock gate (fun () ->
            hostCalls <- hostCalls + 1
            hostCalls)

    /// The number of its next output entry and how many of `chars` characters it keeps, while its
    /// journaled output has room.
    member _.NextPrinted(chars: int) : (int * int) option =
        lock gate (fun () ->
            let room = StepJournal.MaxPrintedChars - printedChars

            if printed >= StepJournal.MaxPrinted || room <= 0 || chars <= 0 then
                None
            else
                let take = min room chars
                printed <- printed + 1
                printedChars <- printedChars + take
                Some(printed, take))

    override _.ToString() = $"running {call}"

/// Appends to a session's step journal, one entry at a time (the turn, the kernel's host calls and
/// its output notifications write concurrently), each keyed so that a retry appends it once. Knows
/// the action that runs now.
[<Sealed>]
type JournalWriter(log: EventLog<StepEvent>, session: string) =
    let appends = new SemaphoreSlim(1, 1)
    let gate = obj ()
    let mutable running: RunningAction option = None
    let mutable tokens = 0

    let pending = Collections.Generic.Dictionary<int, RunningAction * int>()

    member _.Session = session
    member _.Log = log

    /// The action running now.
    member _.Running = lock gate (fun () -> running)

    /// Marks an action as running (its host calls and output are journaled as its), or none.
    member _.SetRunning(action: RunningAction option) = lock gate (fun () -> running <- action)

    /// Takes a host call of the running action: a token for `EndHostCall`, the action and the call's
    /// number in it.
    member _.BeginHostCall(action: RunningAction) : int * int =
        let index = action.NextHostCall()

        lock gate (fun () ->
            tokens <- tokens + 1
            pending[tokens] <- (action, index)
            tokens, index)

    /// The action and number of a host call `BeginHostCall` took.
    member _.EndHostCall(token: int) : (RunningAction * int) option =
        lock gate (fun () ->
            match pending.TryGetValue token with
            | true, taken ->
                pending.Remove token |> ignore
                Some taken
            | _ -> None)

    /// Appends one event with a key (a retry with the same key appends nothing).
    member _.Append<'E when 'E :> IClock and 'E :> IScope>(key: string, event: StepEvent) : Eff<'E, unit> =
        fun env ->
            task {
                do! appends.WaitAsync env.Scope.Cancellation

                try
                    let stream = StepJournal.stream session
                    let now = env.Clock.Now()

                    let meta =
                        {
                            At = now
                            By = Attribution.system "codeact"
                            Key = IdempotencyKey key
                            Cause = None
                        }

                    let mutable attempts = 0
                    let mutable recorded = false

                    while not recorded do
                        attempts <- attempts + 1
                        let! head = log.Head stream env.Scope.Cancellation

                        match! log.Append stream head meta [ event ] env.Scope.Cancellation with
                        | Ok _
                        | Error(Duplicate _) -> recorded <- true
                        | Error(Conflict _) when attempts < 5 -> ()
                        | Error e -> raise (ConcurrencyException(stream, e))
                finally
                    appends.Release() |> ignore
            }

    override _.ToString() = $"journal of {session}"
