namespace Fx.Prompt

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Fx.Contracts
open Fx.Models
open Fx.Sessions

/// Where cache breakpoints go (spec §5, §10.3; `prompt.cache.breakpoints`).
[<RequireQualifiedAccess>]
type Breakpoints =
    /// None.
    | Off
    /// After the prefix only: the frame is shared by every call of the conversation.
    | Prefix
    /// After the prefix, at the end of each request, and where the latest earlier requests ended, so the
    /// next call reads what this one wrote however much was added in between.
    | Rolling

    override this.ToString() =
        match this with
        | Off -> "off"
        | Prefix -> "prefix"
        | Rolling -> "rolling"

type CachePolicy =
    {
        Breakpoints: Breakpoints
        /// The lifetime of the prefix's breakpoint (later ones are short: a long lifetime may not follow a
        /// short one).
        PrefixTtl: CacheTtl
    }

    override this.ToString() =
        $"cache({this.Breakpoints}, prefix {this.PrefixTtl})"

/// What a render needs besides the transcript and what is live.
type RenderConfig =
    {
        /// The prefix's sections as they are now. Until the transcript records a frame they are the
        /// prefix; afterwards the recorded frame is, and `Transcript.reconcile` states changes in the
        /// history.
        Sections: Block list
        /// The suffix's views, in order.
        Views: View list
        Exclusions: Exclusions
        Cache: CachePolicy
        /// How the model caches (its catalog entry's `Caps.Caching`).
        Caching: CachingCaps
    }

    override this.ToString() =
        $"render config ({this.Sections.Length} sections, {this.Views.Length} views, {this.Exclusions}, {this.Cache})"

/// The prompt (spec §5): a stable prefix, an append-only history and a dynamic suffix.
type Prompt =
    {
        Prefix: Block list
        History: Message list
        Suffix: Block list
    }

    override this.ToString() =
        $"prompt ({this.Prefix.Length} sections, {this.History.Length} messages, {this.Suffix.Length} views)"

/// A position in the history: after the first `Parts` parts of message `Message`.
type Mark =
    {
        Message: int
        Parts: int
    }

    override this.ToString() = $"{this.Message}.{this.Parts}"

/// A render's result: the prompt and what it shows.
type Rendered =
    {
        Prompt: Prompt
        /// Where earlier calls' prompts ended (just before each visible reply), oldest first: what a
        /// cache written then covers.
        Ends: Mark list
        /// The file ranges the prompt shows (`SeenRanges`).
        Seen: SeenRanges
        /// The paths whose content was withheld, sorted.
        Withheld: string list
        /// How many replies lost their reasoning because the history before them changed.
        Stale: int
    }

    override this.ToString() =
        $"rendered ({this.Prompt}, {this.Seen}, {this.Withheld.Length} withheld, {this.Stale} stale)"

