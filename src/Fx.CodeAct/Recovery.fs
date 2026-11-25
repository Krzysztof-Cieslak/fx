namespace Fx.CodeAct

open System
open Fx.Contracts
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Envd
open Fx.Prompt
open Fx.Sessions

/// An option of a decision card: its id (what an answer names), a label and what choosing it does.
type DecisionOption =
    {
        Id: string
        Label: string
        Detail: string
    }

    override this.ToString() = $"{this.Id} ({this.Label})"

/// A host call the call in doubt made, as the step journal shows it.
type HostCallSeen =
    {
        Binding: string
        /// Its arguments, bounded.
        Args: string
        /// How it ended; `None`: it was running when the host stopped.
        Ended: string option
    }

    override this.ToString() =
        let ended = this.Ended |> Option.defaultValue "no result"
        $"{this.Binding} → {ended}"

/// What the logs show of a call in doubt (spec §5: a decision with evidence).
type DoubtEvidence =
    {
        /// What the call printed before the host stopped (its end, bounded).
        Output: string
        /// Its host calls that change things (reads are not journaled), in order.
        HostCalls: HostCallSeen list
        /// The workspace's files that changed since the step began, if snapshots could tell (at most
        /// `Recovery.MaxChanges`).
        Changes: FileChange list option
        /// Why the changes are not known, or how many were left out.
        ChangesNote: string option
    }

    override this.ToString() =
        $"evidence ({this.Output.Length} chars of output, {this.HostCalls.Length} host calls)"

/// The call in doubt.
type DoubtfulCall =
    {
        /// The model's id of the tool call.
        Id: string
        Tool: string
        Turn: int
        Step: int
        Index: int
        Attempt: int
        /// The cell's id in its kernel (cells only).
        Cell: string option
        Intent: string option
        /// The cell's code, or the tool's arguments (bounded).
        Action: string
    }

    override this.ToString() =
        $"{this.Tool} {this.Id} (turn {this.Turn} step {this.Step} #{this.Index}, attempt {this.Attempt})"

/// The payload of a `decision.outcomeUnknown` request: a decision card (spec §12.1, `OutcomeUnknown`
/// recovery) about a call that was running when the host stopped.
type OutcomeUnknown =
    {
        Question: string
        /// `session`: a decision of this session (spec §12.3), never committed to a repository.
        Scope: string
        Call: DoubtfulCall
        Evidence: DoubtEvidence
        Options: DecisionOption list
    }

    override this.ToString() = $"outcome unknown: {this.Call}"

/// The answer to a `decision.outcomeUnknown` request: one of its options, and a note for the agent.
type OutcomeAnswer =
    {
        Option: string
        Note: string option
    }

    override this.ToString() =
        match this.Note with
        | Some note -> $"{this.Option}: {note}"
        | None -> this.Option

/// What people decided about a call in doubt.
[<RequireQualifiedAccess>]
type Decided =
    /// It completed: go on as if it ran to the end.
    | Completed of note: string option
    /// It failed or had no effect.
    | Failed of note: string option
    /// Run it again.
    | Rerun of note: string option
    /// The agent should look at what it did before going on.
    | Inspect of note: string option
    /// Nobody decided: the request was withdrawn or expired.
    | Withdrawn of reason: string

    override this.ToString() =
        match this with
        | Completed _ -> "completed"
        | Failed _ -> "failed"
        | Rerun _ -> "rerun"
        | Inspect _ -> "inspect"
        | Withdrawn reason -> "withdrawn: " + reason

/// A turn the host stopped during, as its logs show it: the last reply, the call that started and
/// has no receipt (in doubt), and the calls after it that never started.
type Halted =
    {
        Turn: int
        Step: int
        /// The call in doubt and its latest start.
        Call: ToolCall
        Started: CallStarted
        /// The reply's other calls without a receipt that never started, with their places.
        NotRun: (int * ToolCall) list
    }

    override this.ToString() =
        $"halted turn {this.Turn} step {this.Step} in {this.Call} ({this.NotRun.Length} not run)"

