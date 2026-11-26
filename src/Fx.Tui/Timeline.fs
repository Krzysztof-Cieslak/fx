namespace Fx.Tui

open System
open Fx.Contracts
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Prompt
open Fx.Sessions
open Fx.Store

/// An entry of a session's log as the TUI keeps it: one of the session's own events, or one its
/// turns recorded in the context log (a reply, a receipt, a frame).
[<RequireQualifiedAccess>]
type Entry =
    | Session of Recorded<Event>
    | Context of Recorded<ContextEvent>

    override this.ToString() =
        match this with
        | Session recorded -> "session " + string recorded.Seq
        | Context recorded -> "context " + string recorded.Seq

/// A session as a client sees it (spec §5, principle 4: every client view is a projection of the
/// log): the session's state, folded with the session machine's own `evolve`, and its events and
/// context events in the order the agent saw them (a context event after the session event its
/// envelope's cause names, as `ContextLog.merge` puts them).
type Timeline =
    {
        State: SessionState
        /// The last sequence number of the session's stream folded in.
        Seq: int64
        /// The last sequence number of the context stream received.
        ContextSeq: int64
        /// The state came from a snapshot at this sequence number: the events before it are not here.
        ResetAt: int64 option
        /// Newest first.
        Entries: Entry list
        /// Prompts and other inputs by id, to show them where they were delivered.
        Inputs: Map<int64, Input>
    }

    override this.ToString() =
        $"timeline #{this.Seq} (context #{this.ContextSeq}, {this.Entries.Length} entries)"

/// What one action of a turn looks like: a cell, a read, an edit, a command.
type CallView =
    {
        /// The model's id for the tool call.
        Id: string
        Tool: string
        /// The kind of action, for the activity line: `Cell`, `Read`, `Edit`, `Run`, …
        Label: string
        /// The line after the label: the cell's intent, the path, the command.
        Detail: string
        /// Whether it changes the workspace (`◆`) or only looks or runs (`◇`).
        Changes: bool
        /// A cell's code.
        Code: string option
        /// When its reply was recorded and when its receipt was (microseconds since the epoch).
        Started: int64
        Ended: int64 option
        IsError: bool
        /// The receipt as the model read it.
        Output: string
    }

    override this.ToString() = $"{this.Label} {this.Detail}"

/// What happened in a turn, in order.
[<RequireQualifiedAccess>]
type TurnItem =
    /// What the model wrote besides its tool calls.
    | Said of string
    /// What the model wrote in a reply without a tool call that another reply of the turn followed:
    /// the turn went on after it (CodeAct's text-only nudge), so it is a draft of what came next.
    | Superseded of string
    | Call of CallView
    /// Prompts delivered at a step boundary (steering).
    | Steered of Input list
    /// A request the session opened, and how it was settled if it was.
    | Asked of PendingRequest
    | Settled of RequestResolved
    | Interrupted of by: string
    /// The conversation's oldest part was replaced by this summary in the model's context (compaction).
    | Compacted of summary: string

    override this.ToString() =
        match this with
        | Said text -> "said " + string text.Length
        | Superseded text -> "superseded " + string text.Length
        | Call call -> string call
        | Steered inputs -> $"steered {inputs.Length}"
        | Asked request -> "asked " + request.Request.Id
        | Settled resolved -> "settled " + resolved.Request
        | Interrupted by -> "interrupted by " + by
        | Compacted summary -> "compacted " + string summary.Length

type TurnView =
    {
        Turn: int
        Inputs: Input list
        Started: int64
        Items: TurnItem list
        Ended: (TurnEnd * int64) option
    }

    override this.ToString() =
        $"turn {this.Turn} ({this.Items.Length} items)"

/// What the transcript shows, in order.
[<RequireQualifiedAccess>]
type Block =
    /// Earlier history replaced by a snapshot (`subscribe` far behind).
    | Earlier of seq: int64
    | Turn of TurnView
    /// Something outside turns: a request opened or settled, a fork, the session closed.
    | Note of text: string

    override this.ToString() =
        match this with
        | Earlier seq -> "earlier #" + string seq
        | Turn turn -> string turn
        | Note text -> text

