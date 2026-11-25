namespace Fx.Models

open System
open System.Text
open Fx.Contracts

/// How paths compare when matched against exclusions: per the filesystem the paths come from
/// (case-sensitive on Linux, insensitive on Windows and macOS). Where that is unknown, as at the
/// model-egress hooks, `Insensitive`: excluding too much is the safe side.
[<RequireQualifiedAccess>]
type PathCase =
    | Sensitive
    | Insensitive

    override this.ToString() =
        match this with
        | Sensitive -> "case-sensitive"
        | Insensitive -> "case-insensitive"

/// Paths as exclusions see them (docs/modules/egress.md): `/`-separated, whatever the producer's
/// operating system wrote.
[<RequireQualifiedAccess>]
module WorkspacePath =
    let private isDrive (segment: string) =
        segment.Length = 2 && Char.IsAsciiLetter segment[0] && segment[1] = ':'

    /// `\` becomes `/`; repeated separators, `.` segments and a trailing separator go; `..` removes
    /// the segment before it where there is one (a relative path above the workspace keeps its
    /// `..`s); a leading `./` goes. Absolute paths keep their root (`/`, `C:/`, `//server/`). Case
    /// and Unicode normalization are left alone (matching handles case).
    let normalize (path: string) : string =
        let unified = path.Replace('\\', '/')
        let unc = unified.StartsWith("//", StringComparison.Ordinal)
        let rooted = unified.StartsWith('/')

        let segments =
            unified.Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> Array.fold
                (fun (kept: string list) segment ->
                    match segment, kept with
                    | ".", _ -> kept
                    | "..", [ drive ] when isDrive drive -> kept
                    | "..", last :: rest when last <> ".." -> rest
                    | "..", [] when rooted -> kept
                    | _ -> segment :: kept)
                []
            |> List.rev

        let body = String.Join("/", segments)

        if unc then "//" + body
        elif rooted then "/" + body
        else body

    /// Whether a normalized path is absolute (`/…`, `C:/…`, `//server/…`).
    let isAbsolute (normalized: string) : bool =
        normalized.StartsWith('/')
        || (normalized.Length >= 2
            && Char.IsAsciiLetter normalized[0]
            && normalized[1] = ':')

/// One step of a compiled glob.
[<RequireQualifiedAccess>]
type internal GlobToken =
    | Char of char
    | Slash
    /// `?`: one character but `/`.
    | AnyChar
    /// `*`: any characters but `/`.
    | Star
    /// `[…]`: one character but `/`, in (or, negated, not in) the ranges.
    | Class of negated: bool * ranges: (char * char) list
    /// `**/`: nothing, or any characters ending with `/` (zero or more directories).
    | AnyDirs
    /// A trailing `**`: one or more of any characters.
    | AnyRest

    override this.ToString() =
        match this with
        | Char c -> string c
        | Slash -> "/"
        | AnyChar -> "?"
        | Star -> "*"
        | Class(negated, ranges) -> $"[{negated} {ranges.Length}]"
        | AnyDirs -> "**/"
        | AnyRest -> "**"

[<RequireQualifiedAccess>]
type internal GlobNode =
    | Token of GlobToken
    | Alt of GlobNode list list

    override this.ToString() =
        match this with
        | Token t -> string t
        | Alt alternatives -> $"{{{alternatives.Length}}}"

/// A compiled pattern: gitignore's syntax and semantics (docs/modules/egress.md), with `{a,b}`
/// alternatives. Pure; matching is linear in the pattern times the path, never backtracking.
[<Sealed>]
type Glob private (pattern: string, negated: bool, directoryOnly: bool, alternatives: GlobToken array list) =
    /// The pattern as written.
    member _.Pattern = pattern
    /// `!pattern`: re-includes what an earlier pattern of its list excluded.
    member _.Negated = negated
    /// `pattern/`: matches directories only (and so everything inside them).
    member _.DirectoryOnly = directoryOnly
    member internal _.Alternatives = alternatives
    override _.ToString() = pattern

    static member internal Create(pattern, negated, directoryOnly, alternatives) =
        Glob(pattern, negated, directoryOnly, alternatives)

