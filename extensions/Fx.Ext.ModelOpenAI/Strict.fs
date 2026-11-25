/// OpenAI's strict mode (Structured Outputs) for tool parameters and output formats: it takes a
/// subset of JSON Schema and two rules of its own. `Strict.tryRewrite` rewrites a schema into that
/// dialect where the meaning survives, and gives up where it would not (the wire then sends the schema
/// as given with `strict: false`).
///
/// The rewrite:
///
/// - every object is closed (`additionalProperties: false`); an object that allows other properties,
///   or one without `properties`, gives up;
/// - every property is required, and those that were optional become nullable (`anyOf` with
///   `{"type": "null"}`): the model sends `null` for a property it leaves out, and our codecs read
///   `null` as absent (`Field.optional`, `Field.withDefault`). `default` is left out for the same
///   reason;
/// - only the keywords of the subset may appear (`type`, `description`, `title`, `enum`, `const`,
///   `anyOf`, `$ref`, `$defs`, `definitions`; `pattern` and the supported `format`s for strings;
///   `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf` for numbers; `items`,
///   `minItems`, `maxItems` for arrays; `properties`, `required`, `additionalProperties`). Anything
///   else (`minLength`, `prefixItems`, `allOf`, `oneOf`, `not`, `if`, `patternProperties`, …), a
///   schema without a type, or a root that is not an object with properties gives up.
module Fx.Ext.ModelOpenAI.Strict

open Fx.Contracts

/// String formats strict mode supports.
let formats =
    set
        [
            "date-time"
            "time"
            "date"
            "duration"
            "email"
            "hostname"
            "ipv4"
            "ipv6"
            "uuid"
        ]

/// The keywords strict mode takes (`default` is taken here only to be left out).
let keywords =
    set
        [
            "type"
            "description"
            "title"
            "enum"
            "const"
            "anyOf"
            "$ref"
            "$defs"
            "definitions"
            "default"
            "pattern"
            "format"
            "minimum"
            "maximum"
            "exclusiveMinimum"
            "exclusiveMaximum"
            "multipleOf"
            "items"
            "minItems"
            "maxItems"
            "properties"
            "required"
            "additionalProperties"
        ]

/// Each item's value, in order; the first error stops it.
let private traverse (f: 'T -> Result<'U, string>) (items: 'T list) : Result<'U list, string> =
    let values = ResizeArray<'U>()
    let mutable error = None

    for item in items do
        if error.IsNone then
            match f item with
            | Ok value -> values.Add value
            | Error e -> error <- Some e

    match error with
    | Some e -> Error e
    | None -> Ok(List.ofSeq values)

let private find (name: string) (members: (string * Json) list) : Json option =
    members |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

/// Whether a schema already allows `null`.
let private allowsNull (members: (string * Json) list) : bool =
    let isNullType (json: Json) =
        match json with
        | Json.Object m -> find "type" m = Some(Json.String "null")
        | _ -> false

    match find "type" members, find "anyOf" members, find "enum" members, find "const" members with
    | Some(Json.String "null"), _, _, _ -> true
    | Some(Json.Array types), _, _, _ -> types |> List.contains (Json.String "null")
    | _, Some(Json.Array options), _, _ -> options |> List.exists isNullType
    | _, _, Some(Json.Array values), _ -> values |> List.contains Json.Null
    | _, _, _, Some Json.Null -> true
    | _ -> false

/// The schema, or `null`.
let private nullable (schema: Json) : Json =
    match schema with
    | Json.Object members when allowsNull members -> schema
    | _ -> Json.Object [ "anyOf", Json.Array [ schema; Json.Object [ "type", Json.String "null" ] ] ]

let private isObjectSchema (members: (string * Json) list) : bool =
    match find "type" members with
    | Some(Json.String "object") -> true
    | Some(Json.Array types) -> types |> List.contains (Json.String "object")
    | _ -> (find "properties" members).IsSome

/// The rewrite of one schema, or why it cannot be strict.
let rec private rewrite (schema: Json) : Result<Json, string> =
    match schema with
    | Json.Object members ->
        let typed =
            [ "type"; "anyOf"; "$ref"; "enum"; "const" ]
            |> List.exists (fun name -> (find name members).IsSome)

        match members |> List.tryFind (fun (name, _) -> not (keywords.Contains name)) with
        | Some(name, _) -> Error $"{name} is not in strict mode's subset"
        | None ->
            match find "format" members with
            | Some(Json.String f) when not (formats.Contains f) ->
                Error $"the {f} format is not in strict mode's subset"
            | _ when not typed -> Error "a schema without a type"
            | _ ->
                let kept =
                    members
                    |> List.filter (fun (name, _) ->
                        name <> "default"
                        && name <> "required"
                        && name <> "additionalProperties"
                        && name <> "properties")

                match traverse keyword kept with
                | Error e -> Error e
                | Ok kept when not (isObjectSchema members) -> Ok(Json.Object kept)
                | Ok kept -> closed members kept
    | _ -> Error "a schema that is not an object"

/// A keyword's value, its schemas rewritten.
and private keyword (name: string, value: Json) : Result<string * Json, string> =
    match name, value with
    | "anyOf", Json.Array options -> traverse rewrite options |> Result.map (fun o -> name, Json.Array o)
    | ("$defs" | "definitions"), Json.Object defs ->
        defs
        |> traverse (fun (n, d) -> rewrite d |> Result.map (fun d -> n, d))
        |> Result.map (fun defs -> name, Json.Object defs)
    | "items", (Json.Object _ as items) -> rewrite items |> Result.map (fun items -> name, items)
    | "items", _ -> Error "items that are not a schema"
    | _ -> Ok(name, value)

/// An object schema closed: every property required (the optional ones nullable), no others allowed.
and private closed (members: (string * Json) list) (kept: (string * Json) list) : Result<Json, string> =
    let required =
        match find "required" members with
        | Some(Json.Array names) ->
            names
            |> List.choose (fun n ->
                match n with
                | Json.String s -> Some s
                | _ -> None)
        | _ -> []

    match find "properties" members, find "additionalProperties" members with
    | None, _ -> Error "an object without properties"
    | _, Some(Json.Bool true)
    | _, Some(Json.Object _) -> Error "an object that allows other properties"
    | Some(Json.Object properties), _ ->
        properties
        |> traverse (fun (name, property) ->
            rewrite property
            |> Result.map (fun p -> name, (if List.contains name required then p else nullable p)))
        |> Result.map (fun properties ->
            Json.Object(
                kept
                @ [
                    "properties", Json.Object properties
                    "required", Json.Array(properties |> List.map (fst >> Json.String))
                    "additionalProperties", Json.Bool false
                ]
            ))
    | Some _, _ -> Error "properties that are not an object"

/// The schema in strict mode's dialect, or why it cannot be (the root must be an object schema with
/// properties, not an `anyOf`).
let tryRewrite (schema: Json) : Result<Json, string> =
    match schema with
    | Json.Object members when (find "anyOf" members).IsSome -> Error "the root is an anyOf"
    | Json.Object members when (find "properties" members).IsSome -> rewrite schema
    | _ -> Error "the root is not an object with properties"

/// The schema in strict mode's dialect, if it can be expressed there.
let schema (json: Json) : Json option = tryRewrite json |> Result.toOption
