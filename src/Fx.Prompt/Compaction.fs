namespace Fx.Prompt

open System
open Fx.Contracts
open Fx.Machines
open Fx.Models

/// Rough token counts, to decide when a conversation needs compaction (spec §5): no tokenizer, 3.5
/// characters a token (a little fewer than English text takes, about what code takes), a few tokens a
/// part and a fixed amount an image or document. Deterministic; a provider's own counts (`Usage`) are
/// better where there are any.
[<RequireQualifiedAccess>]
module Tokens =
    let private perPart = 4
    let private perMedia = 1600

    /// Tokens of this many characters, rounded up.
    let ofChars (chars: int) : int =
        int ((int64 (max 0 chars) * 2L + 6L) / 7L)

    let private ofContent (content: ToolContent) =
        match content with
        | ToolContent.Text text -> ofChars text.Length
        | ToolContent.Media _ -> perMedia

    let ofPart (part: Part) : int =
        match part with
        | Part.Text t -> perPart + ofChars t.Text.Length
        | Part.Media _ -> perMedia
        | Part.Reasoning r -> perPart + ofChars r.Text.Length
        | Part.ToolCall c -> perPart + ofChars (c.Name.Length + c.Arguments.Length)
        | Part.ToolResult r -> perPart + ofChars r.Name.Length + List.sumBy ofContent r.Content
        | Part.Refusal text -> perPart + ofChars text.Length
        | Part.CacheBreakpoint _ -> 0

    let ofParts (parts: Part list) : int = List.sumBy ofPart parts

    let ofMessage (message: Message) : int = perPart + ofParts message.Parts

    let ofTool (tool: ToolSpec) : int =
        perPart
        + ofChars (
            tool.Name.Length
            + tool.Description.Length
            + (Json.serialize tool.Parameters).Length
        )

    /// A request's input: its system parts, messages and tools.
    let ofRequest (request: ChatRequest) : int =
        ofParts request.System
        + List.sumBy ofMessage request.Messages
        + List.sumBy ofTool request.Tools

/// Where a summary can cut a conversation's history (spec §5 compaction): it replaces the entries from
/// the history's first to `Last` (a `Collapsed` from `From` to `To`), and its request's history ends at
/// `Mark`.
type Cut =
    {
        From: EventRef
        To: EventRef
        /// The last entry the summary replaces (its index in the transcript).
        Last: int
        /// Where the summary request's history ends: where an earlier call's prompt ended (just before a
        /// visible reply), so the request reads what that call wrote to the cache; `None`: at the end of
        /// the prompt rendered now, its suffix included.
        Mark: Mark option
        /// Estimated tokens of the prompt up to the cut (system parts, tools and history).
        Head: int
        /// Estimated tokens of what stays as it is after the cut (the suffix included).
        Tail: int
    }

    override this.ToString() =
        let at = this.Mark |> Option.map string |> Option.defaultValue "end"
        $"cut #0..#{this.Last} at {at} (head {this.Head}, tail {this.Tail})"

