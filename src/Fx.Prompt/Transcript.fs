namespace Fx.Prompt

open System.Collections.Generic
open Fx.Machines
open Fx.Models
open Fx.Sessions

/// What the history projects, in log order, each at its position: the session's own events (inputs,
/// turns) and the context events its turns add. A fork's history is its parent's items up to the fork
/// point followed by its own: positions name their stream, so they stay distinct.
[<RequireQualifiedAccess>]
type LogItem =
    | Session of EventRef * Event
    | Context of EventRef * ContextEvent

    member this.Ref =
        match this with
        | Session(ref, _)
        | Context(ref, _) -> ref

    override this.ToString() =
        match this with
        | Session(ref, event) -> $"{ref} {event}"
        | Context(ref, event) -> $"{ref} {event}"

/// What one entry of the history says.
[<RequireQualifiedAccess>]
type EntryContent =
    /// Inputs delivered to a turn: when it started (step 0) or at a step boundary.
    | Inputs of turn: int * step: int * inputs: Input list
    | Reply of ModelReply
    /// A receipt. `answers`: it answers an open call of the last reply (a tool result); otherwise the
    /// model reads it as text.
    | Receipt of Receipt * answers: bool
    /// Calls left without a result when something else happened, each answered with why.
    | Closed of calls: ToolCall list * reason: string
    /// A turn that ended without completing (interrupted, failed, abandoned).
    | Ended of turn: int * TurnEnd
    | Update of FrameUpdate

    override this.ToString() =
        match this with
        | Inputs(turn, step, inputs) -> $"inputs turn {turn} step {step} ({inputs.Length})"
        | Reply reply -> string reply
        | Receipt(receipt, answers) -> string receipt + (if answers then "" else " (as text)")
        | Closed(calls, reason) -> $"closed {calls.Length} calls: {reason}"
        | Ended(turn, ended) -> $"turn {turn} {ended}"
        | Update update -> string update

/// An entry of the history: the position of the event that made it, and its index in the transcript.
type Entry =
    {
        Index: int
        Ref: EventRef
        Content: EntryContent
    }

    override this.ToString() =
        $"#{this.Index} {this.Ref} {this.Content}"

/// A collapse in effect: the entries from `Start` to `End` (indices) show as its summary. `At` is how
/// many entries there were when it was applied.
type CollapsedRange =
    {
        Collapse: Collapse
        Start: int
        End: int
        At: int
    }

    override this.ToString() =
        $"collapsed #{this.Start}..#{this.End} at {this.At}"

/// History before an entry was rewritten after these entries were made: entries `From` to `At - 1`
/// (indices) were made with a history that no longer is. Their replies' reasoning is invalid now.
type Rewrite =
    {
        From: int
        At: int
    }

    override this.ToString() =
        $"rewrite from #{this.From} at {this.At}"

type PinState =
    {
        Spec: PinSpec
        /// Where it was pinned.
        Ref: EventRef
    }

    override this.ToString() = $"{this.Spec} at {this.Ref}"

/// What a history shows, in order: an entry, or the summary of a collapsed range.
[<RequireQualifiedAccess>]
type Visible =
    | Entry of Entry
    | Summary of CollapsedRange

    override this.ToString() =
        match this with
        | Entry entry -> string entry
        | Summary range -> string range

/// The agent's context as a fold over its log (spec §5): the frame, the history's entries, inputs not
/// delivered yet, calls without results, pins and collapses. Append-only: an item adds entries at the
/// end and never changes earlier ones; collapses and new frames are recorded as rewrites.
type Transcript =
    {
        /// The prefix the conversation runs with, once recorded.
        Frame: Block list option
        /// The frame's sections with later updates applied: what `reconcile` compares against.
        Current: Block list
        /// Newest first (`Transcript.entries` gives them in order).
        Entries: Entry list
        Count: int
        /// Inputs received and not delivered yet, by the position of the event that received them.
        Pending: Map<EventRef, Input>
        /// The last reply's calls that have no result yet.
        Open: ToolCall list
        /// In the order they were pinned.
        Pins: PinState list
        /// In the order they were applied.
        Collapses: CollapsedRange list
        Rewrites: Rewrite list
        /// Items that could not apply (e.g. a collapse that splits a call from its result), newest first.
        Issues: string list
    }

    override this.ToString() =
        $"transcript ({this.Count} entries, {this.Open.Length} open calls, {this.Pins.Length} pins, {this.Collapses.Length} collapses)"

