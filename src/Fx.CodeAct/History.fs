namespace Fx.CodeAct

open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Interpreters
open Fx.Prompt

/// What a session's cells asked of the conversation's history (`fx.history.*`, spec §5: model-driven
/// compaction), for its turn to act on before the next model call. One turn at a time runs in a
/// session, and its cells ask through its kernel's bridge.
[<Sealed>]
type HistoryAsks() =
    let gate = obj ()
    let mutable mode = CompactionMode.Auto
    let mutable compact: string option option = None

    /// The running turn's `codeact.compaction.mode`.
    member _.Mode
        with get () = lock gate (fun () -> mode)
        and set (value: CompactionMode) = lock gate (fun () -> mode <- value)

    /// Asks for the history to be compacted before the next call, keeping `focus` in particular.
    member _.Compact(focus: string option) =
        lock gate (fun () -> compact <- Some focus)

    /// What was asked since the last time, which is then forgotten: `Some focus` for a compaction.
    member _.TakeCompact() : string option option =
        lock gate (fun () ->
            let asked = compact
            compact <- None
            asked)

    override _.ToString() = "history asks"

/// A part of the history as `history.read` gives it: text of its originals from a character offset.
type HistoryText =
    {
        Range: string
        From: int
        Next: int
        TotalChars: int
        Text: string
    }

    override this.ToString() =
        $"HistoryText({this.Range}, {this.From}-{this.Next} of {this.TotalChars})"

/// Codecs of the `history` bindings' results.
[<RequireQualifiedAccess>]
module HistoryCodecs =
    let collapsedInfo: Codec<CollapsedInfo> =
        Codec.record {
            let! id =
                Codec.field
                    "id"
                    (fun (c: CollapsedInfo) -> c.Id)
                    (Codec.string |> Codec.describe "What history.read takes.")

            and! first =
                Codec.field "first" (fun c -> c.First) (Codec.int |> Codec.describe "The first entry it covers.")

            and! last =
                Codec.field "last" (fun c -> c.Last) (Codec.int |> Codec.describe "The last entry it covers.")

            and! summary =
                Codec.field "summary" (fun c -> c.Summary) (Codec.string |> Codec.describe "What you see instead.")

            and! chars =
                Codec.field "chars" (fun c -> c.Chars) (Codec.int |> Codec.describe "Characters of its originals.")

            return
                {
                    Id = id
                    First = first
                    Last = last
                    Summary = summary
                    Chars = chars
                }
        }
        |> Codec.describe "A part of the conversation that compaction replaced with a summary."
        |> Codec.named "CollapsedRange"

    let readOptions: Codec<int option * int option> =
        Codec.record {
            let! from =
                Codec.optionalField
                    "from"
                    fst
                    (Codec.int
                     |> Codec.atLeast 0
                     |> Codec.describe "A character offset (a next you got); 0 by default.")

            and! maxChars =
                Codec.optionalField
                    "maxChars"
                    snd
                    (Codec.int |> Codec.atLeast 1 |> Codec.describe "At most this many characters.")

            return from, maxChars
        }
        |> Codec.describe "Which part of the originals to read."
        |> Codec.named "HistoryReadOptions"

    let historyText: Codec<HistoryText> =
        Codec.record {
            let! range = Codec.field "range" (fun (t: HistoryText) -> t.Range) Codec.string
            and! from = Codec.field "from" (fun t -> t.From) Codec.int

            and! next =
                Codec.field "next" (fun t -> t.Next) (Codec.int |> Codec.describe "Pass it as from to read on.")

            and! total = Codec.field "totalChars" (fun t -> t.TotalChars) Codec.int
            and! text = Codec.field "text" (fun t -> t.Text) Codec.string

            return
                {
                    Range = range
                    From = from
                    Next = next
                    TotalChars = total
                    Text = text
                }
        }
        |> Codec.describe "The originals of a collapsed part of the conversation, from a character offset."
        |> Codec.named "HistoryText"