[<RequireQualifiedAccess>]
module Glob =
    /// The most alternatives `{…}` may expand to in one pattern.
    let maxAlternatives = 256

    /// The longest pattern.
    let maxLength = 4096

    /// Reads a pattern into nodes; the first problem stops it (no exceptions: a bad pattern is an
    /// expected failure).
    type private Reader() =
        let mutable error: string option = None

        member _.Error = error

        member _.Fail(message: string) =
            if error.IsNone then
                error <- Some message

        member _.Ok = error.IsNone

    /// Reads one character of a class, after `\` if escaped; `None` at the end.
    let private classChar (text: string) (reader: Reader) (i: byref<int>) : char =
        if text[i] = '\\' then
            if i + 1 >= text.Length then
                reader.Fail "a \\ at the end of the pattern"
                i <- text.Length
                '\000'
            else
                i <- i + 2
                text[i - 1]
        else
            i <- i + 1
            text[i - 1]

    /// Parses `[…]` starting after the `[`; returns the token and the index after `]`.
    let private parseClass (text: string) (reader: Reader) (start: int) : GlobToken * int =
        let mutable i = start
        let mutable negated = false

        if i < text.Length && (text[i] = '!' || text[i] = '^') then
            negated <- true
            i <- i + 1

        let ranges = ResizeArray<char * char>()
        let mutable closed = false
        let mutable first = true

        while reader.Ok && not closed do
            if i >= text.Length then
                reader.Fail "an unclosed [ (write \\[ for a literal one)"
            elif text[i] = ']' && not first then
                i <- i + 1
                closed <- true
            else
                let low = classChar text reader &i

                if reader.Ok then
                    if i + 1 < text.Length && text[i] = '-' && text[i + 1] <> ']' then
                        i <- i + 1
                        let high = classChar text reader &i

                        if reader.Ok then
                            if high < low then
                                reader.Fail $"the range {low}-{high} is reversed"
                            else
                                ranges.Add((low, high))
                    else
                        ranges.Add((low, low))

            first <- false

        GlobToken.Class(negated, List.ofSeq ranges), i

    /// Parses a sequence until the end, or (inside braces) until `,` or `}`.
    let rec private parseSeq (text: string) (reader: Reader) (start: int) (inBraces: bool) : GlobNode list * int =
        let nodes = ResizeArray<GlobNode>()
        let mutable i = start
        let mutable stop = false

        while reader.Ok && not stop && i < text.Length do
            match text[i] with
            | ',' when inBraces -> stop <- true
            | '}' when inBraces -> stop <- true
            | '\\' ->
                if i + 1 >= text.Length then
                    reader.Fail "a \\ at the end of the pattern"
                else
                    let c = text[i + 1]
                    nodes.Add(GlobNode.Token(if c = '/' then GlobToken.Slash else GlobToken.Char c))
                    i <- i + 2
            | '/' ->
                nodes.Add(GlobNode.Token GlobToken.Slash)
                i <- i + 1
            | '?' ->
                nodes.Add(GlobNode.Token GlobToken.AnyChar)
                i <- i + 1
            | '*' ->
                nodes.Add(GlobNode.Token GlobToken.Star)
                i <- i + 1
            | '[' ->
                let token, after = parseClass text reader (i + 1)
                nodes.Add(GlobNode.Token token)
                i <- after
            | '{' ->
                let alternatives = ResizeArray<GlobNode list>()
                let mutable j = i + 1
                let mutable closed = false

                while reader.Ok && not closed do
                    let alternative, after = parseSeq text reader j true
                    alternatives.Add alternative

                    if not reader.Ok then
                        ()
                    elif after >= text.Length then
                        reader.Fail "an unclosed { (write \\{ for a literal one)"
                    else
                        closed <- text[after] = '}'
                        j <- after + 1

                nodes.Add(GlobNode.Alt(List.ofSeq alternatives))
                i <- j
            | c ->
                nodes.Add(GlobNode.Token(GlobToken.Char c))
                i <- i + 1

        List.ofSeq nodes, i

    /// Every token sequence the alternatives give, in order; `None` past `maxAlternatives`.
    let rec private expand (nodes: GlobNode list) : GlobToken list list option =
        let mutable acc: GlobToken list list option = Some [ [] ]

        for node in nodes do
            match acc with
            | None -> ()
            | Some prefixes ->
                let options =
                    match node with
                    | GlobNode.Token t -> Some [ [ t ] ]
                    | GlobNode.Alt alternatives ->
                        let expanded = alternatives |> List.map expand

                        if expanded |> List.exists Option.isNone then
                            None
                        else
                            Some(expanded |> List.collect (Option.defaultValue []))

                acc <-
                    match options with
                    | None -> None
                    | Some options when prefixes.Length * options.Length > maxAlternatives -> None
                    | Some options ->
                        Some
                            [
                                for prefix in prefixes do
                                    for option in options -> prefix @ option
                            ]

        acc

    /// `**` segments become `AnyDirs`/`AnyRest`; other runs of `*` one `*`.
    let private globstars (tokens: GlobToken array) : GlobToken list =
        let out = ResizeArray<GlobToken>()
        let mutable i = 0

        while i < tokens.Length do
            match tokens[i] with
            | GlobToken.Star ->
                let mutable j = i

                while j < tokens.Length && tokens[j] = GlobToken.Star do
                    j <- j + 1

                let run = j - i
                let atStart = i = 0 || tokens[i - 1] = GlobToken.Slash
                let atEnd = j = tokens.Length
                let beforeSlash = j < tokens.Length && tokens[j] = GlobToken.Slash

                if run >= 2 && atStart && beforeSlash then
                    out.Add GlobToken.AnyDirs
                    i <- j + 1
                elif run >= 2 && atStart && atEnd then
                    out.Add GlobToken.AnyRest
                    i <- j
                else
                    out.Add GlobToken.Star
                    i <- j
            | token ->
                out.Add token
                i <- i + 1

        List.ofSeq out

    /// One alternative: whether it is directory-only, and its tokens, anchored; `None` if it has
    /// only separators.
    let private compileAlternative (tokens: GlobToken list) : (bool * GlobToken array) option =
        let mutable tokens = Array.ofList tokens
        let mutable directoryOnly = false

        while tokens.Length > 0 && tokens[tokens.Length - 1] = GlobToken.Slash do
            directoryOnly <- true
            tokens <- tokens[.. tokens.Length - 2]

        let leading = tokens.Length > 0 && tokens[0] = GlobToken.Slash

        while tokens.Length > 0 && tokens[0] = GlobToken.Slash do
            tokens <- tokens[1..]

        if tokens.Length = 0 then
            None
        else
            let anchored = leading || Array.contains GlobToken.Slash tokens
            let converted = globstars tokens

            let converted =
                match converted with
                | GlobToken.AnyDirs :: _
                | [ GlobToken.AnyRest ] -> converted
                | _ when not anchored -> GlobToken.AnyDirs :: converted
                | _ -> converted

            Some(directoryOnly, Array.ofList converted)

    /// Compiles a pattern. `None` for blank lines and `#` comments (so lists can be written like a
    /// `.gitignore`); `Error` with the reason for one that cannot be read.
    let parse (pattern: string) : Result<Glob option, string> =
        let text = pattern.Trim()

        if text.Length = 0 || text.StartsWith('#') then
            Ok None
        elif text.Length > maxLength then
            Error $"the pattern is longer than {maxLength} characters"
        else
            let negated = text.StartsWith('!')
            let body = if negated then text.Substring 1 else text
            let reader = Reader()
            let nodes, _ = parseSeq body reader 0 false

            match reader.Error with
            | _ when body.Length = 0 -> Error "! needs a pattern after it"
            | Some message -> Error message
            | None ->
                match expand nodes with
                | None -> Error $"the pattern has more than {maxAlternatives} alternatives"
                | Some expanded ->
                    let compiled = expanded |> List.map compileAlternative

                    if compiled |> List.exists Option.isNone then
                        Error "the pattern matches nothing (it is only separators)"
                    else
                        let compiled = compiled |> List.choose id
                        let directoryOnly = compiled |> List.forall fst

                        // A directory-only alternative among others matches what is inside its
                        // directories.
                        let alternatives =
                            compiled
                            |> List.map (fun (dirOnly, tokens) ->
                                if dirOnly && not directoryOnly then
                                    Array.append tokens [| GlobToken.Slash; GlobToken.AnyRest |]
                                else
                                    tokens)

                        Ok(Some(Glob.Create(text, negated, directoryOnly, alternatives)))

    let private equal (case: PathCase) (a: char) (b: char) =
        a = b
        || (case = PathCase.Insensitive
            && (Char.ToUpperInvariant a = Char.ToUpperInvariant b
                || Char.ToLowerInvariant a = Char.ToLowerInvariant b))

    let private inClass (case: PathCase) (ranges: (char * char) list) (c: char) =
        let inRanges (c: char) =
            ranges |> List.exists (fun (low, high) -> c >= low && c <= high)

        inRanges c
        || (case = PathCase.Insensitive
            && (inRanges (Char.ToUpperInvariant c) || inRanges (Char.ToLowerInvariant c)))

    /// Whether one token sequence matches the whole text: dynamic programming over (token, position),
    /// one row at a time from the last token.
    let private matchesTokens (case: PathCase) (tokens: GlobToken array) (text: string) : bool =
        let m = text.Length
        let mutable next = Array.init (m + 1) (fun j -> j = m)

        for i in tokens.Length - 1 .. -1 .. 0 do
            let current = Array.zeroCreate<bool>(m + 1)

            match tokens[i] with
            | GlobToken.Char c ->
                for j in 0 .. m - 1 do
                    current[j] <- next[j + 1] && equal case text[j] c
            | GlobToken.Slash ->
                for j in 0 .. m - 1 do
                    current[j] <- next[j + 1] && text[j] = '/'
            | GlobToken.AnyChar ->
                for j in 0 .. m - 1 do
                    current[j] <- next[j + 1] && text[j] <> '/'
            | GlobToken.Class(negated, ranges) ->
                for j in 0 .. m - 1 do
                    current[j] <- next[j + 1] && text[j] <> '/' && (inClass case ranges text[j] <> negated)
            | GlobToken.Star ->
                current[m] <- next[m]

                for j in m - 1 .. -1 .. 0 do
                    current[j] <- next[j] || (text[j] <> '/' && current[j + 1])
            | GlobToken.AnyDirs ->
                // Nothing, or text[j..k) ending with '/' for some k > j.
                let mutable later = false
                current[m] <- next[m]

                for j in m - 1 .. -1 .. 0 do
                    if text[j] = '/' && next[j + 1] then
                        later <- true

                    current[j] <- next[j] || later
            | GlobToken.AnyRest ->
                let mutable later = false

                for j in m - 1 .. -1 .. 0 do
                    if next[j + 1] then
                        later <- true

                    current[j] <- later

            next <- current

        next[0]

    /// Whether the glob matches a normalized path (`WorkspacePath.normalize`) that is a directory
    /// or a file. Negation is not applied here: it is the list's business.
    let isMatch (case: PathCase) (glob: Glob) (path: string) (isDirectory: bool) : bool =
        (isDirectory || not glob.DirectoryOnly)
        && glob.Alternatives |> List.exists (fun tokens -> matchesTokens case tokens path)

