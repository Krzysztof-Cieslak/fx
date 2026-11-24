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
