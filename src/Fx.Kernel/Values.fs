namespace Fx.Kernel

open System
open Fx.Contracts
open Fx.Store

/// The id of a host-resident live object (spec §6.6): history, ledger results, agent transcripts,
/// runs. Opaque to kernels. Only the host issues ids (`HandleTable`), from random bytes, and it
/// resolves them only in the session that issued them, so a kernel can neither forge one nor use
/// one from another session.
[<Struct; CustomEquality; NoComparison>]
type HandleId =
    private
    | HandleId of string

    member this.Value =
        let (HandleId text) = this
        text

    override this.Equals(other: obj) =
        match other with
        | :? HandleId as id -> String.Equals(this.Value, id.Value, StringComparison.Ordinal)
        | _ -> false

    override this.GetHashCode() =
        StringComparer.Ordinal.GetHashCode this.Value

    override this.ToString() = this.Value

    interface IEquatable<HandleId> with
        member this.Equals(other: HandleId) =
            String.Equals(this.Value, other.Value, StringComparison.Ordinal)

module HandleId =
    /// The longest handle id on the wire.
    [<Literal>]
    let MaxLength = 128

    /// A handle id as written on the wire: 1 to 128 printable ASCII characters. Says nothing about
    /// whether the host issued it; only `HandleTable.resolve` does.
    let tryParse (text: string) : HandleId option =
        if
            String.IsNullOrEmpty text
            || text.Length > MaxLength
            || text |> Seq.exists (fun c -> c < '!' || c > '~')
        then
            None
        else
            Some(HandleId text)

    /// The id of `count` random bytes: `h-` and lowercase hex. Only `HandleTable` calls this.
    let internal ofRandom (bytes: byte array) : HandleId =
        HandleId("h-" + Convert.ToHexStringLower bytes)

/// The declared type of a handle's object, by name: a named type of the kernel's bindings (spec
/// §6.6 "values decode only into declared types"), e.g. `RunView`.
[<Struct; CustomEquality; NoComparison>]
type TypeRef =
    | TypeRef of string

    member this.Name =
        let (TypeRef name) = this
        name

    override this.Equals(other: obj) =
        match other with
        | :? TypeRef as t -> String.Equals(this.Name, t.Name, StringComparison.Ordinal)
        | _ -> false

    override this.GetHashCode() =
        StringComparer.Ordinal.GetHashCode this.Name

    override this.ToString() = this.Name

    interface IEquatable<TypeRef> with
        member this.Equals(other: TypeRef) =
            String.Equals(this.Name, other.Name, StringComparison.Ordinal)

/// A value crossing a process boundary (spec §6.6): between a kernel and the host, in results,
/// journals and events. Values stay where they were produced; large ones and live objects travel
/// as references with a bounded preview, so the model sees previews and passes values by name.
[<RequireQualifiedAccess>]
type Value =
    /// Small: copied as JSON.
    | Inline of Json
    /// Large: content-addressed in the environment's blob store, read lazily, cached by the host.
    | Blob of blob: BlobRef * preview: Preview
    /// A live object that stays on the host (spec §6.6); kernels hold it opaquely and pass it back.
    | Handle of handle: HandleId * typeRef: TypeRef * preview: Preview

    override this.ToString() =
        match this with
        | Inline json -> "inline " + Json.serialize json
        | Blob(blob, preview) -> $"blob {blob}: {preview.Text}"
        | Handle(handle, typeRef, preview) -> $"handle {handle} ({typeRef}): {preview.Text}"

/// Where a kernel keeps a value it can preview or encode.
[<RequireQualifiedAccess>]
type ValueRef =
    /// A top-level variable of the kernel's language.
    | Variable of name: string
    /// The completion value of a cell, kept by the kernel as `Out[cellId]` (or restored by `recall`).
    | Output of cellId: string

    override this.ToString() =
        match this with
        | Variable name -> "variable " + name
        | Output cellId -> "output of cell " + cellId

/// What the model sees of a kernel value: its type in the kernel's language and a bounded preview.
type ValueInfo =
    {
        /// The type, as the kernel's language names it (`string`, `Array`, `Match`).
        Type: string
        Preview: Preview
    }

    override this.ToString() = $"{this.Type}: {this.Preview.Text}"