/// Patterns from one origin (host config, project config, policy, a provider's policy), in the
/// order written. Within a list, the last pattern that matches decides (`!` re-includes), as in a
/// `.gitignore`; lists never re-include what another list excludes, so adding a list only excludes
/// more.
type ExclusionList =
    {
        /// Who set the patterns, e.g. `host`, `project`, `policy`, `copilot`: shown with every match.
        Origin: string
        Patterns: string list
    }

    override this.ToString() =
        $"exclusions({this.Origin}, {this.Patterns.Length} patterns)"

/// Why a path is excluded: the first list that excludes it, and its pattern that decided.
type ExclusionMatch =
    {
        /// The path, normalized.
        Path: string
        Origin: string
        Pattern: string
    }

    override this.ToString() =
        $"{this.Path} (excluded by {this.Origin}: {this.Pattern})"

type internal CompiledList = { Origin: string; Globs: Glob array }

/// Compiled exclusion lists and how paths compare: what `Exclusions.test` matches against.
[<Sealed>]
type Exclusions private (case: PathCase, lists: CompiledList list) =
    member _.Case = case
    member internal _.Lists = lists
    /// No list has a pattern.
    member _.IsEmpty = lists |> List.forall (fun l -> l.Globs.Length = 0)

    override _.ToString() =
        $"Exclusions({case}, {lists.Length} lists)"

    static member internal Create(case, lists) = Exclusions(case, lists)

