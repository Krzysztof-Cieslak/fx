namespace Fx.Envd

open System
open System.Text
open System.Text.RegularExpressions

/// Globs over relative paths with `/` separators, as git and ripgrep write them:
///
/// - `*` matches within one segment, `?` one character of a segment;
/// - `**` as a whole segment matches any number of segments (`a/**/b`, `**/x`, `dir/**`);
/// - `[abc]`, `[a-z]` and `[!abc]` (or `[^abc]`) match one character of a set;
/// - `{a,b}` matches either alternative (alternatives may hold globs, not nested braces);
/// - `\` escapes the next character;
/// - a glob without `/` matches the last segment (the file name) at any depth; with a `/`, the whole
///   path (a leading `/` anchors it at the root and is dropped).
module Glob =
    let private hasSlash (glob: string) =
        let mutable escaped = false
        let mutable found = false

        for c in glob do
            if escaped then
                escaped <- false
            elif c = '\\' then
                escaped <- true
            elif c = '/' then
                found <- true

        found

    /// The regular expression (anchored) for a glob, or why the glob is invalid.
    let toRegex (glob: string) : Result<string, string> =
        if String.IsNullOrEmpty glob then
            Error "the glob is empty"
        else
            let anchored = hasSlash glob
            let glob = if glob.StartsWith '/' then glob.Substring 1 else glob
            let sb = StringBuilder()
            let mutable error = None
            let mutable inBraces = false
            let mutable i = 0
            let n = glob.Length

            let atSegmentStart (k: int) = k = 0 || glob[k - 1] = '/'

            while error.IsNone && i < n do
                let c = glob[i]

                match c with
                | '*' when
                    i + 1 < n
                    && glob[i + 1] = '*'
                    && atSegmentStart i
                    && (i + 2 = n || glob[i + 2] = '/')
                    ->
                    if i + 2 = n then
                        // `dir/**` or `**`: everything below.
                        sb.Append(".*") |> ignore
                        i <- i + 2
                    else
                        // `**/`: any number of whole segments.
                        sb.Append("(?:[^/]*/)*") |> ignore
                        i <- i + 3
                | '*' ->
                    sb.Append("[^/]*") |> ignore
                    i <- i + 1
                | '?' ->
                    sb.Append("[^/]") |> ignore
                    i <- i + 1
                | '[' ->
                    let close = glob.IndexOf(']', i + 2)

                    if close < 0 then
                        error <- Some $"unclosed [ at {i + 1}"
                    else
                        let body = glob.Substring(i + 1, close - i - 1)

                        let negated, body =
                            if body.StartsWith '!' || body.StartsWith '^' then
                                true, body.Substring 1
                            else
                                false, body

                        let set = StringBuilder()

                        for ch in body do
                            if ch = '\\' || ch = ']' || ch = '[' || ch = '^' then
                                set.Append('\\') |> ignore

                            set.Append(ch) |> ignore

                        if negated then
                            sb.Append("[^/").Append(set).Append(']') |> ignore
                        else
                            sb.Append('[').Append(set).Append(']') |> ignore

                        i <- close + 1
                | '{' when not inBraces ->
                    inBraces <- true
                    sb.Append("(?:") |> ignore
                    i <- i + 1
                | ',' when inBraces ->
                    sb.Append('|') |> ignore
                    i <- i + 1
                | '}' when inBraces ->
                    inBraces <- false
                    sb.Append(')') |> ignore
                    i <- i + 1
                | '{' -> error <- Some $"nested {{ at {i + 1}"
                | '\\' when i + 1 < n ->
                    sb.Append(Regex.Escape(string glob[i + 1])) |> ignore
                    i <- i + 2
                | '\\' -> error <- Some "a trailing \\"
                | c ->
                    sb.Append(Regex.Escape(string c)) |> ignore
                    i <- i + 1

            match error with
            | Some e -> Error e
            | None when inBraces -> Error "unclosed {"
            | None ->
                let body = sb.ToString()
                // Unanchored globs match the file name: any leading directories.
                Ok(
                    if anchored then
                        "^" + body + "$"
                    else
                        "^(?:.*/)?" + body + "$"
                )

    /// A set of globs compiled into one predicate over relative paths (`/` separators).
    type Matcher =
        {
            Globs: string list
            IsMatch: string -> bool
        }

        override this.ToString() = String.Join(", ", this.Globs)

    /// Compiles globs; `None` for no globs. Matching is case-insensitive if asked.
    let compile (ignoreCase: bool) (globs: string list) : Result<Matcher option, string> =
        match globs with
        | [] -> Ok None
        | _ ->
            let parts = globs |> List.map (fun g -> g, toRegex g)

            match
                parts
                |> List.tryPick (fun (g, r) ->
                    (match r with
                     | Error e -> Some(g, e)
                     | Ok _ -> None))
            with
            | Some(glob, reason) -> Error $"invalid glob {glob}: {reason}"
            | None ->
                let pattern =
                    parts
                    |> List.map (fun (_, r) ->
                        match r with
                        | Ok regex -> "(?:" + regex + ")"
                        | Error _ -> "")
                    |> String.concat "|"

                let options =
                    RegexOptions.CultureInvariant
                    ||| RegexOptions.NonBacktracking
                    ||| (if ignoreCase then
                             RegexOptions.IgnoreCase
                         else
                             RegexOptions.None)

                let regex = Regex(pattern, options)

                Ok(
                    Some
                        {
                            Globs = globs
                            IsMatch = fun path -> regex.IsMatch path
                        }
                )

    /// Whether a relative path matches one glob.
    let isMatch (ignoreCase: bool) (glob: string) (path: string) : bool =
        match compile ignoreCase [ glob ] with
        | Ok(Some matcher) -> matcher.IsMatch path
        | _ -> false
