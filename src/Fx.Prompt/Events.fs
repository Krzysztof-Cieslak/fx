namespace Fx.Prompt

open Fx.Contracts
open Fx.Machines
open Fx.Models

/// What one model call used, as the strategy measured it when it recorded the reply: for people
/// (clients show the context used and the cost); the render never reads it.
type ReplyUsage =
    {
        Model: ModelRef
        Tokens: Usage
        /// The model's context window when it replied (its catalog entry).
        Context: int option
        /// What the call cost in US dollars, at the catalog's prices then.
        Cost: decimal option
    }

    override this.ToString() = $"{this.Model}: {this.Tokens}"

/// A model's reply to one call, with the suffix it saw (spec §5). What the model saw before it replied
/// stays before its reply in every later prompt: providers check that the history before a reasoning
/// block is unchanged, and a request that is the previous one plus what happened since reads it from
/// the cache.
type ModelReply =
    {
        Turn: int
        Step: int
        /// The views the call's prompt ended with (`Rendered.Prompt.Suffix`), exactly as rendered.
        Shown: Block list
        /// The assistant's parts as the provider returned them, provider state included.
        Parts: Part list
        /// What the call used (absent in replies recorded before it was kept).
        Used: ReplyUsage option
    }

    override this.ToString() =
        $"reply turn {this.Turn} step {this.Step} ({this.Shown.Length} shown, {this.Parts.Length} parts)"

/// A new kernel a cell ran in (spec §6.5): the kernel before it is gone, and with it the names earlier
/// cells declared. For people (clients show it as a line of its own); the model reads the same in the
/// receipt's first piece, and the render never reads this.
type KernelRestart =
    {
        /// The new kernel's launch in the session, from 1.
        Launch: int
        /// Why the kernel before it is gone, in words: `it was killed by signal 9 (SIGKILL)`.
        Reason: string
    }

    override this.ToString() =
        $"kernel launch {this.Launch}: {this.Reason}"

/// Who let a host call run that no grant in force allowed (spec §13).
[<RequireQualifiedAccess>]
type ApprovedBy =
    /// Auto approvals: a sandbox driver confined it, and nobody was asked.
    | Auto
    /// A person answered its grant request (the request's key in the grants log).
    | Person of request: string
    /// Yolo approvals: nobody was asked, whatever it reached.
    | Yolo

    override this.ToString() =
        match this with
        | Auto -> "auto"
        | Person request -> "person (" + request + ")"
        | Yolo -> "yolo"

/// How one of an action's host calls was let run without a grant that already allowed it
/// (docs/modules/codeact.md, "Approval modes"): for people and audits (clients mark the action's
/// activity line `auto`, `yolo` or `asked`); the render never reads it.
type Approval =
    {
        /// The grant's action (`shell.exec`) and resource (`dotnet test`).
        Action: string
        Resource: string
        By: ApprovedBy
    }

    override this.ToString() =
        $"{this.Action} {this.Resource} by {this.By}"

/// A stable receipt: the result of a tool call or cell, as the model will see it from now on (NOOA).
/// It is recorded once, bounded, with large values by reference, so later events never change it.
type Receipt =
    {
        Turn: int
        Step: int
        /// The tool call it answers; `None` for results the model reads as text (e.g. a cell written in
        /// a reply's text).
        Call: string option
        /// The tool or what ran (`cell`, `read`, …).
        Name: string
        Pieces: Piece list
        IsError: bool
        /// The cell ran in a new kernel (absent in receipts recorded before it was kept).
        Restarted: KernelRestart option
        /// How its host calls ran that no grant in force allowed: auto approvals, or a person's
        /// answer (the first `Receipt.MaxApprovals`; empty in receipts recorded before they were kept).
        Approvals: Approval list
    }

    /// The most approvals a receipt keeps (a cell may run many commands; the step journal has each).
    static member MaxApprovals = 16

    override this.ToString() =
        let call = this.Call |> Option.defaultValue "-"
        let error = if this.IsError then " (error)" else ""
        $"receipt {this.Name} {call}{error} ({this.Pieces.Length} pieces)"

