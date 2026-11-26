namespace Fx.CodeAct

open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Interpreters

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

/// The `history` bindings of the host bridge (spec §6.5: `fx.history`, brokered by the host).
[<RequireQualifiedAccess>]
module HistoryBindings =
    /// The group's description, for the bridge's declarations.
    let groups: (string * string) list =
        [ "history", "This conversation's history as you see it: compaction." ]

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
