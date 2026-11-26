namespace Fx.CodeAct

open System
open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Prompt
open Fx.Sdk

/// What a reply without a tool call does (spec §4.5: explicit policies for text-only replies).
[<RequireQualifiedAccess>]
type TextOnlyPolicy =
    /// The next call reminds the model how a turn ends; a second text-only reply in the turn is a
    /// question to the user (`Ask`).
    | Nudge
    /// The reply is a question to the user: the turn ends `NeedInput` on a question request.
    | Ask
    /// The reply is the turn's result: `Done` with the text as its summary and no evidence.
    | Done

    override this.ToString() =
        match this with
        | Nudge -> "nudge"
        | Ask -> "ask"
        | Done -> "done"

/// What a reply cut at the output limit does.
[<RequireQualifiedAccess>]
type LengthPolicy =
    /// Its complete calls run, and the next call tells the model to go on in smaller pieces, up to
    /// `LengthRetries` cuts in a row.
    | Continue
    /// The turn fails.
    | Fail

    override this.ToString() =
        match this with
        | Continue -> "continue"
        | Fail -> "fail"

/// Whether `Done` must point to evidence (spec §4.5, §6.5).
[<RequireQualifiedAccess>]
type EvidencePolicy =
    | Optional
    | Required

    override this.ToString() =
        match this with
        | Optional -> "optional"
        | Required -> "required"

/// When the strategy compacts a conversation (spec §5 compaction).
[<RequireQualifiedAccess>]
type CompactionMode =
    /// When the prompt reaches `codeact.compaction.threshold` of the model's window (prepared in the
    /// background while the step's actions run), when the window overflows, and when the model asks.
    | Auto
    /// Only when the window overflows, and when the model asks.
    | Overflow
    /// Never: a conversation that does not fit fails the turn, and the model cannot ask.
    | Off

    override this.ToString() =
        match this with
        | Auto -> "auto"
        | Overflow -> "overflow"
        | Off -> "off"

