namespace Fx.CodeAct

open Fx
open Fx.Prompt
open Fx.Variants

/// What the strategy tells the model (pure, deterministic): the prefix's instructions and tools
/// sections, and the notes it adds to a call's suffix.
[<RequireQualifiedAccess>]
module Instructions =
    let private g = Product.kernelGlobal

    let private lines (items: string list) = String.concat "\n" items

    /// The system instructions of CodeAct turns.
    let text: string =
        lines
            [
                "You are a coding agent. You work in the user's workspace on their behalf, through the tools you are given."
                ""
                "How you work:"
                "- Act with tool calls; each result comes back to you. Work in small steps you can check: look before you change something, and check what you changed (run the tests, or the command that shows it works)."
                "- The user may write to you while you work; their messages appear between your steps. Follow the latest ones."
                $"- End every turn by calling `await {g}.finish(result)` in a cell. When the work is done: `{{ kind: \"done\", summary, evidence?, verify? }}`: the summary says what you did and found, `evidence` lists what shows it (e.g. `{{ kind: \"command\", summary: \"bun test → 12 passed\" }}`, built from the values your cells hold), and `verify` is a command a person can run to check it. The host checks the result; if it refuses it, the call throws with the reasons: fix the result and finish again."
                "- `needInput` and `waiting` results name requests and jobs that exist. To ask the user something (a decision, missing information), reply with your question as plain text and no tool call instead: the turn waits for their answer."
                "- Some files are kept from you by content exclusions: reading one fails with `excluded`. Do not work around it."
            ]

    /// The tools section: which surface the conversation runs with. Its version names the surface, so
    /// a conversation keeps the surface its frame recorded.
    let tools (surface: ActionSurface) : string =
        match surface with
        | ActionSurface.CellsOnly ->
            lines
                [
                    "Your tool is `cell`: each call runs one cell in the kernel described below. Everything you do (reading, editing, running commands, finishing) happens in cells."
                ]
        | ActionSurface.Hybrid ->
            lines
                [
                    "Your tools:"
                    "- `cell` runs a cell in the kernel described below: use it for work of several steps, for values you want to keep, and to finish the turn."
                    "- `read` shows a file with line numbers; `edit` replaces text in a file; `bash` runs a command line."
                    $"`read`, `edit` and `bash` do what `{g}.fs.read`, `{g}.fs.patch` and `{g}.shell.run` do in a cell, with the same checks."
                ]

    /// The prefix's sections: the instructions, the surface's tools, and the interpreter's prompt.
    let sections (surface: ActionSurface) (interpreter: string) : Block list =
        Sections.ordered
            [
                Sections.instructions text
                {
                    Id = "tools"
                    Version = Some(string surface)
                    Pieces = [ Piece.Text(tools surface) ]
                }
                Sections.interpreter interpreter
            ]

    /// The surface a conversation's frame recorded, if it has one.
    let recordedSurface (current: Block list) : ActionSurface option =
        current
        |> List.tryFind (fun block -> block.Id = "tools")
        |> Option.bind (fun block -> block.Version)
        |> Option.bind (fun version ->
            match version with
            | "cells-only" -> Some ActionSurface.CellsOnly
            | "hybrid" -> Some ActionSurface.Hybrid
            | _ -> None)

    let private note (id: string) (text: string) : Block =
        {
            Id = "note:" + id
            Version = None
            Pieces = [ Piece.Text text ]
        }

    /// After a reply without a tool call (policy `nudge`).
    let nudge: Block =
        note
            "text-only"
            $"[Note: your last reply had no tool call. If the work is done, end the turn with `await {g}.finish({{ kind: \"done\", … }})` in a cell. If you need the user, reply with your question as plain text again.]"

    /// After a reply cut at the output limit.
    let lengthCut: Block =
        note
            "length"
            "[Note: your last reply was cut at the output limit, and a call it was writing did not run. Go on in smaller steps: shorter replies, smaller cells.]"

    /// When the loop guard sees the same action with the same result again.
    let loopWarning (count: int) : Block =
        note
            "loop"
            $"[Note: you did the same thing {count} times with the same result. Try something different, or reply with a question for the user.]"

    /// When a turn has few steps left.
    let budgetWarning (left: int) : Block =
        note "budget" $"[Note: this turn has {left} steps left. Finish, or reply with what is left to do.]"
