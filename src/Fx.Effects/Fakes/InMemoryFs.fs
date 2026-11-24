namespace Fx.Effects.Fakes

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Fx.Effects

type private Node =
    | FileNode of content: byte array * modified: Instant
    | DirectoryNode of modified: Instant

/// A filesystem in memory, with the path syntax of the OS it runs on (`Path` functions): it passes
/// the same conformance suite as `LocalFs`. Roots (`/`, `C:\`) always exist. Times come from the
/// clock. Set `ignoreCase` to model Windows and macOS, where names compare case-insensitively.
[<Sealed>]
type InMemoryFs(clock: Clock, ignoreCase: bool) =
    let gate = obj ()
    let created = clock.Now()

    let comparer =
        if ignoreCase then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    // Normalized path (the key) -> the path as it was created, and the node.
    let nodes = Dictionary<string, struct (string * Node)>(comparer)

    let normalize (path: string) : Result<string, FsError> =
        if String.IsNullOrEmpty path || not (Path.IsPathFullyQualified path) then
            Error(InvalidPath(path, "not absolute"))
        else
            Ok(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, path)))

    let parentOf (path: string) : string option =
        match Path.GetDirectoryName path with
        | null
        | "" -> None
        | parent -> Some parent

    let isRoot path = (parentOf path).IsNone

    let tryNode (path: string) : struct (string * Node) voption =
        if isRoot path then
            ValueSome(struct (path, DirectoryNode created))
        else
            match nodes.TryGetValue path with
            | true, node -> ValueSome node
            | false, _ -> ValueNone

    let isDirectory path =
        match tryNode path with
        | ValueSome(struct (_, DirectoryNode _)) -> true
        | _ -> false

    let entryOf (struct (display: string, node: Node)) : Entry =
        let name =
            match Path.GetFileName display with
            | "" -> display
            | name -> name

        match node with
        | FileNode(content, modified) ->
            {
                Path = display
                Name = name
                Kind = EntryKind.File
                Size = int64 content.Length
                Modified = modified
            }
        | DirectoryNode modified ->
            {
                Path = display
                Name = name
                Kind = EntryKind.Directory
                Size = 0L
                Modified = modified
            }

    let touchParent (path: string) =
        match parentOf path with
        | Some parent when not (isRoot parent) ->
            match nodes.TryGetValue parent with
            | true, struct (display, DirectoryNode _) -> nodes[parent] <- struct (display, DirectoryNode(clock.Now()))
            | _ -> ()
        | _ -> ()

    let isUnder (ancestor: string) (path: string) =
        let prefix =
            if Path.EndsInDirectorySeparator ancestor then
                ancestor
            else
                ancestor + string Path.DirectorySeparatorChar

        path.StartsWith(
            prefix,
            if ignoreCase then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal
        )

    let operation (path: string) (run: string -> Result<'T, FsError>) : Task<Result<'T, FsError>> =
        let result =
            match normalize path with
            | Error e -> Error e
            | Ok full -> lock gate (fun () -> run full)

        Task.FromResult result

    let readFile path _ =
        operation path (fun full ->
            match tryNode full with
            | ValueSome(struct (_, FileNode(content, _))) -> Ok(Array.copy content)
            | ValueSome _ -> Error(IsADirectory path)
            | ValueNone -> Error(NotFound path))

    let stat path _ =
        operation path (fun full ->
            match tryNode full with
            | ValueSome node -> Ok(entryOf node)
            | ValueNone -> Error(NotFound path))

    let list path _ =
        operation path (fun full ->
            match tryNode full with
            | ValueSome(struct (_, DirectoryNode _)) ->
                nodes.Values
                |> Seq.filter (fun struct (display, _) ->
                    match parentOf display with
                    | Some parent -> comparer.Equals(parent, full)
                    | None -> false)
                |> Seq.map entryOf
                |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))
                |> List.ofSeq
                |> Ok
            | ValueSome _ -> Error(NotADirectory path)
            | ValueNone -> Error(NotFound path))

    let writeFile path (content: byte array) _ =
        operation path (fun full ->
            match tryNode full with
            | ValueSome(struct (_, DirectoryNode _)) -> Error(IsADirectory path)
            | _ ->
                match parentOf full with
                | Some parent when not (isDirectory parent) -> Error(NotFound path)
                | _ ->
                    let display =
                        match nodes.TryGetValue full with
                        | true, struct (display, _) -> display
                        | false, _ -> full

                    nodes[full] <- struct (display, FileNode(Array.copy content, clock.Now()))
                    touchParent full
                    Ok())

    let createDirectory path _ =
        operation path (fun full ->
            let rec create (dir: string) =
                match tryNode dir with
                | ValueSome(struct (_, DirectoryNode _)) -> Ok()
                | ValueSome _ -> Error(AlreadyExists dir)
                | ValueNone ->
                    let parent =
                        match parentOf dir with
                        | Some parent -> create parent
                        | None -> Ok()

                    match parent with
                    | Error e -> Error e
                    | Ok() ->
                        nodes[dir] <- struct (dir, DirectoryNode(clock.Now()))
                        touchParent dir
                        Ok()

            match create full with
            | Error(AlreadyExists dir) when comparer.Equals(dir, full) -> Error(AlreadyExists path)
            | Error(AlreadyExists dir) -> Error(NotADirectory dir)
            | result -> result)

    let delete path _ =
        operation path (fun full ->
            match tryNode full with
            | ValueNone -> Error(NotFound path)
            | ValueSome _ when isRoot full -> Error(AccessDenied path)
            | ValueSome(struct (_, DirectoryNode _)) when nodes.Keys |> Seq.exists (isUnder full) ->
                Error(DirectoryNotEmpty path)
            | ValueSome _ ->
                nodes.Remove full |> ignore
                touchParent full
                Ok())

    let move source destination _ =
        operation source (fun from ->
            match normalize destination with
            | Error e -> Error e
            | Ok target ->
                match tryNode from, tryNode target with
                | ValueNone, _ -> Error(NotFound source)
                | _ when isRoot from -> Error(AccessDenied source)
                | _, ValueSome _ -> Error(AlreadyExists destination)
                | _ when isUnder from target -> Error(InvalidPath(destination, "inside the source"))
                | _ when not (parentOf target |> Option.forall isDirectory) -> Error(NotFound destination)
                | ValueSome _, ValueNone ->
                    let moved =
                        nodes
                        |> Seq.filter (fun pair -> comparer.Equals(pair.Key, from) || isUnder from pair.Key)
                        |> Seq.map (fun pair -> pair.Key, pair.Value)
                        |> List.ofSeq

                    for key, struct (display, node) in moved do
                        nodes.Remove key |> ignore
                        let renamed = target + display.Substring from.Length
                        nodes[renamed] <- struct (renamed, node)

                    touchParent from
                    touchParent target
                    Ok())

    member val FsRead: FsRead =
        {
            ReadFile = readFile
            Stat = stat
            List = list
        }

    member val Fs: Fs =
        {
            WriteFile = writeFile
            CreateDirectory = createDirectory
            Delete = delete
            Move = move
        }

    /// Adds a directory and its parents, for setting up a test. Throws on failure.
    member _.AddDirectory(path: string) =
        match (createDirectory path Threading.CancellationToken.None).Result with
        | Error e -> invalidOp (string e)
        | Ok() -> ()

    /// Adds a file, creating its parent directories, for setting up a test. Throws on failure.
    member this.AddFile(path: string, content: byte array) =
        match Path.GetDirectoryName path with
        | null -> invalidArg (nameof path) "not a file path"
        | parent -> this.AddDirectory parent

        match (writeFile path content Threading.CancellationToken.None).Result with
        | Error e -> invalidOp (string e)
        | Ok() -> ()

    /// Adds a UTF-8 text file, like `AddFile`.
    member this.AddText(path: string, text: string) =
        this.AddFile(path, Text.UTF8Encoding(false).GetBytes text)