[<RequireQualifiedAccess>]
module ExclusionList =
    let codec: Codec<ExclusionList> =
        Codec.record {
            let! origin =
                Codec.field "origin" (fun (l: ExclusionList) -> l.Origin) (Codec.string |> Codec.nonEmpty)

            and! patterns =
                Codec.field "patterns" (fun l -> l.Patterns) (Codec.list Codec.string)

            return { Origin = origin; Patterns = patterns }
        }
        |> Codec.describe "Content exclusion patterns from one origin."
        |> Codec.named "ExclusionList"

    /// The issue of each pattern that cannot be read, at its index.
    let check (patterns: string list) : Issue list =
        patterns
        |> List.indexed
        |> List.choose (fun (i, pattern) ->
            match Glob.parse pattern with
            | Ok _ -> None
            | Error message -> Some(Issue.create message |> Issue.under (PathSegment.Index i)))

    /// Patterns (gitignore syntax), checked when read: for config values and variant slots.
    let patterns: Codec<string list> =
        Codec.list Codec.string
        |> Codec.refine (fun patterns ->
            match check patterns with
            | [] -> Ok()
            | issues -> Error(String.Join("; ", issues |> List.map string)))
        |> Codec.describe
            "Paths to keep out of model requests, gitignore syntax: relative to the workspace; * ? [..] {a,b} **; / anchors; a trailing / matches directories; ! re-includes within the list."

