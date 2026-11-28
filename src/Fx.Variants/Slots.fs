namespace Fx.Variants

open Fx.Contracts

/// How the agent acts (spec §6.9): through cells only, or through cells plus direct tools.
[<RequireQualifiedAccess>]
type ActionSurface =
    /// One cell tool and `finish` (NOOA-style).
    | CellsOnly
    /// Cells plus direct `read`, `edit` and `bash` tools on the same capabilities.
    | Hybrid

    override this.ToString() =
        match this with
        | CellsOnly -> "cells-only"
        | Hybrid -> "hybrid"

/// What editing a range the agent has not seen does (spec §6.8).
[<RequireQualifiedAccess>]
type EditGuard =
    | Off
    | Warn
    | Require

    override this.ToString() =
        match this with
        | Off -> "off"
        | Warn -> "warn"
        | Require -> "require"

/// How a goal's iterations carry context (spec §8.2).
[<RequireQualifiedAccess>]
type GoalContext =
    | Continue
    | Compact
    | FreshHandoff

    override this.ToString() =
        match this with
        | Continue -> "continue"
        | Compact -> "compact"
        | FreshHandoff -> "fresh-handoff"

/// The shell that runs scripts on Windows (spec §6.5).
[<RequireQualifiedAccess>]
type WindowsShell =
    /// PowerShell 7.
    | Pwsh
    /// Windows PowerShell 5.1.
    | WindowsPowerShell

    override this.ToString() =
        match this with
        | Pwsh -> "pwsh"
        | WindowsPowerShell -> "powershell"

/// The core's variant slots: the experience choices of spec §6.9, §8.2, §10.4 and §19.6 that every
/// host has, whatever extensions it loads. The code that reads them comes with later tasks; the
/// defaults are provisional until evals decide (spec §1, principle 7). Slots of one feature that
/// lives in an extension belong to that extension.
module Slots =
    /// `agent.actionSurface`: `cells-only` or `hybrid` (spec §6.9). Both ship with no baseline
    /// preference; the default is provisional until the eval baselines (T218).
    let actionSurface: Slot<ActionSurface> =
        Slot.enum
            "agent.actionSurface"
            "How the agent acts: cells-only (one cell tool and finish) or hybrid (cells plus direct read, edit and bash tools)."
            [ ActionSurface.CellsOnly, "cells-only"; ActionSurface.Hybrid, "hybrid" ]
            ActionSurface.CellsOnly

    /// `edit.guard`: `off`, `warn` or `require` (spec §6.8).
    let editGuard: Slot<EditGuard> =
        Slot.enum
            "edit.guard"
            "What editing a range the agent has not read does: off, warn, or require reading it first (an error)."
            [ EditGuard.Off, "off"; EditGuard.Warn, "warn"; EditGuard.Require, "require" ]
            EditGuard.Warn

    /// `values.previewLimits`: the bounds of the previews models see (spec §6.6, §6.7).
    let previewLimits: Slot<Limits> =
        Slot.create
            "values.previewLimits"
            "The bounds of value previews shown to models: characters, items, depth and string length."
            Codecs.limits
            Limits.defaults

    /// `values.inlineMaxBytes`: the largest value copied inline; larger ones travel as blob
    /// references (spec §6.6).
    let inlineValueMaxBytes: Slot<int> =
        Slot.create
            "values.inlineMaxBytes"
            "The largest value, in bytes of JSON, that is copied inline; larger values travel as blob references."
            (Codec.int |> Codec.between 0 (1024 * 1024))
            (16 * 1024)

    /// `values.argumentMaxBytes`: the largest argument of a host call that a cell passes inline (a
    /// file's text, a command's input), beyond `values.inlineMaxBytes` (spec §6.6). At most 12 MiB,
    /// so a message stays under the JSON-RPC limit of 16 MiB.
    let argumentMaxBytes: Slot<int> =
        Slot.create
            "values.argumentMaxBytes"
            "The largest argument of a host call, in bytes of JSON, that a cell passes inline (a file's text, a command's input)."
            (Codec.int |> Codec.between 1024 (12 * 1024 * 1024))
            (8 * 1024 * 1024)

    /// `shell.windows`: the shell of `Script` commands on Windows (spec §6.5).
    let windowsShell: Slot<WindowsShell> =
        Slot.enum
            "shell.windows"
            "The shell that runs scripts on Windows: pwsh (PowerShell 7) or powershell (Windows PowerShell)."
            [ WindowsShell.Pwsh, "pwsh"; WindowsShell.WindowsPowerShell, "powershell" ]
            WindowsShell.Pwsh

    /// `goal.context`: how goal iterations carry context (spec §8.2; default FreshHandoff).
    let goalContext: Slot<GoalContext> =
        Slot.enum
            "goal.context"
            "How a goal's iterations carry context: continue, compact, or fresh-handoff (a fresh session with a handoff)."
            [
                GoalContext.Continue, "continue"
                GoalContext.Compact, "compact"
                GoalContext.FreshHandoff, "fresh-handoff"
            ]
            GoalContext.FreshHandoff

    /// A model reference, `provider/model`.
    let modelRef: Codec<string> =
        Codec.string
        |> Codec.pattern "^[A-Za-z0-9][A-Za-z0-9._-]*/[^\\s]+$"
        |> Codec.describe "A model: provider/model, e.g. \"anthropic/claude-opus\"."

    /// The roles every host has (spec §10.4). Agents and extensions add their own with `modelRole`.
    let coreModelRoles = [ "main"; "fast"; "summarizer"; "review"; "judge"; "embed" ]

    /// `model.role.<role>`: the model that fills a role (spec §10.4), unset (`null`) until configured.
    /// Agents and extensions define slots for their own roles with this function and register them
    /// as theirs (`Slot.ownedBy`).
    let modelRole (role: string) : Slot<string option> =
        Slot.create
            $"model.role.{role}"
            $"The model for the {role} role, as provider/model; null until configured."
            (Codec.option modelRef)
            None

    /// Every core slot, in the order `fx` lists them.
    let all: SlotInfo list =
        [
            Slot.info actionSurface
            Slot.info editGuard
            Slot.info previewLimits
            Slot.info inlineValueMaxBytes
            Slot.info argumentMaxBytes
            Slot.info windowsShell
            Slot.info goalContext
            yield! coreModelRoles |> List.map (modelRole >> Slot.info)
        ]
