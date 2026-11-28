namespace Fx.Prompt

open System
open System.Text
open Fx.Contracts
open Fx.Models
open Fx.Sessions

/// A range of the history that compaction collapsed (spec §5), as `fx.history.list` shows it: its
/// originals stay in the log and read back with `fx.history.read` (T201b).
type CollapsedInfo =
    {
        /// `r1`, `r2`, … in the order the collapses were applied.
        Id: string
        /// The entries it covers (indices in the transcript), first and last.
        First: int
        Last: int
        /// The summary the model sees instead of them.
        Summary: string
        /// Characters of the originals as `Originals.text` renders them.
        Chars: int
    }

    override this.ToString() =
        $"{this.Id}: #{this.First}..#{this.Last} ({this.Chars} chars)"

/// The originals of collapsed ranges, rendered as text for the model to read back (T201b). Pure: the
/// transcript is the fold of the session's log, and files that content exclusions keep from models are
/// withheld as the prompt's render withholds them.
[<RequireQualifiedAccess>]
module Originals =
    /// The id of the `n`th collapse applied (from 0).
    let idOf (n: int) : string = $"r{n + 1}"

    let private piecesText (exclusions: Fx.Models.Exclusions) (pieces: Piece list) : string =
        let pieces, _ = Withhold.pieces exclusions pieces
        pieces |> List.map Piece.render |> String.concat "\n"

    let private endedText (turn: int) (ended: TurnEnd) : string =
        match ended with
        | TurnEnd.Interrupted -> $"[Turn {turn} was interrupted.]"
        | TurnEnd.Failed reason -> $"[Turn {turn} failed: {reason}]"
        | TurnEnd.Abandoned reason -> $"[Turn {turn} was abandoned: {reason}]"
        | TurnEnd.Completed result -> $"[Turn {turn} ended: {result}]"

    /// One entry as text: who wrote it, then what it says.
    let entryText (exclusions: Fx.Models.Exclusions) (entry: Entry) : string =
        let lines (items: string list) =
            items |> List.filter (fun s -> s <> "") |> String.concat "\n"

        match entry.Content with
        | EntryContent.Inputs(_, _, inputs) ->
            inputs
            |> List.map Render.inputText
            |> List.filter (fun text -> text <> "")
            |> List.map (fun text -> "[user]\n" + text)
            |> lines
        | EntryContent.Reply reply ->
            lines
                [
                    for block in reply.Shown do
                        piecesText exclusions block.Pieces
                    for part in reply.Parts do
                        match part with
                        | Part.Text t when t.Text <> "" -> "[assistant]\n" + t.Text
                        | Part.ToolCall call -> $"[call {call.Name} {call.Id}]\n{call.Arguments}"
                        | Part.Refusal text -> "[refusal]\n" + text
                        | Part.Media _ -> "[media]"
                        | _ -> ""
                ]
        | EntryContent.Receipt(receipt, _) ->
            let call =
                match receipt.Call with
                | Some id -> $"[result of {receipt.Name} {id}]"
                | None -> $"[{receipt.Name}]"

            call + "\n" + piecesText exclusions receipt.Pieces
        | EntryContent.Closed(calls, reason) -> $"[{calls.Length} calls closed without a result: {reason}]"
        | EntryContent.Ended(turn, ended) -> endedText turn ended
        | EntryContent.Update _ -> "[The context's sections were updated.]"

    /// The originals of a collapsed range as text, entry after entry.
    let text (exclusions: Fx.Models.Exclusions) (transcript: Transcript) (range: CollapsedRange) : string =
        Transcript.entries transcript
        |> List.filter (fun e -> e.Index >= range.Start && e.Index <= range.End)
        |> List.map (entryText exclusions)
        |> List.filter (fun t -> t <> "")
        |> String.concat "\n\n"

    /// The collapsed ranges, in the order they were applied.
    let list (exclusions: Fx.Models.Exclusions) (transcript: Transcript) : CollapsedInfo list =
        transcript.Collapses
        |> List.mapi (fun n range ->
            {
                Id = idOf n
                First = range.Start
                Last = range.End
                Summary = piecesText exclusions range.Collapse.Summary
                Chars = (text exclusions transcript range).Length
            })

    /// The collapsed range an id names.
    let find (transcript: Transcript) (id: string) : CollapsedRange option =
        transcript.Collapses
        |> List.indexed
        |> List.tryPick (fun (n, r) -> if idOf n = id then Some r else None)