/// Compaction (spec §5): the oldest part of a conversation's history replaced by a summary the model
/// writes (`Collapsed`), its originals kept in the log. Pure: which cuts there are, which to make, the
/// summary request, and the collapse. The summary request shares the provider's cache with the
/// conversation's own requests: the same system parts, tools and options, and the history as an
/// earlier call sent it, then the instruction to summarize.
[<RequireQualifiedAccess>]
module Compaction =
    /// Every place a summary can cut the history, oldest first: just before each visible reply but the
    /// first (so a summary replaces at least one reply), and the end of the history. A cut keeps every
    /// tool call with its result and contains every earlier collapse (the transcript would refuse it
    /// otherwise). None while a call is open.
    let cuts (transcript: Transcript) (rendered: Rendered) (tools: ToolSpec list) : Cut list =
        let entries = Transcript.entries transcript |> Array.ofList

        if entries.Length = 0 || rendered.Replies.IsEmpty || not transcript.Open.IsEmpty then
            []
        else
            let history = rendered.Prompt.History |> Array.ofList

            let fixedTokens =
                Tokens.ofParts (Render.system rendered.Prompt) + List.sumBy Tokens.ofTool tools

            let suffixTokens =
                rendered.Prompt.Suffix
                |> List.sumBy (fun block -> Tokens.ofParts (Piece.parts block.Pieces))

            let historyTokens = history |> Array.sumBy Tokens.ofMessage

            let before (mark: Mark) =
                let whole = Array.sub history 0 mark.Message |> Array.sumBy Tokens.ofMessage

                whole
                + Tokens.ofMessage
                    { history[mark.Message] with
                        Parts = history[mark.Message].Parts |> List.truncate mark.Parts
                    }

            let candidate (last: int) (mark: Mark option) : Cut option =
                if last < 0 || last >= entries.Length then
                    None
                else
                    let collapse =
                        {
                            From = entries[0].Ref
                            To = entries[last].Ref
                            Summary = []
                        }

                    let after =
                        Transcript.applyContext entries[last].Ref (ContextEvent.Collapsed collapse) transcript

                    let applies =
                        after.Collapses.Length = transcript.Collapses.Length + 1
                        && (let range = List.last after.Collapses
                            range.Start = 0 && range.End = last)

                    if not applies then
                        None
                    else
                        let head, tail =
                            match mark with
                            | Some mark ->
                                let upTo = before mark
                                fixedTokens + upTo, historyTokens - upTo + suffixTokens
                            | None -> fixedTokens + historyTokens + suffixTokens, 0

                        Some
                            {
                                From = collapse.From
                                To = collapse.To
                                Last = last
                                Mark = mark
                                Head = head
                                Tail = tail
                            }

            [
                for reply in List.tail rendered.Replies do
                    match candidate (reply.Entry - 1) (Some reply.End) with
                    | Some cut -> cut
                    | None -> ()

                match candidate (entries.Length - 1) None with
                | Some cut -> cut
                | None -> ()
            ]

    /// The cut to make: the latest that keeps at least `keep` tokens as they are, among those whose
    /// summary request has at most `fits` tokens; failing that, the earliest that fits (it keeps the
    /// most). `None` when none fits.
    let choose (keep: int) (fits: int option) (cuts: Cut list) : Cut option =
        let fitting =
            cuts
            |> List.filter (fun cut -> fits |> Option.forall (fun limit -> cut.Head <= limit))

        match fitting |> List.filter (fun cut -> cut.Tail >= keep) |> List.tryLast with
        | Some cut -> Some cut
        | None -> List.tryHead fitting

    /// The summary request (spec §5: cache-sharing): the request this render makes, cut where the
    /// cut's request ended (or whole, suffix included), with that request's cache breakpoints, and the
    /// instruction to summarize after them. Everything that shapes the cache (the system parts, tools,
    /// tool choice, reasoning) is the template's, as in the conversation's own requests; only the output
    /// limit may differ.
    let request
        (cfg: RenderConfig)
        (template: ChatRequest)
        (rendered: Rendered)
        (cut: Cut)
        (instruction: string)
        : ChatRequest =
        let request =
            match cut.Mark with
            | Some mark -> Render.requestUpTo cfg template rendered mark
            | None -> Render.request cfg template rendered

        let text = Part.text instruction

        let messages =
            match List.tryLast request.Messages with
            | Some last when last.Role = Role.User ->
                (request.Messages |> List.truncate (request.Messages.Length - 1))
                @ [
                    { last with
                        Parts = last.Parts @ [ text ]
                    }
                ]
            | _ -> request.Messages @ [ Message.user [ text ] ]

        { request with Messages = messages }

    /// The files a request quotes, which its summary may quote too: each path once, as a whole file,
    /// sorted.
    let sources (request: ChatRequest) : ContentSource list =
        let ofContent (content: ToolContent) =
            match content with
            | ToolContent.Media media -> media.Sources
            | ToolContent.Text _ -> []

        let ofPart (part: Part) =
            match part with
            | Part.Text text -> text.Sources
            | Part.Media media -> media.Sources
            | Part.ToolResult result -> result.Sources @ List.collect ofContent result.Content
            | _ -> []

        (request.System @ (request.Messages |> List.collect _.Parts))
        |> List.collect ofPart
        |> List.map _.Path
        |> List.distinct
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.map ContentSource.file

    /// The collapse a summary makes of a cut: its entries shown as the summary, a quote of the files the
    /// summary request quoted (so the render withholds it, and the model-egress hooks redact it, if one
    /// of them is excluded later).
    let collapse (cut: Cut) (summary: string) (sources: ContentSource list) : Collapse =
        {
            From = cut.From
            To = cut.To
            Summary = [ Piece.Quote { Text = summary; Sources = sources } ]
        }

    /// Whether a collapse applies to the transcript now (its range is in the history, keeps calls with
    /// their results, and contains or misses every earlier collapse).
    let applies (transcript: Transcript) (collapse: Collapse) : bool =
        let after =
            Transcript.applyContext collapse.To (ContextEvent.Collapsed collapse) transcript

        after.Collapses.Length = transcript.Collapses.Length + 1
