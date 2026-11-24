namespace Fx.Contracts

open System
open System.Collections.Generic
open System.Text.RegularExpressions

/// A typed contract (spec §4.1): one value that gives the JSON shape of `'T` (`Schema`), writes it
/// (`Encode`), reads and validates it with model-readable issues (`Decode`) and renders bounded
/// previews of it (`Preview`). Build codecs with the `Codec` combinators, never by reflection.
type Codec<'T> =
    {
        Schema: Schema
        Encode: 'T -> Json
        /// Accumulates every issue it finds, with paths relative to the decoded value.
        Decode: Json -> Validation<'T, Issue>
        /// Must respect `Limits.MaxChars`; `Codec.withPreview` enforces it for custom renderers.
        Preview: Limits -> 'T -> Preview
    }

    override this.ToString() =
        "Codec<" + Schema.render this.Schema + ">"

/// A field of a JSON object without a getter: its name, presence and how its value is read and
/// written. Built by the `Field` module; used by `Codec.fieldOf` in records and by union cases.
type FieldSpec<'F> =
    {
        Name: string
        Schema: FieldSchema
        /// `None` leaves the field out.
        Encode: 'F -> Json option
        /// Gets the member's value, or `None` if it is absent. Issues are relative to the field.
        Decode: Json option -> Validation<'F, Issue>
        /// `None` leaves the field out of previews.
        Show: 'F -> (Limits -> Preview) option
    }

    override this.ToString() = string this.Schema

/// A decoded value without its type, never null (unboxing null would need reflection on FSharp.Core
/// type information).
[<Sealed>]
type internal Slot<'A>(value: 'A) =
    member _.Value = value
    override _.ToString() = "Slot"

/// Record fields without their value type. Decoded values travel in `Slot`s, so that combining fields
/// never builds nested generic types (see `RecordBuilder`).
type internal RecordPart<'T> =
    {
        Fields: FieldSchema list
        /// The fields' members, in order; absent optional fields are left out.
        Encode: 'T -> (string * Json) list
        /// The value in a `Slot`, or the issues of every field.
        Decode: (string * Json) list -> Validation<obj, Issue>
        Show: 'T -> (string * (Limits -> Preview)) list
    }

    override this.ToString() =
        "RecordPart(" + String.Join(", ", this.Fields |> List.map string) + ")"

/// Fields of a record `'T` that decode into `'A`. Combine them with `and!` in `Codec.record { }`.
type RecordFields<'T, 'A> =
    internal
        {
            Part: RecordPart<'T>
        }

    override this.ToString() = string this.Part

/// One encoded union case, made by a case encoder inside the function given to `Codec.union`.
[<Sealed>]
type CaseValue internal (tag: string, members: unit -> (string * Json) list, show: Limits -> Preview) =
    member internal _.Tag = tag
    member internal _.Members = members
    member internal _.Show = show
    override _.ToString() = tag

type internal CaseDef<'U> =
    {
        Tag: string
        Description: string option
        Payload: ObjectSchema
        /// Decodes the object's members (the tag member included).
        Decode: (string * Json) list -> Validation<'U, Issue>
    }

    override this.ToString() = this.Tag

/// A union codec under construction: `Codec.union`, one `Codec.case*` per case, then `Codec.buildUnion`.
/// `'Match` is what remains of the match function: one case encoder per case still to add, then the
/// union value.
[<Sealed>]
type UnionBuilder<'U, 'Match> internal (discriminator: string, matcher: 'Match, cases: CaseDef<'U> list) =
    member internal _.Discriminator = discriminator
    member internal _.Match = matcher
    /// Added cases, last first.
    member internal _.Cases = cases

    override _.ToString() =
        $"UnionBuilder({discriminator}: {String.Join(' ', cases |> List.rev |> List.map string)})"

