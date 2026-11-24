namespace Fx.Contracts

open System
open System.Text

/// One step into a JSON value: an object member or an array element.
[<RequireQualifiedAccess>]
type PathSegment =
    | Key of string
    | Index of int

    override this.ToString() =
        match this with
        | Key key -> key
        | Index index -> string index

/// Paths into JSON values, as lists of segments from the root. The empty list is the root.
[<RequireQualifiedAccess>]
module JsonPath =

    let private isIdentifier (key: string) =
        let isStart (c: char) =
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_' || c = '$'

        key.Length > 0
        && isStart key[0]
        && key |> Seq.forall (fun c -> isStart c || (c >= '0' && c <= '9'))

    /// JSONPath-style text in JavaScript accessor syntax: `$`, `$.items[2].name`, `$["a key"]`.
    let render (path: PathSegment list) : string =
        let sb = StringBuilder "$"

        for segment in path do
            match segment with
            | PathSegment.Key key when isIdentifier key -> sb.Append('.').Append(key) |> ignore
            | PathSegment.Key key -> sb.Append('[').Append(Json.quote key).Append(']') |> ignore
            | PathSegment.Index index -> sb.Append('[').Append(index).Append(']') |> ignore

        sb.ToString()

/// A problem found while decoding, worded for a model to read and fix: where (`Path`) and what
/// (`Message`). Rendered as `$.items[2].name: expected string, got number 42`.
type Issue =
    {
        /// Where the problem is, from the root of the decoded value.
        Path: PathSegment list
        /// What is wrong, in a short sentence without the path, e.g. `expected string, got number 42`.
        Message: string
    }

    override this.ToString() =
        JsonPath.render this.Path + ": " + this.Message

[<RequireQualifiedAccess>]
module Issue =

    /// An issue at the root of the decoded value.
    let create (message: string) : Issue = { Path = []; Message = message }

    /// An issue at a path.
    let at (path: PathSegment list) (message: string) : Issue = { Path = path; Message = message }

    /// Moves an issue one level down: under an object member or an array element.
    let under (segment: PathSegment) (issue: Issue) : Issue =
        { issue with
            Path = segment :: issue.Path
        }

    /// `expected WHAT, got DESCRIPTION-OF-THE-VALUE`, e.g. `expected string, got number 42`.
    let expected (what: string) (got: Json) : Issue =
        create $"expected {what}, got {Json.describe got}"

    /// A comma-separated list of quoted names, e.g. `"a", "b"`.
    let quoteAll (names: string seq) : string =
        String.Join(", ", names |> Seq.map Json.quote)

    /// One issue per line.
    let render (issues: Issue list) : string =
        String.Join("\n", issues |> List.map string)

    /// At most `max` issues, one per line, then `… and N more issues` for the rest.
    let renderAtMost (max: int) (issues: Issue list) : string =
        let count = List.length issues

        if count <= max then
            render issues
        else
            let shown = List.truncate (Math.Max(max, 0)) issues
            let rest = count - shown.Length

            let more =
                if rest = 1 then
                    "… and 1 more issue"
                else
                    $"… and {rest} more issues"

            String.Join("\n", (shown |> List.map string) @ [ more ])