/// The `history` bindings of the host bridge (spec §6.5: `fx.history`, brokered by the host).
[<RequireQualifiedAccess>]
module HistoryBindings =
    /// The group's description, for the bridge's declarations.
    let groups: (string * string) list =
        [
            "history", "This conversation's history as you see it: compaction, and reading collapsed parts back."
        ]

    /// `history.compact(focus?)`: the oldest part of the conversation is replaced in the model's context
    /// by a summary it writes, before its next step.
    let compact<'K> (asks: HistoryAsks) : HostBinding<'K> =
        HostBinding.create1
            "history.compact"
            "Replaces the oldest part of this conversation in your context with a summary you write, before your next step; the newest part stays as it is, and the originals stay in the session's log. Use it after finishing a part of the work whose details you no longer need."
            (Arg.optional "focus" "What the summary should keep in particular." Codec.string)
            Codec.string
            (fun focus ->
                if asks.Mode = CompactionMode.Off then
                    EffResult.error (
                        HostCallError.Failed(
                            "off",
                            "compaction is off in this session (codeact.compaction.mode)",
                            None
                        )
                    )
                else
                    asks.Compact focus
                    EffResult.ok "The conversation will be compacted before your next step.")

    /// `history.list()`: the parts of the conversation compaction replaced with summaries, oldest first.
    let list<'K> (transcript: Eff<'K, Transcript>) (exclusions: Fx.Models.Exclusions) : HostBinding<'K> =
        HostBinding.create0
            "history.list"
            "The parts of this conversation that compaction replaced with summaries, oldest first: their ids for history.read, the entries they cover, their summaries and how long their originals are."
            (Codec.list HistoryCodecs.collapsedInfo)
            (fun () -> transcript |> Eff.map (Originals.list exclusions >> Ok))

    /// The kind of `HistoryRange` handles over a session's table: their object is the range's id.
    let rangeKind (table: HandleTable<string>) : HandleKind<string> =
        HandleKind.create "HistoryRange" "A part of this conversation that compaction replaced with a summary." table

    let private notFound (id: string) =
        HostCallError.Failed("notFound", $"no collapsed range {Json.quote id} (history.list gives their ids)", None)

    /// `history.read(range, options?)`: the originals of a collapsed part (an id, or a `HistoryRange`
    /// handle), bounded, from a character offset; files content exclusions keep from models stay withheld.
    let read<'K>
        (transcript: Eff<'K, Transcript>)
        (kind: HandleKind<string>)
        (exclusions: Fx.Models.Exclusions)
        (outputChars: int)
        : HostBinding<'K> =
        HostBinding.create2
            "history.read"
            "Reads back the originals of a part of this conversation that compaction replaced (an id from history.list, or a HistoryRange from history.open), from a character offset, bounded. Pass `next` as `from` to read on."
            (Arg.valueOrHandle "range" "A range's id from history.list, or a HistoryRange." Codec.string kind,
             Arg.optional "options" "" HistoryCodecs.readOptions)
            HistoryCodecs.historyText
            (fun (range, options) ->
                eff {
                    let id =
                        match range with
                        | Choice1Of2 id
                        | Choice2Of2 id -> id

                    let! t = transcript

                    match Originals.find t id with
                    | None -> return Error(notFound id)
                    | Some found ->
                        let text = Originals.text exclusions t found
                        let from, maxChars = options |> Option.defaultValue (None, None)

                        let from, count =
                            CoreBindings.window
                                text
                                (from |> Option.defaultValue 0)
                                (maxChars |> Option.defaultValue outputChars)

                        return
                            Ok
                                {
                                    Range = id
                                    From = from
                                    Next = from + count
                                    TotalChars = text.Length
                                    Text = text.Substring(from, count)
                                }
                })

    /// `history.open(id)`: a `HistoryRange` handle for a collapsed part, which `history.read` takes (T201b).
    let openRange<'K when 'K :> IIds>
        (transcript: Eff<'K, Transcript>)
        (kind: HandleKind<string>)
        (exclusions: Fx.Models.Exclusions)
        : HostBinding<'K> =
        HostBinding.createHandle1
            "history.open"
            "A HistoryRange for a part of this conversation that compaction replaced (an id from history.list): pass it to history.read."
            (Arg.required "id" "A range's id, from history.list." Codec.string)
            kind
            (fun id ->
                eff {
                    let! t = transcript

                    match Originals.list exclusions t |> List.tryFind (fun info -> info.Id = id) with
                    | None -> return Error(notFound id)
                    | Some info ->
                        let summary = info.Summary.Replace('\n', ' ')
                        let cut = summary.Length > 120

                        let text =
                            $"{id}, entries {info.First}-{info.Last}: "
                            + (if cut then
                                   summary.Substring(0, snd (CoreBindings.window summary 0 119)) + "…"
                               else
                                   summary)

                        return Ok(id, { Text = text; Truncated = cut })
                })