module internal CodecSupport =

    let slot (value: 'A) : obj = Slot<'A>(value)

    let read<'A> (slot: obj) : 'A = (slot :?> Slot<'A>).Value

    let tryMember (name: string) (members: (string * Json) list) : Json option =
        let rec find (members: (string * Json) list) =
            match members with
            | [] -> None
            | (key, value) :: rest ->
                if String.Equals(key, name, StringComparison.Ordinal) then
                    Some value
                else
                    find rest

        find members

    let decodeField (spec: FieldSpec<'F>) (members: (string * Json) list) : Validation<'F, Issue> =
        spec.Decode(tryMember spec.Name members)
        |> Validation.mapErrors (Issue.under (PathSegment.Key spec.Name))

    let checkUnique (what: string) (names: string list) =
        let seen = HashSet<string>(StringComparer.Ordinal)

        for name in names do
            if not (seen.Add name) then
                invalidArg (nameof names) $"Duplicate {what} {Json.quote name}."

    let expected (what: string) (json: Json) : Validation<'T, Issue> = Error [ Issue.expected what json ]

    /// Combines parts: decodes all of them, keeping every issue, and builds one value in a `Slot`.
    let merge (parts: RecordPart<'T> list) (combine: obj array -> obj) : RecordPart<'T> =
        let parts = Array.ofList parts

        {
            Fields = parts |> Seq.collect (fun p -> p.Fields) |> List.ofSeq
            Encode = fun value -> parts |> Seq.collect (fun p -> p.Encode value) |> List.ofSeq
            Decode =
                fun members ->
                    let values: obj array = Array.zeroCreate parts.Length
                    let issues = ResizeArray<Issue>()

                    for i in 0 .. parts.Length - 1 do
                        match parts[i].Decode members with
                        | Ok value -> values[i] <- value
                        | Error es -> issues.AddRange es

                    if issues.Count = 0 then
                        Ok(combine values)
                    else
                        Error(List.ofSeq issues)
            Show = fun value -> parts |> Seq.collect (fun p -> p.Show value) |> List.ofSeq
        }

    let objectCodec (part: RecordPart<'T>) (make: obj -> 'T) : Codec<'T> =
        checkUnique "field" (part.Fields |> List.map (fun f -> f.Name))

        {
            Schema =
                Schema.ofKind (
                    SchemaKind.Object
                        {
                            Fields = part.Fields
                            UnknownFields = UnknownFields.Ignore
                        }
                )
            Encode = fun value -> Json.Object(part.Encode value)
            Decode =
                fun json ->
                    match json with
                    | Json.Object members -> part.Decode members |> Result.map make
                    | other -> expected "object" other
            Preview = fun limits value -> Preview.record limits (part.Show value)
        }

/// The `Codec.record { }` builder: each field is bound with `let!`/`and!`, and `return` builds the
/// record from them. Decoding accumulates the issues of every field.
///
/// Native AOT: `and!` normally nests one tuple per field (`'A * ('B * ('C * …))`), and ILC aborts
/// generic expansions nested deeper than 4 levels inside FSharp.Core's closures (IL3054). So up to 12
/// fields merge into one flat tuple (`MergeSources3` … `MergeSources12`; the compiler chains them for
/// more), and values travel between fields in `Slot`s instead of through generic closures.
type RecordBuilder() =
    member _.BindReturn(fields: RecordFields<'T, 'A>, make: 'A -> 'T) : Codec<'T> =
        CodecSupport.objectCodec fields.Part (fun value -> make (CodecSupport.read<'A> value))

    /// A record without fields, encoded as `{}`.
    member _.Return(value: 'T) : Codec<'T> =
        CodecSupport.objectCodec
            {
                Fields = []
                Encode = fun _ -> []
                Decode = fun _ -> Ok(CodecSupport.slot ())
                Show = fun _ -> []
            }
            (fun _ -> value)

    member _.MergeSources(a: RecordFields<'T, 'A>, b: RecordFields<'T, 'B>) : RecordFields<'T, 'A * 'B> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part ] (fun v ->
                    CodecSupport.slot (CodecSupport.read<'A> v[0], CodecSupport.read<'B> v[1]))
        }

    member _.MergeSources3
        (a: RecordFields<'T, 'A>, b: RecordFields<'T, 'B>, c: RecordFields<'T, 'C>)
        : RecordFields<'T, 'A * 'B * 'C> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part; c.Part ] (fun v ->
                    CodecSupport.slot (
                        CodecSupport.read<'A> v[0],
                        CodecSupport.read<'B> v[1],
                        CodecSupport.read<'C> v[2]
                    ))
        }

    member _.MergeSources4
        (a: RecordFields<'T, 'A>, b: RecordFields<'T, 'B>, c: RecordFields<'T, 'C>, d: RecordFields<'T, 'D>)
        : RecordFields<'T, 'A * 'B * 'C * 'D> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part; c.Part; d.Part ] (fun v ->
                    CodecSupport.slot (
                        CodecSupport.read<'A> v[0],
                        CodecSupport.read<'B> v[1],
                        CodecSupport.read<'C> v[2],
                        CodecSupport.read<'D> v[3]
                    ))
        }

    member _.MergeSources5
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part; c.Part; d.Part; e.Part ] (fun v ->
                    CodecSupport.slot (
                        CodecSupport.read<'A> v[0],
                        CodecSupport.read<'B> v[1],
                        CodecSupport.read<'C> v[2],
                        CodecSupport.read<'D> v[3],
                        CodecSupport.read<'E> v[4]
                    ))
        }

    member _.MergeSources6
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>,
            f: RecordFields<'T, 'F>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E * 'F> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part; c.Part; d.Part; e.Part; f.Part ] (fun v ->
                    CodecSupport.slot (
                        CodecSupport.read<'A> v[0],
                        CodecSupport.read<'B> v[1],
                        CodecSupport.read<'C> v[2],
                        CodecSupport.read<'D> v[3],
                        CodecSupport.read<'E> v[4],
                        CodecSupport.read<'F> v[5]
                    ))
        }

    member _.MergeSources7
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>,
            f: RecordFields<'T, 'F>,
            g: RecordFields<'T, 'G>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E * 'F * 'G> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part; c.Part; d.Part; e.Part; f.Part; g.Part ] (fun v ->
                    CodecSupport.slot (
                        CodecSupport.read<'A> v[0],
                        CodecSupport.read<'B> v[1],
                        CodecSupport.read<'C> v[2],
                        CodecSupport.read<'D> v[3],
                        CodecSupport.read<'E> v[4],
                        CodecSupport.read<'F> v[5],
                        CodecSupport.read<'G> v[6]
                    ))
        }

    member _.MergeSources8
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>,
            f: RecordFields<'T, 'F>,
            g: RecordFields<'T, 'G>,
            h: RecordFields<'T, 'H>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part; c.Part; d.Part; e.Part; f.Part; g.Part; h.Part ] (fun v ->
                    CodecSupport.slot (
                        CodecSupport.read<'A> v[0],
                        CodecSupport.read<'B> v[1],
                        CodecSupport.read<'C> v[2],
                        CodecSupport.read<'D> v[3],
                        CodecSupport.read<'E> v[4],
                        CodecSupport.read<'F> v[5],
                        CodecSupport.read<'G> v[6],
                        CodecSupport.read<'H> v[7]
                    ))
        }

    member _.MergeSources9
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>,
            f: RecordFields<'T, 'F>,
            g: RecordFields<'T, 'G>,
            h: RecordFields<'T, 'H>,
            i: RecordFields<'T, 'I>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I> =
        {
            Part =
                CodecSupport.merge [ a.Part; b.Part; c.Part; d.Part; e.Part; f.Part; g.Part; h.Part; i.Part ] (fun v ->
                    CodecSupport.slot (
                        CodecSupport.read<'A> v[0],
                        CodecSupport.read<'B> v[1],
                        CodecSupport.read<'C> v[2],
                        CodecSupport.read<'D> v[3],
                        CodecSupport.read<'E> v[4],
                        CodecSupport.read<'F> v[5],
                        CodecSupport.read<'G> v[6],
                        CodecSupport.read<'H> v[7],
                        CodecSupport.read<'I> v[8]
                    ))
        }

    member _.MergeSources10
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>,
            f: RecordFields<'T, 'F>,
            g: RecordFields<'T, 'G>,
            h: RecordFields<'T, 'H>,
            i: RecordFields<'T, 'I>,
            j: RecordFields<'T, 'J>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J> =
        {
            Part =
                CodecSupport.merge
                    [
                        a.Part
                        b.Part
                        c.Part
                        d.Part
                        e.Part
                        f.Part
                        g.Part
                        h.Part
                        i.Part
                        j.Part
                    ]
                    (fun v ->
                        CodecSupport.slot (
                            CodecSupport.read<'A> v[0],
                            CodecSupport.read<'B> v[1],
                            CodecSupport.read<'C> v[2],
                            CodecSupport.read<'D> v[3],
                            CodecSupport.read<'E> v[4],
                            CodecSupport.read<'F> v[5],
                            CodecSupport.read<'G> v[6],
                            CodecSupport.read<'H> v[7],
                            CodecSupport.read<'I> v[8],
                            CodecSupport.read<'J> v[9]
                        ))
        }

    member _.MergeSources11
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>,
            f: RecordFields<'T, 'F>,
            g: RecordFields<'T, 'G>,
            h: RecordFields<'T, 'H>,
            i: RecordFields<'T, 'I>,
            j: RecordFields<'T, 'J>,
            k: RecordFields<'T, 'K>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K> =
        {
            Part =
                CodecSupport.merge
                    [
                        a.Part
                        b.Part
                        c.Part
                        d.Part
                        e.Part
                        f.Part
                        g.Part
                        h.Part
                        i.Part
                        j.Part
                        k.Part
                    ]
                    (fun v ->
                        CodecSupport.slot (
                            CodecSupport.read<'A> v[0],
                            CodecSupport.read<'B> v[1],
                            CodecSupport.read<'C> v[2],
                            CodecSupport.read<'D> v[3],
                            CodecSupport.read<'E> v[4],
                            CodecSupport.read<'F> v[5],
                            CodecSupport.read<'G> v[6],
                            CodecSupport.read<'H> v[7],
                            CodecSupport.read<'I> v[8],
                            CodecSupport.read<'J> v[9],
                            CodecSupport.read<'K> v[10]
                        ))
        }

    member _.MergeSources12
        (
            a: RecordFields<'T, 'A>,
            b: RecordFields<'T, 'B>,
            c: RecordFields<'T, 'C>,
            d: RecordFields<'T, 'D>,
            e: RecordFields<'T, 'E>,
            f: RecordFields<'T, 'F>,
            g: RecordFields<'T, 'G>,
            h: RecordFields<'T, 'H>,
            i: RecordFields<'T, 'I>,
            j: RecordFields<'T, 'J>,
            k: RecordFields<'T, 'K>,
            l: RecordFields<'T, 'L>
        ) : RecordFields<'T, 'A * 'B * 'C * 'D * 'E * 'F * 'G * 'H * 'I * 'J * 'K * 'L> =
        {
            Part =
                CodecSupport.merge
                    [
                        a.Part
                        b.Part
                        c.Part
                        d.Part
                        e.Part
                        f.Part
                        g.Part
                        h.Part
                        i.Part
                        j.Part
                        k.Part
                        l.Part
                    ]
                    (fun v ->
                        CodecSupport.slot (
                            CodecSupport.read<'A> v[0],
                            CodecSupport.read<'B> v[1],
                            CodecSupport.read<'C> v[2],
                            CodecSupport.read<'D> v[3],
                            CodecSupport.read<'E> v[4],
                            CodecSupport.read<'F> v[5],
                            CodecSupport.read<'G> v[6],
                            CodecSupport.read<'H> v[7],
                            CodecSupport.read<'I> v[8],
                            CodecSupport.read<'J> v[9],
                            CodecSupport.read<'K> v[10],
                            CodecSupport.read<'L> v[11]
                        ))
        }

