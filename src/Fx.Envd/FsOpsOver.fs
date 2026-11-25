namespace Fx.Envd

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Store

/// `FsOps` built from the basic capabilities (`FsRead`, `Fs`): the generic parts every interpreter
/// shares (batch stat, patches, search), and a complete interpreter for fakes such as `InMemoryFs`.
module FsOpsOver =
    /// One `Stat` per path, in order.
    let statMany (read: FsRead) (paths: string list) (cancellation: CancellationToken) =
        task {
            let results = ResizeArray()

            for path in paths do
                let! entry = read.Stat path cancellation
                results.Add entry

            return List.ofSeq results
        }

    /// A range of a file read whole, then cut (for interpreters that cannot seek).
    let readRange (read: FsRead) (path: string) (offset: int64) (count: int) (cancellation: CancellationToken) =
        FsOps.checkRange offset count

        task {
            match! read.ReadFile path cancellation with
            | Error e -> return Error e
            | Ok content ->
                let size = content.LongLength
                let start = min offset size
                let length = int (min (int64 count) (size - start))

                return
                    Ok
                        {
                            Offset = offset
                            Content = Array.sub content (int start) length
                            FileSize = size
                        }
        }

    /// The SHA-256 of some content, as patches name file versions.
    let digest (content: byte array) : BlobHash = BlobHash.ofBytes content

    /// What one checked patch will write.
    type private Planned =
        | Write of path: string * content: byte array * lines: int
        | Remove of path: string

    /// Applies patches: reads and checks every file first, then writes them with `writeAtomic` and
    /// deletes with `delete`. If a write fails midway, the files before it stay changed.
    let applyPatch
        (read: FsRead)
        (writeAtomic: string -> byte array -> CancellationToken -> Task<Result<unit, FsError>>)
        (delete: string -> CancellationToken -> Task<Result<unit, FsError>>)
        (patches: FilePatch list)
        (cancellation: CancellationToken)
        : Task<Result<PatchedFile list, PatchError>> =
        task {
            let planned = ResizeArray<Planned>()
            let mutable failure = None
            let mutable remaining = patches

            let checkDigest (path: string) (expected: BlobHash option) (content: byte array) =
                match expected with
                | Some expected when expected <> digest content -> Some(Stale(path, expected, digest content))
                | _ -> None

            while failure.IsNone && not remaining.IsEmpty do
                let patch = remaining.Head
                remaining <- remaining.Tail

                match patch with
                | Create(path, text) ->
                    match! read.Stat path cancellation with
                    | Ok _ -> failure <- Some(PatchFsError(AlreadyExists path))
                    | Error(NotFound _) ->
                        let content = Text.UTF8Encoding(false).GetBytes text
                        planned.Add(Write(path, content, TextPatch.lineCount text))
                    | Error e -> failure <- Some(PatchFsError e)
                | Edit(path, expected, edits) ->
                    match! read.ReadFile path cancellation with
                    | Error e -> failure <- Some(PatchFsError e)
                    | Ok content ->
                        match checkDigest path expected content with
                        | Some stale -> failure <- Some stale
                        | None ->
                            match TextPatch.apply content edits with
                            | Ok(edited, lines) -> planned.Add(Write(path, edited, lines))
                            | Error(Choice1Of2()) -> failure <- Some(NotText path)
                            | Error(Choice2Of2 reason) -> failure <- Some(InvalidEdit(path, reason))
                | Delete(path, expected) ->
                    match expected with
                    | None ->
                        match! read.Stat path cancellation with
                        | Ok entry when entry.Kind = EntryKind.Directory ->
                            failure <- Some(PatchFsError(IsADirectory path))
                        | Ok _ -> planned.Add(Remove path)
                        | Error e -> failure <- Some(PatchFsError e)
                    | Some _ ->
                        match! read.ReadFile path cancellation with
                        | Error e -> failure <- Some(PatchFsError e)
                        | Ok content ->
                            match checkDigest path expected content with
                            | Some stale -> failure <- Some stale
                            | None -> planned.Add(Remove path)

            match failure with
            | Some e -> return Error e
            | None ->
                let results = ResizeArray<PatchedFile>()
                let mutable index = 0

                while failure.IsNone && index < planned.Count do
                    let step = planned[index]
                    index <- index + 1

                    match step with
                    | Write(path, content, lines) ->
                        match! writeAtomic path content cancellation with
                        | Ok() ->
                            results.Add
                                {
                                    Path = path
                                    Digest = Some(digest content)
                                    Lines = lines
                                }
                        | Error e -> failure <- Some(PatchFsError e)
                    | Remove path ->
                        match! delete path cancellation with
                        | Ok() ->
                            results.Add
                                {
                                    Path = path
                                    Digest = None
                                    Lines = 0
                                }
                        | Error e -> failure <- Some(PatchFsError e)

                match failure with
                | Some e -> return Error e
                | None -> return Ok(List.ofSeq results)
        }

    /// A watch that the interpreter does not support.
    let unsupportedWatch (spec: WatchSpec) (_: CancellationToken) : Task<Result<Watcher, FsError>> =
        Task.FromResult(Error(InvalidPath(spec.Root, "watching is not supported here")))

    /// A complete interpreter over the basic capabilities. Writes replace files with `Fs.WriteFile`
    /// (atomic only if that is, as in `InMemoryFs`), and watching is not supported.
    let create (read: FsRead) (fs: Fs) : FsOps =
        {
            StatMany = statMany read
            ReadRange = readRange read
            WriteAtomic = fs.WriteFile
            ApplyPatch = applyPatch read fs.WriteFile fs.Delete
            Search = Search.run read
            Watch = unsupportedWatch
        }
