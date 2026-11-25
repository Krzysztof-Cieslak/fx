namespace Fx.Models

open System
open Fx.Contracts

/// How far a family's provider state may travel when the conversation moves to another model
/// (spec §10.3 "recorded model-switch rules").
[<RequireQualifiedAccess>]
type ReplayScope =
    /// Only to the model that produced it.
    | SameModel
    /// To any model of the same provider and family.
    | SameProvider
    /// To any model of the same family, at any provider (e.g. one model family behind several
    /// clouds).
    | SameFamily

    override this.ToString() =
        match this with
        | SameModel -> "same model"
        | SameProvider -> "same provider"
        | SameFamily -> "same family"

/// What becomes of a reasoning part whose state may not be replayed.
[<RequireQualifiedAccess>]
type UnreplayableReasoning =
    /// Left out: the new model never sees it.
    | Drop
    /// Kept as plain text in the assistant's message.
    | AsText

    override this.ToString() =
        match this with
        | Drop -> "drop"
        | AsText -> "as text"

/// The model-switch rules, as data (recorded with the session's profile). State of another family is
/// never replayed. Text and tool calls keep their content without the state; reasoning follows
/// `Reasoning`.
type ReplayPolicy =
    {
        /// The scope of each family that differs from `Default`.
        Families: (string * ReplayScope) list
        Default: ReplayScope
        Reasoning: UnreplayableReasoning
    }

    override this.ToString() =
        $"replay(default {this.Default}, {this.Families.Length} families, reasoning {this.Reasoning})"

/// What preparing a conversation for another model changed (recorded, shown in traces).
[<RequireQualifiedAccess>]
type ReplayChange =
    /// A part's state was dropped (message and part index).
    | StateDropped of message: int * part: int * family: string
    /// A reasoning part was left out.
    | ReasoningDropped of message: int * part: int
    /// A reasoning part became text.
    | ReasoningAsText of message: int * part: int

    override this.ToString() =
        match this with
        | StateDropped(m, p, family) -> $"message {m} part {p}: {family} state dropped"
        | ReasoningDropped(m, p) -> $"message {m} part {p}: reasoning dropped"
        | ReasoningAsText(m, p) -> $"message {m} part {p}: reasoning kept as text"

[<RequireQualifiedAccess>]
module Replay =
    /// The default rules: state replays only to the model that produced it. Adapters that know
    /// better (a family whose signatures hold across its models) widen it for their family.
    let defaults: ReplayPolicy =
        {
            Families = []
            Default = ReplayScope.SameModel
            Reasoning = UnreplayableReasoning.Drop
        }

    let private scope: Codec<ReplayScope> =
        Codec.enum
            [
                ReplayScope.SameModel, "sameModel"
                ReplayScope.SameProvider, "sameProvider"
                ReplayScope.SameFamily, "sameFamily"
            ]

    let codec: Codec<ReplayPolicy> =
        Codec.record {
            let! families =
                Codec.defaultField "families" (fun (p: ReplayPolicy) -> p.Families) [] (Codec.keyValues scope)

            and! fallback =
                Codec.defaultField "default" (fun p -> p.Default) ReplayScope.SameModel scope

            and! reasoning =
                Codec.defaultField
                    "reasoning"
                    (fun p -> p.Reasoning)
                    UnreplayableReasoning.Drop
                    (Codec.enum [ UnreplayableReasoning.Drop, "drop"; UnreplayableReasoning.AsText, "asText" ])

            return
                {
                    Families = families
                    Default = fallback
                    Reasoning = reasoning
                }
        }
        |> Codec.describe "Which provider state may be replayed to which model."
        |> Codec.named "ReplayPolicy"

    /// Whether state may be replayed to a model served by a family.
    let allows (policy: ReplayPolicy) (target: ModelRef) (family: string) (state: ProviderState) : bool =
        if state.Family <> family then
            false
        else
            let scope =
                policy.Families
                |> List.tryFind (fun (f, _) -> f = family)
                |> Option.map snd
                |> Option.defaultValue policy.Default

            match scope with
            | ReplayScope.SameModel -> state.Model = target
            | ReplayScope.SameProvider -> state.Model.Provider = target.Provider
            | ReplayScope.SameFamily -> true

    /// The messages as they may be sent to `target` (served by `family`): state that may not travel
    /// there removed, reasoning without replayable state dropped or kept as text. Returns what changed.
    let prepare
        (policy: ReplayPolicy)
        (target: ModelRef)
        (family: string)
        (messages: Message list)
        : Message list * ReplayChange list =
        let changes = ResizeArray<ReplayChange>()

        let keep (state: ProviderState option) =
            state |> Option.filter (allows policy target family)

        let prepared =
            messages
            |> List.mapi (fun m message ->
                let parts =
                    message.Parts
                    |> List.mapi (fun p part ->
                        match part with
                        | Part.Reasoning r when Option.isSome r.State && Option.isNone (keep r.State) ->
                            match policy.Reasoning with
                            | UnreplayableReasoning.Drop ->
                                changes.Add(ReplayChange.ReasoningDropped(m, p))
                                None
                            | UnreplayableReasoning.AsText when r.Text = "" ->
                                changes.Add(ReplayChange.ReasoningDropped(m, p))
                                None
                            | UnreplayableReasoning.AsText ->
                                changes.Add(ReplayChange.ReasoningAsText(m, p))
                                Some(Part.text r.Text)
                        | Part.Text t when Option.isSome t.State && Option.isNone (keep t.State) ->
                            changes.Add(ReplayChange.StateDropped(m, p, t.State.Value.Family))
                            Some(Part.Text { t with State = None })
                        | Part.ToolCall c when Option.isSome c.State && Option.isNone (keep c.State) ->
                            changes.Add(ReplayChange.StateDropped(m, p, c.State.Value.Family))
                            Some(Part.ToolCall { c with State = None })
                        | part -> Some part)
                    |> List.choose id

                { message with Parts = parts })

        prepared, List.ofSeq changes
