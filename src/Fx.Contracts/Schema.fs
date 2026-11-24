namespace Fx.Contracts

open System
open System.Collections.Generic
open System.Text

/// A bound on a number: inclusive (minimum, maximum) or exclusive (exclusiveMinimum, exclusiveMaximum).
[<RequireQualifiedAccess; NoComparison>]
type Bound =
    | Inclusive of JsonNumber
    | Exclusive of JsonNumber

    override this.ToString() =
        match this with
        | Inclusive n -> $"inclusive {n}"
        | Exclusive n -> $"exclusive {n}"

/// Constraints on integers and numbers.
[<NoComparison>]
type NumberFacets =
    {
        Minimum: Bound option
        Maximum: Bound option
    }

    override this.ToString() =
        let show label (bound: Bound option) =
            bound |> Option.map (fun b -> $"{label} {b}") |> Option.defaultValue ""

        let minimum = show "minimum" this.Minimum
        let maximum = show "maximum" this.Maximum
        $"NumberFacets({minimum} {maximum})"

/// Constraints on strings. Lengths count Unicode code points, as in JSON Schema. `Pattern` is an
/// ECMAScript regular expression that must match somewhere in the string (it is not anchored).
type StringFacets =
    {
        MinLength: int option
        MaxLength: int option
        Pattern: string option
        /// A JSON Schema format name, e.g. `date-time` or `uri`. Documentation only: not validated.
        Format: string option
    }

    override this.ToString() =
        let show label (value: string option) =
            value |> Option.map (fun v -> $" {label}={v}") |> Option.defaultValue ""

        "StringFacets("
        + show "minLength" (this.MinLength |> Option.map string)
        + show "maxLength" (this.MaxLength |> Option.map string)
        + show "pattern" this.Pattern
        + show "format" this.Format
        + " )"

/// What a decoder does with object members that are not fields of the type.
[<RequireQualifiedAccess>]
type UnknownFields =
    /// Skip them: readers stay compatible with writers that add fields.
    | Ignore
    /// Report each one as an issue.
    | Reject

    override this.ToString() =
        match this with
        | Ignore -> "ignore"
        | Reject -> "reject"

/// Whether an object field must be present.
[<RequireQualifiedAccess; NoComparison>]
type Presence =
    /// Must be present.
    | Required
    /// May be absent (or `null`); absent means "no value". Encoders omit the field.
    | Optional
    /// May be absent (or `null`), meaning this value. Encoders always write the field.
    | Defaulted of Json

    override this.ToString() =
        match this with
        | Required -> "required"
        | Optional -> "optional"
        | Defaulted value -> $"default {value}"

/// The shape of JSON a codec reads and writes. Rich enough to emit JSON Schema for model tool
/// definitions (`Schema.toJsonSchema`) and to generate TypeScript types: named types and references,
/// tagged unions, string enums, literals, optional versus nullable, descriptions and constraints.
[<NoComparison>]
type Schema =
    {
        Kind: SchemaKind
        /// Documentation for models and generated code.
        Description: string option
    }

    override this.ToString() = SchemaText.Render(this, true)

and [<RequireQualifiedAccess; NoComparison>] SchemaKind =
    /// Any JSON value.
    | Any
    | Null
    | Boolean
    /// A number with an integral value.
    | Integer of NumberFacets
    | Number of NumberFacets
    | String of StringFacets
    /// Exactly this value.
    | Const of Json
    /// One of these strings.
    | Enum of string list
    | Array of ArraySchema
    /// An array with exactly these items, in order.
    | Tuple of Schema list
    /// An object with any keys and values of one schema.
    | Map of Schema
    /// An object with known fields.
    | Object of ObjectSchema
    /// Objects discriminated by a string tag field, one shape per tag.
    | Union of UnionSchema
    /// A value matching at least one of the schemas.
    | AnyOf of Schema list
    /// The schema or `null`.
    | Nullable of Schema
    /// A reference to a named type defined elsewhere in the same schema (recursion).
    | Ref of string
    /// A named type: a definition that generators emit once and refer to by name.
    | Named of NamedSchema

    override this.ToString() =
        SchemaText.Render({ Kind = this; Description = None }, true)

and [<NoComparison>] ArraySchema =
    {
        Items: Schema
        MinItems: int option
        MaxItems: int option
    }

    override this.ToString() =
        SchemaText.Render(
            {
                Kind = SchemaKind.Array this
                Description = None
            },
            true
        )

and [<NoComparison>] ObjectSchema =
    {
        /// Fields in declaration order; the encoders write them in this order.
        Fields: FieldSchema list
        UnknownFields: UnknownFields
    }

    override this.ToString() =
        SchemaText.Render(
            {
                Kind = SchemaKind.Object this
                Description = None
            },
            true
        )