[<RequireQualifiedAccess>]
module Render =
    /// The text of an input as the model reads it.
    let inputText (input: Input) : string =
        match input.Content with
        | InputContent.Text text -> text
        | InputContent.Resolved(request, Resolution.Answered response) ->
            $"Request {request} was answered: {Json.serialize response}"
        | InputContent.Resolved(request, Resolution.Cancelled reason) -> $"Request {request} was cancelled: {reason}"
        | InputContent.Resolved(request, Resolution.Expired None) -> $"Request {request} expired without an answer."
        | InputContent.Resolved(request, Resolution.Expired(Some response)) ->
            $"Request {request} expired; its default answer: {Json.serialize response}"
        | InputContent.JobFinished(job, note) -> $"Job {job} finished: {note}"

    let private endedText (turn: int) (ended: TurnEnd) : string =
        match ended with
        | TurnEnd.Interrupted -> $"[Turn {turn} was interrupted.]"
        | TurnEnd.Failed reason -> $"[Turn {turn} failed: {reason}]"
        | TurnEnd.Abandoned reason -> $"[Turn {turn} was abandoned: {reason}]"
        | TurnEnd.Completed result -> $"[Turn {turn} ended: {result}]"

    let private updatePieces (update: FrameUpdate) : Piece list =
        [
            Piece.Text "[Context update: these sections changed since the conversation began.]"
            for block in update.Changed do
                Piece.Text $"[{block.Id}]"
                yield! block.Pieces
            if not update.Removed.IsEmpty then
                Piece.Text("[No longer in effect: " + String.concat ", " update.Removed + "]")
        ]

    let private summaryHeader =
        Piece.Text "[Summary of an earlier part of this conversation:]"

    /// A reply without its reasoning and provider state: what is left of it once the history before it
    /// changed (a provider's check of the reasoning's prefix would fail).
    let private strip (parts: Part list) : Part list * bool =
        let mutable dropped = false

        let kept =
            parts
            |> List.choose (fun part ->
                match part with
                | Part.Reasoning _ ->
                    dropped <- true
                    None
                | Part.Text t when t.State.IsSome ->
                    dropped <- true
                    Some(Part.Text { t with State = None })
                | Part.ToolCall c when c.State.IsSome ->
                    dropped <- true
                    Some(Part.ToolCall { c with State = None })
                | Part.CacheBreakpoint _ -> None
                | part -> Some part)

        kept, dropped

    /// Messages from (role, part) pairs, consecutive parts of one role merged into one message, and
    /// positions in the pairs (after the first k) as marks.
    let private group (flat: (Role * Part) list) (positions: int list) : Message list * Mark list =
        let messages = ResizeArray<Role * ResizeArray<Part>>()
        // For each pair, the message it went to and its index there.
        let at = ResizeArray<int * int>()

        for role, part in flat do
            if messages.Count = 0 || fst messages[messages.Count - 1] <> role then
                messages.Add(role, ResizeArray())

            let parts = snd messages[messages.Count - 1]
            parts.Add part
            at.Add(messages.Count - 1, parts.Count)

        let marks =
            positions
            |> List.choose (fun k ->
                if k <= 0 || k > at.Count then
                    None
                else
                    let message, parts = at[k - 1]
                    Some { Message = message; Parts = parts })
            |> List.distinct

        (messages
         |> Seq.map (fun (role, parts) ->
             {
                 Role = role
                 Parts = List.ofSeq parts
             })
         |> List.ofSeq),
        marks

    /// Renders the prompt (spec §5): the frame (or the sections, before one is recorded) as the prefix,
    /// the visible history as messages, and the views as the suffix. Pure and deterministic: the same
    /// configuration, transcript and live data give the same prompt, byte for byte.
    ///
    /// Stability: appending to the log only appends to the history. What a call's suffix showed is in
    /// the reply's `Shown`, so it stays where it was. Content exclusions withhold files everywhere; a
    /// file the log recorded and the render now withholds (the exclusions changed) rewrites the history
    /// from there, and so do collapses and new frames: replies after a rewrite lose their reasoning.
    let render (cfg: RenderConfig) (transcript: Transcript) (live: Live) : Rendered =
        let withheld = SortedSet<string>(StringComparer.Ordinal)
        let mutable seen = SeenRanges.empty

        let filter (pieces: Piece list) : Piece list * bool =
            let pieces, changed = Withhold.pieces cfg.Exclusions pieces

            for piece in pieces do
                match piece with
                | Piece.Withheld path -> withheld.Add path |> ignore
                | _ -> ()

            seen <- SeenRanges.addPieces pieces seen
            pieces, changed

        let filterBlock (block: Block) : Block * bool =
            let pieces, changed = filter block.Pieces

            (if changed then { block with Pieces = pieces } else block), changed

        // The prefix.
        let frame = transcript.Frame |> Option.defaultValue cfg.Sections
        let prefix = frame |> List.map filterBlock
        let mutable rewriteAt = if prefix |> List.exists snd then 0 else Int32.MaxValue

        // The history.
        let visible = Transcript.visible transcript
        let flat = ResizeArray<Role * Part>()
        let ends = ResizeArray<int>()
        let mutable stale = 0

        let user (parts: Part list) =
            for part in parts do
                flat.Add(Role.User, part)

        let userPieces (index: int) (pieces: Piece list) =
            let pieces, changed = filter pieces

            if changed then
                rewriteAt <- min rewriteAt index

            user (Piece.parts pieces)

        for v in visible do
            match v with
            | Visible.Summary range -> userPieces range.Start (summaryHeader :: range.Collapse.Summary)
            | Visible.Entry entry ->
                match entry.Content with
                | EntryContent.Inputs(_, _, inputs) ->
                    inputs
                    |> List.map inputText
                    |> List.filter (fun text -> text <> "")
                    |> List.map Part.text
                    |> user
                | EntryContent.Reply reply ->
                    for block in reply.Shown do
                        let block, changed = filterBlock block

                        if changed then
                            rewriteAt <- min rewriteAt entry.Index

                        user (Piece.parts block.Pieces)

                    ends.Add flat.Count

                    let parts =
                        if Transcript.isStale entry.Index transcript || entry.Index >= rewriteAt then
                            let parts, dropped = strip reply.Parts

                            if dropped then
                                stale <- stale + 1

                            parts
                        else
                            reply.Parts
                            |> List.filter (fun p ->
                                match p with
                                | Part.CacheBreakpoint _ -> false
                                | _ -> true)

                    for part in parts do
                        flat.Add(Role.Assistant, part)
                | EntryContent.Receipt(receipt, true) ->
                    let pieces, changed = filter receipt.Pieces

                    if changed then
                        rewriteAt <- min rewriteAt entry.Index

                    user
                        [
                            Part.ToolResult
                                {
                                    CallId = receipt.Call |> Option.defaultValue ""
                                    Name = receipt.Name
                                    Content = Piece.toolContent pieces
                                    IsError = receipt.IsError
                                    Sources = pieces |> List.collect Piece.sources
                                }
                        ]
                | EntryContent.Receipt(receipt, false) ->
                    let pieces =
                        match receipt.Call with
                        | Some id ->
                            Piece.Text $"[Result of {receipt.Name} (call {id}), after the call was closed:]"
                            :: receipt.Pieces
                        | None -> receipt.Pieces

                    userPieces entry.Index pieces
                | EntryContent.Closed(calls, reason) ->
                    calls
                    |> List.map (fun call ->
                        Part.ToolResult
                            {
                                CallId = call.Id
                                Name = call.Name
                                Content = [ ToolContent.Text $"No result: {reason}." ]
                                IsError = true
                                Sources = []
                            })
                    |> user
                | EntryContent.Ended(turn, ended) -> user [ Part.text (endedText turn ended) ]
                | EntryContent.Update update -> userPieces entry.Index (updatePieces update)

        let history, endMarks = group (List.ofSeq flat) (List.ofSeq ends)

        // The suffix.
        let input =
            {
                Transcript = transcript
                Visible = visible
                Live = live
            }

        let suffix =
            cfg.Views
            |> List.collect (fun view -> view.Render input)
            |> List.map (filterBlock >> fst)
            |> List.filter (fun block -> not block.Pieces.IsEmpty)

        {
            Prompt =
                {
                    Prefix = prefix |> List.map fst
                    History = history
                    Suffix = suffix
                }
            Ends = endMarks
            Seen = seen
            Withheld = List.ofSeq withheld
            Stale = stale
        }

    /// The prefix as the request's system parts: one text part per section, quoting (`Sources`) the
    /// files its pieces show.
    let system (prompt: Prompt) : Part list =
        prompt.Prefix
        |> List.map (fun block -> Block.render block, block.Pieces |> List.collect Piece.sources)
        |> List.filter (fun (text, _) -> text <> "")
        |> List.map (fun (text, sources) -> Part.quoting sources text)

    /// A stable key for the prefix (the system parts and the tools): the first 32 hex digits of the
    /// SHA-256 of their canonical JSON, as they leave for the model (without provenance). Sent as the
    /// cache key (OpenAI `prompt_cache_key`) where the model takes one; it changes only when the prefix
    /// does.
    let cacheKey (system: Part list) (tools: ToolSpec list) : string =
        let parts =
            system
            |> List.filter (fun p ->
                match p with
                | Part.CacheBreakpoint _ -> false
                | _ -> true)
            |> List.map Part.withoutSources

        let json =
            Json.Object
                [
                    "system", Json.Array(parts |> List.map Part.codec.Encode)
                    "tools", Json.Array(tools |> List.map ToolSpec.codec.Encode)
                ]

        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(Json.serializeCanonical json))
        Convert.ToHexStringLower(hash).Substring(0, 32)

    let private insert (marks: (Mark * CacheTtl) list) (messages: Message list) : Message list =
        messages
        |> List.mapi (fun m message ->
            let here =
                marks
                |> List.filter (fun (mark, _) -> mark.Message = m)
                |> List.sortBy (fun (mark, _) -> mark.Parts)

            if here.IsEmpty then
                message
            else
                let parts = ResizeArray<Part>()

                message.Parts
                |> List.iteri (fun i part ->
                    parts.Add part

                    for _, ttl in here |> List.filter (fun (mark, _) -> mark.Parts = i + 1) do
                        parts.Add(Part.CacheBreakpoint ttl))

                { message with
                    Parts = List.ofSeq parts
                })

    /// The canonical request for a rendered prompt (spec §10.3): the template's model, tools and
    /// options; the prefix as the system parts; the history and then the suffix as messages (the suffix
    /// in a user message: in the history's last one if that is the user's); cache breakpoints by the
    /// policy within the model's limit (most valuable first: the end of this request, the prefix, then
    /// the latest earlier ends), and the prefix's key as the cache key where the model takes one.
    let request (cfg: RenderConfig) (template: ChatRequest) (rendered: Rendered) : ChatRequest =
        let prompt = rendered.Prompt
        let system = system prompt

        let suffix = prompt.Suffix |> List.collect (fun block -> Piece.parts block.Pieces)

        let messages =
            match suffix with
            | [] -> prompt.History
            | parts ->
                match List.tryLast prompt.History with
                | Some last when last.Role = Role.User ->
                    (prompt.History |> List.truncate (prompt.History.Length - 1))
                    @ [ { last with Parts = last.Parts @ parts } ]
                | _ -> prompt.History @ [ Message.user parts ]

        let caps = cfg.Caching
        let policy = cfg.Cache.Breakpoints

        let budget =
            if caps.Mode = CacheMode.Breakpoints && policy <> Breakpoints.Off then
                caps.MaxBreakpoints - (if template.Cache.Tools.IsSome then 1 else 0)
            else
                0

        let prefixTtl = if caps.LongTtl then cfg.Cache.PrefixTtl else CacheTtl.Short

        let finalMark =
            match List.tryLast messages with
            | Some last when not last.Parts.IsEmpty ->
                Some
                    {
                        Message = messages.Length - 1
                        Parts = last.Parts.Length
                    }
            | _ -> None

        // Candidates, most valuable first: `None` stands for the prefix.
        let candidates =
            match policy with
            | Breakpoints.Off -> []
            | Breakpoints.Prefix -> [ None ]
            | Breakpoints.Rolling ->
                (finalMark |> Option.map Some |> Option.toList)
                @ [ None ]
                @ (rendered.Ends |> List.rev |> List.map Some)
            |> List.filter (fun c -> c.IsSome || not system.IsEmpty)
            |> List.distinct
            |> List.truncate (max 0 budget)

        let system =
            if candidates |> List.contains None then
                system @ [ Part.CacheBreakpoint prefixTtl ]
            else
                system

        let marks =
            candidates |> List.choose (Option.map (fun mark -> mark, CacheTtl.Short))

        let key =
            if caps.Key && policy <> Breakpoints.Off then
                Some(cacheKey system template.Tools)
            else
                template.Cache.Key

        { template with
            System = system
            Messages = insert marks messages
            Cache = { template.Cache with Key = key }
        }