/// The CodeAct strategy's variant slots (spec §15): its policies, budgets and bounds. Core slots defined
/// next to the code that reads them: the host registers `CodeActSlots.all` (with `PromptSlots.all`,
/// `Slots.all` and `ContentExclusions.slots`). Defaults are provisional until evals decide.
[<RequireQualifiedAccess>]
module CodeActSlots =
    let private count (low: int) = Codec.int |> Codec.atLeast low

    /// `codeact.policy.textOnly`: `nudge`, `ask` or `done`.
    let textOnly: Slot<TextOnlyPolicy> =
        Slot.enum
            "codeact.policy.textOnly"
            "What a reply without a tool call does: nudge (remind the model once how a turn ends, then treat it as a question), ask (a question to the user), done (the turn's result)."
            [
                TextOnlyPolicy.Nudge, "nudge"
                TextOnlyPolicy.Ask, "ask"
                TextOnlyPolicy.Done, "done"
            ]
            TextOnlyPolicy.Nudge

    /// `codeact.policy.lengthCut`: `continue` or `fail`.
    let lengthCut: Slot<LengthPolicy> =
        Slot.enum
            "codeact.policy.lengthCut"
            "What a reply cut at the output limit does: continue (run its complete calls and ask for smaller steps) or fail the turn."
            [ LengthPolicy.Continue, "continue"; LengthPolicy.Fail, "fail" ]
            LengthPolicy.Continue

    /// `codeact.policy.lengthRetries`: how many cut replies in a row a turn continues after.
    let lengthRetries: Slot<int> =
        Slot.create
            "codeact.policy.lengthRetries"
            "How many replies in a row may be cut at the output limit before the turn fails."
            (count 0)
            2

    /// `codeact.policy.emptyRetries`: how often an empty reply is asked for again.
    let emptyRetries: Slot<int> =
        Slot.create
            "codeact.policy.emptyRetries"
            "How often an empty reply (no text and no tool call) is asked for again before the turn fails."
            (count 0)
            2

    /// `codeact.policy.modelRetries`: how often a retryable model error is retried.
    let modelRetries: Slot<int> =
        Slot.create
            "codeact.policy.modelRetries"
            "How often a call that failed with a retryable error (rate limit, overload, server or connection error) is tried again."
            (count 0)
            4

    /// `codeact.policy.retryDelayMs`: the first wait before a retry; it doubles with each attempt.
    let retryDelayMs: Slot<int> =
        Slot.create
            "codeact.policy.retryDelayMs"
            "Milliseconds before the first retry of a model call; each further retry waits twice as long (at most a minute), or as long as the provider asked."
            (count 0)
            1000

    /// `codeact.loopGuard.repeats`: how many identical actions with identical results stop a turn.
    let loopRepeats: Slot<int> =
        Slot.create
            "codeact.loopGuard.repeats"
            "How many times the same action with the same result may happen in a turn before it stops and asks the user (one fewer warns the model); 0 turns the guard off."
            (count 0)
            3

    /// `codeact.budget.steps`: the model calls a turn may make.
    let maxSteps: Slot<int> =
        Slot.create
            "codeact.budget.steps"
            "How many model calls a turn may make before it stops and asks the user to go on."
            (count 1)
            100

    /// `codeact.budget.tokens`: the tokens (input and output, over every call) a turn may use.
    let maxTokens: Slot<int option> =
        Slot.create
            "codeact.budget.tokens"
            "How many tokens (input plus output, summed over the turn's model calls) a turn may use before it stops and asks the user to go on; null for no limit."
            (Codec.option (count 1))
            None

    /// `codeact.maxOutputTokens`: the output limit of each call.
    let maxOutputTokens: Slot<int option> =
        Slot.create
            "codeact.maxOutputTokens"
            "The most tokens one model reply may have; null for the model's own limit."
            (Codec.option (count 1))
            None

    /// `codeact.cell.timeoutMs`: how long a cell may run.
    let cellTimeoutMs: Slot<int> =
        Slot.create "codeact.cell.timeoutMs" "Milliseconds a cell may run before the kernel stops it." (count 1) 600000

    /// `codeact.cell.typeCheck`: whether cells with type errors are refused.
    let typeCheck: Slot<bool> =
        Slot.create
            "codeact.cell.typeCheck"
            "Whether a cell with type errors is refused (not run), where the interpreter can check cells."
            Codec.bool
            false
        |> Slot.withChoices [ false; true ]

    /// `codeact.output.maxChars`: what a cell's or command's output keeps.
    let outputChars: Slot<int> =
        Slot.create
            "codeact.output.maxChars"
            "Characters kept of a cell's output and of each output stream of a command (the head and the tail)."
            (count 256)
            16000

    /// `codeact.receipt.maxChars`: the length of a receipt.
    let receiptChars: Slot<int> =
        Slot.create
            "codeact.receipt.maxChars"
            "Characters of an action's result the model sees (the head and the tail)."
            (count 256)
            20000

    /// `codeact.read.maxLines`: the lines one hybrid `read` shows.
    let readLines: Slot<int> =
        Slot.create
            "codeact.read.maxLines"
            "The most lines one read (hybrid action surface) shows; read further with an offset."
            (count 1)
            2000

    /// `codeact.finish.evidence`: `optional` or `required`.
    let evidence: Slot<EvidencePolicy> =
        Slot.enum
            "codeact.finish.evidence"
            "Whether a done result must point to evidence: optional or required."
            [ EvidencePolicy.Optional, "optional"; EvidencePolicy.Required, "required" ]
            EvidencePolicy.Optional

    /// `codeact.journal.snapshots`: whether each step records the workspace before its actions.
    let snapshots: Slot<bool> =
        Slot.create
            "codeact.journal.snapshots"
            "Whether each step records a snapshot of the workspace before its actions, where the environment has snapshots, so that a call in doubt after the host stopped shows what it changed."
            Codec.bool
            true
        |> Slot.withChoices [ true; false ]

    /// `codeact.recall.maxBytes`: the largest value (its JSON) the journal keeps for `recall`.
    let recallMaxBytes: Slot<int> =
        Slot.create
            "codeact.recall.maxBytes"
            "The most bytes of JSON of a cell's value the journal keeps for recall after a kernel restart (values beyond values.inlineMaxBytes go to the host's blob store); 0 keeps none."
            (count 0)
            1048576

    /// `codeact.recall.restore`: how many kept values a new kernel gets back as `Out[cell]`.
    let recallRestore: Slot<int> =
        Slot.create
            "codeact.recall.restore"
            "How many of the latest kept values a new kernel gets back as Out[cell] when it starts after another (fx.recall gives back any kept value)."
            (count 0)
            20

    /// `codeact.compaction.mode`: `auto`, `overflow` or `off`.
    let compactionMode: Slot<CompactionMode> =
        Slot.enum
            "codeact.compaction.mode"
            "When the conversation's oldest part is replaced by a summary the model writes: auto (when the prompt reaches codeact.compaction.threshold of the model's context window, when the window overflows, and when the model asks), overflow (only when the window overflows, and when the model asks), or off."
            [
                CompactionMode.Auto, "auto"
                CompactionMode.Overflow, "overflow"
                CompactionMode.Off, "off"
            ]
            CompactionMode.Auto

    /// `codeact.compaction.threshold`: the share of the window that starts a compaction.
    let compactionThreshold: Slot<int> =
        Slot.create
            "codeact.compaction.threshold"
            "The percentage of the model's context window the prompt may fill before it is compacted (mode auto)."
            (Codec.int |> Codec.atLeast 10 |> Codec.atMost 95)
            80

    /// `codeact.compaction.keep`: the share of the window the newest history keeps as it is.
    let compactionKeep: Slot<int> =
        Slot.create
            "codeact.compaction.keep"
            "The percentage of the model's context window the newest part of the conversation keeps as it is when the part before it is summarized."
            (Codec.int |> Codec.atLeast 0 |> Codec.atMost 90)
            30

    /// `codeact.compaction.summaryTokens`: the output limit of a summary.
    let summaryTokens: Slot<int> =
        Slot.create
            "codeact.compaction.summaryTokens"
            "The most tokens the model may write for a summary of the conversation's oldest part."
            (count 256)
            8192

    /// The strategy's slots, for the host's registry.
    let all: SlotInfo list =
        [
            Slot.info textOnly
            Slot.info lengthCut
            Slot.info lengthRetries
            Slot.info emptyRetries
            Slot.info modelRetries
            Slot.info retryDelayMs
            Slot.info loopRepeats
            Slot.info maxSteps
            Slot.info maxTokens
            Slot.info maxOutputTokens
            Slot.info cellTimeoutMs
            Slot.info typeCheck
            Slot.info outputChars
            Slot.info receiptChars
            Slot.info readLines
            Slot.info evidence
            Slot.info snapshots
            Slot.info recallMaxBytes
            Slot.info recallRestore
            Slot.info compactionMode
            Slot.info compactionThreshold
            Slot.info compactionKeep
            Slot.info summaryTokens
        ]

    /// Every slot a turn reads: the core's, the render's, content exclusions', the strategy's and its
    /// grants guard's.
    let read: SlotInfo list =
        Slots.all @ PromptSlots.all @ ContentExclusions.slots @ all @ GrantSlots.all

