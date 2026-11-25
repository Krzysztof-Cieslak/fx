namespace Fx.Grants

/// What kind of resource an action is about. It decides what a valid resource is and how patterns
/// match it (docs/modules/grants.md, "Resources and patterns").
[<RequireQualifiedAccess>]
type ResourceKind =
    /// A file or directory: `/`-separated and canonical, i.e. no `\`, no `.` or `..` segment and no
    /// empty segment except the first of an absolute path (`/etc/hosts`, `C:/Users/me`, `src/a.fs`).
    | Path
    /// One simple command: its arguments joined by single spaces (`git log -n 3`), no control
    /// characters. A requester splits compound commands (`;`, `&&`, `|`) and checks each part.
    | Command
    /// An opaque name: a host and port (`api.github.com:443`), a component (`bun@1.4.5`), a model
    /// (`copilot/gpt-5`), a secret, a budget. No control characters.
    | Name

    override this.ToString() =
        match this with
        | Path -> "path"
        | Command -> "command"
        | Name -> "name"

/// How specific a pattern is: patterns that match fewer resources are more specific. Compared field
/// by field: exact patterns first, then more literal characters, then fewer `**`, fewer `*`, more `?`.
type Specificity =
    {
        /// No wildcard at all: the pattern matches one resource.
        Exact: bool
        /// Literal characters, separators included.
        Literals: int
        DoubleStars: int
        /// Runs of wildcards (`*` and `?` next to each other) with at least one `*`.
        Stars: int
        Questions: int
    }

    override this.ToString() =
        (if this.Exact then "exact" else "glob")
        + "("
        + string this.Literals
        + " literals, "
        + string this.DoubleStars
        + " **, "
        + string this.Stars
        + " *, "
        + string this.Questions
        + " ?)"

/// One element of a pattern segment.
[<RequireQualifiedAccess>]
type internal Token =
    | Literal of char
    /// `*`: any run of characters (none included) within the segment.
    | Star
    /// `?`: exactly one character within the segment.
    | One

    override this.ToString() =
        match this with
        | Literal c -> string c
        | Star -> "*"
        | One -> "?"

/// A segment of a parsed pattern.
[<RequireQualifiedAccess>]
type internal Segment =
    /// `**`: any number of whole segments, none included.
    | Any
    | Glob of Token array

    override this.ToString() =
        match this with
        | Any -> "**"
        | Glob tokens -> tokens |> Seq.map string |> String.concat ""

/// Ordinal text helpers that compile to FSharp.Core and `String` only (Fable).
[<RequireQualifiedAccess>]
module internal Text =
    /// The pieces between separators, empty ones included (`"a//b"` is `a`, ``, `b`).
    let split (separator: char) (text: string) : string list =
        let mutable pieces = []
        let mutable start = 0

        for i in 0 .. text.Length - 1 do
            if text[i] = separator then
                pieces <- text.Substring(start, i - start) :: pieces
                start <- i + 1

        List.rev (text.Substring start :: pieces)

    /// Whether `part` occurs in `text`, comparing characters exactly.
    let contains (part: string) (text: string) : bool =
        let mutable found = false
        let mutable i = 0

        while not found && i + part.Length <= text.Length do
            if text.Substring(i, part.Length) = part then
                found <- true

            i <- i + 1

        found

    /// Lowercase, character by character, in the invariant culture.
    let lower (text: string) : string =
        text |> String.map System.Char.ToLowerInvariant

