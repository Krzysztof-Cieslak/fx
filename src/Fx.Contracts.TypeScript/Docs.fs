/// JSDoc comments: descriptions, constraints in words, and standard tags (`@default`, `@param`,
/// `@returns`).
module internal Fx.Contracts.TypeScript.Docs

open System
open System.Text
open Fx.Contracts

/// Keeps text from ending the comment: `*/` becomes `*\/`, which means the same in a regular
/// expression or a JSON string.
let private escape (line: string) = line.Replace("*/", "*\\/")

let private splitLines (text: string) : string list =
    text.Replace("\r\n", "\n").Replace('\r', '\n').Split '\n'
    |> Array.map (fun line -> line.TrimEnd())
    |> List.ofArray

let private trimBlank (lines: string list) =
    lines
    |> List.skipWhile String.IsNullOrEmpty
    |> List.rev
    |> List.skipWhile String.IsNullOrEmpty
    |> List.rev

/// A JSDoc comment: paragraphs (each may span lines), then tags after a blank line. One line of
/// content gives `/** text */`; none gives no comment.
let comment (paragraphs: string list) (tags: string list) : string list =
    let body = paragraphs |> List.collect (splitLines >> trimBlank)
    let tags = tags |> List.collect splitLines

    let content =
        match body, tags with
        | [], tags -> tags
        | body, [] -> body
        | body, tags -> body @ [ "" ] @ tags

    match content with
    | [] -> []
    | [ single ] -> [ $"/** {escape single} */" ]
    | lines ->
        [
            "/**"
            for line in lines do
                if line = "" then " *" else " * " + escape line
            " */"
        ]

/// A regular expression as a JavaScript literal: `/` and line breaks are escaped, which keeps the
/// meaning.
let private regexLiteral (pattern: string) =
    let sb = StringBuilder("/")
    let mutable escaped = false

    for c in pattern do
        match c with
        | '/' when not escaped -> sb.Append "\\/" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\u2028' -> sb.Append "\\u2028" |> ignore
        | '\u2029' -> sb.Append "\\u2029" |> ignore
        | c -> sb.Append c |> ignore

        escaped <- c = '\\' && not escaped

    sb.Append('/').ToString()

let private number (n: JsonNumber) = (JsonNumber.normalize n).Text

let private bounds (facets: NumberFacets) : string option =
    let lower =
        match facets.Minimum with
        | Some(Bound.Inclusive n) -> Some $"at least {number n}"
        | Some(Bound.Exclusive n) -> Some $"greater than {number n}"
        | None -> None

    let upper =
        match facets.Maximum with
        | Some(Bound.Inclusive n) -> Some $"at most {number n}"
        | Some(Bound.Exclusive n) -> Some $"less than {number n}"
        | None -> None

    match facets.Minimum, facets.Maximum, lower, upper with
    | Some(Bound.Inclusive low), Some(Bound.Inclusive high), _, _ when number low = number high ->
        Some $"exactly {number low}"
    | Some(Bound.Inclusive low), Some(Bound.Inclusive high), _, _ -> Some $"from {number low} to {number high}"
    | _, _, Some lower, Some upper -> Some $"{lower} and {upper}"
    | _, _, Some only, None
    | _, _, None, Some only -> Some only
    | _, _, None, None -> None

let private numberSentence (kind: string) (facets: NumberFacets) =
    match bounds facets with
    | Some phrase -> $"{kind}, {phrase}."
    | None -> $"{kind}."

/// `Non-empty.`, `At least 2 items.`, `From 1 to 8 characters.`, …
let private countSentence (noun: string) (minimum: int option) (maximum: int option) : string list =
    let count (n: int) =
        if n = 1 then $"1 {noun}" else $"{n} {noun}s"

    match minimum, maximum with
    | Some 1, None -> [ "Non-empty." ]
    | Some low, Some high when low = high -> [ $"Exactly {count low}." ]
    | Some low, Some high -> [ $"From {low} to {count high}." ]
    | Some low, None -> [ $"At least {count low}." ]
    | None, Some high -> [ $"At most {count high}." ]
    | None, None -> []

/// What the type of a value cannot say, in words: integers, bounds, lengths, patterns, formats and
/// item counts. Looks through `null`, and one level into the items of an array and the values of a
/// map (`Items: …`); named types document their own.
let rec constraints (schema: Schema) : string list = constraintsAt true schema

and private constraintsAt (nested: bool) (schema: Schema) : string list =
    match schema.Kind with
    | SchemaKind.Integer facets -> [ numberSentence "Integer" facets ]
    | SchemaKind.Number facets when facets.Minimum.IsSome || facets.Maximum.IsSome -> [ numberSentence "Number" facets ]
    | SchemaKind.String facets ->
        [
            yield! countSentence "character" facets.MinLength facets.MaxLength
            match facets.Pattern with
            | Some pattern -> $"Matches {regexLiteral pattern}."
            | None -> ()
            match facets.Format with
            | Some format -> $"Format: {format}."
            | None -> ()
        ]
    | SchemaKind.Array array ->
        countSentence "item" array.MinItems array.MaxItems
        @ (if nested then inner "Items" array.Items else [])
    | SchemaKind.Map values -> if nested then inner "Values" values else []
    | SchemaKind.Nullable inner -> constraintsAt nested inner
    | _ -> []

and private inner (label: string) (schema: Schema) : string list =
    match schema.Kind with
    | SchemaKind.Named _
    | SchemaKind.Ref _ -> []
    | _ ->
        let described =
            match schema.Description with
            | Some d -> splitLines d |> trimBlank
            | None -> []

        match described @ constraintsAt false schema with
        | [] -> []
        | parts -> [ label + ": " + String.Join(" ", parts) ]

/// `@default <json>`.
let defaultTag (value: Json) = "@default " + Json.serialize value
