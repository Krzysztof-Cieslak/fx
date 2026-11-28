namespace Fx.CodeAct

open System.Text
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Kernel
open Fx.Interpreters

/// Cells' values kept in the step journal (spec §5: `fx.recall(id)` / `Out[cell]` restore values; §6.5
/// "Durability": without snapshots, Jupyter semantics plus a journal). After a cell returns a value
/// the host asks the kernel to encode it (`value/encode`) and journals its JSON: inline when small, in
/// the host's blob store when larger, up to `codeact.recall.maxBytes`. A new kernel gets the latest
/// kept values back as `Out[cell]` (`recall`), and `fx.recall(id)` gives any kept value back to a
/// cell. Only JSON comes back: live objects (functions, class instances, open handles, promises) are
/// gone with their kernel, and the names cells declared are not restored.
[<RequireQualifiedAccess>]
module Recall =
    let private put (blobs: Blobs) (bytes: byte array) : Eff<'K, BlobRef> when 'K :> IScope =
        fun env -> blobs.Put bytes env.Scope.Cancellation

    let private get (blobs: Blobs) (hash: BlobHash) : Eff<'K, Result<byte array, BlobError>> when 'K :> IScope =
        fun env -> blobs.Get hash env.Scope.Cancellation

    /// Keeps a cell's value: what the journal records about it (`ValueKept`, or `ValueLost` and why),
    /// and its JSON when the kernel encoded it (the cell's receipt shows it from that, T203a).
    let keep<'K when 'K :> IScope>
        (blobs: Blobs option)
        (inlineBytes: int)
        (maxBytes: int)
        (kernel: Kernel)
        (call: CallRef)
        (cell: string)
        (info: ValueInfo)
        : Eff<'K, StepEvent * Json option> =
        eff {
            let lost (reason: string) =
                StepEvent.ValueLost
                    {
                        Call = call
                        Cell = cell
                        Type = info.Type
                        Reason = reason
                    },
                None

            let kept (value: KeptJson) (json: Json) =
                StepEvent.ValueKept
                    {
                        Call = call
                        Cell = cell
                        Value = value
                        Type = info.Type
                    },
                Some json

            if maxBytes <= 0 then
                return lost "values are not kept (codeact.recall.maxBytes is 0)"
            else
                match! Kernel.encode kernel (ValueRef.Output cell) (Some maxBytes) with
                | Ok(Value.Inline json) ->
                    let bytes = Encoding.UTF8.GetBytes(Json.serializeCanonical json)

                    if bytes.Length <= inlineBytes then
                        return kept (KeptJson.Inline json) json
                    else
                        match blobs with
                        | Some blobs ->
                            let! stored = put blobs bytes
                            return kept (KeptJson.Stored stored) json
                        | None ->
                            let event, _ =
                                lost $"its JSON has {bytes.Length} bytes, more than are kept without a blob store"

                            return event, Some json
                | Ok(Value.Blob _) -> return lost "a value in the environment's blob store is not kept yet"
                | Ok(Value.Handle(id, typeRef, preview)) ->
                    // By reference (T201b): a handle has no JSON form; it comes back into a new kernel
                    // while the session's table has it.
                    return
                        StepEvent.ValueKept
                            {
                                Call = call
                                Cell = cell
                                Value = KeptJson.Handle(id, typeRef, preview)
                                Type = info.Type
                            },
                        None
                | Error(KernelFailure.Refused(KernelError.TooLarge(bytes, limit))) ->
                    return lost $"its JSON has {bytes} bytes, more than {limit} (codeact.recall.maxBytes)"
                | Error(KernelFailure.Refused(KernelError.NotEncodable(typeName, reason))) ->
                    return lost $"a {typeName} has no JSON form: {reason}"
                | Error failure -> return lost $"the kernel did not encode it: {failure}"
        }

    /// What a journal keeps: the latest kept value of each cell, in the order they were kept.
    let kept (journal: Recorded<StepEvent> list) : KeptValue list =
        journal
        |> List.choose (fun recorded ->
            match recorded.Event with
            | StepEvent.ValueKept value -> Some value
            | _ -> None)
        |> List.rev
        |> List.distinctBy (fun value -> value.Cell)
        |> List.rev

    /// The kept value a `recall` id names: a cell's id, or the id of the tool call that ran it (its
    /// latest attempt). `Error` says why there is none.
    let find (journal: Recorded<StepEvent> list) (id: string) : Result<KeptValue, string> =
        let byCell (value: KeptValue) = value.Cell = id
        let byCall (value: KeptValue) = value.Call.Id = id
        let values = kept journal

        let lostOf (matches: LostValue -> bool) =
            journal
            |> List.choose (fun recorded ->
                match recorded.Event with
                | StepEvent.ValueLost lost when matches lost -> Some lost
                | _ -> None)
            |> List.tryLast

        match List.tryFindBack byCell values, List.tryFindBack byCall values with
        | Some value, _
        | None, Some value -> Ok value
        | None, None ->
            match lostOf (fun l -> l.Cell = id || l.Call.Id = id) with
            | Some lost -> Error $"the value of {lost.Cell} was not kept: {lost.Reason}"
            | None -> Error $"no value of a cell or call {Json.quote id} was kept"

    /// A kept value's JSON, read from the blob store if it is there.
    let json<'K when 'K :> IScope> (blobs: Blobs option) (value: KeptValue) : Eff<'K, Result<Json, string>> =
        eff {
            match value.Value, blobs with
            | KeptJson.Inline json, _ -> return Ok json
            | KeptJson.Stored blob, Some blobs ->
                match! get blobs blob.Hash with
                | Ok bytes ->
                    match Json.parse (Encoding.UTF8.GetString bytes) with
                    | Ok json -> return Ok json
                    | Error e -> return Error $"its stored JSON does not parse: {e}"
                | Error e -> return Error $"its stored JSON is not readable: {e}"
            | KeptJson.Stored _, None -> return Error "it is in a blob store this host does not have"
            | KeptJson.Handle(_, typeRef, _), _ ->
                return
                    Error
                        $"it is a {typeRef}, a handle of the host, which has no JSON form: Out[{Json.quote value.Cell}] holds it, and a new kernel gets it back unless the host restarted (then it is gone)"
        }

    /// Puts the last `limit` kept values back into a new kernel as `Out[cell]`, oldest first; gives the
    /// cells restored. A handle comes back only while `live` says the session's table has it: after the
    /// host restarted its handles are gone (T201b).
    let restore<'K when 'K :> IScope>
        (blobs: Blobs option)
        (live: HandleId -> bool)
        (limit: int)
        (journal: Recorded<StepEvent> list)
        (kernel: Kernel)
        : Eff<'K, string list> =
        eff {
            let values = kept journal
            let latest = values |> List.skip (max 0 (values.Length - max 0 limit))
            let restored = ResizeArray<string>()

            for value in latest do
                match value.Value with
                | KeptJson.Handle(id, typeRef, preview) ->
                    if live id then
                        match! Kernel.recall kernel value.Cell (Value.Handle(id, typeRef, preview)) with
                        | Ok _ -> restored.Add value.Cell
                        | Error _ -> ()
                | _ ->
                    match! json blobs value with
                    | Ok json ->
                        match! Kernel.recall kernel value.Cell (Value.Inline json) with
                        | Ok _ -> restored.Add value.Cell
                        | Error _ -> ()
                    | Error _ -> ()

            return List.ofSeq restored
        }

    /// The `recall` binding (`fx.recall(id)`): the value an earlier cell returned, by the cell's id
    /// or the id of the tool call that ran it, from the journal: also after its kernel restarted.
    let binding<'K when 'K :> IScope>
        (load: Eff<'K, Recorded<StepEvent> list>)
        (blobs: Blobs option)
        : HostBinding<'K> =
        HostBinding.create1
            "recall"
            "The value an earlier cell returned (its last expression), by the cell's id (as in `Out[id]`) or the id of the tool call that ran it: also after the kernel restarted. Only JSON comes back; values that had no JSON form were not kept."
            (Arg.required "id" "The cell's id, or the tool call's." Codec.string)
            Codec.json
            (fun id ->
                eff {
                    let! journal = load

                    match find journal id with
                    | Error reason -> return Error(HostCallError.Failed("notKept", reason, None))
                    | Ok value ->
                        match! json blobs value with
                        | Ok json -> return Ok json
                        | Error reason ->
                            return Error(HostCallError.Failed("notKept", $"the value of {value.Cell}: {reason}", None))
                })