[<RequireQualifiedAccess>]
module ExclusionMatch =
    let codec: Codec<ExclusionMatch> =
        Codec.record {
            let! path = Codec.field "path" (fun (m: ExclusionMatch) -> m.Path) Codec.string
            and! origin = Codec.field "origin" (fun m -> m.Origin) Codec.string
            and! pattern = Codec.field "pattern" (fun m -> m.Pattern) Codec.string

            return
                {
                    Path = path
                    Origin = origin
                    Pattern = pattern
                }
        }
        |> Codec.named "ExclusionMatch"

/// Content exclusions (spec §5, §6.5, §18): the one matcher every place that keeps content from
/// models uses (prompt render, host bindings, activity, the model-egress hooks).
[<RequireQualifiedAccess>]
module Exclusions =
    /// No exclusions.
    let none: Exclusions = Exclusions.Create(PathCase.Insensitive, [])

    /// Compiles lists; every pattern that cannot be read is an issue (under `[list].patterns[i]`).
    let create (case: PathCase) (lists: ExclusionList list) : Validation<Exclusions, Issue> =
        let compiled =
            lists
            |> List.mapi (fun index list ->
                let globs, issues =
                    list.Patterns
                    |> List.mapi (fun i pattern ->
                        match Glob.parse pattern with
                        | Ok glob -> glob, None
                        | Error message ->
                            None,
                            Some(
                                Issue.create message
                                |> Issue.under (PathSegment.Index i)
                                |> Issue.under (PathSegment.Key "patterns")
                                |> Issue.under (PathSegment.Index index)
                            ))
                    |> List.unzip

                {
                    Origin = list.Origin
                    Globs = globs |> List.choose id |> Array.ofList
                },
                List.choose id issues)

        match compiled |> List.collect snd with
        | [] -> Ok(Exclusions.Create(case, compiled |> List.map fst))
        | issues -> Error issues

    /// The last pattern of the list that matches, if it excludes.
    let private decide (case: PathCase) (list: CompiledList) (path: string) (isDirectory: bool) : Glob option =
        let mutable last: Glob option = None

        for glob in list.Globs do
            if Glob.isMatch case glob path isDirectory then
                last <- Some glob

        last |> Option.filter (fun glob -> not glob.Negated)

    /// The directories above a normalized path, the outermost first (not the root itself).
    let private ancestors (path: string) : string list =
        [
            for i in 1 .. path.Length - 1 do
                if path[i] = '/' && path[i - 1] <> '/' then
                    path.Substring(0, i)
        ]

    let private testEntry (exclusions: Exclusions) (path: string) (isDirectory: bool) : ExclusionMatch option =
        let normalized = WorkspacePath.normalize path

        if normalized.Length = 0 || exclusions.IsEmpty then
            None
        else
            let inList (list: CompiledList) =
                // As in git: nothing inside an excluded directory can be re-included.
                let above =
                    ancestors normalized
                    |> List.tryPick (fun dir -> decide exclusions.Case list dir true)

                match above with
                | Some glob -> Some glob
                | None -> decide exclusions.Case list normalized isDirectory

            exclusions.Lists
            |> List.tryPick (fun list ->
                inList list
                |> Option.map (fun glob ->
                    {
                        Path = normalized
                        Origin = list.Origin
                        Pattern = glob.Pattern
                    }))

    /// Why a file is excluded, if it is.
    let test (exclusions: Exclusions) (path: string) : ExclusionMatch option = testEntry exclusions path false

    /// Why a directory (and so everything in it) is excluded, if it is.
    let testDirectory (exclusions: Exclusions) (path: string) : ExclusionMatch option = testEntry exclusions path true

    /// The first of the sources that is excluded.
    let firstExcluded (exclusions: Exclusions) (sources: ContentSource list) : ExclusionMatch option =
        sources |> List.tryPick (fun s -> test exclusions s.Path)

/// What a placeholder says in place of excluded content.
[<RequireQualifiedAccess>]
type PlaceholderStyle =
    /// Only that content was excluded.
    | Plain
    /// Also which file it came from.
    | WithPath

    override this.ToString() =
        match this with
        | Plain -> "plain"
        | WithPath -> "path"