/// Object fields without getters, for union cases (`Codec.case1` …) and `Codec.fieldOf`.
[<RequireQualifiedAccess>]
module Field =

    let private schema (name: string) (codec: Codec<'F>) (presence: Presence) : FieldSchema =
        {
            Name = name
            Schema = codec.Schema
            Presence = presence
        }

    /// A field that must be present.
    let required (name: string) (codec: Codec<'F>) : FieldSpec<'F> =
        {
            Name = name
            Schema = schema name codec Presence.Required
            Encode = fun value -> Some(codec.Encode value)
            Decode =
                fun json ->
                    match json with
                    | Some json -> codec.Decode json
                    | None -> Error [ Issue.create "missing required field" ]
            Show = fun value -> Some(fun limits -> codec.Preview limits value)
        }

    /// A field that may be absent; `null` also reads as absent. `None` is written by leaving the field
    /// out. Do not combine with `Codec.option`: `null` could not be told apart from absent.
    let optional (name: string) (codec: Codec<'F>) : FieldSpec<'F option> =
        {
            Name = name
            Schema = schema name codec Presence.Optional
            Encode = Option.map codec.Encode
            Decode =
                fun json ->
                    match json with
                    | None
                    | Some Json.Null -> Ok None
                    | Some json -> codec.Decode json |> Result.map Some
            Show = Option.map (fun value -> fun limits -> codec.Preview limits value)
        }

    /// A field that reads as `value` when absent or `null`. It is always written.
    let withDefault (name: string) (defaultValue: 'F) (codec: Codec<'F>) : FieldSpec<'F> =
        {
            Name = name
            Schema = schema name codec (Presence.Defaulted(codec.Encode defaultValue))
            Encode = fun value -> Some(codec.Encode value)
            Decode =
                fun json ->
                    match json with
                    | None
                    | Some Json.Null -> Ok defaultValue
                    | Some json -> codec.Decode json
            Show = fun value -> Some(fun limits -> codec.Preview limits value)
        }

/// Codec combinators: primitives, collections, records, unions, refinements, mapping, names and
/// recursion. See docs/modules/contracts.md.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Codec =
    open CodecSupport

    // ---- Construction and use -------------------------------------------------------------------

    /// A codec from its parts, previewing the encoded JSON generically (`Preview.ofJson`).
    let create (schema: Schema) (encode: 'T -> Json) (decode: Json -> Validation<'T, Issue>) : Codec<'T> =
        {
            Schema = schema
            Encode = encode
            Decode = decode
            Preview = fun limits value -> Preview.ofJson limits (encode value)
        }

    /// Replaces the preview, e.g. with a domain summary such as `Match[](len=37, files=14, …)`.
    /// The result is clamped to `MaxChars`.
    let withPreview (preview: Limits -> 'T -> Preview) (codec: Codec<'T>) : Codec<'T> =
        { codec with
            Preview = fun limits value -> Preview.clamp limits (preview limits value)
        }

    let encode (codec: Codec<'T>) (value: 'T) : Json = codec.Encode value

    let decode (codec: Codec<'T>) (json: Json) : Validation<'T, Issue> = codec.Decode json

    let preview (codec: Codec<'T>) (limits: Limits) (value: 'T) : Preview = codec.Preview limits value

    /// Compact JSON text.
    let encodeText (codec: Codec<'T>) (value: 'T) : string = Json.serialize (codec.Encode value)

    /// Reads JSON text (`Json.parse`) and decodes it. A syntax error is a single issue at the root.
    let decodeText (codec: Codec<'T>) (text: string) : Validation<'T, Issue> =
        match Json.parse text with
        | Ok json -> codec.Decode json
        | Error error -> Error [ Issue.create $"invalid JSON: {error}" ]

    // ---- Documentation, names and recursion -----------------------------------------------------

    /// Documents the value for models and generated code (the schema's description).
    let describe (description: string) (codec: Codec<'T>) : Codec<'T> =
        { codec with
            Schema = Schema.describe description codec.Schema
        }

    /// Makes the codec a named type (`[A-Za-z_][A-Za-z0-9_]*`): generators define it once and refer to
    /// it by name. Records preview with the name in front: `Match(path="a.fs", line=3)`.
    let named (name: string) (codec: Codec<'T>) : Codec<'T> =
        Schema.checkName name

        let preview =
            match codec.Schema.Kind with
            | SchemaKind.Object _ ->
                fun limits value -> Preview.prefixed name limits (fun limits -> codec.Preview limits value)
            | _ -> codec.Preview

        {
            Schema = Schema.ofKind (SchemaKind.Named { Name = name; Body = codec.Schema })
            Encode = codec.Encode
            Decode = codec.Decode
            Preview = preview
        }

    /// A recursive named type. `build` gets a codec for the type itself (its schema is a reference to
    /// `name`) and returns the codec of the type's body.
    let recursive (name: string) (build: Codec<'T> -> Codec<'T>) : Codec<'T> =
        Schema.checkName name
        let cell: Codec<'T> option ref = ref None

        let resolved () =
            match cell.Value with
            | Some codec -> codec
            | None -> invalidOp $"The recursive codec {name} was used while it was being built."

        let self =
            {
                Schema = Schema.ofKind (SchemaKind.Ref name)
                Encode = fun value -> (resolved ()).Encode value
                Decode = fun json -> (resolved ()).Decode json
                Preview = fun limits value -> (resolved ()).Preview limits value
            }

        let codec = named name (build self)
        cell.Value <- Some codec
        codec

    // ---- Mapping ---------------------------------------------------------------------------------

    /// The same JSON shape for another type, through a two-way conversion.
    let map (decode: 'A -> 'B) (encode: 'B -> 'A) (codec: Codec<'A>) : Codec<'B> =
        {
            Schema = codec.Schema
            Encode = fun value -> codec.Encode(encode value)
            Decode = fun json -> codec.Decode json |> Result.map decode
            Preview = fun limits value -> codec.Preview limits (encode value)
        }

    /// Like `map`, but decoding can fail with a message (an issue at the value's path), e.g. parsing a
    /// date from a string.
    let tryMap (decode: 'A -> Result<'B, string>) (encode: 'B -> 'A) (codec: Codec<'A>) : Codec<'B> =
        {
            Schema = codec.Schema
            Encode = fun value -> codec.Encode(encode value)
            Decode =
                fun json ->
                    codec.Decode json
                    |> Result.bind (fun a -> decode a |> Result.mapError (fun message -> [ Issue.create message ]))
            Preview = fun limits value -> codec.Preview limits (encode value)
        }

    // ---- Refinements -----------------------------------------------------------------------------

    /// Adds a check after decoding. An `Error` message becomes an issue at the value's path; write it
    /// for a model, e.g. `expected a positive number, got -3`.
    let refine (check: 'T -> Result<unit, string>) (codec: Codec<'T>) : Codec<'T> =
        { codec with
            Decode =
                fun json ->
                    codec.Decode json
                    |> Result.bind (fun value ->
                        match check value with
                        | Ok() -> Ok value
                        | Error message -> Error [ Issue.create message ])
        }

    let private withKind (update: SchemaKind -> SchemaKind) (codec: Codec<'T>) : Codec<'T> =
        { codec with
            Schema =
                { codec.Schema with
                    Kind = update codec.Schema.Kind
                }
        }

    let private stringFacets (update: StringFacets -> StringFacets) (kind: SchemaKind) =
        match kind with
        | SchemaKind.String facets -> SchemaKind.String(update facets)
        | other -> other

    /// Unicode code points, as JSON Schema counts string length.
    let private codePoints (s: string) =
        let mutable count = s.Length

        for i in 1 .. s.Length - 1 do
            if JsonWriter.IsLowSurrogate s[i] && JsonWriter.IsHighSurrogate s[i - 1] then
                count <- count - 1

        count

    /// Strings of at least `n` characters (code points).
    let minLength (n: int) (codec: Codec<string>) : Codec<string> =
        codec
        |> refine (fun s ->
            let length = codePoints s

            if length >= n then Ok()
            elif n = 1 then Error "expected a non-empty string"
            else Error $"expected at least {n} characters, got {length}")
        |> withKind (stringFacets (fun f -> { f with MinLength = Some n }))

    /// Strings of at most `n` characters (code points).
    let maxLength (n: int) (codec: Codec<string>) : Codec<string> =
        codec
        |> refine (fun s ->
            let length = codePoints s

            if length <= n then
                Ok()
            else
                Error $"expected at most {n} characters, got {length}")
        |> withKind (stringFacets (fun f -> { f with MaxLength = Some n }))

    /// Non-empty strings.
    let nonEmpty (codec: Codec<string>) : Codec<string> = minLength 1 codec

    /// Strings in which the ECMAScript regular expression `regex` matches (unanchored, as JSON Schema
    /// `pattern`; add `^…$` to match the whole string).
    let pattern (regex: string) (codec: Codec<string>) : Codec<string> =
        let compiled = Regex(regex, RegexOptions.ECMAScript)

        codec
        |> refine (fun s ->
            if compiled.IsMatch s then
                Ok()
            else
                Error $"expected a string matching /{regex}/, got {Json.describe (Json.String s)}")
        |> withKind (stringFacets (fun f -> { f with Pattern = Some regex }))

    /// Documents a JSON Schema `format` such as `date-time` or `uri`. Not validated: pair it with a
    /// refinement or `tryMap` that checks it.
    let format (name: string) (codec: Codec<string>) : Codec<string> =
        codec |> withKind (stringFacets (fun f -> { f with Format = Some name }))

    let private noun (kind: SchemaKind) =
        match kind with
        | SchemaKind.Integer _ -> "an integer"
        | SchemaKind.Number _ -> "a number"
        | _ -> "a value"

    let private numberFacets (update: NumberFacets -> NumberFacets) (kind: SchemaKind) =
        match kind with
        | SchemaKind.Integer facets -> SchemaKind.Integer(update facets)
        | SchemaKind.Number facets -> SchemaKind.Number(update facets)
        | other -> other

    let private bound (codec: Codec<'T>) (value: 'T) : Bound option =
        match codec.Encode value with
        | Json.Number n -> Some(Bound.Inclusive n)
        | _ -> None

    /// Values from `low` to `high`, inclusive.
    let between (low: 'T) (high: 'T) (codec: Codec<'T>) : Codec<'T> =
        let what = noun codec.Schema.Kind

        let lowText, highText =
            Json.serialize (codec.Encode low), Json.serialize (codec.Encode high)

        codec
        |> refine (fun value ->
            if value >= low && value <= high then
                Ok()
            else
                Error $"expected {what} from {lowText} to {highText}, got {Json.describe (codec.Encode value)}")
        |> withKind (
            numberFacets (fun f ->
                { f with
                    Minimum = bound codec low
                    Maximum = bound codec high
                })
        )

    /// Values of at least `low`.
    let atLeast (low: 'T) (codec: Codec<'T>) : Codec<'T> =
        let what = noun codec.Schema.Kind
        let lowText = Json.serialize (codec.Encode low)

        codec
        |> refine (fun value ->
            if value >= low then
                Ok()
            else
                Error $"expected {what} of at least {lowText}, got {Json.describe (codec.Encode value)}")
        |> withKind (numberFacets (fun f -> { f with Minimum = bound codec low }))

    /// Values of at most `high`.
    let atMost (high: 'T) (codec: Codec<'T>) : Codec<'T> =
        let what = noun codec.Schema.Kind
        let highText = Json.serialize (codec.Encode high)

        codec
        |> refine (fun value ->
            if value <= high then
                Ok()
            else
                Error $"expected {what} of at most {highText}, got {Json.describe (codec.Encode value)}")
        |> withKind (numberFacets (fun f -> { f with Maximum = bound codec high }))

    let private arrayFacets (update: ArraySchema -> ArraySchema) (kind: SchemaKind) =
        match kind with
        | SchemaKind.Array array -> SchemaKind.Array(update array)
        | other -> other

    /// Lists of at least `n` items.
    let minItems (n: int) (codec: Codec<'T list>) : Codec<'T list> =
        codec
        |> refine (fun items ->
            let length = List.length items

            if length >= n then Ok()
            elif n = 1 then Error "expected a non-empty array"
            else Error $"expected at least {n} items, got {length}")
        |> withKind (arrayFacets (fun a -> { a with MinItems = Some n }))

    /// Lists of at most `n` items.
    let maxItems (n: int) (codec: Codec<'T list>) : Codec<'T list> =
        codec
        |> refine (fun items ->
            let length = List.length items

            if length <= n then
                Ok()
            else
                Error $"expected at most {n} items, got {length}")
        |> withKind (arrayFacets (fun a -> { a with MaxItems = Some n }))

    // ---- Records ---------------------------------------------------------------------------------

    /// Builds a record codec. Bind each field with `let!`/`and!` and `return` the record:
    ///
    /// ```fsharp
    /// Codec.record {
    ///     let! path = Codec.field "path" (fun m -> m.Path) Codec.string
    ///     and! line = Codec.field "line" (fun m -> m.Line) Codec.int
    ///     and! note = Codec.optionalField "note" (fun m -> m.Note) Codec.string
    ///     return { Path = path; Line = line; Note = note } }
    /// ```
    ///
    /// Fields are written in this order. Decoding reports every missing or invalid field and ignores
    /// unknown members (see `strict`).
    let record = RecordBuilder()

    /// A record field described by a `FieldSpec`, read from the record with `get`.
    let fieldOf (spec: FieldSpec<'F>) (get: 'T -> 'F) : RecordFields<'T, 'F> =
        {
            Part =
                {
                    Fields = [ spec.Schema ]
                    Encode =
                        fun value ->
                            match spec.Encode(get value) with
                            | Some json -> [ spec.Name, json ]
                            | None -> []
                    Decode = fun members -> decodeField spec members |> Result.map slot
                    Show =
                        fun value ->
                            match spec.Show(get value) with
                            | Some render -> [ spec.Name, render ]
                            | None -> []
                }
        }

    /// A required record field.
    let field (name: string) (get: 'T -> 'F) (codec: Codec<'F>) : RecordFields<'T, 'F> =
        fieldOf (Field.required name codec) get

    /// An optional record field (`Field.optional`): absent or `null` reads as `None`.
    let optionalField (name: string) (get: 'T -> 'F option) (codec: Codec<'F>) : RecordFields<'T, 'F option> =
        fieldOf (Field.optional name codec) get

    /// A record field that reads as `value` when absent or `null` (`Field.withDefault`).
    let defaultField (name: string) (get: 'T -> 'F) (value: 'F) (codec: Codec<'F>) : RecordFields<'T, 'F> =
        fieldOf (Field.withDefault name value codec) get

    // ---- Unions ----------------------------------------------------------------------------------

    /// Starts a union codec whose JSON objects carry the case tag in the `discriminator` field, e.g.
    /// `{"kind": "done", "summary": "…"}`. `matcher` gets one encoder per case, in the order the cases
    /// are added, then the value; matching on the value keeps encoding exhaustive:
    ///
    /// ```fsharp
    /// Codec.union "kind" (fun finished waiting value ->
    ///     match value with
    ///     | Finished summary -> finished summary
    ///     | Waiting(jobs, note) -> waiting jobs note)
    /// |> Codec.case1 "finished" Finished (Field.required "summary" Codec.string)
    /// |> Codec.case2 "waiting" (fun jobs note -> Waiting(jobs, note))
    ///     (Field.required "jobs" (Codec.list Codec.string)) (Field.required "note" Codec.string)
    /// |> Codec.buildUnion
    /// ```
    ///
    /// Cases with the same payload types can be swapped by mistake: the round-trip tests catch that.
    let union (discriminator: string) (matcher: 'Match) : UnionBuilder<'U, 'Match> =
        UnionBuilder(discriminator, matcher, [])

    let private entry (spec: FieldSpec<'F>) (value: 'F) : (string * Json) option =
        spec.Encode value |> Option.map (fun json -> spec.Name, json)

    let private shown (spec: FieldSpec<'F>) (value: 'F) : (string * (Limits -> Preview)) option =
        spec.Show value |> Option.map (fun render -> spec.Name, render)

    let private caseValue
        (tag: string)
        (members: unit -> (string * Json) option list)
        (fields: unit -> (string * (Limits -> Preview)) option list)
        : CaseValue =
        CaseValue(
            tag,
            (fun () -> List.choose id (members ())),
            fun limits -> Preview.prefixed tag limits (fun limits -> Preview.record limits (List.choose id (fields ())))
        )

    let private addCase
        (tag: string)
        (description: string option)
        (fields: FieldSchema list)
        (decode: (string * Json) list -> Validation<'U, Issue>)
        (rest: 'Rest)
        (builder: UnionBuilder<'U, 'M>)
        : UnionBuilder<'U, 'Rest> =
        let def: CaseDef<'U> =
            {
                Tag = tag
                Description = description
                Payload =
                    {
                        Fields = fields
                        UnknownFields = UnknownFields.Ignore
                    }
                Decode = decode
            }

        UnionBuilder(builder.Discriminator, rest, def :: builder.Cases)

    /// A case without fields: `{"kind": "cancelled"}`. Its encoder is the `CaseValue` itself.
    let case0 (tag: string) (value: 'U) (builder: UnionBuilder<'U, CaseValue -> 'Rest>) : UnionBuilder<'U, 'Rest> =
        let encoded =
            CaseValue(tag, (fun () -> []), (fun limits -> Preview.atom limits tag))

        builder |> addCase tag None [] (fun _ -> Ok value) (builder.Match encoded)

    /// A case with one field.
    let case1
        (tag: string)
        (make: 'A -> 'U)
        (f1: FieldSpec<'A>)
        (builder: UnionBuilder<'U, ('A -> CaseValue) -> 'Rest>)
        : UnionBuilder<'U, 'Rest> =
        let encoder a =
            caseValue tag (fun () -> [ entry f1 a ]) (fun () -> [ shown f1 a ])

        builder
        |> addCase
            tag
            None
            [ f1.Schema ]
            (fun members -> decodeField f1 members |> Result.map make)
            (builder.Match encoder)

    /// A case with two fields.
    let case2
        (tag: string)
        (make: 'A -> 'B -> 'U)
        (f1: FieldSpec<'A>)
        (f2: FieldSpec<'B>)
        (builder: UnionBuilder<'U, ('A -> 'B -> CaseValue) -> 'Rest>)
        : UnionBuilder<'U, 'Rest> =
        let encoder a b =
            caseValue tag (fun () -> [ entry f1 a; entry f2 b ]) (fun () -> [ shown f1 a; shown f2 b ])

        let decode members =
            Validation.map2 make (decodeField f1 members) (decodeField f2 members)

        builder
        |> addCase tag None [ f1.Schema; f2.Schema ] decode (builder.Match encoder)

    /// A case with three fields.
    let case3
        (tag: string)
        (make: 'A -> 'B -> 'C -> 'U)
        (f1: FieldSpec<'A>)
        (f2: FieldSpec<'B>)
        (f3: FieldSpec<'C>)
        (builder: UnionBuilder<'U, ('A -> 'B -> 'C -> CaseValue) -> 'Rest>)
        : UnionBuilder<'U, 'Rest> =
        let encoder a b c =
            caseValue tag (fun () -> [ entry f1 a; entry f2 b; entry f3 c ]) (fun () ->
                [ shown f1 a; shown f2 b; shown f3 c ])

        let decode members =
            Validation.map3 make (decodeField f1 members) (decodeField f2 members) (decodeField f3 members)

        builder
        |> addCase tag None [ f1.Schema; f2.Schema; f3.Schema ] decode (builder.Match encoder)

    /// A case with four fields.
    let case4
        (tag: string)
        (make: 'A -> 'B -> 'C -> 'D -> 'U)
        (f1: FieldSpec<'A>)
        (f2: FieldSpec<'B>)
        (f3: FieldSpec<'C>)
        (f4: FieldSpec<'D>)
        (builder: UnionBuilder<'U, ('A -> 'B -> 'C -> 'D -> CaseValue) -> 'Rest>)
        : UnionBuilder<'U, 'Rest> =
        let encoder a b c d =
            caseValue tag (fun () -> [ entry f1 a; entry f2 b; entry f3 c; entry f4 d ]) (fun () ->
                [ shown f1 a; shown f2 b; shown f3 c; shown f4 d ])

        let decode members =
            Validation.map2
                (fun (a, b) (c, d) -> make a b c d)
                (Validation.zip (decodeField f1 members) (decodeField f2 members))
                (Validation.zip (decodeField f3 members) (decodeField f4 members))

        builder
        |> addCase tag None [ f1.Schema; f2.Schema; f3.Schema; f4.Schema ] decode (builder.Match encoder)

    /// A case whose fields are those of an object codec (a record, possibly named), written next to the
    /// tag: `{"kind": "blob", "digest": "…", "size": 3}`. For cases with many fields.
    let caseOf
        (tag: string)
        (make: 'P -> 'U)
        (payload: Codec<'P>)
        (builder: UnionBuilder<'U, ('P -> CaseValue) -> 'Rest>)
        : UnionBuilder<'U, 'Rest> =
        let fields, name, description =
            match payload.Schema.Kind with
            | SchemaKind.Object o -> o.Fields, None, payload.Schema.Description
            | SchemaKind.Named {
                                   Name = name
                                   Body = { Kind = SchemaKind.Object o } as body
                               } -> o.Fields, Some name, Option.orElse body.Description payload.Schema.Description
            | _ -> invalidArg (nameof payload) $"The payload of case {Json.quote tag} must be an object codec."

        let discriminator = builder.Discriminator

        let encoder (value: 'P) =
            let members () =
                match payload.Encode value with
                | Json.Object members -> members
                | _ -> invalidOp $"The payload of case {Json.quote tag} did not encode to an object."

            let show (limits: Limits) =
                Preview.prefixed tag limits (fun limits ->
                    match name with
                    | None -> payload.Preview limits value
                    | Some name ->
                        // Render with room for the type name, then drop it: `tag(fields)`.
                        let inner =
                            payload.Preview
                                { limits with
                                    MaxChars = limits.MaxChars + name.Length
                                }
                                value

                        if inner.Text.StartsWith(name + "(", StringComparison.Ordinal) then
                            { inner with
                                Text = inner.Text.Substring name.Length
                            }
                        else
                            Preview.clamp limits inner)

            CaseValue(tag, members, show)

        let decode (members: (string * Json) list) =
            members
            |> List.filter (fun (key, _) -> not (String.Equals(key, discriminator, StringComparison.Ordinal)))
            |> Json.Object
            |> payload.Decode
            |> Result.map make

        builder |> addCase tag description fields decode (builder.Match encoder)

    /// Finishes a union once every case of the match function has been added.
    let buildUnion (builder: UnionBuilder<'U, 'U -> CaseValue>) : Codec<'U> =
        let discriminator = builder.Discriminator
        let cases = List.rev builder.Cases
        checkUnique "case tag" (cases |> List.map (fun c -> c.Tag))

        for case in cases do
            if case.Payload.Fields |> List.exists (fun f -> f.Name = discriminator) then
                invalidArg
                    (nameof builder)
                    $"Case {Json.quote case.Tag} has a field named like the discriminator {Json.quote discriminator}."

            checkUnique "field" (case.Payload.Fields |> List.map (fun f -> f.Name))

        let byTag = cases |> List.map (fun c -> c.Tag, c) |> Map.ofList

        let tags = Issue.quoteAll (cases |> List.map (fun c -> c.Tag))

        let tagIssue (message: string) =
            Error [ Issue.at [ PathSegment.Key discriminator ] message ]

        let matcher = builder.Match

        {
            Schema =
                Schema.ofKind (
                    SchemaKind.Union
                        {
                            Discriminator = discriminator
                            Cases =
                                cases
                                |> List.map (fun c ->
                                    ({
                                        Tag = c.Tag
                                        Description = c.Description
                                        Payload = c.Payload
                                    }
                                    : UnionCase))
                        }
                )
            Encode =
                fun value ->
                    let case = matcher value
                    Json.Object((discriminator, Json.String case.Tag) :: case.Members())
            Decode =
                fun json ->
                    match json with
                    | Json.Object members ->
                        match tryMember discriminator members with
                        | None -> tagIssue $"missing required field; expected one of {tags}"
                        | Some(Json.String tag as found) ->
                            match byTag.TryFind tag with
                            | Some case -> case.Decode members
                            | None -> tagIssue $"expected one of {tags}, got {Json.describe found}"
                        | Some other -> tagIssue $"expected one of {tags}, got {Json.describe other}"
                    | other -> expected "object" other
            Preview = fun limits value -> Preview.clamp limits ((matcher value).Show limits)
        }

    /// Documents one case of a union codec.
    let describeCase (tag: string) (description: string) (codec: Codec<'U>) : Codec<'U> =
        let describeIn (u: UnionSchema) =
            if not (u.Cases |> List.exists (fun c -> c.Tag = tag)) then
                invalidArg (nameof tag) $"The union has no case {Json.quote tag}."

            SchemaKind.Union
                { u with
                    Cases =
                        u.Cases
                        |> List.map (fun c ->
                            if c.Tag = tag then
                                { c with
                                    Description = Some description
                                }
                            else
                                c)
                }

        let kind =
            match codec.Schema.Kind with
            | SchemaKind.Union u -> describeIn u
            | SchemaKind.Named({ Body = { Kind = SchemaKind.Union u } } as named) ->
                SchemaKind.Named
                    { named with
                        Body = { named.Body with Kind = describeIn u }
                    }
            | _ -> invalidArg (nameof codec) "Codec.describeCase applies to union codecs."

        { codec with
            Schema = { codec.Schema with Kind = kind }
        }

    /// Rejects object members that are not fields of the record (or of the decoded union case), one
    /// issue each, e.g. for model input where a misspelt optional field must not pass silently.
    /// Apply it before `named`; inside `recursive`, apply it to the body.
    let strict (codec: Codec<'T>) : Codec<'T> =
        let unknown (known: string list) (members: (string * Json) list) =
            let knownSet = HashSet<string>(known, StringComparer.Ordinal)
            let expectedText = Issue.quoteAll known

            members
            |> List.choose (fun (key, _) ->
                if knownSet.Contains key then
                    None
                else
                    Some(Issue.at [ PathSegment.Key key ] $"unknown field; known fields are {expectedText}"))

        let rec rewrite (schema: Schema) : Schema * ((string * Json) list -> Issue list) =
            match schema.Kind with
            | SchemaKind.Object o ->
                let known = o.Fields |> List.map (fun f -> f.Name)

                { schema with
                    Kind =
                        SchemaKind.Object
                            { o with
                                UnknownFields = UnknownFields.Reject
                            }
                },
                unknown known
            | SchemaKind.Union u ->
                let known =
                    u.Cases
                    |> List.map (fun c -> c.Tag, u.Discriminator :: (c.Payload.Fields |> List.map (fun f -> f.Name)))
                    |> Map.ofList

                let check (members: (string * Json) list) =
                    match tryMember u.Discriminator members with
                    | Some(Json.String tag) ->
                        match known.TryFind tag with
                        | Some fields -> unknown fields members
                        | None -> []
                    | _ -> []

                { schema with
                    Kind =
                        SchemaKind.Union
                            { u with
                                Cases =
                                    u.Cases
                                    |> List.map (fun c ->
                                        { c with
                                            Payload =
                                                { c.Payload with
                                                    UnknownFields = UnknownFields.Reject
                                                }
                                        })
                            }
                },
                check
            | SchemaKind.Named named ->
                let body, check = rewrite named.Body

                { schema with
                    Kind = SchemaKind.Named { named with Body = body }
                },
                check
            | _ -> invalidArg (nameof codec) "Codec.strict applies to record and union codecs."

        let schema, check = rewrite codec.Schema

        { codec with
            Schema = schema
            Decode =
                fun json ->
                    let unknownIssues =
                        match json with
                        | Json.Object members -> check members
                        | _ -> []

                    match codec.Decode json, unknownIssues with
                    | result, [] -> result
                    | Ok _, issues -> Error issues
                    | Error issues, more -> Error(issues @ more)
        }

    // ---- Collections -----------------------------------------------------------------------------

    let private itemTypeName (schema: Schema) =
        let name = Schema.typeName schema
        if name.Contains " | " then "(" + name + ")" else name

    let private decodeItems (item: Codec<'T>) (items: Json list) : Validation<'T list, Issue> =
        items
        |> Validation.traverseIndexed (fun index json ->
            item.Decode json |> Validation.mapErrors (Issue.under (PathSegment.Index index)))

    let private previewItems (item: Codec<'T>) (items: 'T seq) : seq<Limits -> Preview> =
        items |> Seq.map (fun value -> fun limits -> item.Preview limits value)

    /// A JSON array as an F# list.
    let list (item: Codec<'T>) : Codec<'T list> =
        let itemType = itemTypeName item.Schema

        {
            Schema =
                Schema.ofKind (
                    SchemaKind.Array
                        {
                            Items = item.Schema
                            MinItems = None
                            MaxItems = None
                        }
                )
            Encode = fun items -> Json.Array(List.map item.Encode items)
            Decode =
                fun json ->
                    match json with
                    | Json.Array items -> decodeItems item items
                    | other -> expected "array" other
            Preview = fun limits items -> Preview.list limits itemType (List.length items) (previewItems item items)
        }

    /// A JSON array as an F# array.
    let array (item: Codec<'T>) : Codec<'T array> =
        let asList = list item

        {
            Schema = asList.Schema
            Encode = fun items -> Json.Array [ for value in items -> item.Encode value ]
            Decode = fun json -> asList.Decode json |> Result.map Array.ofList
            Preview =
                fun limits items ->
                    Preview.list limits (itemTypeName item.Schema) items.Length (previewItems item items)
        }

    let private decodeMembers (values: Codec<'V>) (members: (string * Json) list) =
        members
        |> Validation.traverse (fun (key, json) ->
            values.Decode json
            |> Result.map (fun value -> key, value)
            |> Validation.mapErrors (Issue.under (PathSegment.Key key)))

    /// A JSON object with arbitrary keys, as an F# map. Encoded in key order (ordinal).
    let stringMap (values: Codec<'V>) : Codec<Map<string, 'V>> =
        {
            Schema = Schema.ofKind (SchemaKind.Map values.Schema)
            Encode = fun map -> Json.Object [ for KeyValue(key, value) in map -> key, values.Encode value ]
            Decode =
                fun json ->
                    match json with
                    | Json.Object members -> decodeMembers values members |> Result.map Map.ofList
                    | other -> expected "object" other
            Preview =
                fun limits map ->
                    Preview.map
                        limits
                        map.Count
                        (map
                         |> Seq.map (fun (KeyValue(key, value)) -> key, (fun limits -> values.Preview limits value)))
        }

    /// A JSON object with arbitrary keys, as key-value pairs in member order.
    let keyValues (values: Codec<'V>) : Codec<(string * 'V) list> =
        {
            Schema = Schema.ofKind (SchemaKind.Map values.Schema)
            Encode = fun pairs -> Json.Object(pairs |> List.map (fun (key, value) -> key, values.Encode value))
            Decode =
                fun json ->
                    match json with
                    | Json.Object members -> decodeMembers values members
                    | other -> expected "object" other
            Preview =
                fun limits pairs ->
                    Preview.map
                        limits
                        (List.length pairs)
                        (pairs
                         |> Seq.map (fun (key, value) -> key, (fun limits -> values.Preview limits value)))
        }

    let private tupleItems (count: int) (json: Json) : Result<Json list, Issue list> =
        match json with
        | Json.Array items when List.length items = count -> Ok items
        | other -> Error [ Issue.expected $"array of {count} items" other ]

    let private at (index: int) (validation: Validation<'T, Issue>) =
        validation |> Validation.mapErrors (Issue.under (PathSegment.Index index))

    /// A pair as a two-item JSON array.
    let tuple2 (a: Codec<'A>) (b: Codec<'B>) : Codec<'A * 'B> =
        {
            Schema = Schema.ofKind (SchemaKind.Tuple [ a.Schema; b.Schema ])
            Encode = fun (x, y) -> Json.Array [ a.Encode x; b.Encode y ]
            Decode =
                fun json ->
                    tupleItems 2 json
                    |> Result.bind (fun items ->
                        Validation.map2 (fun x y -> x, y) (at 0 (a.Decode items[0])) (at 1 (b.Decode items[1])))
            Preview =
                fun limits (x, y) ->
                    Preview.tuple limits [ (fun limits -> a.Preview limits x); (fun limits -> b.Preview limits y) ]
        }

    /// A triple as a three-item JSON array.
    let tuple3 (a: Codec<'A>) (b: Codec<'B>) (c: Codec<'C>) : Codec<'A * 'B * 'C> =
        {
            Schema = Schema.ofKind (SchemaKind.Tuple [ a.Schema; b.Schema; c.Schema ])
            Encode = fun (x, y, z) -> Json.Array [ a.Encode x; b.Encode y; c.Encode z ]
            Decode =
                fun json ->
                    tupleItems 3 json
                    |> Result.bind (fun items ->
                        Validation.map3
                            (fun x y z -> x, y, z)
                            (at 0 (a.Decode items[0]))
                            (at 1 (b.Decode items[1]))
                            (at 2 (c.Decode items[2])))
            Preview =
                fun limits (x, y, z) ->
                    Preview.tuple
                        limits
                        [
                            (fun limits -> a.Preview limits x)
                            (fun limits -> b.Preview limits y)
                            (fun limits -> c.Preview limits z)
                        ]
        }

    /// `null` for `None`. Do not nest options: `Some None` and `None` would both be `null`. For record
    /// fields that may be absent, use `optionalField`.
    let option (codec: Codec<'T>) : Codec<'T option> =
        {
            Schema = Schema.ofKind (SchemaKind.Nullable codec.Schema)
            Encode =
                fun value ->
                    match value with
                    | Some value -> codec.Encode value
                    | None -> Json.Null
            Decode =
                fun json ->
                    match json with
                    | Json.Null -> Ok None
                    | json -> codec.Decode json |> Result.map Some
            Preview =
                fun limits value ->
                    match value with
                    | Some value -> codec.Preview limits value
                    | None -> Preview.atom limits "null"
        }

    // ---- Scalars ---------------------------------------------------------------------------------
    // Last, because they shadow the conversion functions of the same names.

    /// Any JSON value, unchanged.
    let json: Codec<Json> =
        {
            Schema = Schema.ofKind SchemaKind.Any
            Encode = id
            Decode = Ok
            Preview = Preview.ofJson
        }

    /// Exactly `value` (compared in canonical form); decodes to unit. For fixed members such as
    /// `"jsonrpc": "2.0"`.
    let literal (value: Json) : Codec<unit> =
        let canonical = Json.canonicalize value
        let text = Json.serialize value

        {
            Schema = Schema.ofKind (SchemaKind.Const value)
            Encode = fun () -> value
            Decode =
                fun json ->
                    if Json.canonicalize json = canonical then
                        Ok()
                    else
                        expected text json
            Preview = fun limits () -> Preview.atom limits text
        }

    /// One of a fixed set of strings, each standing for one value.
    let enum (cases: ('T * string) list) : Codec<'T> =
        checkUnique "enum value" (List.map snd cases)
        let names = Issue.quoteAll (List.map snd cases)

        let nameOf (value: 'T) =
            match cases |> List.tryFind (fun (case, _) -> case = value) with
            | Some(_, name) -> name
            | None -> invalidArg (nameof value) $"The value {value} is missing from the enum codec."

        {
            Schema = Schema.ofKind (SchemaKind.Enum(List.map snd cases))
            Encode = fun value -> Json.String(nameOf value)
            Decode =
                fun json ->
                    let found =
                        match json with
                        | Json.String s -> cases |> List.tryFind (fun (_, name) -> name = s)
                        | _ -> None

                    match found with
                    | Some(value, _) -> Ok value
                    | None -> expected $"one of {names}" json
            Preview = fun limits value -> Preview.string limits (nameOf value)
        }

    /// `null`.
    let unit: Codec<unit> =
        {
            Schema = Schema.ofKind SchemaKind.Null
            Encode = fun () -> Json.Null
            Decode =
                fun json ->
                    match json with
                    | Json.Null -> Ok()
                    | other -> expected "null" other
            Preview = fun limits () -> Preview.atom limits "null"
        }

    let bool: Codec<bool> =
        {
            Schema = Schema.ofKind SchemaKind.Boolean
            Encode = Json.Bool
            Decode =
                fun json ->
                    match json with
                    | Json.Bool value -> Ok value
                    | other -> expected "boolean" other
            Preview = fun limits value -> Preview.atom limits (if value then "true" else "false")
        }

    let string: Codec<string> =
        {
            Schema = Schema.ofKind (SchemaKind.String Schema.noStringFacets)
            Encode = Json.String
            Decode =
                fun json ->
                    match json with
                    | Json.String value -> Ok value
                    | other -> expected "string" other
            Preview = Preview.string
        }

    /// Any JSON number, with its exact text.
    let number: Codec<JsonNumber> =
        {
            Schema = Schema.ofKind (SchemaKind.Number Schema.noNumberFacets)
            Encode = Json.Number
            Decode =
                fun json ->
                    match json with
                    | Json.Number value -> Ok value
                    | other -> expected "number" other
            Preview = fun limits value -> Preview.atom limits value.Text
        }

    let private integer (range: string) (convert: JsonNumber -> 'T option) (toNumber: 'T -> JsonNumber) : Codec<'T> =
        {
            Schema = Schema.ofKind (SchemaKind.Integer Schema.noNumberFacets)
            Encode = fun value -> Json.Number(toNumber value)
            Decode =
                fun json ->
                    match json with
                    | Json.Number n ->
                        match convert n with
                        | Some value -> Ok value
                        | None when JsonNumber.isInteger n -> expected $"integer {range}" json
                        | None -> expected "integer" json
                    | other -> expected "integer" other
            Preview = fun limits value -> Preview.atom limits (toNumber value).Text
        }

    /// A 32-bit integer. Accepts any number with an integral value in range, such as `3.0` or `1e3`.
    let int: Codec<int> =
        integer $"from {Int32.MinValue} to {Int32.MaxValue}" JsonNumber.tryToInt JsonNumber.ofInt

    /// A 64-bit integer, exact. JavaScript numbers are exact only up to 2^53.
    let int64: Codec<int64> =
        integer $"from {Int64.MinValue} to {Int64.MaxValue}" JsonNumber.tryToInt64 JsonNumber.ofInt64

    /// A decimal, written with its scale (`1.10`). Digits beyond decimal precision round when reading.
    let decimal: Codec<decimal> =
        {
            Schema = Schema.ofKind (SchemaKind.Number Schema.noNumberFacets)
            Encode = fun value -> Json.Number(JsonNumber.ofDecimal value)
            Decode =
                fun json ->
                    match json with
                    | Json.Number n ->
                        match JsonNumber.tryToDecimal n with
                        | Some value -> Ok value
                        | None -> expected "number within the decimal range" json
                    | other -> expected "number" other
            Preview = fun limits value -> Preview.atom limits (JsonNumber.ofDecimal value).Text
        }

    /// A float, written as its shortest round-trip text. JSON has no NaN or infinities: like the
    /// protobuf JSON mapping, they are written as the strings `"NaN"`, `"Infinity"` and `"-Infinity"`.
    let float: Codec<float> =
        let encode (value: float) =
            if Double.IsNaN value then
                Json.String "NaN"
            elif Double.IsPositiveInfinity value then
                Json.String "Infinity"
            elif Double.IsNegativeInfinity value then
                Json.String "-Infinity"
            else
                Json.Number(JsonNumber.ofFloat value)

        {
            Schema = Schema.ofKind (SchemaKind.Number Schema.noNumberFacets)
            Encode = encode
            Decode =
                fun json ->
                    match json with
                    | Json.Number n ->
                        let value = JsonNumber.toFloat n

                        if Double.IsFinite value then
                            Ok value
                        else
                            expected "number within the float range" json
                    | Json.String "NaN" -> Ok Double.NaN
                    | Json.String "Infinity" -> Ok Double.PositiveInfinity
                    | Json.String "-Infinity" -> Ok Double.NegativeInfinity
                    | other -> expected "number" other
            Preview =
                fun limits value ->
                    match encode value with
                    | Json.Number n -> Preview.atom limits n.Text
                    | Json.String s -> Preview.atom limits s
                    | other -> Preview.ofJson limits other
        }
