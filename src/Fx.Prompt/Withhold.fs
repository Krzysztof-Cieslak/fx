namespace Fx.Prompt

open System

/// One compiled exclusion pattern.
type internal Glob =
    {
        Source: string
        /// Matched from the workspace root (a leading `/`, or a `/` inside), else at any depth.
        Anchored: bool
        /// Ended with `/`: matches directories only, so only what is under them.
        DirectoryOnly: bool
        Segments: string list
    }

    override this.ToString() = this.Source

/// Content exclusions (spec §18): paths and globs whose content never enters a prompt. The render
/// withholds the content of every file they match and leaves it out of `SeenRanges`; the model-egress
/// hooks (T124) check what leaves as well.
///
/// Patterns follow `.gitignore`: `*` and `?` within a segment, `[abc]` and `[a-z]` classes (`[!a]`
/// negated), `**` for any number of segments, no escapes (`\` separates segments, as on Windows); a pattern without a `/` (other than a trailing one)
/// matches a name at any depth; a leading `/` or a `/` inside anchors it at the workspace root; a
/// trailing `/` matches directories only. A matched directory excludes everything under it. Matching
/// ignores case, so a pattern holds on case-insensitive file systems too. There are no negations:
/// exclusions only ever withhold more.
[<Sealed>]
type Exclusions internal (globs: Glob list) =
    member _.Patterns = globs |> List.map _.Source

    member internal _.Globs = globs

    override _.ToString() = $"exclusions({globs.Length} patterns)"

[<RequireQualifiedAccess>]
module Exclusions =
    let private equalChar (a: char) (b: char) =
        a = b || Char.ToLowerInvariant a = Char.ToLowerInvariant b

    /// Matches a class at `p` (just after `[`) against `c`; returns whether it matched and where the
    /// class ends, or `None` when the class is not closed (then `[` is a literal).
    let private matchClass (pattern: string) (p: int) (c: char) : (bool * int) option =
        let negated = p < pattern.Length && (pattern[p] = '!' || pattern[p] = '^')
        let start = if negated then p + 1 else p
        let mutable i = start
        let mutable matched = false
        let mutable closed = false

        while not closed && i < pattern.Length do
            if pattern[i] = ']' && i > start then
                closed <- true
            elif i + 2 < pattern.Length && pattern[i + 1] = '-' && pattern[i + 2] <> ']' then
                let low = Char.ToLowerInvariant pattern[i]
                let high = Char.ToLowerInvariant pattern[i + 2]
                let lower = Char.ToLowerInvariant c

                if (low <= lower && lower <= high) || (pattern[i] <= c && c <= pattern[i + 2]) then
                    matched <- true

                i <- i + 3
            else
                if equalChar pattern[i] c then
                    matched <- true

                i <- i + 1

        if closed then Some(matched <> negated, i + 1) else None

    /// Whether one path segment matches one pattern segment (`*`, `?`, classes; no `/` in either).
    let rec private matchSegment (pattern: string) (p: int) (name: string) (n: int) : bool =
        if p = pattern.Length then
            n = name.Length
        else
            match pattern[p] with
            | '*' ->
                // Any run of characters: try every split, shortest first.
                let mutable k = n
                let mutable found = false

                while not found && k <= name.Length do
                    found <- matchSegment pattern (p + 1) name k
                    k <- k + 1

                found
            | '?' -> n < name.Length && matchSegment pattern (p + 1) name (n + 1)
            | '[' when n < name.Length ->
                match matchClass pattern (p + 1) name[n] with
                | Some(true, next) -> matchSegment pattern next name (n + 1)
                | Some(false, _) -> false
                | None -> equalChar '[' name[n] && matchSegment pattern (p + 1) name (n + 1)
            | c ->
                n < name.Length
                && equalChar c name[n]
                && matchSegment pattern (p + 1) name (n + 1)

    /// Whether all of `path` (segments) matches the pattern's segments, `**` spanning any number.
    let rec private matchSegments (pattern: string list) (path: string list) : bool =
        match pattern, path with
        | [], [] -> true
        | [ "**" ], _ -> true
        | "**" :: rest, _ ->
            let rec anySuffix (path: string list) =
                matchSegments rest path
                || (match path with
                    | _ :: tail -> anySuffix tail
                    | [] -> false)

            anySuffix path
        | segment :: rest, name :: names -> matchSegment segment 0 name 0 && matchSegments rest names
        | _ -> false

    let private compile (pattern: string) : Result<Glob, string> =
        let trimmed = pattern.Trim().Replace('\\', '/')

        if trimmed = "" then
            Error "an exclusion pattern is empty"
        elif trimmed.StartsWith '!' then
            Error $"\"{pattern}\": negations are not supported: exclusions only withhold more"
        else
            let directoryOnly = trimmed.EndsWith '/'
            let body = trimmed.Trim('/')

            if body = "" then
                Error $"\"{pattern}\" names no path"
            else
                let segments =
                    body.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.filter (fun s -> s <> ".")
                    |> List.ofArray

                let anchored = trimmed.StartsWith '/' || body.Contains '/'

                Ok
                    {
                        Source = pattern
                        Anchored = anchored
                        DirectoryOnly = directoryOnly
                        Segments = if anchored then segments else "**" :: segments
                    }

    /// No exclusions.
    let none: Exclusions = Exclusions([])

    /// Exclusions from patterns; every invalid pattern is an error (a silently ignored exclusion would
    /// leak what it was meant to withhold).
    let create (patterns: string list) : Result<Exclusions, string list> =
        let compiled = patterns |> List.map compile

        match compiled |> List.choose (fun r -> if r.IsError then Some r else None) with
        | [] -> Ok(Exclusions(compiled |> List.choose Result.toOption))
        | errors ->
            Error(
                errors
                |> List.choose (fun r ->
                    match r with
                    | Error e -> Some e
                    | Ok _ -> None)
            )

    /// The patterns it was made from.
    let patterns (exclusions: Exclusions) : string list = exclusions.Patterns

    /// Whether a path's content is excluded: the path, or a directory above it, matches a pattern.
    let excludes (exclusions: Exclusions) (path: string) : bool =
        let segments =
            (Paths.normalize path).Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        let n = segments.Length

        exclusions.Globs
        |> List.exists (fun glob ->
            // The path itself (unless the pattern names directories only), then each directory above it.
            let upTo = if glob.DirectoryOnly then n - 1 else n
            let mutable k = 1
            let mutable found = false

            while not found && k <= upTo do
                found <- matchSegments glob.Segments (List.truncate k segments)
                k <- k + 1

            found)

    /// The pieces with the content of excluded files withheld, and whether any was.
    let filter (exclusions: Exclusions) (pieces: Piece list) : Piece list * bool =
        let mutable changed = false

        let filtered =
            pieces
            |> List.map (fun piece ->
                match piece with
                | Piece.File view when excludes exclusions view.Path ->
                    changed <- true
                    Piece.Withheld(Paths.normalize view.Path)
                | piece -> piece)

        (if changed then filtered else pieces), changed

    /// A block with the content of excluded files withheld, and whether any was.
    let filterBlock (exclusions: Exclusions) (block: Block) : Block * bool =
        match filter exclusions block.Pieces with
        | _, false -> block, false
        | pieces, true -> { block with Pieces = pieces }, true