[<RequireQualifiedAccess>]
module Placeholder =
    let styleCodec: Codec<PlaceholderStyle> =
        Codec.enum [ PlaceholderStyle.Plain, "plain"; PlaceholderStyle.WithPath, "path" ]
        |> Codec.named "PlaceholderStyle"

    /// The text that replaces excluded content: the same for the same input (so a redacted history
    /// stays a stable, cacheable prefix), telling the model the content exists but is excluded. It
    /// never says how large the content was or what it hashes to.
    let text (style: PlaceholderStyle) (excluded: ExclusionMatch) : string =
        match style with
        | PlaceholderStyle.Plain ->
            "[content excluded: it comes from a file that content exclusions keep out of model requests]"
        | PlaceholderStyle.WithPath ->
            $"[content excluded: {excluded.Path} is kept out of model requests by content exclusions]"

/// Where in a request excluded content was found: `system[i]`, `messages[i].parts[j]` or
/// `messages[i].parts[j].content[k]`, and why.
type ExcludedContent =
    {
        Location: string
        Match: ExclusionMatch
    }

    override this.ToString() = $"{this.Location}: {this.Match}"

/// Content exclusions over requests: replacing excluded parts with placeholders, and finding what
/// is left (spec §18; the model-egress hooks in `ContentExclusions`, Fx.Sdk).
[<RequireQualifiedAccess>]
module ExclusionRedaction =
    let private placeholderPart (text: string) : Part = Part.text text

    let private redactPart
        (placeholder: ExclusionMatch -> string)
        (exclusions: Exclusions)
        (location: string)
        (found: ResizeArray<ExcludedContent>)
        (part: Part)
        : Part =
        let excluded (sources: ContentSource list) =
            Exclusions.firstExcluded exclusions sources

        let record (where: string) (m: ExclusionMatch) =
            found.Add { Location = where; Match = m }

        match part with
        | Part.Text t ->
            match excluded t.Sources with
            | Some m ->
                record location m
                placeholderPart (placeholder m)
            | None -> part
        | Part.Media media ->
            match excluded media.Sources with
            | Some m ->
                record location m
                placeholderPart (placeholder m)
            | None -> part
        | Part.ToolResult result ->
            match excluded result.Sources with
            | Some m ->
                record location m

                Part.ToolResult
                    { result with
                        Content = [ ToolContent.Text(placeholder m) ]
                        Sources = []
                    }
            | None ->
                let mutable changed = false

                let content =
                    result.Content
                    |> List.mapi (fun k item ->
                        match item with
                        | ToolContent.Media media ->
                            match excluded media.Sources with
                            | Some m ->
                                record $"{location}.content[{k}]" m
                                changed <- true
                                ToolContent.Text(placeholder m)
                            | None -> item
                        | ToolContent.Text _ -> item)

                if changed then
                    Part.ToolResult { result with Content = content }
                else
                    part
        | Part.Reasoning _
        | Part.ToolCall _
        | Part.Refusal _
        | Part.CacheBreakpoint _ -> part

    /// Replaces every part whose sources are excluded with a placeholder (`placeholder` gives its
    /// text): text and media become a text part, a tool result keeps its call id, name and error flag
    /// with the placeholder as its content, excluded media in a tool result become text. Returns the
    /// request and what was replaced, in request order. Parts without sources are left alone.
    let redact
        (placeholder: ExclusionMatch -> string)
        (exclusions: Exclusions)
        (request: ChatRequest)
        : ChatRequest * ExcludedContent list =
        if exclusions.IsEmpty || (ChatRequest.sources request).IsEmpty then
            request, []
        else
            let found = ResizeArray<ExcludedContent>()

            let system =
                request.System
                |> List.mapi (fun i part -> redactPart placeholder exclusions $"system[{i}]" found part)

            let messages =
                request.Messages
                |> List.mapi (fun i message ->
                    { message with
                        Parts =
                            message.Parts
                            |> List.mapi (fun j part ->
                                redactPart placeholder exclusions $"messages[{i}].parts[{j}]" found part)
                    })

            if found.Count = 0 then
                request, []
            else
                { request with
                    System = system
                    Messages = messages
                },
                List.ofSeq found

    /// The excluded content still in a request: every part (or tool result content) whose sources
    /// are excluded, in request order.
    let find (exclusions: Exclusions) (request: ChatRequest) : ExcludedContent list =
        snd (redact (fun _ -> "") exclusions request)