and [<NoComparison>] FieldSchema =
    {
        Name: string
        Schema: Schema
        Presence: Presence
    }

    override this.ToString() =
        let optional = if this.Presence = Presence.Required then "" else "?"
        $"{this.Name}{optional}: {this.Schema}"

and [<NoComparison>] UnionSchema =
    {
        /// The tag field, e.g. `kind`.
        Discriminator: string
        Cases: UnionCase list
    }

    override this.ToString() =
        SchemaText.Render(
            {
                Kind = SchemaKind.Union this
                Description = None
            },
            true
        )

and [<NoComparison>] UnionCase =
    {
        /// The tag value that selects this case.
        Tag: string
        Description: string option
        /// The fields next to the tag field.
        Payload: ObjectSchema
    }

    override this.ToString() =
        SchemaText.Render(
            {
                Kind =
                    SchemaKind.Union
                        {
                            Discriminator = "tag"
                            Cases = [ this ]
                        }
                Description = None
            },
            true
        )

and [<NoComparison>] NamedSchema =
    {
        /// An identifier (`[A-Za-z_][A-Za-z0-9_]*`), usable as a TypeScript type name.
        Name: string
        Body: Schema
    }

    override this.ToString() = this.Name

/// Renders schemas as TypeScript-like type expressions, e.g. `{ path: string; line?: number }[]`.
and [<AbstractClass; Sealed>] internal SchemaText =

    static member private Fields(sb: StringBuilder, fields: FieldSchema list, prefix: string) =
        sb.Append "{ " |> ignore
        sb.Append prefix |> ignore

        fields
        |> List.iteri (fun i field ->
            if i > 0 || prefix.Length > 0 then
                sb.Append "; " |> ignore

            sb.Append(field.Name) |> ignore

            if field.Presence <> Presence.Required then
                sb.Append '?' |> ignore

            sb.Append(": ").Append(SchemaText.Render(field.Schema, true)) |> ignore)

        sb.Append " }" |> ignore

    /// `deep` renders object shapes and shows `integer`; otherwise objects are just `object` and
    /// integers are `number`, as in TypeScript.
    static member Render(schema: Schema, deep: bool) : string =
        let render s = SchemaText.Render(s, deep)

        let atom (s: Schema) =
            let text = render s

            match s.Kind with
            | SchemaKind.Nullable _
            | SchemaKind.AnyOf _ -> "(" + text + ")"
            | SchemaKind.Enum values when values.Length > 1 -> "(" + text + ")"
            | SchemaKind.Union u when deep && u.Cases.Length > 1 -> "(" + text + ")"
            | _ -> text

        match schema.Kind with
        | SchemaKind.Any -> "any"
        | SchemaKind.Null -> "null"
        | SchemaKind.Boolean -> "boolean"
        | SchemaKind.Integer _ -> if deep then "integer" else "number"
        | SchemaKind.Number _ -> "number"
        | SchemaKind.String _ -> "string"
        | SchemaKind.Const value -> string value
        | SchemaKind.Enum values -> String.Join(" | ", values |> List.map JsonWriter.Quote)
        | SchemaKind.Array array -> atom array.Items + "[]"
        | SchemaKind.Tuple items -> "[" + String.Join(", ", items |> List.map render) + "]"
        | SchemaKind.Map values when deep -> "{ [key: string]: " + render values + " }"
        | SchemaKind.Object o when deep ->
            let sb = StringBuilder()
            SchemaText.Fields(sb, o.Fields, "")
            sb.ToString()
        | SchemaKind.Union u when deep ->
            let cases =
                u.Cases
                |> List.map (fun case ->
                    let sb = StringBuilder()
                    SchemaText.Fields(sb, case.Payload.Fields, $"{u.Discriminator}: {JsonWriter.Quote case.Tag}")
                    sb.ToString())

            String.Join(" | ", cases)
        | SchemaKind.Map _
        | SchemaKind.Object _
        | SchemaKind.Union _ -> "object"
        | SchemaKind.AnyOf schemas -> String.Join(" | ", schemas |> List.map atom)
        | SchemaKind.Nullable inner -> atom inner + " | null"
        | SchemaKind.Ref name -> name
        | SchemaKind.Named named -> named.Name