[<RequireQualifiedAccess>]
module Timeline =
    let empty: Timeline =
        {
            State = Session.initial
            Seq = 0L
            ContextSeq = 0L
            ResetAt = None
            Entries = []
            Inputs = Map.empty
        }

    /// Where an entry goes: after the session event a context event's cause names, in each stream's
    /// order.
    let private position (entry: Entry) : struct (int64 * int * int64) =
        match entry with
        | Entry.Session recorded -> struct (recorded.Seq, 0, recorded.Seq)
        | Entry.Context recorded ->
            let cause =
                match recorded.Meta.Cause with
                | Some cause when cause.Stream.Value.StartsWith(Session.StreamKind + "/", StringComparison.Ordinal) ->
                    cause.Seq
                | _ -> 0L

            struct (cause, 1, recorded.Seq)

    let private insert (entry: Entry) (entries: Entry list) : Entry list =
        let key = position entry

        let rec go (newer: Entry list) (rest: Entry list) =
            match rest with
            | head :: tail when compare (position head) key > 0 -> go (head :: newer) tail
            | _ -> List.rev newer @ (entry :: rest)

        go [] entries

    let private remember (event: Event) (inputs: Map<int64, Input>) =
        match Session.inputOf event with
        | Some input -> Map.add input.Id input inputs
        | None -> inputs

    /// Folds one item of a session subscription in: an event (once, in order) or a snapshot that
    /// replaces the state (the events before it are gone from the view).
    let session (item: Feed<SessionState, Event>) (timeline: Timeline) : Timeline =
        match item with
        | Feed.Reset snapshot ->
            { timeline with
                State = snapshot.State
                Seq = snapshot.Seq
                ResetAt = Some snapshot.Seq
                Entries =
                    timeline.Entries
                    |> List.filter (fun entry ->
                        match entry with
                        | Entry.Session recorded -> recorded.Seq > snapshot.Seq
                        | Entry.Context _ -> true)
                Inputs =
                    (snapshot.State.Queue
                     @ snapshot.State.Inbox
                     @ (snapshot.State.Running |> Option.map _.Inputs |> Option.defaultValue []))
                    |> List.fold (fun inputs input -> Map.add input.Id input inputs) timeline.Inputs
            }
        | Feed.Next recorded when recorded.Seq <= timeline.Seq -> timeline
        | Feed.Next recorded ->
            { timeline with
                State = Session.evolve timeline.State recorded.Event
                Seq = recorded.Seq
                Entries = insert (Entry.Session recorded) timeline.Entries
                Inputs = remember recorded.Event timeline.Inputs
            }

    /// Adds a context event (once, in order).
    let context (recorded: Recorded<ContextEvent>) (timeline: Timeline) : Timeline =
        if recorded.Seq <= timeline.ContextSeq then
            timeline
        else
            { timeline with
                ContextSeq = recorded.Seq
                Entries = insert (Entry.Context recorded) timeline.Entries
            }

    let private argument (name: string) (arguments: string) : string option =
        match Json.parse arguments with
        | Ok json ->
            match Json.tryField name json with
            | Some(Json.String value) -> Some value
            | Some(Json.Number n) -> Some(string n)
            | _ -> None
        | Error _ -> None

    /// A cell's `// intent:` line, or its first line.
    let intent (code: string) : string option =
        let lines = (Text.clean code).Split('\n') |> Array.map (fun l -> l.Trim())

        match
            lines
            |> Array.tryFind (fun l -> l.StartsWith("// intent:", StringComparison.Ordinal))
        with
        | Some line -> Some(line.Substring("// intent:".Length).Trim())
        | None -> None

    /// An action's activity line, from its tool call.
    let call (at: int64) (toolCall: ToolCall) : CallView =
        let arg name =
            argument name toolCall.Arguments |> Option.defaultValue ""

        let label, detail, changes, code =
            match toolCall.Name with
            | "cell" ->
                let code = arg "code"

                let detail =
                    match intent code with
                    | Some intent -> intent
                    | None -> Text.firstLine code

                "Cell", detail, false, Some code
            | "read" ->
                let path = arg "path"

                let from =
                    match argument "offset" toolCall.Arguments with
                    | Some offset -> ":" + offset
                    | None -> ""

                "Read", path + from, false, None
            | "edit" -> "Edit", arg "path", true, None
            | "bash" -> "Run", Text.firstLine (arg "command"), false, None
            | other -> (if other = "" then "Tool" else other), Text.firstLine toolCall.Arguments, false, None

        {
            Id = toolCall.Id
            Tool = toolCall.Name
            Label = label
            Detail = detail
            Changes = changes
            Code = code
            Started = at
            Ended = None
            IsError = false
            Output = ""
        }

    let private atOf (meta: EventMeta) = meta.At.UnixMicroseconds

    /// The text of the agent's question (`agent.question`): the request's payload.
    let question (request: PendingRequest) : string option =
        if request.Request.Kind = "agent.question" then
            match Json.tryField "question" request.Request.Payload with
            | Some(Json.String text) when not (String.IsNullOrWhiteSpace text) -> Some text
            | _ -> None
        else
            None

    /// What a request asks, as a person reads it: the agent's question itself, else its title.
    let asks (request: PendingRequest) : string =
        question request |> Option.defaultValue request.Request.Title

    let private normalize (text: string) =
        (Text.clean text).Trim().TrimEnd('…').TrimEnd()

    /// The agent's questions of a turn that repeat what the turn's last reply said just before them
    /// (CodeAct asks with a reply's text): the transcript shows that text once.
    let echoes (items: TurnItem list) : Set<string> =
        let folder (said: string list, found: Set<string>) (item: TurnItem) =
            match item with
            | TurnItem.Said text -> said @ [ text ], found
            | TurnItem.Asked request ->
                match question request with
                | Some q when not said.IsEmpty ->
                    let reply = normalize (String.concat "\n\n" said)
                    let q = normalize q

                    if q <> "" && reply.StartsWith(q, StringComparison.Ordinal) then
                        [], Set.add request.Request.Id found
                    else
                        [], found
                | _ -> [], found
            | _ -> [], found

        items |> List.fold folder ([], Set.empty) |> snd

    /// The transcript's blocks: the session's turns with what each did, and what happened between
    /// them.
    let blocks (timeline: Timeline) : Block list =
        let blocks = ResizeArray<Block>()
        let mutable current: TurnView option = None
        // Calls run one after another: each starts when the one before it ended.
        let mutable lastEnd = 0L
        // The turn's last reply had no action (no tool call, no receipt since): if another reply
        // follows, what it said was a draft.
        let mutable draft = false

        let flush () =
            draft <- false

            match current with
            | Some turn ->
                blocks.Add(
                    Block.Turn
                        { turn with
                            Items = List.rev turn.Items
                        }
                )

                current <- None
            | None -> ()

        let add (item: TurnItem) =
            match current with
            | Some turn -> current <- Some { turn with Items = item :: turn.Items }
            | None ->
                match item with
                | TurnItem.Asked request -> blocks.Add(Block.Note("? " + asks request))
                | TurnItem.Settled resolved ->
                    blocks.Add(Block.Note("· " + resolved.Request + " " + string resolved.Resolution))
                | _ -> ()

        let inputs (ids: int64 list) =
            ids |> List.choose (fun id -> Map.tryFind id timeline.Inputs)

        match timeline.ResetAt with
        | Some seq -> blocks.Add(Block.Earlier seq)
        | None -> ()

        for entry in List.rev timeline.Entries do
            match entry with
            | Entry.Session recorded ->
                match recorded.Event with
                | Event.TurnStarted(turn, ids, _, at) ->
                    flush ()

                    current <-
                        Some
                            {
                                Turn = turn
                                Inputs = inputs ids
                                Started = at
                                Items = []
                                Ended = None
                            }
                | Event.InputsDelivered(_, _, ids) ->
                    match inputs ids with
                    | [] -> ()
                    | delivered -> add (TurnItem.Steered delivered)
                | Event.TurnEnded(turn, ended, at) ->
                    match current with
                    | Some view when view.Turn = turn ->
                        current <- Some { view with Ended = Some(ended, at) }
                        flush ()
                    | _ ->
                        flush ()

                        blocks.Add(
                            Block.Turn
                                {
                                    Turn = turn
                                    Inputs = []
                                    Started = at
                                    Items = []
                                    Ended = Some(ended, at)
                                }
                        )
                | Event.Interrupted(Some _, by) -> add (TurnItem.Interrupted by.Principal)
                | Event.RequestOpened request -> add (TurnItem.Asked request)
                | Event.RequestResolved resolved -> add (TurnItem.Settled resolved)
                | Event.Forked(child, upTo, _) -> blocks.Add(Block.Note($"forked into {child} at #{upTo}"))
                | Event.Closed by -> blocks.Add(Block.Note("closed by " + by.Principal))
                | Event.Reopened by -> blocks.Add(Block.Note("reopened by " + by.Principal))
                | _ -> ()
            | Entry.Context recorded ->
                match recorded.Event with
                | ContextEvent.Replied reply ->
                    lastEnd <- atOf recorded.Meta

                    // What the replies without an action said since the turn's last action (newest
                    // first, up to the first call) was superseded by this reply.
                    match current with
                    | Some turn when draft ->
                        let rec supersede (items: TurnItem list) =
                            match items with
                            | TurnItem.Said text :: rest -> TurnItem.Superseded text :: supersede rest
                            | (TurnItem.Call _ as call) :: rest -> call :: rest
                            | other :: rest -> other :: supersede rest
                            | [] -> []

                        current <-
                            Some
                                { turn with
                                    Items = supersede turn.Items
                                }
                    | _ -> ()

                    draft <-
                        current.IsSome
                        && reply.Parts
                           |> List.forall (fun part ->
                               match part with
                               | Part.ToolCall _ -> false
                               | _ -> true)

                    for part in reply.Parts do
                        match part with
                        | Part.Text text when not (String.IsNullOrWhiteSpace text.Text) ->
                            add (TurnItem.Said(text.Text.Trim()))
                        | Part.Refusal text -> add (TurnItem.Said text)
                        | Part.ToolCall toolCall -> add (TurnItem.Call(call (atOf recorded.Meta) toolCall))
                        | _ -> ()
                | ContextEvent.Returned receipt ->
                    draft <- false
                    let text = receipt.Pieces |> List.map Piece.render |> String.concat "\n"

                    let at = atOf recorded.Meta
                    let floor = lastEnd

                    let finish (view: CallView) =
                        { view with
                            Started = max view.Started floor
                            Ended = Some at
                            IsError = receipt.IsError
                            Output = text
                        }

                    match current, receipt.Call with
                    | Some turn, Some id ->
                        // Items are newest first: the calls after this one come before it.
                        let found = ref false

                        let items =
                            turn.Items
                            |> List.rev
                            |> List.map (fun item ->
                                match item with
                                | TurnItem.Call view when view.Id = id && view.Ended.IsNone ->
                                    found.Value <- true
                                    TurnItem.Call(finish view)
                                | TurnItem.Call view when found.Value && view.Ended.IsNone ->
                                    TurnItem.Call
                                        { view with
                                            Started = max view.Started at
                                        }
                                | other -> other)
                            |> List.rev

                        current <- Some { turn with Items = items }
                        lastEnd <- at
                    | _ -> ()
                | ContextEvent.Collapsed collapse ->
                    add (TurnItem.Compacted(collapse.Summary |> List.map Piece.render |> String.concat "\n"))
                | _ -> ()

        flush ()
        List.ofSeq blocks

    /// Whether the transcript shows this request where it was asked.
    let shows (id: string) (timeline: Timeline) : bool =
        timeline.Entries
        |> List.exists (fun entry ->
            match entry with
            | Entry.Session recorded ->
                match recorded.Event with
                | Event.RequestOpened request -> request.Request.Id = id
                | _ -> false
            | Entry.Context _ -> false)

    /// The calls of the running turn that have no receipt yet: the first is running.
    let running (timeline: Timeline) : CallView option =
        match timeline.State.Running with
        | None -> None
        | Some running ->
            blocks timeline
            |> List.tryPick (fun block ->
                match block with
                | Block.Turn turn when turn.Turn = running.Turn ->
                    turn.Items
                    |> List.tryPick (fun item ->
                        match item with
                        | TurnItem.Call view when view.Ended.IsNone -> Some view
                        | _ -> None)
                | _ -> None)

    /// When the running turn last made progress (its start, a reply or a receipt), in microseconds.
    let lastProgress (timeline: Timeline) : int64 option =
        match timeline.State.Running with
        | None -> None
        | Some running ->
            timeline.Entries
            |> List.tryPick (fun entry ->
                match entry with
                | Entry.Context recorded ->
                    match recorded.Meta.Cause with
                    | Some cause when cause.Seq >= running.Seq -> Some(atOf recorded.Meta)
                    | _ -> None
                | Entry.Session recorded when recorded.Seq = running.Seq -> Some running.At
                | Entry.Session _ -> None)
            |> Option.orElse (Some running.At)

    /// The text prompts sent in this session, oldest first: the editor's history.
    let prompts (timeline: Timeline) : string list =
        timeline.Inputs
        |> Map.toList
        |> List.choose (fun (_, input) ->
            match input.Content with
            | InputContent.Text text -> Some text
            | _ -> None)
