namespace Fx.Envd

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// `FsOps.Search` over any `FsRead`: a depth-first walk in name order that skips `.git` directories
/// and does not follow symlinks; binary files (a zero byte in the first 8000 bytes) and files larger
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
                    let mutable truncated = false
                    let mutable failure = None

                    let excluded (path: string) =
                        match excludes with
                        | Some m -> m.IsMatch path
                        | None -> false

                    let included (path: string) =
                        match includes with
                        | Some m -> m.IsMatch path
                        | None -> true

                    // Directories still to visit, as (absolute path, relative path); a stack, with
                    // children pushed in reverse so they come out in name order.
                    let pending = Stack<string * string>()
                    pending.Push(query.Root, "")

                    while failure.IsNone && not truncated && pending.Count > 0 do
                        cancellation.ThrowIfCancellationRequested()
                        let directory, rel = pending.Pop()

                        match! read.List directory cancellation with
                        | Error e when rel = "" -> failure <- Some(SearchFsError e)
                        | Error _ -> () // vanished or unreadable below the root: skip it
                        | Ok entries ->
                            let subdirectories = ResizeArray<string * string>()
                            let mutable index = 0
                            let entries = Array.ofList entries

                            while not truncated && index < entries.Length do
                                let entry = entries[index]
                                index <- index + 1
                                let path = relative rel entry.Name

                                match entry.Kind with
                                | EntryKind.Directory when entry.Name <> ".git" && not (excluded path) ->
                                    subdirectories.Add(entry.Path, path)
                                | EntryKind.File when not (excluded path) && included path ->
                                    match regex with
                                    | None ->
                                        if files.Count >= query.MaxResults then
                                            truncated <- true
                                        else
                                            files.Add path
                                    | Some regex when entry.Size <= query.MaxFileBytes ->
                                        match! read.ReadFile entry.Path cancellation with
                                        | Ok content when not (isBinary content) ->
                                            let text = Encoding.UTF8.GetString content
                                            let room = query.MaxResults - matches.Count
                                            let found = matchLines regex path text (room + 1)

                                            let taken =
                                                if found.Length > room then
                                                    truncated <- true
                                                    List.take room found
                                                else
                                                    found

                                            if not taken.IsEmpty then
                                                files.Add path
                                                matches.AddRange taken
                                        | _ -> ()
                                    | Some _ -> ()
                                | _ -> ()

                            for i in subdirectories.Count - 1 .. -1 .. 0 do
                                pending.Push subdirectories[i]

                    match failure with
                    | Some e -> return Error e
                    | None ->
                        return
                            Ok
                                {
                                    Files = List.ofSeq files
                                    Matches = List.ofSeq matches
                                    Truncated = truncated
                                }
                }