/// Crash recovery (spec §5, §12.1, §19.1; docs/modules/codeact.md, "Crash recovery"): a call that
/// started and has no receipt is in doubt (`OutcomeUnknown`). The turn pauses on a session-scoped
/// decision with evidence, a request of kind `decision.outcomeUnknown`; nothing runs again unless a
/// person chooses it, and their answer becomes the call's receipt. Pure.
[<RequireQualifiedAccess>]
module Recovery =
    [<Literal>]
    let KindId = "decision.outcomeUnknown"

    /// The most changed files a card lists.
    [<Literal>]
    let MaxChanges = 50

    /// The most characters of output a card and a receipt show.
    [<Literal>]
    let OutputChars = 4000

    /// The most characters of a call's code or arguments a card shows.
    [<Literal>]
    let ActionChars = 4000

    let private count (low: int) : Codec<int> = Codec.int |> Codec.atLeast low

    let option: Codec<DecisionOption> =
        Codec.record {
            let! id =
                Codec.field "id" (fun (o: DecisionOption) -> o.Id) (Codec.string |> Codec.nonEmpty)

            and! label = Codec.field "label" (fun o -> o.Label) Codec.string
            and! detail = Codec.defaultField "detail" (fun o -> o.Detail) "" Codec.string

            return
                {
                    Id = id
                    Label = label
                    Detail = detail
                }
        }
        |> Codec.named "DecisionOption"

    let hostCallSeen: Codec<HostCallSeen> =
        Codec.record {
            let! binding =
                Codec.field "binding" (fun (h: HostCallSeen) -> h.Binding) Codec.string

            and! args = Codec.defaultField "args" (fun h -> h.Args) "" Codec.string
            and! ended = Codec.optionalField "ended" (fun h -> h.Ended) Codec.string

            return
                {
                    Binding = binding
                    Args = args
                    Ended = ended
                }
        }
        |> Codec.named "HostCallSeen"

    let evidenceCodec: Codec<DoubtEvidence> =
        Codec.record {
            let! output =
                Codec.defaultField "output" (fun (e: DoubtEvidence) -> e.Output) "" Codec.string

            and! hostCalls =
                Codec.defaultField "hostCalls" (fun e -> e.HostCalls) [] (Codec.list hostCallSeen)

            and! changes =
                Codec.optionalField "changes" (fun e -> e.Changes) (Codec.list EnvCodecs.fileChange)

            and! changesNote =
                Codec.optionalField "changesNote" (fun e -> e.ChangesNote) Codec.string

            return
                {
                    Output = output
                    HostCalls = hostCalls
                    Changes = changes
                    ChangesNote = changesNote
                }
        }
        |> Codec.named "DoubtEvidence"

    let call: Codec<DoubtfulCall> =
        Codec.record {
            let! id = Codec.field "id" (fun (c: DoubtfulCall) -> c.Id) Codec.string
            and! tool = Codec.field "tool" (fun c -> c.Tool) Codec.string
            and! turn = Codec.field "turn" (fun c -> c.Turn) (count 1)
            and! step = Codec.field "step" (fun c -> c.Step) (count 1)
            and! index = Codec.field "index" (fun c -> c.Index) (count 1)
            and! attempt = Codec.defaultField "attempt" (fun c -> c.Attempt) 1 (count 1)
            and! cell = Codec.optionalField "cell" (fun c -> c.Cell) Codec.string
            and! intent = Codec.optionalField "intent" (fun c -> c.Intent) Codec.string
            and! action = Codec.defaultField "action" (fun c -> c.Action) "" Codec.string

            return
                {
                    Id = id
                    Tool = tool
                    Turn = turn
                    Step = step
                    Index = index
                    Attempt = attempt
                    Cell = cell
                    Intent = intent
                    Action = action
                }
        }
        |> Codec.named "DoubtfulCall"

    let payload: Codec<OutcomeUnknown> =
        Codec.record {
            let! question =
                Codec.field "question" (fun (o: OutcomeUnknown) -> o.Question) (Codec.string |> Codec.nonEmpty)

            and! scope = Codec.defaultField "scope" (fun o -> o.Scope) "session" Codec.string
            and! call' = Codec.field "call" (fun o -> o.Call) call
            and! evidence = Codec.field "evidence" (fun o -> o.Evidence) evidenceCodec

            and! options =
                Codec.field "options" (fun o -> o.Options) (Codec.list option |> Codec.minItems 1)

            return
                {
                    Question = question
                    Scope = scope
                    Call = call'
                    Evidence = evidence
                    Options = options
                }
        }
        |> Codec.describe
            "A decision about a tool call that was running when the host stopped: what happened to it (spec §5, OutcomeUnknown recovery). The evidence is what the logs show; the options say how the turn goes on."
        |> Codec.named "OutcomeUnknownDecision"

    let answer: Codec<OutcomeAnswer> =
        Codec.record {
            let! option =
                Codec.field "option" (fun (a: OutcomeAnswer) -> a.Option) (Codec.string |> Codec.nonEmpty)

            and! note = Codec.optionalField "note" (fun a -> a.Note) Codec.string
            return { Option = option; Note = note }
        }
        |> Codec.describe "The option chosen, and a note for the agent."
        |> Codec.named "OutcomeUnknownAnswer"

    /// The request kind: answered with one of the card's options.
    let kind: RequestKind<OutcomeUnknown, OutcomeAnswer> =
        RequestKind.create
            KindId
            "What happened to a tool call that was running when the host stopped: a decision for a person."
            payload
            answer
        |> RequestKind.withCheck (fun card answer ->
            if card.Options |> List.exists (fun o -> o.Id = answer.Option) then
                Ok()
            else
                Error(
                    "expected one of "
                    + String.concat ", " (card.Options |> List.map (fun o -> Json.quote o.Id))
                ))

    /// The options of every card.
    let options: DecisionOption list =
        [
            {
                Id = "completed"
                Label = "It completed"
                Detail = "Go on as if the call ran to the end; what it did stays."
            }
            {
                Id = "failed"
                Label = "It failed"
                Detail = "Go on as if the call failed; the agent checks what it left behind before relying on it."
            }
            {
                Id = "rerun"
                Label = "Run it again"
                Detail = "Run the call again from the start; its new result is what the agent sees."
            }
            {
                Id = "inspect"
                Label = "Inspect first"
                Detail = "The agent looks at what the call did (files, processes) before it goes on."
            }
        ]

    /// The request's id: derived from the call (and the attempt after the first), so recovering again
    /// opens the same request.
    let requestId (call: CallRef) : string =
        if call.Attempt <= 1 then
            "unknown-" + call.Name
        else
            $"unknown-{call.Name}-{call.Attempt}"

    /// The calls of a reply, with their places (from 1).
    let private callsOf (reply: ModelReply) : (int * ToolCall) list =
        reply.Parts
        |> List.choose (fun part ->
            match part with
            | Part.ToolCall call -> Some call
            | _ -> None)
        |> List.mapi (fun i call -> i + 1, call)

    /// What the logs say of a turn the host stopped during: the call in doubt, if one is (pure). The
    /// session's own context stream counts; a fork's parent's turns are not this session's.
    let find
        (contextStream: StreamId)
        (turn: int)
        (items: LogItem list)
        (journal: Recorded<StepEvent> list)
        : Halted option =
        let own =
            items
            |> List.choose (fun item ->
                match item with
                | LogItem.Context(ref, event) when ref.Stream = contextStream -> Some event
                | _ -> None)

        let reply =
            own
            |> List.choose (fun event ->
                match event with
                | ContextEvent.Replied reply when reply.Turn = turn -> Some reply
                | _ -> None)
            |> List.tryLast

        match reply with
        | None -> None
        | Some reply ->
            let answered =
                own
                |> List.choose (fun event ->
                    match event with
                    | ContextEvent.Returned receipt when receipt.Turn = turn && receipt.Step = reply.Step ->
                        receipt.Call
                    | _ -> None)

            let starts =
                journal
                |> List.choose (fun recorded ->
                    match recorded.Event with
                    | StepEvent.CallStarted started when started.Call.Turn = turn && started.Call.Step = reply.Step ->
                        Some started
                    | _ -> None)

            let latest (id: string) =
                starts
                |> List.filter (fun s -> s.Call.Id = id)
                |> List.sortBy (fun s -> s.Call.Attempt)
                |> List.tryLast

            let unanswered =
                callsOf reply |> List.filter (fun (_, c) -> not (List.contains c.Id answered))

            match
                unanswered
                |> List.tryPick (fun (_, c) -> latest c.Id |> Option.map (fun s -> c, s))
            with
            | None -> None
            | Some(call, started) ->
                Some
                    {
                        Turn = turn
                        Step = reply.Step
                        Call = call
                        Started = started
                        NotRun =
                            unanswered
                            |> List.filter (fun (_, c) -> c.Id <> call.Id && (latest c.Id).IsNone)
                    }

    /// The end of a text, at most `limit` characters, with `…` where it was cut.
    let private tail (limit: int) (text: string) : string =
        if text.Length <= limit then
            text
        else
            let start = text.Length - (limit - 1)

            let start = if Char.IsLowSurrogate text[start] then start + 1 else start

            "…" + text.Substring start

    /// What the journal shows of a call: its output, and its host calls with how they ended.
    let journaled (call: CallRef) (journal: Recorded<StepEvent> list) : string * HostCallSeen list =
        let output =
            journal
            |> List.choose (fun recorded ->
                match recorded.Event with
                | StepEvent.Printed printed when printed.Call = call -> Some printed.Text
                | _ -> None)
            |> String.concat ""

        let ended =
            journal
            |> List.choose (fun recorded ->
                match recorded.Event with
                | StepEvent.HostCallEnded e when e.Call = call -> Some(e.Index, e.Summary)
                | _ -> None)
            |> Map.ofList

        let calls =
            journal
            |> List.choose (fun recorded ->
                match recorded.Event with
                | StepEvent.HostCallStarted s when s.Call = call ->
                    Some
                        {
                            Binding = s.Binding
                            Args = s.Args
                            Ended = ended |> Map.tryFind s.Index
                        }
                | _ -> None)

        tail OutputChars output, calls

    /// The evidence of a call in doubt: the journal's, and the workspace's changes since the step began
    /// (`Ok` the changes, `Error` why they are not known).
    let evidence
        (call: CallRef)
        (journal: Recorded<StepEvent> list)
        (changes: Result<FileChange list, string>)
        : DoubtEvidence =
        let output, calls = journaled call journal

        let changes, note =
            match changes with
            | Ok all when all.Length > MaxChanges ->
                Some(List.truncate MaxChanges all), Some $"{all.Length - MaxChanges} more files changed"
            | Ok all -> Some all, None
            | Error reason -> None, Some reason

        {
            Output = output
            HostCalls = calls
            Changes = changes
            ChangesNote = note
        }

    /// The card for a halted turn (pure).
    let card (halted: Halted) (evidence: DoubtEvidence) : OutcomeUnknown =
        let started = halted.Started
        let call = halted.Call

        let action =
            match started.Tool, Json.parse call.Arguments with
            | "cell", Ok(Json.Object members) ->
                match members |> List.tryFind (fun (name, _) -> name = "code") with
                | Some(_, Json.String code) -> code
                | _ -> call.Arguments
            | _ -> call.Arguments

        let what =
            match started.Cell, started.Intent with
            | Some cell, Some intent -> $"cell {cell} (intent: {intent})"
            | Some cell, None -> $"cell {cell}"
            | None, _ -> $"the {started.Tool} call {call.Id}"

        {
            Question = $"The host stopped while {what} ran, before its result was recorded. What happened to it?"
            Scope = "session"
            Call =
                {
                    Id = call.Id
                    Tool = started.Tool
                    Turn = halted.Turn
                    Step = halted.Step
                    Index = started.Call.Index
                    Attempt = started.Call.Attempt
                    Cell = started.Cell
                    Intent = started.Intent
                    Action = StepJournal.bound ActionChars action
                }
            Evidence = evidence
            Options = options
        }

    /// The request of a card: answered by a person, awaited by the paused turn (it ends with the turn).
    let request (halted: Halted) (card: OutcomeUnknown) : RequestSpec =
        let title =
            match halted.Started.Tool with
            | "cell" -> "The host stopped during a cell"
            | tool -> $"The host stopped during {tool}"

        RequestKind.request
            kind
            (requestId halted.Started.Call)
            title
            (RequestOrigin.Turn halted.Turn)
            Reply.ToOpener
            Lifetime.Turn
            card

    /// What a resolution decides (an answer that does not decode is a defect of the session, which
    /// checked it).
    let decided (resolution: Resolution) : Decided =
        match resolution with
        | Resolution.Answered json
        | Resolution.Expired(Some json) ->
            match answer.Decode json with
            | Ok answer ->
                match answer.Option with
                | "completed" -> Decided.Completed answer.Note
                | "failed" -> Decided.Failed answer.Note
                | "rerun" -> Decided.Rerun answer.Note
                | "inspect" -> Decided.Inspect answer.Note
                | other -> Decided.Withdrawn $"the answer {Json.quote other} is not an option"
            | Error issues -> Decided.Withdrawn("the answer does not decode: " + Issue.renderAtMost 3 issues)
        | Resolution.Cancelled reason -> Decided.Withdrawn reason
        | Resolution.Expired None -> Decided.Withdrawn "it expired"

    let private noted (note: string option) =
        match note with
        | Some note when not (String.IsNullOrWhiteSpace note) -> $" Their note: {note.Trim()}"
        | _ -> ""

    /// The evidence as the model reads it.
    let private evidenceText (evidence: DoubtEvidence) : string =
        let lines = ResizeArray<string>()

        if evidence.Output <> "" then
            lines.Add "What it printed before the host stopped:"
            lines.Add(evidence.Output.TrimEnd())

        if not evidence.HostCalls.IsEmpty then
            lines.Add "Its host calls:"

            for call in evidence.HostCalls do
                let ended = call.Ended |> Option.defaultValue "no result (it was running)"
                lines.Add $"- {call.Binding} {call.Args} → {ended}"

        match evidence.Changes with
        | Some [] -> lines.Add "No file of the workspace changed since the step began."
        | Some changes ->
            lines.Add "Files changed since the step began:"

            for change in changes do
                lines.Add $"- {change.Kind} {change.Path}"
        | None -> ()

        match evidence.ChangesNote with
        | Some note -> lines.Add $"({note})"
        | None -> ()

        String.concat "\n" lines

    /// The receipt of a call in doubt once people decided (not for `Rerun`: the call's new result is
    /// its receipt): what they decided, then the evidence, bounded. And whether it is an error.
    let receipt (maxChars: int) (card: OutcomeUnknown) (decided: Decided) : Piece list * bool =
        let header, isError =
            match decided with
            | Decided.Completed note ->
                $"[The host stopped while this call ran, before its result was recorded. The user decided that it completed.{noted note}]",
                false
            | Decided.Failed note ->
                $"[The host stopped while this call ran, before its result was recorded. The user decided that it failed.{noted note} Check what it left behind before relying on it.]",
                true
            | Decided.Inspect note ->
                $"[The host stopped while this call ran, before its result was recorded. The user asks you to inspect what it did (files, processes) before you go on.{noted note}]",
                true
            | Decided.Rerun note ->
                $"[The host stopped while this call ran, before its result was recorded. The user chose to run it again.{noted note}]",
                false
            | Decided.Withdrawn reason ->
                $"[The host stopped while this call ran, before its result was recorded, and nobody decided what happened ({reason}). Check what it did before you go on.]",
                true

        let body = evidenceText card.Evidence

        [
            Piece.Text header
            if body <> "" then
                Piece.output maxChars body
        ],
        isError

    /// The header of a call's receipt when it ran again.
    let rerun (decided: Decided) : Piece =
        let note =
            match decided with
            | Decided.Rerun note -> noted note
            | _ -> ""

        Piece.Text
            $"[The host stopped while this call ran, before its result was recorded. The user chose to run it again; this is its new result.{note}]"

    /// The receipt of a call of the halted reply that never started.
    let notRun: Piece list =
        [ Piece.Text "Not run: the host stopped before this call started." ]