/// When and how a conversation is compacted (`codeact.compaction.*`).
type CompactionSettings =
    {
        Mode: CompactionMode
        /// Percent of the model's context window.
        Threshold: int
        /// Percent of the model's context window.
        Keep: int
        SummaryTokens: int
    }

    override this.ToString() =
        $"compaction {this.Mode} (at {this.Threshold} percent, keep {this.Keep} percent, summary {this.SummaryTokens} tokens)"

/// What a turn runs with, read once at its start through `IVariants` (recorded and replayable).
type CodeActSettings =
    {
        /// `agent.actionSurface`; a conversation keeps the surface its frame recorded.
        Surface: ActionSurface
        /// `model.role.main`.
        Model: string option
        Prompt: PromptSettings
        Exclusions: ExclusionSettings
        TextOnly: TextOnlyPolicy
        LengthCut: LengthPolicy
        LengthRetries: int
        EmptyRetries: int
        ModelRetries: int
        RetryDelay: TimeSpan
        LoopRepeats: int
        MaxSteps: int
        MaxTokens: int option
        MaxOutputTokens: int option
        CellTimeoutMs: int
        TypeCheck: bool
        OutputChars: int
        ReceiptChars: int
        ReadLines: int
        Evidence: EvidencePolicy
        /// `values.previewLimits`: previews of cells' values.
        PreviewLimits: Limits
        /// `values.inlineMaxBytes`: the largest value a kernel copies inline.
        InlineBytes: int
        /// `codeact.journal.snapshots`.
        Snapshots: bool
        /// `codeact.recall.maxBytes`.
        RecallMaxBytes: int
        /// `codeact.recall.restore`.
        RecallRestore: int
        /// `codeact.compaction.*`.
        Compaction: CompactionSettings
    }

    override this.ToString() =
        $"codeact settings ({this.Surface}, steps {this.MaxSteps}, text-only {this.TextOnly}, length {this.LengthCut})"

