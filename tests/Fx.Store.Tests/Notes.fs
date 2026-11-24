/// A small domain whose events changed shape over time, for the schema, projection and store tests:
/// version 1 wrote `added` with a `text` field; version 2 renamed it `body` and added `edited`.
module Fx.Store.Tests.Notes

open Fx.Contracts
open Fx.Machines
open Fx.Store

/// The events as version 1 knew them.
type NoteV1 =
    | AddedV1 of text: string
    | RemovedV1 of index: int

    override this.ToString() =
        match this with
        | AddedV1 text -> $"AddedV1 {text}"
        | RemovedV1 index -> $"RemovedV1 {index}"

/// The events now.
type Note =
    | Added of body: string
    | Removed of index: int
    | Edited of index: int * body: string

    override this.ToString() =
        match this with
        | Added body -> $"Added {body}"
        | Removed index -> $"Removed {index}"
        | Edited(index, body) -> $"Edited {index} {body}"

let codecV1: Codec<NoteV1> =
    Codec.union "kind" (fun added removed value ->
        match value with
        | AddedV1 text -> added text
        | RemovedV1 index -> removed index)
    |> Codec.case1 "added" AddedV1 (Field.required "text" Codec.string)
    |> Codec.case1 "removed" RemovedV1 (Field.required "index" Codec.int)
    |> Codec.buildUnion

let codec: Codec<Note> =
    Codec.union "kind" (fun added removed edited value ->
        match value with
        | Added body -> added body
        | Removed index -> removed index
        | Edited(index, body) -> edited index body)
    |> Codec.case1 "added" Added (Field.required "body" Codec.string)
    |> Codec.case1 "removed" Removed (Field.required "index" Codec.int)
    |> Codec.case2
        "edited"
        (fun index body -> Edited(index, body))
        (Field.required "index" Codec.int)
        (Field.required "body" Codec.string)
    |> Codec.buildUnion

/// Version 1 to 2: `added.text` became `added.body`.
let renameText: Upcaster =
    fun json ->
        match json with
        | Json.Object members when List.contains ("kind", Json.String "added") members ->
            match List.tryFind (fun (name, _) -> name = "text") members with
            | Some _ ->
                Ok(
                    Json.Object(
                        members
                        |> List.map (fun (name, value) -> (if name = "text" then "body" else name), value)
                    )
                )
            | None -> Error "an added note without text"
        | Json.Object _ -> Ok json
        | other -> Error $"expected an object, got {Json.describe other}"

let schemaV1: EventSchema<NoteV1> = EventSchema.create codecV1

let schema: EventSchema<Note> = schemaV1 |> EventSchema.evolve renameText codec

/// The notes, in order.
type Notes =
    {
        Items: string list
        /// How many events were folded into this state (to see what a snapshot saved).
        Folded: int
    }

    override this.ToString() =
        let items = String.concat "|" this.Items
        $"[{items}] after {this.Folded}"

let initial = { Items = []; Folded = 0 }

let evolve (notes: Notes) (event: Note) : Notes =
    let items =
        match event with
        | Added body -> notes.Items @ [ body ]
        | Removed index ->
            notes.Items
            |> List.indexed
            |> List.filter (fun (i, _) -> i <> index)
            |> List.map snd
        | Edited(index, body) -> notes.Items |> List.mapi (fun i item -> if i = index then body else item)

    {
        Items = items
        Folded = notes.Folded + 1
    }

let notesCodec: Codec<Notes> =
    Codec.record {
        let! items =
            Codec.field "items" (fun (n: Notes) -> n.Items) (Codec.list Codec.string)

        and! folded = Codec.field "folded" (fun n -> n.Folded) Codec.int
        return { Items = items; Folded = folded }
    }

let snapshotSchema = SnapshotSchema.create "notes" 1 notesCodec

let projection: Projection<Notes, Note> =
    Projection.create initial evolve snapshotSchema

let stream = StreamId "notes/1"
