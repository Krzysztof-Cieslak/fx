namespace Fx.Variants

open System.Collections.Generic
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Replay

/// The variants capability (spec §4.2, §15): the values of variant slots. All configurable behaviour
/// reads them through it, never configuration directly, so that every value is in the profile and
/// every read can be recorded and replayed.
///
/// Interpreters: `Variants.ofResolution` (values resolved from config layers and policies),
/// `Variants.record` (wraps another and records each slot's first read), `Variants.replay` (answers
/// from recorded reads), `FakeVariants` (Fx.Variants.Fakes). All pass `VariantsConformance`
/// (tests/Fx.Variants.Tests).
type Variants =
    {
        /// The value of a slot and where it came from; `None` if the slot is not registered.
        Read: SlotId -> Resolved option
    }

    override _.ToString() = "Variants"

/// An environment that can read variant slots.
type IVariants =
    abstract Variants: Variants

/// The reads of one interpreter (`Variants.record`): each slot once, in the order it was first read,
/// with its value and source. The values of one interpreter never change, so later reads add
/// nothing. A session or run journals them (T131), and they show which slots a result depended on.
/// Thread-safe.
[<Sealed>]
type VariantReads() =
    let gate = obj ()
    let seen = HashSet<SlotId>()
    let reads = ResizeArray<Resolved>()

    member _.Add(read: Resolved) =
        lock gate (fun () ->
            if seen.Add read.Slot then
                reads.Add read)

    /// In the order first read.
    member _.Reads: Resolved list = lock gate (fun () -> List.ofSeq reads)

    override this.ToString() = $"VariantReads({this.Reads.Length})"

module Variants =
    /// Reads the values of a resolution.
    let ofResolution (resolution: Resolution) : Variants = { Read = resolution.TryFind }

    /// Records the first read of each slot in `reads`.
    let record (reads: VariantReads) (variants: Variants) : Variants =
        {
            Read =
                fun slot ->
                    let read = variants.Read slot

                    match read with
                    | Some value -> reads.Add value
                    | None -> ()

                    read
        }

    /// Answers from recorded reads (exact replay, spec §15), whatever the current config says. Reading a
    /// slot the recorded run did not read throws `ReplayDivergedException`: the replayed code depends on
    /// something the recording does not have.
    let replay (recorded: Resolved list) : Variants =
        let byId = recorded |> List.map (fun r -> r.Slot, r) |> Map.ofList

        {
            Read =
                fun slot ->
                    match byId.TryFind slot with
                    | Some read -> Some read
                    | None ->
                        raise (
                            ReplayDivergedException
                                $"replay read the variant slot {slot}, which the recorded run did not read"
                        )
        }

    /// The value of a slot and where it came from, untyped. Throws `UnregisteredSlotException` for a
    /// slot the registry does not have (a defect).
    let resolved<'R when 'R :> IVariants> (slot: SlotId) : Eff<'R, Resolved> =
        fun env ->
            match env.Variants.Read slot with
            | Some read -> Task.FromResult read
            | None -> raise (UnregisteredSlotException slot)

    let private decode (slot: Slot<'T>) (read: Resolved) : 'T =
        match slot.Codec.Decode read.Value with
        | Ok value -> value
        | Error issues -> raise (SlotTypeMismatchException(slot.Id, issues))

    /// The value of a slot and where it came from, e.g. to show why a session behaves as it does.
    let read<'R, 'T when 'R :> IVariants> (slot: Slot<'T>) : Eff<'R, 'T * Source> =
        fun env ->
            match env.Variants.Read slot.Id with
            | Some read -> Task.FromResult(decode slot read, read.Source)
            | None -> raise (UnregisteredSlotException slot.Id)

    /// The value of a slot: how configurable behaviour reads its configuration.
    let get<'R, 'T when 'R :> IVariants> (slot: Slot<'T>) : Eff<'R, 'T> =
        fun env ->
            match env.Variants.Read slot.Id with
            | Some read -> Task.FromResult(decode slot read)
            | None -> raise (UnregisteredSlotException slot.Id)

    /// Recorded reads as JSON: an array of `{"slot", "value", "source"}`.
    let readsCodec: Codec<Resolved list> =
        Codec.list Resolved.codec
        |> Codec.describe "The variant slots a run read, each once, in the order first read."