/// What a pin keeps in view (spec §6.6 `fx.context.pin`, `pinFile`).
[<RequireQualifiedAccess>]
type Pin =
    /// A value as it was when pinned.
    | Value of Piece list
    /// A file's lines (none: the whole file), shown as they are at each call: a live view.
    | File of path: string * lines: LineRange list

    override this.ToString() =
        match this with
        | Value pieces -> $"value ({pieces.Length} pieces)"
        | File(path, lines) ->
            let lines =
                if lines.IsEmpty then
                    ""
                else
                    ":" + (lines |> List.map string |> String.concat ",")

            "file " + path + lines

type PinSpec =
    {
        Name: string
        Pin: Pin
    }

    override this.ToString() = $"pin {this.Name}: {this.Pin}"

/// Compaction (spec §5, T206): the entries from `From` to `To` (inclusive, in transcript order) are
/// shown as `Summary`. The originals stay in the log, addressable.
type Collapse =
    {
        From: EventRef
        To: EventRef
        Summary: Piece list
    }

    override this.ToString() =
        $"collapse {this.From}..{this.To} ({this.Summary.Length} pieces)"

/// Sections of the frame that changed after the conversation began, stated at the end of the history
/// instead of changing the prefix.
type FrameUpdate =
    {
        Changed: Block list
        Removed: string list
    }

    override this.ToString() =
        $"update ({this.Changed.Length} changed, {this.Removed.Length} removed)"

/// What the agent's turns add to its context (spec §5: model actions become events): facts kept
/// forever, in the session's log order. The session's own events (inputs, turns) are the rest of what
/// the history projects.
[<RequireQualifiedAccess>]
type ContextEvent =
    /// The prefix the conversation runs with (system instructions, the interpreter's prompt, agents,
    /// a repo map): recorded before the first call, sent unchanged from then on. A later `Framed`
    /// starts a new frame: a documented cache and reasoning boundary.
    | Framed of Block list
    /// Sections of the frame changed since; shown in the history where they changed.
    | Updated of FrameUpdate
    | Replied of ModelReply
    | Returned of Receipt
    | Pinned of PinSpec
    | Unpinned of name: string
    | Collapsed of Collapse

    override this.ToString() =
        match this with
        | Framed blocks -> $"framed ({blocks.Length} sections)"
        | Updated update -> string update
        | Replied reply -> string reply
        | Returned receipt -> string receipt
        | Pinned pin -> string pin
        | Unpinned name -> "unpin " + name
        | Collapsed collapse -> string collapse

[<RequireQualifiedAccess>]
module ContextEvent =
    let private pieces (exclusions: Exclusions) (pieces: Piece list) = fst (Withhold.pieces exclusions pieces)

    let private block (exclusions: Exclusions) (block: Block) = fst (Withhold.block exclusions block)

    /// The event with the content of excluded files withheld. Record context events through it: the
    /// log then never holds what the exclusions withhold, and a later render has nothing to rewrite.
    let withhold (exclusions: Exclusions) (event: ContextEvent) : ContextEvent =
        match event with
        | ContextEvent.Framed sections -> ContextEvent.Framed(sections |> List.map (block exclusions))
        | ContextEvent.Updated update ->
            ContextEvent.Updated
                { update with
                    Changed = update.Changed |> List.map (block exclusions)
                }
        | ContextEvent.Replied reply ->
            ContextEvent.Replied
                { reply with
                    Shown = reply.Shown |> List.map (block exclusions)
                }
        | ContextEvent.Returned receipt ->
            ContextEvent.Returned
                { receipt with
                    Pieces = pieces exclusions receipt.Pieces
                }
        | ContextEvent.Pinned({ Pin = Pin.Value value } as spec) ->
            ContextEvent.Pinned
                { spec with
                    Pin = Pin.Value(pieces exclusions value)
                }
        | ContextEvent.Collapsed collapse ->
            ContextEvent.Collapsed
                { collapse with
                    Summary = pieces exclusions collapse.Summary
                }
        | ContextEvent.Pinned _
        | ContextEvent.Unpinned _ -> event