/// Resources and the glob patterns grants match them with (docs/modules/grants.md):
///
/// - `*` matches any run of characters, none included, except the separator: `/` in paths, a space
///   in commands; names have no separator, so `*` matches anything in a name.
/// - `?` matches exactly one character except the separator.
/// - `**` (paths and commands only) is a whole segment and matches any number of whole segments, none
///   included: `src/**` matches `src` and everything under it, `git **` any `git` command.
/// - `\` escapes the next character (`\*`, `\?`, `\\`); everything else matches itself.
/// - Case: `matches` compares exactly, `matchesIgnoringCase` ignores case (invariant culture). Deny
///   grants and policy rules use the second, allow grants the first, so a resource spelled with
///   other case is never more permitted ("fail closed", e.g. on case-insensitive filesystems).
[<RequireQualifiedAccess>]
module Pattern =
    let private separator (kind: ResourceKind) : char option =
        match kind with
        | ResourceKind.Path -> Some '/'
        | ResourceKind.Command -> Some ' '
        | ResourceKind.Name -> None

    let private isControl (c: char) = c < ' ' || c = '\u007f'

    /// The longest pattern, in UTF-16 code units.
    [<Literal>]
    let MaxLength = 1024

    /// The most `**` segments in one pattern: each may backtrack over the resource's segments.
    [<Literal>]
    let MaxDoubleStars = 4

    /// Splits a pattern into segments, or says why it is not one.
    let internal parse (kind: ResourceKind) (pattern: string) : Result<Segment list, string> =
        if System.String.IsNullOrEmpty pattern then
            Error "a pattern cannot be empty"
        elif pattern.Length > MaxLength then
            Error("a pattern has at most " + string MaxLength + " characters")
        elif pattern |> Seq.exists isControl then
            Error "a pattern cannot contain control characters"
        else
            let sep = separator kind
            // Built in reverse, with F# lists only (Fable).
            let mutable segments: Segment list = []
            let mutable tokens: Token list = []
            // Unescaped stars that came in pairs in the current segment, and whether it has anything else.
            let mutable bareStars = 0
            let mutable other = false
            let mutable error = None
            let mutable i = 0

            let finish () =
                if bareStars = 2 && not other then
                    segments <- Segment.Any :: segments
                elif bareStars >= 2 then
                    error <- Some "`**` must be a whole segment"
                else
                    segments <- Segment.Glob(tokens |> List.rev |> Array.ofList) :: segments

                tokens <- []
                bareStars <- 0
                other <- false

            while error.IsNone && i < pattern.Length do
                let c = pattern[i]

                if c = '\\' then
                    if i + 1 >= pattern.Length then
                        error <- Some "a pattern cannot end with `\\`"
                    else
                        tokens <- Token.Literal pattern[i + 1] :: tokens
                        other <- true
                        i <- i + 2
                elif Some c = sep then
                    finish ()
                    i <- i + 1
                elif c = '*' then
                    if i + 1 < pattern.Length && pattern[i + 1] = '*' then
                        if sep.IsNone then
                            error <- Some "`**` is for paths and commands; in a name `*` matches anything"
                        else
                            bareStars <- bareStars + 2
                            i <- i + 2
                    else
                        tokens <- Token.Star :: tokens
                        other <- true
                        i <- i + 1
                elif c = '?' then
                    tokens <- Token.One :: tokens
                    other <- true
                    i <- i + 1
                else
                    tokens <- Token.Literal c :: tokens
                    other <- true
                    i <- i + 1

            if error.IsNone then
                finish ()

            match error with
            | Some message -> Error message
            | None ->
                let segments = List.rev segments

                let empty =
                    segments
                    |> List.mapi (fun index segment -> index, segment)
                    |> List.exists (fun (index, segment) ->
                        match segment with
                        | Segment.Glob tokens when tokens.Length = 0 ->
                            // An absolute path starts with an empty segment.
                            not (kind = ResourceKind.Path && index = 0 && segments.Length > 1)
                        | _ -> false)

                let dots =
                    kind = ResourceKind.Path
                    && segments
                       |> List.exists (fun segment ->
                           match segment with
                           | Segment.Glob [| Token.Literal '.' |]
                           | Segment.Glob [| Token.Literal '.'; Token.Literal '.' |] -> true
                           | _ -> false)

                if empty then
                    match kind with
                    | ResourceKind.Path -> Error "a path pattern cannot have an empty segment (`//` or a trailing `/`)"
                    | _ -> Error "a command pattern has single spaces between its words, and none around them"
                elif dots then
                    Error "a path pattern cannot have a `.` or `..` segment"
                elif (segments |> List.filter (fun s -> s = Segment.Any) |> List.length) > MaxDoubleStars then
                    Error("a pattern has at most " + string MaxDoubleStars + " `**` segments")
                else
                    Ok segments

    /// `Ok` if the text is a pattern for resources of this kind.
    let validate (kind: ResourceKind) (pattern: string) : Result<unit, string> = parse kind pattern |> Result.map ignore

    let private equal (ignoreCase: bool) (a: char) (b: char) =
        a = b
        || ignoreCase && System.Char.ToLowerInvariant a = System.Char.ToLowerInvariant b

    /// Whether a segment's tokens match a piece of text with no separator: the classic wildcard
    /// matcher, backtracking only to the last star.
    let private glob (ignoreCase: bool) (tokens: Token array) (text: string) : bool =
        let mutable p = 0
        let mutable t = 0
        let mutable star = -1
        let mutable mark = 0
        let mutable failed = false

        while not failed && t < text.Length do
            if
                p < tokens.Length
                && (match tokens[p] with
                    | Token.One -> true
                    | Token.Literal c -> equal ignoreCase c text[t]
                    | Token.Star -> false)
            then
                p <- p + 1
                t <- t + 1
            elif p < tokens.Length && tokens[p] = Token.Star then
                star <- p
                mark <- t
                p <- p + 1
            elif star >= 0 then
                p <- star + 1
                mark <- mark + 1
                t <- mark
            else
                failed <- true

        while not failed && p < tokens.Length && tokens[p] = Token.Star do
            p <- p + 1

        not failed && p = tokens.Length

    let rec private segments (ignoreCase: bool) (pattern: Segment list) (text: string list) : bool =
        match pattern with
        | [] -> List.isEmpty text
        | Segment.Any :: rest ->
            segments ignoreCase rest text
            || (match text with
                | [] -> false
                | _ :: tail -> segments ignoreCase pattern tail)
        | Segment.Glob tokens :: rest ->
            match text with
            | [] -> false
            | head :: tail -> glob ignoreCase tokens head && segments ignoreCase rest tail

    let private split (kind: ResourceKind) (resource: string) : string list =
        match separator kind with
        | Some sep -> Text.split sep resource
        | None -> [ resource ]

    let private matchWith (ignoreCase: bool) (kind: ResourceKind) (pattern: string) (resource: string) : bool =
        match parse kind pattern with
        | Error _ -> false
        | Ok parsed -> segments ignoreCase parsed (split kind resource)

    /// Whether the pattern matches the resource exactly as written. An invalid pattern matches nothing.
    let matches (kind: ResourceKind) (pattern: string) (resource: string) : bool = matchWith false kind pattern resource

    /// Whether the pattern matches the resource when case is ignored (invariant culture). Every
    /// resource `matches` matches this too.
    let matchesIgnoringCase (kind: ResourceKind) (pattern: string) (resource: string) : bool =
        matchWith true kind pattern resource

    /// The pattern that matches exactly this resource: wildcards and `\` escaped.
    let exact (resource: string) : string =
        resource
        |> String.collect (fun c ->
            if c = '*' || c = '?' || c = '\\' then
                "\\" + string c
            else
                string c)

    /// How specific a valid pattern is (`compareSpecificity` orders them).
    let specificity (kind: ResourceKind) (pattern: string) : Specificity =
        match parse kind pattern with
        | Error _ ->
            {
                Exact = false
                Literals = 0
                DoubleStars = 0
                Stars = 0
                Questions = 0
            }
        | Ok parsed ->
            let separators = parsed.Length - 1

            let count (pick: Token -> bool) =
                parsed
                |> List.sumBy (fun segment ->
                    match segment with
                    | Segment.Any -> 0
                    | Segment.Glob tokens -> tokens |> Seq.filter pick |> Seq.length)

            let doubleStars =
                parsed |> List.filter (fun segment -> segment = Segment.Any) |> List.length

            // Runs of wildcards with a star in them: `?*?*` is one run (two characters or more),
            // so collapsing it to `?*` makes the pattern less specific, not more.
            let stars =
                parsed
                |> List.sumBy (fun segment ->
                    match segment with
                    | Segment.Any -> 0
                    | Segment.Glob tokens ->
                        let mutable runs = 0
                        let mutable inRun = false
                        let mutable starred = false

                        for token in tokens do
                            match token with
                            | Token.Literal _ ->
                                if inRun && starred then
                                    runs <- runs + 1

                                inRun <- false
                                starred <- false
                            | Token.Star ->
                                inRun <- true
                                starred <- true
                            | Token.One -> inRun <- true

                        if inRun && starred then runs + 1 else runs)

            let questions = count (fun token -> token = Token.One)

            let literals =
                separators
                + count (fun token ->
                    match token with
                    | Token.Literal _ -> true
                    | _ -> false)

            {
                Exact = doubleStars = 0 && stars = 0 && questions = 0
                Literals = literals
                DoubleStars = doubleStars
                Stars = stars
                Questions = questions
            }

    /// Orders specificities: positive if `a` is more specific than `b`, negative if less, 0 if equal.
    /// A `?` constrains (exactly one character), so more of them is more specific: `*?` before `*`.
    let compareSpecificity (a: Specificity) (b: Specificity) : int =
        let ranks (s: Specificity) =
            [
                (if s.Exact then 1 else 0)
                s.Literals
                -s.DoubleStars
                -s.Stars
                s.Questions
            ]

        List.compareWith compare (ranks a) (ranks b)