[<RequireQualifiedAccess>]
module CodeActSettings =
    /// Reads the settings through `IVariants`.
    let read<'R when 'R :> IVariants> : Eff<'R, CodeActSettings> =
        eff {
            let! surface = Variants.get Slots.actionSurface
            let! model = Variants.get (Slots.modelRole "main")
            let! prompt = PromptSettings.read
            let! exclusions = ContentExclusions.read
            let! textOnly = Variants.get CodeActSlots.textOnly
            let! lengthCut = Variants.get CodeActSlots.lengthCut
            let! lengthRetries = Variants.get CodeActSlots.lengthRetries
            let! emptyRetries = Variants.get CodeActSlots.emptyRetries
            let! modelRetries = Variants.get CodeActSlots.modelRetries
            let! retryDelay = Variants.get CodeActSlots.retryDelayMs
            let! loopRepeats = Variants.get CodeActSlots.loopRepeats
            let! maxSteps = Variants.get CodeActSlots.maxSteps
            let! maxTokens = Variants.get CodeActSlots.maxTokens
            let! maxOutputTokens = Variants.get CodeActSlots.maxOutputTokens
            let! cellTimeout = Variants.get CodeActSlots.cellTimeoutMs
            let! typeCheck = Variants.get CodeActSlots.typeCheck
            let! outputChars = Variants.get CodeActSlots.outputChars
            let! receiptChars = Variants.get CodeActSlots.receiptChars
            let! readLines = Variants.get CodeActSlots.readLines
            let! evidence = Variants.get CodeActSlots.evidence
            let! inlineBytes = Variants.get Slots.inlineValueMaxBytes
            let! snapshots = Variants.get CodeActSlots.snapshots
            let! recallMaxBytes = Variants.get CodeActSlots.recallMaxBytes
            let! recallRestore = Variants.get CodeActSlots.recallRestore
            let! compactionMode = Variants.get CodeActSlots.compactionMode
            let! compactionThreshold = Variants.get CodeActSlots.compactionThreshold
            let! compactionKeep = Variants.get CodeActSlots.compactionKeep
            let! summaryTokens = Variants.get CodeActSlots.summaryTokens

            return
                {
                    Surface = surface
                    Model = model
                    Prompt = prompt
                    Exclusions = exclusions
                    TextOnly = textOnly
                    LengthCut = lengthCut
                    LengthRetries = lengthRetries
                    EmptyRetries = emptyRetries
                    ModelRetries = modelRetries
                    RetryDelay = TimeSpan.FromMilliseconds(float retryDelay)
                    LoopRepeats = loopRepeats
                    MaxSteps = maxSteps
                    MaxTokens = maxTokens
                    MaxOutputTokens = maxOutputTokens
                    CellTimeoutMs = cellTimeout
                    TypeCheck = typeCheck
                    OutputChars = outputChars
                    ReceiptChars = receiptChars
                    ReadLines = readLines
                    Evidence = evidence
                    PreviewLimits = prompt.Limits
                    InlineBytes = inlineBytes
                    Snapshots = snapshots
                    RecallMaxBytes = recallMaxBytes
                    RecallRestore = recallRestore
                    Compaction =
                        {
                            Mode = compactionMode
                            Threshold = compactionThreshold
                            Keep = compactionKeep
                            SummaryTokens = summaryTokens
                        }
                }
        }

    type private VariantsEnv =
        {
            Variants: Variants
        }

        interface IVariants with
            member env.Variants = env.Variants

    /// The settings of a variants interpreter (a turn's profile), whatever the caller's environment.
    let ofVariants<'R> (variants: Variants) : Eff<'R, CodeActSettings> = fun _ -> read { Variants = variants }