/// The JSON of context events: version 1 of what the log keeps (never change an old event's JSON
/// without a new version and an upcaster; `tests/Fx.Prompt.Tests/golden/context-events-v1.jsonl` is
/// what version 1 wrote).
[<RequireQualifiedAccess>]
module ContextCodecs =
    /// The version of the context events' JSON.
    [<Literal>]
    let Version = 1

    let private count: Codec<int> = Codec.int |> Codec.atLeast 0

    let usage: Codec<ReplyUsage> =
        Codec.record {
            let! model = Codec.field "model" (fun (u: ReplyUsage) -> u.Model) ModelRef.codec
            and! tokens = Codec.field "tokens" (fun u -> u.Tokens) Usage.codec
            and! context = Codec.optionalField "context" (fun u -> u.Context) count
            and! cost = Codec.optionalField "cost" (fun u -> u.Cost) Codec.decimal

            return
                {
                    Model = model
                    Tokens = tokens
                    Context = context
                    Cost = cost
                }
        }
        |> Codec.describe
            "What a model call used: the model, its tokens, its context window and its cost in US dollars."
        |> Codec.named "ReplyUsage"

    let reply: Codec<ModelReply> =
        Codec.record {
            let! turn = Codec.field "turn" (fun (r: ModelReply) -> r.Turn) count
            and! step = Codec.field "step" (fun r -> r.Step) count

            and! shown =
                Codec.defaultField "shown" (fun r -> r.Shown) [] (Codec.list Block.codec)

            and! parts = Codec.field "parts" (fun r -> r.Parts) (Codec.list Part.codec)
            and! used = Codec.optionalField "used" (fun r -> r.Used) usage

            return
                {
                    Turn = turn
                    Step = step
                    Shown = shown
                    Parts = parts
                    Used = used
                }
        }
        |> Codec.named "ModelReply"

    let kernelRestart: Codec<KernelRestart> =
        Codec.record {
            let! launch =
                Codec.field "launch" (fun (r: KernelRestart) -> r.Launch) (Codec.int |> Codec.atLeast 1)

            and! reason = Codec.field "reason" (fun r -> r.Reason) Codec.string
            return { Launch = launch; Reason = reason }
        }
        |> Codec.describe "A new kernel a cell ran in: its launch, and why the kernel before it is gone."
        |> Codec.named "KernelRestart"

    let approvedBy: Codec<ApprovedBy> =
        Codec.union "kind" {
            let! auto = Codec.case0 "auto" ApprovedBy.Auto

            and! person =
                Codec.case1 "person" ApprovedBy.Person (Field.required "request" Codec.string)

            and! yolo = Codec.case0 "yolo" ApprovedBy.Yolo

            return
                fun by ->
                    match by with
                    | ApprovedBy.Auto -> auto
                    | ApprovedBy.Person request -> person request
                    | ApprovedBy.Yolo -> yolo
        }
        |> Codec.describe
            "Who let a host call run: auto approvals, a person who answered its grant request, or yolo approvals."
        |> Codec.named "ApprovedBy"

    let approval: Codec<Approval> =
        Codec.record {
            let! action = Codec.field "action" (fun (a: Approval) -> a.Action) Codec.string
            and! resource = Codec.field "resource" (fun a -> a.Resource) Codec.string
            and! by = Codec.field "by" (fun a -> a.By) approvedBy

            return
                {
                    Action = action
                    Resource = resource
                    By = by
                }
        }
        |> Codec.describe
            "How a host call ran that no grant in force allowed: its grant's action and resource, and who let it."
        |> Codec.named "Approval"

    /// Approvals as an optional `approvals` member: absent when there are none, so what was written
    /// before they were kept reads and writes the same.
    let approvals (get: 'T -> Approval list) : RecordFields<'T, Approval list option> =
        Codec.optionalField
            "approvals"
            (fun value ->
                match get value with
                | [] -> None
                | approvals -> Some approvals)
            (Codec.list approval)

    let receipt: Codec<Receipt> =
        Codec.record {
            let! turn = Codec.field "turn" (fun (r: Receipt) -> r.Turn) count
            and! step = Codec.field "step" (fun r -> r.Step) count
            and! call = Codec.optionalField "call" (fun r -> r.Call) Codec.string
            and! name = Codec.field "name" (fun r -> r.Name) Codec.string
            and! pieces = Codec.field "pieces" (fun r -> r.Pieces) (Codec.list Piece.codec)
            and! isError = Codec.defaultField "isError" (fun r -> r.IsError) false Codec.bool
            and! restarted = Codec.optionalField "restarted" (fun r -> r.Restarted) kernelRestart
            and! approved = approvals (fun (r: Receipt) -> r.Approvals)

            return
                {
                    Turn = turn
                    Step = step
                    Call = call
                    Name = name
                    Pieces = pieces
                    IsError = isError
                    Restarted = restarted
                    Approvals = approved |> Option.defaultValue []
                }
        }
        |> Codec.named "Receipt"

    let pin: Codec<Pin> =
        Codec.union "kind" {
            let! value =
                Codec.case1 "value" Pin.Value (Field.required "pieces" (Codec.list Piece.codec))

            and! file =
                Codec.case2
                    "file"
                    (fun path lines -> Pin.File(path, lines))
                    (Field.required "path" Codec.string)
                    (Field.withDefault "lines" [] (Codec.list LineRange.codec))

            return
                fun p ->
                    match p with
                    | Pin.Value pieces -> value pieces
                    | Pin.File(path, lines) -> file path lines
        }
        |> Codec.named "Pin"

    let pinSpec: Codec<PinSpec> =
        Codec.record {
            let! name =
                Codec.field "name" (fun (p: PinSpec) -> p.Name) (Codec.string |> Codec.nonEmpty)

            and! pin = Codec.field "pin" (fun p -> p.Pin) pin
            return { Name = name; Pin = pin }
        }
        |> Codec.named "PinSpec"

    let collapse: Codec<Collapse> =
        Codec.record {
            let! first =
                Codec.field "from" (fun (c: Collapse) -> c.From) Fx.Sessions.Codecs.eventRef

            and! last = Codec.field "to" (fun c -> c.To) Fx.Sessions.Codecs.eventRef
            and! summary = Codec.field "summary" (fun c -> c.Summary) (Codec.list Piece.codec)

            return
                {
                    From = first
                    To = last
                    Summary = summary
                }
        }
        |> Codec.named "Collapse"

    let frameUpdate: Codec<FrameUpdate> =
        Codec.record {
            let! changed =
                Codec.defaultField "changed" (fun (u: FrameUpdate) -> u.Changed) [] (Codec.list Block.codec)

            and! removed =
                Codec.defaultField "removed" (fun u -> u.Removed) [] (Codec.list Codec.string)

            return { Changed = changed; Removed = removed }
        }
        |> Codec.named "FrameUpdate"

    let event: Codec<ContextEvent> =
        Codec.union "kind" {
            let! framed =
                Codec.case1 "framed" ContextEvent.Framed (Field.required "sections" (Codec.list Block.codec))

            and! updated = Codec.caseOf "updated" ContextEvent.Updated frameUpdate
            and! replied = Codec.caseOf "replied" ContextEvent.Replied reply
            and! returned = Codec.caseOf "returned" ContextEvent.Returned receipt
            and! pinned = Codec.caseOf "pinned" ContextEvent.Pinned pinSpec

            and! unpinned =
                Codec.case1 "unpinned" ContextEvent.Unpinned (Field.required "name" Codec.string)

            and! collapsed = Codec.caseOf "collapsed" ContextEvent.Collapsed collapse

            return
                fun e ->
                    match e with
                    | ContextEvent.Framed sections -> framed sections
                    | ContextEvent.Updated u -> updated u
                    | ContextEvent.Replied r -> replied r
                    | ContextEvent.Returned r -> returned r
                    | ContextEvent.Pinned p -> pinned p
                    | ContextEvent.Unpinned name -> unpinned name
                    | ContextEvent.Collapsed c -> collapsed c
        }
        |> Codec.describe "What a session's turns add to its context: frames, replies, receipts, pins, collapses."
        |> Codec.named "ContextEvent"