/// Resources as requesters give them.
[<RequireQualifiedAccess>]
module Resource =
    /// The longest resource, in UTF-16 code units.
    [<Literal>]
    let MaxLength = 32768

    /// `Ok` if the text is a valid, canonical resource of this kind (see `ResourceKind`).
    let validate (kind: ResourceKind) (resource: string) : Result<unit, string> =
        if System.String.IsNullOrEmpty resource then
            Error "a resource cannot be empty"
        elif resource.Length > MaxLength then
            Error("a resource has at most " + string MaxLength + " characters")
        elif resource |> Seq.exists (fun c -> c < ' ' || c = '\u007f') then
            Error "a resource cannot contain control characters"
        else
            match kind with
            | ResourceKind.Name -> Ok()
            | ResourceKind.Command ->
                if
                    resource[0] = ' '
                    || resource[resource.Length - 1] = ' '
                    || Text.contains "  " resource
                then
                    Error "a command has single spaces between its words, and none around them"
                else
                    Ok()
            | ResourceKind.Path ->
                let segments = Text.split '/' resource

                if resource |> String.exists (fun c -> c = '\\') then
                    Error "a path is `/`-separated: it cannot contain `\\`"
                elif segments |> List.exists (fun s -> s = "." || s = "..") then
                    Error "a path must be canonical: no `.` or `..` segment"
                elif
                    segments
                    |> List.mapi (fun index segment -> index, segment)
                    |> List.exists (fun (index, segment) -> segment = "" && not (index = 0 && segments.Length > 1))
                then
                    Error "a path cannot have an empty segment (`//` or a trailing `/`)"
                else
                    Ok()

    /// Whether the text is a repo-relative path: "" for the root, otherwise a canonical relative
    /// `/`-separated path (no leading `/`, no drive).
    let isRepoPath (path: string) : bool =
        path = ""
        || (validate ResourceKind.Path path = Ok()
            && path[0] <> '/'
            && not (path |> String.exists (fun c -> c = ':')))

    /// A command's resource: its arguments joined by single spaces.
    let ofArgv (argv: string list) : string = String.concat " " argv

    /// Whether `path` is `prefix` or under it, segment by segment ("" is the root of everything).
    let isUnder (ignoreCase: bool) (prefix: string) (path: string) : bool =
        let fold (text: string) =
            if ignoreCase then Text.lower text else text

        let prefix = fold prefix
        let path = fold path

        prefix = ""
        || path = prefix
        || (path.Length > prefix.Length
            && path[prefix.Length] = '/'
            && path.Substring(0, prefix.Length) = prefix)
