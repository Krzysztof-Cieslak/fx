namespace Fx.Host

open System
open Fx.Contracts
open Fx.Models
open Fx.Prompt
open Fx.Sessions

/// What a turn did, as short lines for a terminal (spec §19.2: activity lines): pure renders of the
/// context events a turn records and of how it ended. The TUI (T130) draws richer ones from the same
/// events.
[<RequireQualifiedAccess>]
module Activity =
    /// The first non-blank line of a text, at most `max` characters.
    let firstLine (max: int) (text: string) : string =
        let line =
            text.Split('\n')
            |> Array.map (fun l -> l.Trim())
            |> Array.tryFind (fun l -> l <> "")
            |> Option.defaultValue ""

        if line.Length > max then
            line.Substring(0, max - 1) + "…"
        else
            line

    let private argument (name: string) (arguments: string) : string option =
        match Json.parse arguments with
        | Ok json ->
            match Json.tryField name json with
            | Some(Json.String value) -> Some value
            | _ -> None
        | Error _ -> None

    /// A cell's `// intent:` line, or its first line.
    let intent (code: string) : string =
        let lines = code.Split('\n') |> Array.map (fun l -> l.Trim())

        match
            lines
            |> Array.tryFind (fun l -> l.StartsWith("// intent:", StringComparison.Ordinal))
        with
        | Some line -> line.Substring("// intent:".Length).Trim()
        | None -> firstLine 80 code

    /// One line per tool call of a reply (`◇` reads and runs, `◆` changes), and the reply's text.
    let ofReply (reply: ModelReply) : string list =
        reply.Parts
        |> List.choose (fun part ->
            match part with
            | Part.ToolCall call ->
                let detail (name: string) =
                    argument name call.Arguments |> Option.defaultValue "" |> firstLine 100

                match call.Name with
                | "cell" -> Some("◇ cell  " + intent (argument "code" call.Arguments |> Option.defaultValue ""))
                | "read" -> Some("◇ read  " + detail "path")
                | "edit" -> Some("◆ edit  " + detail "path")
                | "bash" -> Some("◇ bash  " + detail "command")
                | other -> Some("◇ " + other)
            | Part.Text text when not (String.IsNullOrWhiteSpace text.Text) -> Some("  " + firstLine 120 text.Text)
            | _ -> None)

    /// A new kernel, and a failed action's first line (after the note of a new kernel, which the
    /// receipt starts with: `Receipt.Restarted`).
    let ofReceipt (receipt: Receipt) : string list =
        let pieces =
            match receipt.Restarted, receipt.Pieces with
            | Some _, _ :: rest -> rest
            | _, pieces -> pieces

        let auto =
            receipt.Approvals
            |> List.filter (fun approval -> approval.By = ApprovedBy.Auto)
            |> List.map _.Resource

        [
            match receipt.Restarted with
            | Some restart -> $"  ↻ new kernel  {firstLine 160 restart.Reason}; names from earlier cells are gone"
            | None -> ()
            // What ran without asking anyone: the sandbox confined it (auto approvals).
            if not auto.IsEmpty then
                "  ✓ auto-approved  " + firstLine 160 (String.concat ", " auto)
            if receipt.IsError then
                let text = pieces |> List.map Piece.render |> String.concat "\n"
                "  ✗ " + firstLine 120 text
        ]

    /// A compaction: the summary that now stands for the conversation's oldest part.
    let ofCollapse (collapse: Collapse) : string list =
        let text = collapse.Summary |> List.map Piece.render |> String.concat "\n"

        [
            $"◇ compacted  the earlier conversation into a summary ({text.Length} characters)"
        ]

    /// The lines of a context event.
    let ofContext (event: ContextEvent) : string list =
        match event with
        | ContextEvent.Replied reply -> ofReply reply
        | ContextEvent.Returned receipt -> ofReceipt receipt
        | ContextEvent.Collapsed collapse -> ofCollapse collapse
        | _ -> []

    /// How a turn ended, for a person: the result card of spec §19.2 in plain lines.
    let ending (questions: string list) (ended: TurnEnd) : string list =
        match ended with
        | TurnEnd.Completed(TurnResult.Done(summary, evidence, verify)) ->
            [
                "✓ Done  " + summary
                for e in evidence do
                    "  Evidence  " + e.Summary
                match verify with
                | Some verify -> "  Verify    " + verify
                | None -> ()
            ]
        | TurnEnd.Completed(TurnResult.NeedInput _) ->
            [
                "? Needs input"
                for question in questions do
                    "  " + question
            ]
        | TurnEnd.Completed(TurnResult.Waiting(_, note)) -> [ "… Waiting  " + note ]
        | TurnEnd.Interrupted -> [ "✗ Interrupted" ]
        | TurnEnd.Failed reason -> [ "✗ Failed  " + reason ]
        | TurnEnd.Abandoned reason -> [ "✗ Abandoned  " + reason ]
