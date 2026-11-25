namespace Fx.Contracts

/// Codecs for the contract types themselves, so that issues, limits and previews can cross the wire
/// (protocol errors, variant values, `Value` previews).
[<RequireQualifiedAccess>]
module Codecs =

    /// A key as a string, an index as a non-negative integer.
    let pathSegment: Codec<PathSegment> =
        let schema =
            Schema.ofKind (
                SchemaKind.AnyOf
                    [
                        Codec.string.Schema
                        Schema.ofKind (
                            SchemaKind.Integer
                                {
                                    Minimum = Some(Bound.Inclusive(JsonNumber.ofInt 0))
                                    Maximum = None
                                }
                        )
                    ]
            )

        let encode (segment: PathSegment) =
            match segment with
            | PathSegment.Key key -> Json.String key
            | PathSegment.Index index -> Json.int index

        let decode (json: Json) =
            match json with
            | Json.String key -> Ok(PathSegment.Key key)
            | Json.Number n ->
                match JsonNumber.tryToInt n with
                | Some index when index >= 0 -> Ok(PathSegment.Index index)
                | _ -> Error [ Issue.expected "string or non-negative integer" json ]
            | other -> Error [ Issue.expected "string or non-negative integer" other ]

        Codec.create schema encode decode

    /// `{"path": ["items", 2, "name"], "message": "expected string, got number 42"}`.
    let issue: Codec<Issue> =
        Codec.record {
            let! path = Codec.field "path" (fun (i: Issue) -> i.Path) (Codec.list pathSegment)
            and! message = Codec.field "message" (fun i -> i.Message) Codec.string
            return { Path = path; Message = message }
        }
        |> Codec.named "Issue"
        |> Codec.describe "A problem in a value: where it is (keys and indexes from the root) and what is wrong."

    let private count (name: string) (get: Limits -> int) =
        Codec.field name get (Codec.int |> Codec.atLeast 0)

    /// `{"maxChars": 1000, "maxItems": 10, "maxDepth": 3, "maxStringChars": 120}`.
    let limits: Codec<Limits> =
        Codec.record {
            let! maxChars = count "maxChars" (fun l -> l.MaxChars)
            and! maxItems = count "maxItems" (fun l -> l.MaxItems)
            and! maxDepth = count "maxDepth" (fun l -> l.MaxDepth)
            and! maxStringChars = count "maxStringChars" (fun l -> l.MaxStringChars)

            return
                {
                    MaxChars = maxChars
                    MaxItems = maxItems
                    MaxDepth = maxDepth
                    MaxStringChars = maxStringChars
                }
        }
        |> Codec.named "Limits"
        |> Codec.describe "Bounds for a preview."

    /// `{"text": "[1, 2, …+8]", "truncated": true}`.
    let preview: Codec<Preview> =
        Codec.record {
            let! text = Codec.field "text" (fun (p: Preview) -> p.Text) Codec.string
            and! truncated = Codec.field "truncated" (fun p -> p.Truncated) Codec.bool
            return { Text = text; Truncated = truncated }
        }
        |> Codec.named "Preview"
        |> Codec.describe "A bounded rendering of a value; truncated when something was left out."

    // ---- Schemas ---------------------------------------------------------------------------------

    /// `{"kind": "inclusive", "value": 0}`.
    let schemaBound: Codec<Bound> =
        Codec.union "kind" {
            let! inclusive =
                Codec.case1 "inclusive" Bound.Inclusive (Field.required "value" Codec.number)

            and! exclusive =
                Codec.case1 "exclusive" Bound.Exclusive (Field.required "value" Codec.number)

            return
                fun value ->
                    match value with
                    | Bound.Inclusive n -> inclusive n
                    | Bound.Exclusive n -> exclusive n
        }
        |> Codec.describe "A bound on a number: inclusive (minimum, maximum) or exclusive."
        |> Codec.named "SchemaBound"

    /// `{"kind": "required"}`, `{"kind": "optional"}` or `{"kind": "defaulted", "value": …}`.
    let schemaPresence: Codec<Presence> =
        Codec.union "kind" {
            let! required = Codec.case0 "required" Presence.Required
            and! optional = Codec.case0 "optional" Presence.Optional

            and! defaulted =
                Codec.case1 "defaulted" Presence.Defaulted (Field.required "value" Codec.json)

            return
                fun value ->
                    match value with
                    | Presence.Required -> required
                    | Presence.Optional -> optional
                    | Presence.Defaulted json -> defaulted json
        }
        |> Codec.describe
            "Whether a field must be present: required; optional (absent means no value); or defaulted (absent means this value, and it is always written)."
        |> Codec.named "SchemaPresence"

    let private unknownFields: Codec<UnknownFields> =
        Codec.enum [ UnknownFields.Ignore, "ignore"; UnknownFields.Reject, "reject" ]
        |> Codec.describe "What a decoder does with members that are not fields: ignore them, or reject each one."

    let private size = Codec.int |> Codec.atLeast 0

    let private typeName =
        Codec.string
        |> Codec.pattern "^[A-Za-z_][A-Za-z0-9_]*$"
        |> Codec.describe "A type name: an identifier."

    let private numberFacets (schema: Schema) : NumberFacets =
        match schema.Kind with
        | SchemaKind.Integer facets
        | SchemaKind.Number facets -> facets
        | _ -> Schema.noNumberFacets

    let private stringFacets (schema: Schema) : StringFacets =
        match schema.Kind with
        | SchemaKind.String facets -> facets
        | _ -> Schema.noStringFacets

    let private arraySchema (schema: Schema) : ArraySchema =
        match schema.Kind with
        | SchemaKind.Array array -> array
        | _ ->
            {
                Items = schema
                MinItems = None
                MaxItems = None
            }

    let private objectSchema (schema: Schema) : ObjectSchema =
        match schema.Kind with
        | SchemaKind.Object o -> o
        | _ ->
            {
                Fields = []
                UnknownFields = UnknownFields.Ignore
            }

    let private unionSchema (schema: Schema) : UnionSchema =
        match schema.Kind with
        | SchemaKind.Union u -> u
        | _ -> { Discriminator = ""; Cases = [] }

    let private namedSchema (schema: Schema) : NamedSchema =
        match schema.Kind with
        | SchemaKind.Named named -> named
        | _ -> { Name = ""; Body = schema }

    let private schemas (schema: Schema) : Schema list =
        match schema.Kind with
        | SchemaKind.Tuple items
        | SchemaKind.AnyOf items -> items
        | _ -> []

    let private inner (schema: Schema) : Schema =
        match schema.Kind with
        | SchemaKind.Map inner
        | SchemaKind.Nullable inner -> inner
        | _ -> schema

    let private refName (schema: Schema) : string =
        match schema.Kind with
        | SchemaKind.Ref name -> name
        | _ -> ""

    let private constValue (schema: Schema) : Json =
        match schema.Kind with
        | SchemaKind.Const value -> value
        | _ -> Json.Null

    let private enumValues (schema: Schema) : string list =
        match schema.Kind with
        | SchemaKind.Enum values -> values
        | _ -> []

    let private describedBy =
        Codec.optionalField "description" (fun (s: Schema) -> s.Description) Codec.string

    let private plain (kind: SchemaKind) : Codec<Schema> =
        Codec.record {
            let! description = describedBy

            return
                {
                    Kind = kind
                    Description = description
                }
        }

    let private numeric (integer: bool) : Codec<Schema> =
        Codec.record {
            let! minimum =
                Codec.optionalField "minimum" (fun s -> (numberFacets s).Minimum) schemaBound

            and! maximum =
                Codec.optionalField "maximum" (fun s -> (numberFacets s).Maximum) schemaBound

            and! description = describedBy

            return
                {
                    Kind =
                        if integer then
                            SchemaKind.Integer { Minimum = minimum; Maximum = maximum }
                        else
                            SchemaKind.Number { Minimum = minimum; Maximum = maximum }
                    Description = description
                }
        }

    /// The wire form of a `Schema`, so that schemas cross processes and languages unchanged: the
    /// TypeScript SDK's codecs build values of this shape (its type is generated from this codec), and
    /// tests compare them with the F# codecs' schemas. Tagged by `kind`, one case per `SchemaKind`, each
    /// with its facets and an optional `description`, e.g.
    /// `{"kind": "array", "items": {"kind": "string", "minLength": 1}, "maxItems": 3}`. Unknown members
    /// are issues.
    let schema: Codec<Schema> =
        Codec.recursive "Schema" (fun self ->
            let field: Codec<FieldSchema> =
                Codec.record {
                    let! name = Codec.field "name" (fun (f: FieldSchema) -> f.Name) Codec.string
                    and! schema = Codec.field "schema" (fun f -> f.Schema) self
                    and! presence = Codec.field "presence" (fun f -> f.Presence) schemaPresence

                    return
                        {
                            Name = name
                            Schema = schema
                            Presence = presence
                        }
                }
                |> Codec.strict
                |> Codec.describe "A field of an object or of a union case."
                |> Codec.named "SchemaField"

            let fields =
                Codec.field "fields" (fun (s: Schema) -> (objectSchema s).Fields) (Codec.list field)

            let unknown =
                Codec.defaultField
                    "unknownFields"
                    (fun (s: Schema) -> (objectSchema s).UnknownFields)
                    UnknownFields.Ignore
                    unknownFields

            let case: Codec<UnionCase> =
                Codec.record {
                    let! tag = Codec.field "tag" (fun (c: UnionCase) -> c.Tag) Codec.string

                    and! description =
                        Codec.optionalField "description" (fun c -> c.Description) Codec.string

                    and! fields = Codec.field "fields" (fun c -> c.Payload.Fields) (Codec.list field)

                    and! unknown =
                        Codec.defaultField
                            "unknownFields"
                            (fun c -> c.Payload.UnknownFields)
                            UnknownFields.Ignore
                            unknownFields

                    return
                        {
                            Tag = tag
                            Description = description
                            Payload =
                                {
                                    Fields = fields
                                    UnknownFields = unknown
                                }
                        }
                }
                |> Codec.strict
                |> Codec.describe "A case of a union: its tag and the fields next to the tag."
                |> Codec.named "SchemaCase"

            let text =
                Codec.record {
                    let! minLength =
                        Codec.optionalField "minLength" (fun s -> (stringFacets s).MinLength) size

                    and! maxLength =
                        Codec.optionalField "maxLength" (fun s -> (stringFacets s).MaxLength) size

                    and! pattern =
                        Codec.optionalField "pattern" (fun s -> (stringFacets s).Pattern) Codec.string

                    and! format =
                        Codec.optionalField "format" (fun s -> (stringFacets s).Format) Codec.string

                    and! description = describedBy

                    return
                        {
                            Kind =
                                SchemaKind.String
                                    {
                                        MinLength = minLength
                                        MaxLength = maxLength
                                        Pattern = pattern
                                        Format = format
                                    }
                            Description = description
                        }
                }

            let constant =
                Codec.record {
                    let! value = Codec.field "value" constValue Codec.json
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.Const value
                            Description = description
                        }
                }

            let enumeration =
                Codec.record {
                    let! values = Codec.field "values" enumValues (Codec.list Codec.string)
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.Enum values
                            Description = description
                        }
                }

            let array =
                Codec.record {
                    let! items = Codec.field "items" (fun s -> (arraySchema s).Items) self

                    and! minItems =
                        Codec.optionalField "minItems" (fun s -> (arraySchema s).MinItems) size

                    and! maxItems =
                        Codec.optionalField "maxItems" (fun s -> (arraySchema s).MaxItems) size

                    and! description = describedBy

                    return
                        {
                            Kind =
                                SchemaKind.Array
                                    {
                                        Items = items
                                        MinItems = minItems
                                        MaxItems = maxItems
                                    }
                            Description = description
                        }
                }

            let tuple =
                Codec.record {
                    let! items = Codec.field "items" schemas (Codec.list self)
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.Tuple items
                            Description = description
                        }
                }

            let map =
                Codec.record {
                    let! values = Codec.field "values" inner self
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.Map values
                            Description = description
                        }
                }

            let record =
                Codec.record {
                    let! fields = fields
                    and! unknown = unknown
                    and! description = describedBy

                    return
                        {
                            Kind =
                                SchemaKind.Object
                                    {
                                        Fields = fields
                                        UnknownFields = unknown
                                    }
                            Description = description
                        }
                }

            let union =
                Codec.record {
                    let! discriminator =
                        Codec.field "discriminator" (fun s -> (unionSchema s).Discriminator) Codec.string

                    and! cases = Codec.field "cases" (fun s -> (unionSchema s).Cases) (Codec.list case)
                    and! description = describedBy

                    return
                        {
                            Kind =
                                SchemaKind.Union
                                    {
                                        Discriminator = discriminator
                                        Cases = cases
                                    }
                            Description = description
                        }
                }

            let anyOf =
                Codec.record {
                    let! items = Codec.field "schemas" schemas (Codec.list self)
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.AnyOf items
                            Description = description
                        }
                }

            let nullable =
                Codec.record {
                    let! value = Codec.field "schema" inner self
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.Nullable value
                            Description = description
                        }
                }

            let reference =
                Codec.record {
                    let! name = Codec.field "name" refName typeName
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.Ref name
                            Description = description
                        }
                }

            let named =
                Codec.record {
                    let! name = Codec.field "name" (fun s -> (namedSchema s).Name) typeName
                    and! body = Codec.field "body" (fun s -> (namedSchema s).Body) self
                    and! description = describedBy

                    return
                        {
                            Kind = SchemaKind.Named { Name = name; Body = body }
                            Description = description
                        }
                }

            Codec.union "kind" {
                let! anyCase = Codec.caseOf "any" id (plain SchemaKind.Any)
                and! nullCase = Codec.caseOf "null" id (plain SchemaKind.Null)
                and! booleanCase = Codec.caseOf "boolean" id (plain SchemaKind.Boolean)
                and! integerCase = Codec.caseOf "integer" id (numeric true)
                and! numberCase = Codec.caseOf "number" id (numeric false)
                and! stringCase = Codec.caseOf "string" id text
                and! constCase = Codec.caseOf "const" id constant
                and! enumCase = Codec.caseOf "enum" id enumeration
                and! arrayCase = Codec.caseOf "array" id array
                and! tupleCase = Codec.caseOf "tuple" id tuple
                and! mapCase = Codec.caseOf "map" id map
                and! objectCase = Codec.caseOf "object" id record
                and! unionCase = Codec.caseOf "union" id union
                and! anyOfCase = Codec.caseOf "anyOf" id anyOf
                and! nullableCase = Codec.caseOf "nullable" id nullable
                and! refCase = Codec.caseOf "ref" id reference
                and! namedCase = Codec.caseOf "named" id named

                return
                    fun (value: Schema) ->
                        match value.Kind with
                        | SchemaKind.Any -> anyCase value
                        | SchemaKind.Null -> nullCase value
                        | SchemaKind.Boolean -> booleanCase value
                        | SchemaKind.Integer _ -> integerCase value
                        | SchemaKind.Number _ -> numberCase value
                        | SchemaKind.String _ -> stringCase value
                        | SchemaKind.Const _ -> constCase value
                        | SchemaKind.Enum _ -> enumCase value
                        | SchemaKind.Array _ -> arrayCase value
                        | SchemaKind.Tuple _ -> tupleCase value
                        | SchemaKind.Map _ -> mapCase value
                        | SchemaKind.Object _ -> objectCase value
                        | SchemaKind.Union _ -> unionCase value
                        | SchemaKind.AnyOf _ -> anyOfCase value
                        | SchemaKind.Nullable _ -> nullableCase value
                        | SchemaKind.Ref _ -> refCase value
                        | SchemaKind.Named _ -> namedCase value
            }
            |> Codec.strict
            |> Codec.describe
                "The JSON shape of a value (a codec's schema), tagged by kind; every node may carry a description.")