/// Building and inspecting schemas, and emitting JSON Schema.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Schema =

    let ofKind (kind: SchemaKind) : Schema = { Kind = kind; Description = None }

    /// Sets the description.
    let describe (description: string) (schema: Schema) : Schema =
        { schema with
            Description = Some description
        }

    let noNumberFacets: NumberFacets = { Minimum = None; Maximum = None }

    let noStringFacets: StringFacets =
        {
            MinLength = None
            MaxLength = None
            Pattern = None
            Format = None
        }

    /// A short TypeScript-like name for messages and previews: the name of a named type, `string`,
    /// `number`, `Match[]`, `object`, `string | null`, ….
    let typeName (schema: Schema) : string = SchemaText.Render(schema, false)

    /// A TypeScript-like type expression, e.g. `{ path: string; line?: number }`. Named types appear
    /// by name.
    let render (schema: Schema) : string = SchemaText.Render(schema, true)

    let private isIdentifier (name: string) =
        let isStart (c: char) =
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_'

        name.Length > 0
        && isStart name[0]
        && name |> Seq.forall (fun c -> isStart c || (c >= '0' && c <= '9'))

    /// Throws unless `name` can name a type (`[A-Za-z_][A-Za-z0-9_]*`).
    let internal checkName (name: string) =
        if not (isIdentifier name) then
            invalidArg (nameof name) $"Type name {Json.quote name} must match [A-Za-z_][A-Za-z0-9_]*."

    /// Calls `visit` on the schema and every schema nested in it (not following references).
    let rec private iter (visit: Schema -> unit) (schema: Schema) : unit =
        visit schema

        let fields (o: ObjectSchema) =
            for field in o.Fields do
                iter visit field.Schema

        match schema.Kind with
        | SchemaKind.Array a -> iter visit a.Items
        | SchemaKind.Tuple items
        | SchemaKind.AnyOf items -> List.iter (iter visit) items
        | SchemaKind.Map inner
        | SchemaKind.Nullable inner -> iter visit inner
        | SchemaKind.Object o -> fields o
        | SchemaKind.Union u ->
            for case in u.Cases do
                fields case.Payload
        | SchemaKind.Named n -> iter visit n.Body
        | SchemaKind.Any
        | SchemaKind.Null
        | SchemaKind.Boolean
        | SchemaKind.Integer _
        | SchemaKind.Number _
        | SchemaKind.String _
        | SchemaKind.Const _
        | SchemaKind.Enum _
        | SchemaKind.Ref _ -> ()

    /// Every named type used in the schema, including the schema itself if it is named, sorted by name.
    /// Throws if two different types share a name.
    let definitions (schema: Schema) : NamedSchema list =
        let found = Dictionary<string, NamedSchema>(StringComparer.Ordinal)

        schema
        |> iter (fun s ->
            match s.Kind with
            | SchemaKind.Named n ->
                match found.TryGetValue n.Name with
                | true, existing when existing.Body <> n.Body ->
                    invalidArg (nameof schema) $"Two different types are named {Json.quote n.Name}."
                | true, _ -> ()
                | false, _ -> found[n.Name] <- n
            | _ -> ())

        found.Values
        |> List.ofSeq
        |> List.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))

    /// The names of every `Ref` in the schema.
    let private references (schema: Schema) : HashSet<string> =
        let names = HashSet<string>(StringComparer.Ordinal)

        schema
        |> iter (fun s ->
            match s.Kind with
            | SchemaKind.Ref name -> names.Add name |> ignore
            | _ -> ())

        names

    let private str (s: string) = Json.String s

    /// The root type refers to itself as `#`; names are identifiers, so pointers need no escaping.
    let private refTo (root: string option) (name: string) =
        if root = Some name then
            str "#"
        else
            str ("#/$defs/" + name)

    let private bounds (facets: NumberFacets) : (string * Json) list =
        [
            match facets.Minimum with
            | Some(Bound.Inclusive n) -> "minimum", Json.Number n
            | Some(Bound.Exclusive n) -> "exclusiveMinimum", Json.Number n
            | None -> ()
            match facets.Maximum with
            | Some(Bound.Inclusive n) -> "maximum", Json.Number n
            | Some(Bound.Exclusive n) -> "exclusiveMaximum", Json.Number n
            | None -> ()
        ]

    let rec private emit (root: string option) (schema: Schema) : (string * Json) list =
        let body =
            match schema.Kind with
            | SchemaKind.Any -> []
            | SchemaKind.Null -> [ "type", str "null" ]
            | SchemaKind.Boolean -> [ "type", str "boolean" ]
            | SchemaKind.Integer facets -> ("type", str "integer") :: bounds facets
            | SchemaKind.Number facets -> ("type", str "number") :: bounds facets
            | SchemaKind.String facets ->
                [
                    "type", str "string"
                    match facets.MinLength with
                    | Some n -> "minLength", Json.int n
                    | None -> ()
                    match facets.MaxLength with
                    | Some n -> "maxLength", Json.int n
                    | None -> ()
                    match facets.Pattern with
                    | Some p -> "pattern", str p
                    | None -> ()
                    match facets.Format with
                    | Some f -> "format", str f
                    | None -> ()
                ]
            | SchemaKind.Const value ->
                [
                    match value with
                    | Json.Null -> "type", str "null"
                    | Json.Bool _ -> "type", str "boolean"
                    | Json.String _ -> "type", str "string"
                    | Json.Number n when JsonNumber.isInteger n -> "type", str "integer"
                    | Json.Number _ -> "type", str "number"
                    | Json.Array _ -> "type", str "array"
                    | Json.Object _ -> "type", str "object"
                    "const", value
                ]
            | SchemaKind.Enum values -> [ "type", str "string"; "enum", Json.Array(List.map str values) ]
            | SchemaKind.Array array ->
                [
                    "type", str "array"
                    "items", Json.Object(emit root array.Items)
                    match array.MinItems with
                    | Some n -> "minItems", Json.int n
                    | None -> ()
                    match array.MaxItems with
                    | Some n -> "maxItems", Json.int n
                    | None -> ()
                ]
            | SchemaKind.Tuple items ->
                [
                    "type", str "array"
                    "prefixItems", Json.Array(items |> List.map (emit root >> Json.Object))
                    "items", Json.Bool false
                    "minItems", Json.int items.Length
                    "maxItems", Json.int items.Length
                ]
            | SchemaKind.Map values -> [ "type", str "object"; "additionalProperties", Json.Object(emit root values) ]
            | SchemaKind.Object o -> emitObject root [] o
            | SchemaKind.Union u ->
                let case (c: UnionCase) =
                    let tag =
                        {
                            Name = u.Discriminator
                            Schema = ofKind (SchemaKind.Const(str c.Tag))
                            Presence = Presence.Required
                        }

                    let members = emitObject root [ tag ] c.Payload

                    match c.Description with
                    | Some d -> members @ [ "description", str d ]
                    | None -> members

                match u.Cases with
                | [ single ] -> case single
                | cases -> [ "anyOf", Json.Array(cases |> List.map (case >> Json.Object)) ]
            | SchemaKind.AnyOf schemas -> [ "anyOf", Json.Array(schemas |> List.map (emit root >> Json.Object)) ]
            | SchemaKind.Nullable inner ->
                [
                    "anyOf", Json.Array [ Json.Object(emit root inner); Json.Object [ "type", str "null" ] ]
                ]
            | SchemaKind.Ref name -> [ "$ref", refTo root name ]
            | SchemaKind.Named n -> [ "$ref", refTo root n.Name ]

        match schema.Description with
        | Some d -> body @ [ "description", str d ]
        | None -> body

    and private emitObject (root: string option) (leading: FieldSchema list) (o: ObjectSchema) : (string * Json) list =
        let fields = leading @ o.Fields

        let property (field: FieldSchema) =
            let members = emit root field.Schema

            match field.Presence with
            | Presence.Defaulted value -> field.Name, Json.Object(members @ [ "default", value ])
            | Presence.Required
            | Presence.Optional -> field.Name, Json.Object members

        let required =
            fields
            |> List.filter (fun f -> f.Presence = Presence.Required)
            |> List.map (fun f -> str f.Name)

        [
            "type", str "object"
            "properties", Json.Object(List.map property fields)
            if not required.IsEmpty then
                "required", Json.Array required
            if o.UnknownFields = UnknownFields.Reject then
                "additionalProperties", Json.Bool false
        ]

    /// JSON Schema (draft 2020-12) for the schema, as used in model tool definitions.
    ///
    /// - A named root is inlined at the top level and refers to itself as `"$ref": "#"`; other named
    ///   types become `$defs` entries referred to with `"$ref": "#/$defs/Name"`.
    /// - Unions become `anyOf` over objects whose tag field is a `const`; nullable values become
    ///   `anyOf` with `{"type": "null"}`; optional fields are left out of `required`.
    /// - Objects that reject unknown fields get `"additionalProperties": false`.
    ///
    /// Throws if two different types share a name or a reference has no definition.
    let toJsonSchema (schema: Schema) : Json =
        let body, rootName =
            match schema.Kind with
            | SchemaKind.Named n ->
                { n.Body with
                    Description = Option.orElse n.Body.Description schema.Description
                },
                Some n.Name
            | _ -> schema, None

        let named = definitions schema |> List.filter (fun d -> Some d.Name <> rootName)

        let defined =
            HashSet<string>(named |> List.map (fun d -> d.Name), StringComparer.Ordinal)

        rootName |> Option.iter (defined.Add >> ignore)

        for name in references schema do
            if not (defined.Contains name) then
                invalidArg (nameof schema) $"The schema refers to {Json.quote name} but does not define it."

        let defs =
            if named.IsEmpty then
                []
            else
                [
                    "$defs", Json.Object(named |> List.map (fun d -> d.Name, Json.Object(emit rootName d.Body)))
                ]

        Json.Object(emit rootName body @ defs)
