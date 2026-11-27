namespace Fx.CodeAct

open Fx
open Fx.Prompt
open Fx.Sdk
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

    /// What the sandbox the hands' processes run in keeps from the model (spec §9.1, §18), so that a
    /// command it refuses fails understandably: what stays read-only in the workspace (git's config
    /// and hooks: a `git config` fails with `Device or resource busy` under bubblewrap), that
    /// credentials are hidden, that the home is read-only (tools that keep state there can fail), and
    /// the network. Only when a driver confines them (the sandbox is opt-in: unsandboxed hands get no
    /// section). From the policy only, with paths relative to the workspace, so environments with the
    /// same policy share the text.
    let sandbox (policy: SandboxPolicy) : string =
        let workspace = SandboxPath.trim policy.Workspace

        let inWorkspace (paths: string list) =
            paths
            |> List.map SandboxPath.trim
            |> List.filter (fun path -> path <> workspace && SandboxPath.isUnder workspace path)
            |> List.map (fun path ->
                let relative = path.Substring(workspace.Length).TrimStart('/', '\\')
                "`" + relative.Replace('\\', '/') + "`")
            |> List.distinct

        let listed (items: string list) =
            match List.rev items with
            | [] -> ""
            | [ one ] -> one
            | last :: rest -> String.concat ", " (List.rev rest) + " and " + last

        let kept = inWorkspace policy.Protected
        let hidden = inWorkspace policy.Denied

        lines
            [
                "Your commands and cells run in a sandbox that the host set up. It is a boundary: do not try to get around it; when it keeps you from a step, say so and leave the step to the user."
                match policy.WorkspaceAccess with
                | WorkspaceAccess.ReadOnly -> "- The workspace is read-only: you can read it, not change it."
                | WorkspaceAccess.ReadWrite when kept.IsEmpty -> "- You can change files in the workspace."
                | WorkspaceAccess.ReadWrite ->
                    $"- You can change files in the workspace, except {listed kept}: they stay read-only, because what is there runs later outside the sandbox. Commands that write them fail: `git config`, `git remote add`, setting an upstream or installing hooks fail with errors such as `could not write config file .git/config: Device or resource busy` or `Operation not permitted`."
                if not hidden.IsEmpty then
                    $"- {listed hidden} cannot be read."
                "- Outside the workspace only temporary files (`$TMPDIR`), the environment's caches and paths the host made writable can be written; the rest of the machine, the home directory included, is read-only. Common tools' caches (`XDG_CACHE_HOME`, Bun, npm, Yarn, pip, Go modules) are the environment's own, so they may download again; tools that keep state in the home directory (NuGet's packages, Cargo's registry, build servers such as MSBuild's) can fail. When one does, say so: the user can make its directory writable (`sandbox.writable`) or run the step."
                "- Credentials are hidden: SSH and GPG keys, cloud and package registry tokens and the files that hold them (such as `~/.nuget/NuGet/NuGet.Config`, `~/.npmrc`, `~/.yarnrc.yml`), logins of tools such as `gh`. What needs them (pushing, signing commits, publishing, logging in) fails, and so can a tool that reads one of those files."
                match policy.Network with
                | NetworkAccess.Allowed -> "- The network is available."
                | NetworkAccess.Denied -> "- There is no network: downloads and other connections fail."
            ]

    /// The prefix's sections: the instructions, the surface's tools, the interpreter's prompt, and
    /// the sandbox's description when one confines the hands' processes.
    let sections (surface: ActionSurface) (interpreter: string) (sandboxPolicy: SandboxPolicy option) : Block list =
        Sections.ordered
            [
                Sections.instructions text
                {
                    Id = "tools"
                    Version = Some(string surface)
                    Pieces = [ Piece.Text(tools surface) ]
                }
                Sections.interpreter interpreter
                {
                    Id = "sandbox"
                    Version = None
                    Pieces =
                        match sandboxPolicy with
                        | Some policy -> [ Piece.Text(sandbox policy) ]
                        | None -> []
                }
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

    /// What a summary request asks after the conversation (compaction, spec §5): a summary that
    /// replaces the conversation so far in the model's context, written as plain text.
    let summarize (focus: string option) : string =
        lines
            [
                "[Compaction: your context is filling up. Everything above this note will be replaced in your context by a summary you write now; what comes after it stays as it is. Reply with that summary only, as plain text, and call no tool.]"
                ""
                "Write it for yourself, to go on with the work without the details:"
                "- the user's requests and instructions (quote short ones), and decisions made and why;"
                "- what you did and found: the files you read and changed (their paths), the commands that mattered and what they showed, errors and how they were solved;"
                "- the current state, and what is left to do, in order;"
                $"- the cells and tool calls whose values or results you may need again, by id: `{g}.recall(id)` gives back a cell's value."
                match focus with
                | Some focus when not (System.String.IsNullOrWhiteSpace focus) ->
                    ""
                    "Keep in particular: " + focus.Trim()
                | _ -> ()
            ]

    /// After the model asked to compact and it did not happen (nothing to summarize yet, or the summary
    /// failed).
    let compactionFailed (reason: string) : Block =
        note "compaction" $"[Note: the conversation was not compacted: {reason}.]"