module Value =
    /// The preview of a value: inline JSON is rendered within the limits; references carry theirs.
    let preview (limits: Limits) (value: Value) : Preview =
        match value with
        | Value.Inline json -> Preview.ofJson limits json
        | Value.Blob(_, preview) -> Preview.clamp limits preview
        | Value.Handle(_, typeRef, preview) ->
            Preview.clamp
                limits
                {
                    Text = typeRef.Name + "(" + preview.Text + ")"
                    Truncated = preview.Truncated
                }

    /// The JSON of an inline value; references are not JSON.
    let tryInline (value: Value) : Json option =
        match value with
        | Value.Inline json -> Some json
        | _ -> None

    /// Decodes a value into a declared type: only inline JSON decodes, with the type's codec, and
    /// the issues say what does not fit (spec §6.6, §18 "declared-type decoding"). References never
    /// decode into data: a blob is read and a handle resolved by the code that expects one.
    let decode (codec: Codec<'T>) (value: Value) : Validation<'T, Issue> =
        match value with
        | Value.Inline json -> codec.Decode json
        | Value.Blob(blob, _) ->
            Error
                [
                    Issue.create $"expected {Schema.typeName codec.Schema}, got a blob reference ({blob.Size} bytes)"
                ]
        | Value.Handle(_, typeRef, _) ->
            Error
                [
                    Issue.create $"expected {Schema.typeName codec.Schema}, got a handle to {typeRef.Name}"
                ]

/// Codecs of values and references.
[<RequireQualifiedAccess>]
module ValueCodecs =
    let handleId: Codec<HandleId> =
        Codec.string
        |> Codec.minLength 1
        |> Codec.maxLength HandleId.MaxLength
        |> Codec.pattern "^[!-~]+$"
        |> Codec.tryMap
            (fun text ->
                match HandleId.tryParse text with
                | Some id -> Ok id
                | None -> Error "expected 1 to 128 printable ASCII characters")
            (fun id -> id.Value)
        |> Codec.describe "An opaque handle id, issued by the host."

    let typeRef: Codec<TypeRef> =
        Codec.string
        |> Codec.nonEmpty
        |> Codec.map TypeRef (fun t -> t.Name)
        |> Codec.describe "The name of a declared type."

    /// `{"kind": "inline", "value": …}`, `{"kind": "blob", "blob": {…}, "preview": {…}}`,
    /// `{"kind": "handle", "handle": "h-…", "type": "RunView", "preview": {…}}`.
    let value: Codec<Value> =
        Codec.union "kind" {
            let! inlined =
                Codec.case1
                    "inline"
                    Value.Inline
                    (Field.required "value" (Codec.json |> Codec.describe "The value itself, as JSON."))

            and! blob =
                Codec.case2
                    "blob"
                    (fun blob preview -> Value.Blob(blob, preview))
                    (Field.required "blob" Codecs.blobRef)
                    (Field.required "preview" Fx.Contracts.Codecs.preview)

            and! handle =
                Codec.case3
                    "handle"
                    (fun handle typeRef preview -> Value.Handle(handle, typeRef, preview))
                    (Field.required "handle" handleId)
                    (Field.required "type" typeRef)
                    (Field.required "preview" Fx.Contracts.Codecs.preview)

            return
                fun value ->
                    match value with
                    | Value.Inline json -> inlined json
                    | Value.Blob(b, preview) -> blob b preview
                    | Value.Handle(h, typeRef, preview) -> handle h typeRef preview
        }
        |> Codec.describeCase "inline" "Small: copied."
        |> Codec.describeCase "blob" "Large: content-addressed in the environment, read lazily."
        |> Codec.describeCase "handle" "A live object that stays on the host; pass it back as it is."
        |> Codec.withPreview Value.preview
        |> Codec.describe "A value crossing a process boundary: copied when small, referenced otherwise."
        |> Codec.named "Value"

    let valueRef: Codec<ValueRef> =
        Codec.union "kind" {
            let! variable =
                Codec.case1 "variable" ValueRef.Variable (Field.required "name" (Codec.string |> Codec.nonEmpty))

            and! output =
                Codec.case1 "output" ValueRef.Output (Field.required "cellId" (Codec.string |> Codec.nonEmpty))

            return
                fun value ->
                    match value with
                    | ValueRef.Variable name -> variable name
                    | ValueRef.Output cellId -> output cellId
        }
        |> Codec.describeCase "variable" "A top-level variable."
        |> Codec.describeCase "output" "The completion value of a cell (`Out[cellId]`)."
        |> Codec.describe "Where the kernel keeps a value."
        |> Codec.named "ValueRef"

    let valueInfo: Codec<ValueInfo> =
        Codec.record {
            let! typeName = Codec.field "type" (fun (v: ValueInfo) -> v.Type) Codec.string
            and! preview = Codec.field "preview" (fun v -> v.Preview) Fx.Contracts.Codecs.preview
            return { Type = typeName; Preview = preview }
        }
        |> Codec.describe "What the model sees of a kernel value: its type and a bounded preview."
        |> Codec.named "ValueInfo"
