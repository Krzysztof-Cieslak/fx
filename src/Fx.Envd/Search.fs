namespace Fx.Envd

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// `FsOps.Search` over any `FsRead`: a depth-first walk in name order that skips `.git` directories
/// and does not follow symlinks, and, as the query asks, what ignore files ignore and hidden entries
/// (`Ignores`); binary files (a zero byte in the first 8000 bytes) and files larger
/// than the limit are not searched for the pattern. Lines are decoded as UTF-8 (invalid bytes become
/// U+FFFD) and matched one at a time with .NET's linear-time engine (`RegexOptions.NonBacktracking`).
module Search =
    let private maxLineChars = 500

    let private isBinary (content: byte array) =
        let n = min content.Length 8000
        let mutable found = false
        let mutable i = 0

        while not found && i < n do
            if content[i] = 0uy then
                found <- true

            i <- i + 1

        found

    /// The matches of a regular expression in a text, one per matching line (the first match's column).
    let matchLines (regex: Regex) (path: string) (text: string) (limit: int) : SearchMatch list =
        let results = ResizeArray<SearchMatch>()
        let mutable line = 1
        let mutable start = 0
        let mutable i = 0

        let check (stop: int) =
            let length =
                if stop > start && text[stop - 1] = '\r' then
                    stop - 1 - start
                else
                    stop - start

            let content = text.Substring(start, length)
            let m = regex.Match content

            if m.Success then
                results.Add
                    {
                        Path = path
                        Line = line
                        Column = m.Index + 1
                        Text =
                            if content.Length > maxLineChars then
                                content.Substring(0, maxLineChars)
                            else
                                content
                    }

        while results.Count < limit && i < text.Length do
            if text[i] = '\n' then
                check i
                line <- line + 1
                start <- i + 1

            i <- i + 1

        if results.Count < limit && start < text.Length then
            check text.Length

        List.ofSeq results

    /// How many lines of a text match.
    let countLines (regex: Regex) (text: string) : int =
        let mutable count = 0
        let mutable start = 0

        let check (stop: int) =
            let stop =
                if stop > start && text[stop - 1] = '\r' then
                    stop - 1
                else
                    stop

            if regex.IsMatch(text.AsSpan(start, stop - start)) then
                count <- count + 1

        for i in 0 .. text.Length - 1 do
            if text[i] = '\n' then
                check i
                start <- i + 1

        if start < text.Length then
            check text.Length

        count

    /// The path of `name` below the relative directory `parent` (`""` for the root).
    let private relative (parent: string) (name: string) =
        if parent = "" then name else parent + "/" + name

    let private compileRegex (query: SearchQuery) : Result<Regex option, string> =
        match query.Pattern with
        | None -> Ok None
        | Some pattern ->
            let options =
                RegexOptions.CultureInvariant
                ||| RegexOptions.NonBacktracking
                ||| (if query.IgnoreCase then
                         RegexOptions.IgnoreCase
                     else
                         RegexOptions.None)

            try
                Ok(Some(Regex(pattern, options)))
            with
            | :? ArgumentException as e -> Error $"invalid pattern: {e.Message}"
            | :? NotSupportedException as e -> Error $"unsupported pattern: {e.Message}"

    /// A path below a directory, in the directory's syntax.
    let private below (directory: string) (segments: string list) : string =
        segments
        |> List.fold (fun path segment -> IO.Path.Join(path, segment)) directory

    /// The base whose ignore files apply (`IgnoresFrom` when `Root` is it or below it, else `Root`)
    /// and `Root` relative to it (with `/`; `""` for the base itself).
    let private offset (query: SearchQuery) : string * string =
        match query.IgnoresFrom with
        | Some from ->
            let trim (p: string) = p.Replace('\\', '/').TrimEnd('/')
            let root, trimmed = trim query.Root, trim from

            if root = trimmed then
                from, ""
            elif root.StartsWith(trimmed + "/", StringComparison.Ordinal) then
                from, root.Substring(trimmed.Length + 1)
            else
                query.Root, ""
        | None -> query.Root, ""

    /// The ignore rules in force at the root: `.git/info/exclude` and the `.gitignore` of every directory
    /// from the base down to the root's parent (the root's own is read as the walk lists it).
    let private initialRules
        (read: FsRead)
        (query: SearchQuery)
        (cancellation: CancellationToken)
        : Task<Ignores.Rules list> =
        task {
            let baseDir, rootOffset = offset query
            let rules = ResizeArray<Ignores.Rules>()

            let readRules (directory: string) (file: string) =
                task {
                    match! read.ReadFile file cancellation with
                    | Ok content when not (isBinary content) ->
                        rules.Add(Ignores.ofText directory (Encoding.UTF8.GetString content))
                    | _ -> ()
                }

            do! readRules "" (below baseDir [ ".git"; "info"; "exclude" ])

            let segments =
                if rootOffset = "" then
                    []
                else
                    List.ofArray (rootOffset.Split '/')

            // The base and each directory above the root: their .gitignore files.
            for depth in 0 .. segments.Length - 1 do
                let above = List.take depth segments
                do! readRules (String.Join("/", above)) (below baseDir (above @ [ ".gitignore" ]))

            return List.ofSeq rules
        }

    /// Runs a query over a filesystem.
    let run
        (read: FsRead)
        (query: SearchQuery)
        (cancellation: CancellationToken)
        : Task<Result<SearchResult, SearchError>> =
        let compiled =
            match
                compileRegex query,
                Glob.compile query.IgnoreCase query.Include,
                Glob.compile query.IgnoreCase query.Exclude
            with
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
            | Ok regex, Ok includes, Ok excludes -> Ok(regex, includes, excludes)

        if query.MaxResults < 1 then
            Task.FromResult(Error(InvalidQuery "MaxResults must be at least 1"))
        else
            match compiled with
            | Error reason -> Task.FromResult(Error(InvalidQuery reason))
            | Ok(regex, includes, excludes) ->
                task {
                    let files = ResizeArray<string>()
                    let matches = ResizeArray<SearchMatch>()
                    let counts = ResizeArray<SearchCount>()
                    let mutable truncated = false
                    let mutable countsComplete = true
                    let mutable stopped = false
                    let mutable failure = None
                    let _, rootOffset = offset query

                    let! rootRules =
                        if query.Ignores then
                            initialRules read query cancellation
                        else
                            Task.FromResult []

                    let excluded (path: string) =
                        match excludes with
                        | Some m -> m.IsMatch path
                        | None -> false

                    let included (path: string) =
                        match includes with
                        | Some m -> m.IsMatch path
                        | None -> true

                    // A path relative to the root, as ignore rules see it: relative to the base.
                    let fromBase (path: string) =
                        if rootOffset = "" then path
                        elif path = "" then rootOffset
                        else rootOffset + "/" + path

                    let visible (name: string) =
                        query.Hidden || not (name.StartsWith '.')

                    // Directories still to visit, as (absolute path, relative path, the ignore rules in
                    // force there); a stack, with children pushed in reverse so they come out in name order.
                    let pending = Stack<string * string * Ignores.Rules list>()
                    pending.Push(query.Root, "", rootRules)

                    while failure.IsNone && not stopped && pending.Count > 0 do
                        cancellation.ThrowIfCancellationRequested()
                        let directory, rel, inherited = pending.Pop()

                        match! read.List directory cancellation with
                        | Error e when rel = "" -> failure <- Some(SearchFsError e)
                        | Error _ -> () // vanished or unreadable below the root: skip it
                        | Ok entries ->
                            let entries = Array.ofList entries

                            let! rules =
                                task {
                                    match
                                        entries
                                        |> Array.tryFind (fun e -> e.Name = ".gitignore" && e.Kind = EntryKind.File)
                                    with
                                    | Some file when query.Ignores ->
                                        match! read.ReadFile file.Path cancellation with
                                        | Ok content when not (isBinary content) ->
                                            return
                                                inherited
                                                @ [ Ignores.ofText (fromBase rel) (Encoding.UTF8.GetString content) ]
                                        | _ -> return inherited
                                    | _ -> return inherited
                                }

                            let ignored (path: string) (isDirectory: bool) =
                                query.Ignores && Ignores.isIgnored rules (fromBase path) isDirectory

                            let subdirectories = ResizeArray<string * string>()
                            let mutable index = 0

                            while not stopped && index < entries.Length do
                                let entry = entries[index]
                                index <- index + 1
                                let path = relative rel entry.Name

                                match entry.Kind with
                                | EntryKind.Directory when
                                    entry.Name <> ".git"
                                    && visible entry.Name
                                    && not (excluded path)
                                    && not (ignored path true)
                                    ->
                                    subdirectories.Add(entry.Path, path)
                                | EntryKind.File when
                                    visible entry.Name
                                    && not (excluded path)
                                    && included path
                                    && not (ignored path false)
                                    ->
                                    match regex with
                                    | None ->
                                        if files.Count >= query.MaxResults then
                                            truncated <- true
                                            stopped <- true
                                        else
                                            files.Add path
                                    | Some regex when entry.Size <= query.MaxFileBytes ->
                                        match! read.ReadFile entry.Path cancellation with
                                        | Ok content when not (isBinary content) ->
                                            let text = Encoding.UTF8.GetString content
                                            let room = max 0 (query.MaxResults - matches.Count)
                                            // One more than there is room for: whether the limit cut matches.
                                            let found = matchLines regex path text (room + 1)

                                            let taken =
                                                if found.Length > room then
                                                    truncated <- true
                                                    List.take room found
                                                else
                                                    found

                                            let count =
                                                if query.CountAll then
                                                    countLines regex text
                                                else
                                                    taken.Length

                                            if count > 0 then
                                                if counts.Count >= SearchQuery.MaxCountedFiles then
                                                    countsComplete <- false
                                                    truncated <- true
                                                    stopped <- true
                                                else
                                                    counts.Add { Path = path; Matches = count }

                                            if not taken.IsEmpty then
                                                files.Add path
                                                matches.AddRange taken

                                            if truncated && not query.CountAll then
                                                stopped <- true
                                        | _ -> ()
                                    | Some _ -> ()
                                | _ -> ()

                            for i in subdirectories.Count - 1 .. -1 .. 0 do
                                let dir, path = subdirectories[i]
                                pending.Push(dir, path, rules)

                    match failure with
                    | Some e -> return Error e
                    | None ->
                        return
                            Ok
                                {
                                    Files = List.ofSeq files
                                    Matches = List.ofSeq matches
                                    Truncated = truncated
                                    Counts = List.ofSeq counts
                                    CountsComplete = countsComplete && not (truncated && not query.CountAll)
                                }
                }