[<RequireQualifiedAccess>]
module Frame =
    /// Sections with an update applied: changed ones replaced where they were, new ones at the end,
    /// removed ones left out.
    let apply (update: FrameUpdate) (sections: Block list) : Block list =
        let replaced =
            sections
            |> List.map (fun section ->
                match update.Changed |> List.tryFind (fun c -> c.Id = section.Id) with
                | Some changed -> changed
                | None -> section)

        let added =
            update.Changed
            |> List.filter (fun c -> not (sections |> List.exists (fun s -> s.Id = c.Id)))

        replaced @ added
        |> List.filter (fun s -> not (update.Removed |> List.contains s.Id))

[<RequireQualifiedAccess>]
module Transcript =
    let empty: Transcript =
        {
            Frame = None
            Current = []
            Entries = []
            Count = 0
            Pending = Map.empty
            Open = []
            Pins = []
            Collapses = []
            Rewrites = []
            Issues = []
        }

    /// The entries, oldest first.
    let entries (transcript: Transcript) : Entry list = List.rev transcript.Entries

    let private append (ref: EventRef) (content: EntryContent) (t: Transcript) : Transcript =
        { t with
            Entries =
                {
                    Index = t.Count
                    Ref = ref
                    Content = content
                }
                :: t.Entries
            Count = t.Count + 1
        }

    /// Answers the calls left open, so no call ever goes to a model without a result.
    let private close (ref: EventRef) (reason: string) (t: Transcript) : Transcript =
        if t.Open.IsEmpty then
            t
        else
            append ref (EntryContent.Closed(t.Open, reason)) { t with Open = [] }

    let private issue (ref: EventRef) (message: string) (t: Transcript) : Transcript =
        { t with
            Issues = $"{ref}: {message}" :: t.Issues
        }

    let private deliver (ref: EventRef) (turn: int) (step: int) (ids: int64 list) (t: Transcript) : Transcript =
        let keys = ids |> List.map (EventRef.create ref.Stream)
        let inputs = keys |> List.choose (fun key -> t.Pending |> Map.tryFind key)

        if inputs.IsEmpty then
            t
        else
            append
                ref
                (EntryContent.Inputs(turn, step, inputs))
                { t with
                    Pending = keys |> List.fold (fun pending key -> Map.remove key pending) t.Pending
                }

    /// Applies one of the session's events (docs/modules/sessions.md).
    let applySession (ref: EventRef) (event: Event) (t: Transcript) : Transcript =
        match event with
        | Event.TurnStarted(turn, ids, _, _) -> t |> close ref "a new turn started" |> deliver ref turn 0 ids
        | Event.InputsDelivered(turn, step, ids) -> deliver ref turn step ids t
        | Event.PromptWithdrawn(id, _) ->
            { t with
                Pending = t.Pending |> Map.remove (EventRef.create ref.Stream id)
            }
        | Event.TurnEnded(turn, ended, _) ->
            let reason =
                match ended with
                | TurnEnd.Completed _ -> "the turn ended"
                | TurnEnd.Interrupted -> "the turn was interrupted"
                | TurnEnd.Failed _ -> "the turn failed"
                | TurnEnd.Abandoned _ -> "the turn was abandoned"

            let t = close ref reason t

            match ended with
            | TurnEnd.Completed _ -> t
            | _ -> append ref (EntryContent.Ended(turn, ended)) t
        | event ->
            match Session.inputOf event with
            | Some input ->
                { t with
                    Pending = t.Pending |> Map.add (EventRef.create ref.Stream input.Id) input
                }
            | None -> t

    let private callsOf (reply: ModelReply) : ToolCall list =
        reply.Parts
        |> List.choose (fun part ->
            match part with
            | Part.ToolCall call -> Some call
            | _ -> None)

    let private collapse (ref: EventRef) (c: Collapse) (t: Transcript) : Transcript =
        let entries = entries t |> Array.ofList
        let start = entries |> Array.tryFindIndex (fun e -> e.Ref = c.From)
        let finish = entries |> Array.tryFindIndexBack (fun e -> e.Ref = c.To)

        match start, finish with
        | Some s, Some e when s <= e ->
            let called = HashSet<string>()
            let answered = HashSet<string>()

            for i in s..e do
                match entries[i].Content with
                | EntryContent.Reply reply ->
                    for call in callsOf reply do
                        called.Add call.Id |> ignore
                | EntryContent.Receipt({ Call = Some id }, true) -> answered.Add id |> ignore
                | EntryContent.Closed(calls, _) ->
                    for call in calls do
                        answered.Add call.Id |> ignore
                | _ -> ()

            let contains (outer: CollapsedRange) (start: int) (finish: int) =
                outer.Start <= start && finish <= outer.End

            if not (called.SetEquals answered) then
                issue ref "the collapse would split a tool call from its result" t
            elif t.Collapses |> List.exists (fun x -> contains x s e) then
                issue ref "the range is collapsed already" t
            elif
                t.Collapses
                |> List.exists (fun x -> not (x.End < s || e < x.Start) && not (s <= x.Start && x.End <= e))
            then
                issue ref "the collapse overlaps another without containing it" t
            else
                { t with
                    Collapses =
                        t.Collapses
                        @ [
                            {
                                Collapse = c
                                Start = s
                                End = e
                                At = t.Count
                            }
                        ]
                    Rewrites = { From = s; At = t.Count } :: t.Rewrites
                }
        | _ -> issue ref "the collapse's range is not in the history" t

    /// Applies one context event.
    let applyContext (ref: EventRef) (event: ContextEvent) (t: Transcript) : Transcript =
        match event with
        | ContextEvent.Framed sections ->
            let rewrites =
                match t.Frame with
                | Some _ -> { From = 0; At = t.Count } :: t.Rewrites
                | None -> t.Rewrites

            { t with
                Frame = Some sections
                Current = sections
                Rewrites = rewrites
            }
        | ContextEvent.Updated update ->
            append
                ref
                (EntryContent.Update update)
                { t with
                    Current = Frame.apply update t.Current
                }
        | ContextEvent.Replied reply ->
            let t = close ref "the model replied again first" t

            append ref (EntryContent.Reply reply) { t with Open = callsOf reply }
        | ContextEvent.Returned receipt ->
            match receipt.Call with
            | Some id when t.Open |> List.exists (fun c -> c.Id = id) ->
                append
                    ref
                    (EntryContent.Receipt(receipt, true))
                    { t with
                        Open = t.Open |> List.filter (fun c -> c.Id <> id)
                    }
            | _ -> append ref (EntryContent.Receipt(receipt, false)) t
        | ContextEvent.Pinned spec ->
            { t with
                Pins =
                    (t.Pins |> List.filter (fun p -> p.Spec.Name <> spec.Name))
                    @ [ { Spec = spec; Ref = ref } ]
            }
        | ContextEvent.Unpinned name ->
            { t with
                Pins = t.Pins |> List.filter (fun p -> p.Spec.Name <> name)
            }
        | ContextEvent.Collapsed c -> collapse ref c t

    let apply (t: Transcript) (item: LogItem) : Transcript =
        match item with
        | LogItem.Session(ref, event) -> applySession ref event t
        | LogItem.Context(ref, event) -> applyContext ref event t

    /// The transcript of a log (a fork's: its parent's items up to the fork point, then its own).
    let ofItems (items: LogItem seq) : Transcript = Seq.fold apply empty items

    /// What the history shows, in order: entries outside collapsed ranges, and each outermost range's
    /// summary where the range began.
    let visible (t: Transcript) : Visible list =
        let outer =
            t.Collapses
            |> List.filter (fun c ->
                not (
                    t.Collapses
                    |> List.exists (fun o -> o <> c && o.Start <= c.Start && c.End <= o.End)
                ))
            |> List.sortBy _.Start

        let result = ResizeArray<Visible>()
        let mutable ranges = outer

        for entry in entries t do
            match ranges with
            | range :: rest when entry.Index > range.End -> ranges <- rest
            | _ -> ()

            match ranges with
            | range :: _ when entry.Index = range.Start -> result.Add(Visible.Summary range)
            | range :: _ when entry.Index > range.Start && entry.Index <= range.End -> ()
            | _ -> result.Add(Visible.Entry entry)

        List.ofSeq result

    /// Whether a reply's reasoning was made with a history that has since been rewritten.
    let isStale (index: int) (t: Transcript) : bool =
        t.Rewrites |> List.exists (fun r -> r.From <= index && index < r.At)

    /// The event that keeps the frame stable for the next call: `Framed` with the sections before the
    /// first call; afterwards `Updated` with the sections that changed or went away (stated at the end
    /// of the history, so the prefix and every reply's reasoning stay valid); `None` when nothing
    /// changed. Sections are compared by id and content; their order counts only when first framed.
    let reconcile (sections: Block list) (t: Transcript) : ContextEvent option =
        match t.Frame with
        | None -> Some(ContextEvent.Framed sections)
        | Some _ ->
            let changed = sections |> List.filter (fun s -> not (t.Current |> List.contains s))

            let removed =
                t.Current
                |> List.filter (fun c -> not (sections |> List.exists (fun s -> s.Id = c.Id)))
                |> List.map _.Id

            if changed.IsEmpty && removed.IsEmpty then
                None
            else
                Some(ContextEvent.Updated { Changed = changed; Removed = removed })

    /// The last block with this id the visible history showed (in a reply's `Shown`), if any.
    let lastShown (id: string) (visible: Visible list) : Block option =
        visible
        |> List.rev
        |> List.tryPick (fun v ->
            match v with
            | Visible.Entry { Content = EntryContent.Reply reply } ->
                reply.Shown |> List.tryFindBack (fun b -> b.Id = id)
            | _ -> None)
