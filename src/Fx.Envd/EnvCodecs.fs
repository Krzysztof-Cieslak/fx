namespace Fx.Envd

open System
open Fx.Contracts
open Fx.Effects
open Fx.Pty
open Fx.Store

/// Codecs of the capability types that cross the environment protocol.
[<RequireQualifiedAccess>]
module EnvCodecs =
    /// Bytes as base64 text.
    let bytes: Codec<byte array> =
        Codec.string
        |> Codec.tryMap
            (fun text ->
                let buffer = Array.zeroCreate<byte>((text.Length * 3) / 4 + 3)
                let mutable written = 0

                if Convert.TryFromBase64String(text, Span buffer, &written) then
                    Ok(Array.sub buffer 0 written)
                else
                    Error "expected base64")
            Convert.ToBase64String
        |> Codec.describe "Bytes, base64-encoded."

    let shellSpec: Codec<ShellSpec> =
        Codec.record {
            let! program = Codec.field "program" (fun (s: ShellSpec) -> s.Program) Codec.string
            and! args = Codec.field "args" (fun s -> s.Args) (Codec.list Codec.string)
            return { Program = program; Args = args }
        }

    let platform: Codec<Platform> =
        Codec.record {
            let! os = Codec.field "os" (fun (p: Platform) -> p.Os) Codec.string
            and! arch = Codec.field "arch" (fun p -> p.Arch) Codec.string
            and! libc = Codec.optionalField "libc" (fun p -> p.Libc) Codec.string
            and! shell = Codec.field "shell" (fun p -> p.Shell) shellSpec
            and! separator = Codec.field "pathSeparator" (fun p -> p.PathSeparator) Codec.string

            and! caseSensitive =
                Codec.field "caseSensitive" (fun p -> p.CaseSensitive) Codec.bool

            and! workspace = Codec.field "workspace" (fun p -> p.Workspace) Codec.string
            and! version = Codec.field "version" (fun p -> p.Version) Codec.string

            and! features =
                Codec.field "features" (fun p -> p.Features) (Codec.list Codec.string)

            return
                {
                    Os = os
                    Arch = arch
                    Libc = libc
                    Shell = shell
                    PathSeparator = separator
                    CaseSensitive = caseSensitive
                    Workspace = workspace
                    Version = version
                    Features = features
                }
        }
        |> Codec.named "Platform"

    // ---- Files ----------------------------------------------------------------------------------

    let entryKind: Codec<EntryKind> =
        Codec.enum
            [
                EntryKind.File, "file"
                EntryKind.Directory, "directory"
                EntryKind.Symlink, "symlink"
                EntryKind.Other, "other"
            ]

    let entry: Codec<Entry> =
        Codec.record {
            let! path = Codec.field "path" (fun (e: Entry) -> e.Path) Codec.string
            and! name = Codec.field "name" (fun e -> e.Name) Codec.string
            and! kind = Codec.field "kind" (fun e -> e.Kind) entryKind
            and! size = Codec.field "size" (fun e -> e.Size) Codec.int64
            and! modified = Codec.field "modified" (fun e -> e.Modified) Fx.Store.Codecs.instant

            return
                {
                    Path = path
                    Name = name
                    Kind = kind
                    Size = size
                    Modified = modified
                }
        }
        |> Codec.named "Entry"

    type private FsErrorWire =
        {
            Kind: string
            Path: string
            Detail: string option
        }

    let private fsErrorKinds =
        [
            "notFound"
            "alreadyExists"
            "notADirectory"
            "isADirectory"
            "directoryNotEmpty"
            "accessDenied"
            "invalidPath"
            "ioFailed"
        ]

    /// `{"kind": "notFound", "path": "…", "detail": "…"}`: one flat object, not a union codec (eight
    /// cases are more than `Codec.union` takes under Native AOT).
    let fsError: Codec<FsError> =
        Codec.record {
            let! kind =
                Codec.field
                    "kind"
                    (fun (w: FsErrorWire) -> w.Kind)
                    (Codec.enum (fsErrorKinds |> List.map (fun k -> k, k)))

            and! path = Codec.field "path" (fun w -> w.Path) Codec.string
            and! detail = Codec.optionalField "detail" (fun w -> w.Detail) Codec.string

            return
                {
                    Kind = kind
                    Path = path
                    Detail = detail
                }
        }
        |> Codec.map
            (fun w ->
                let detail = w.Detail |> Option.defaultValue ""

                match w.Kind with
                | "notFound" -> NotFound w.Path
                | "alreadyExists" -> AlreadyExists w.Path
                | "notADirectory" -> NotADirectory w.Path
                | "isADirectory" -> IsADirectory w.Path
                | "directoryNotEmpty" -> DirectoryNotEmpty w.Path
                | "accessDenied" -> AccessDenied w.Path
                | "invalidPath" -> InvalidPath(w.Path, detail)
                | _ -> IoFailed(w.Path, detail))
            (fun error ->
                let wire kind path detail =
                    {
                        Kind = kind
                        Path = path
                        Detail = detail
                    }

                match error with
                | NotFound path -> wire "notFound" path None
                | AlreadyExists path -> wire "alreadyExists" path None
                | NotADirectory path -> wire "notADirectory" path None
                | IsADirectory path -> wire "isADirectory" path None
                | DirectoryNotEmpty path -> wire "directoryNotEmpty" path None
                | AccessDenied path -> wire "accessDenied" path None
                | InvalidPath(path, reason) -> wire "invalidPath" path (Some reason)
                | IoFailed(path, message) -> wire "ioFailed" path (Some message))
        |> Codec.named "FsError"

    /// An entry or why there is none: `{"entry": …}` or `{"error": …}`.
    let entryResult: Codec<Result<Entry, FsError>> =
        Codec.record {
            let! found =
                Codec.optionalField
                    "entry"
                    (fun (r: Result<Entry, FsError>) ->
                        match r with
                        | Ok e -> Some e
                        | Error _ -> None)
                    entry

            and! failed =
                Codec.optionalField
                    "error"
                    (fun r ->
                        match r with
                        | Error e -> Some e
                        | Ok _ -> None)
                    fsError

            return
                match found, failed with
                | Some e, _ -> Ok e
                | None, Some e -> Error e
                | None, None -> Error(IoFailed("", "neither an entry nor an error"))
        }

    let blobHash = Fx.Store.Codecs.blobHash
    let blobRef = Fx.Store.Codecs.blobRef

    let textEdit: Codec<TextEdit> =
        Codec.record {
            let! line = Codec.field "line" (fun (e: TextEdit) -> e.Line) Codec.int
            and! count = Codec.field "count" (fun e -> e.Count) Codec.int
            and! lines = Codec.field "lines" (fun e -> e.Lines) (Codec.list Codec.string)

            return
                {
                    Line = line
                    Count = count
                    Lines = lines
                }
        }
        |> Codec.named "TextEdit"

    let filePatch: Codec<FilePatch> =
        Codec.union "op" (fun create edit delete value ->
            match value with
            | Create(path, text) -> create path text
            | Edit(path, digest, edits) -> edit path digest edits
            | Delete(path, digest) -> delete path digest)
        |> Codec.case2
            "create"
            (fun path text -> Create(path, text))
            (Field.required "path" Codec.string)
            (Field.required "text" Codec.string)
        |> Codec.case3
            "edit"
            (fun path digest edits -> Edit(path, digest, edits))
            (Field.required "path" Codec.string)
            (Field.optional "digest" blobHash)
            (Field.required "edits" (Codec.list textEdit))
        |> Codec.case2
            "delete"
            (fun path digest -> Delete(path, digest))
            (Field.required "path" Codec.string)
            (Field.optional "digest" blobHash)
        |> Codec.buildUnion
        |> Codec.named "FilePatch"

    let patchedFile: Codec<PatchedFile> =
        Codec.record {
            let! path = Codec.field "path" (fun (f: PatchedFile) -> f.Path) Codec.string
            and! digest = Codec.optionalField "digest" (fun f -> f.Digest) blobHash
            and! lines = Codec.field "lines" (fun f -> f.Lines) Codec.int

            return
                {
                    Path = path
                    Digest = digest
                    Lines = lines
                }
        }

    let patchError: Codec<PatchError> =
        Codec.union "kind" (fun stale invalid notText fs value ->
            match value with
            | Stale(path, expected, actual) -> stale path expected actual
            | InvalidEdit(path, reason) -> invalid path reason
            | NotText path -> notText path
            | PatchFsError e -> fs e)
        |> Codec.case3
            "stale"
            (fun path expected actual -> Stale(path, expected, actual))
            (Field.required "path" Codec.string)
            (Field.required "expected" blobHash)
            (Field.required "actual" blobHash)
        |> Codec.case2
            "invalidEdit"
            (fun path reason -> InvalidEdit(path, reason))
            (Field.required "path" Codec.string)
            (Field.required "reason" Codec.string)
        |> Codec.case1 "notText" NotText (Field.required "path" Codec.string)
        |> Codec.case1 "fs" PatchFsError (Field.required "error" fsError)
        |> Codec.buildUnion

    let searchQuery: Codec<SearchQuery> =
        Codec.record {
            let! root = Codec.field "root" (fun (q: SearchQuery) -> q.Root) Codec.string

            and! includes =
                Codec.defaultField "include" (fun q -> q.Include) [] (Codec.list Codec.string)

            and! excludes =
                Codec.defaultField "exclude" (fun q -> q.Exclude) [] (Codec.list Codec.string)

            and! pattern = Codec.optionalField "pattern" (fun q -> q.Pattern) Codec.string

            and! ignoreCase =
                Codec.defaultField "ignoreCase" (fun q -> q.IgnoreCase) false Codec.bool

            and! maxResults = Codec.field "maxResults" (fun q -> q.MaxResults) Codec.int
            and! maxFileBytes = Codec.field "maxFileBytes" (fun q -> q.MaxFileBytes) Codec.int64

            return
                {
                    Root = root
                    Include = includes
                    Exclude = excludes
                    Pattern = pattern
                    IgnoreCase = ignoreCase
                    MaxResults = maxResults
                    MaxFileBytes = maxFileBytes
                }
        }

    let searchMatch: Codec<SearchMatch> =
        Codec.record {
            let! path = Codec.field "path" (fun (m: SearchMatch) -> m.Path) Codec.string
            and! line = Codec.field "line" (fun m -> m.Line) Codec.int
            and! column = Codec.field "column" (fun m -> m.Column) Codec.int
            and! text = Codec.field "text" (fun m -> m.Text) Codec.string

            return
                {
                    Path = path
                    Line = line
                    Column = column
                    Text = text
                }
        }

    let searchResult: Codec<SearchResult> =
        Codec.record {
            let! files =
                Codec.field "files" (fun (r: SearchResult) -> r.Files) (Codec.list Codec.string)

            and! matches = Codec.field "matches" (fun r -> r.Matches) (Codec.list searchMatch)
            and! truncated = Codec.field "truncated" (fun r -> r.Truncated) Codec.bool

            return
                {
                    Files = files
                    Matches = matches
                    Truncated = truncated
                }
        }

    let searchError: Codec<SearchError> =
        Codec.union "kind" (fun invalid fs value ->
            match value with
            | InvalidQuery reason -> invalid reason
            | SearchFsError e -> fs e)
        |> Codec.case1 "invalidQuery" InvalidQuery (Field.required "reason" Codec.string)
        |> Codec.case1 "fs" SearchFsError (Field.required "error" fsError)
        |> Codec.buildUnion

    let changeKind: Codec<ChangeKind> =
        Codec.enum
            [
                ChangeKind.Added, "added"
                ChangeKind.Modified, "modified"
                ChangeKind.Deleted, "deleted"
            ]

    let fileChange: Codec<FileChange> =
        Codec.record {
            let! path = Codec.field "path" (fun (c: FileChange) -> c.Path) Codec.string
            and! kind = Codec.field "kind" (fun c -> c.Kind) changeKind
            return { Path = path; Kind = kind }
        }

    // ---- Processes ------------------------------------------------------------------------------

    let program: Codec<Program> =
        Codec.union "kind" (fun exec script value ->
            match value with
            | Exec(file, args) -> exec file args
            | Script text -> script text)
        |> Codec.case2
            "exec"
            (fun file args -> Exec(file, args))
            (Field.required "file" Codec.string)
            (Field.withDefault "args" [] (Codec.list Codec.string))
        |> Codec.case1 "script" Script (Field.required "script" Codec.string)
        |> Codec.buildUnion

    let private variable: Codec<string * string> =
        Codec.record {
            let! name = Codec.field "name" (fun (n: string, _: string) -> n) Codec.string
            and! value = Codec.field "value" snd Codec.string
            return name, value
        }

    let private change: Codec<string * string option> =
        Codec.record {
            let! name = Codec.field "name" (fun (n: string, _: string option) -> n) Codec.string
            and! value = Codec.optionalField "value" snd Codec.string
            return name, value
        }

    let envVars: Codec<EnvVars> =
        Codec.union "mode" (fun inherits exactly value ->
            match value with
            | Inherit changes -> inherits changes
            | Exactly vars -> exactly vars)
        |> Codec.case1 "inherit" Inherit (Field.withDefault "changes" [] (Codec.list change))
        |> Codec.case1 "exactly" Exactly (Field.required "vars" (Codec.list variable))
        |> Codec.buildUnion

    let ptySize: Codec<PtySize> =
        Codec.record {
            let! cols = Codec.field "cols" (fun (s: PtySize) -> s.Cols) Codec.int
            and! rows = Codec.field "rows" (fun s -> s.Rows) Codec.int
            return { Cols = cols; Rows = rows }
        }

    let logStatus: Codec<LogStatus> =
        Codec.record {
            let! total = Codec.field "total" (fun (s: LogStatus) -> s.Total) Codec.int64
            and! available = Codec.field "available" (fun s -> s.Available) Codec.int64
            and! spill = Codec.optionalField "spill" (fun s -> s.SpillPath) Codec.string
            and! ended = Codec.field "ended" (fun s -> s.Ended) Codec.bool

            return
                {
                    Total = total
                    Available = available
                    SpillPath = spill
                    Ended = ended
                }
        }

    // ---- Blobs and snapshots --------------------------------------------------------------------

    let blobError: Codec<BlobError> =
        Codec.union "kind" (fun notFound corrupt value ->
            match value with
            | BlobError.NotFound hash -> notFound hash
            | BlobError.Corrupt hash -> corrupt hash)
        |> Codec.case1 "notFound" BlobError.NotFound (Field.required "hash" blobHash)
        |> Codec.case1 "corrupt" BlobError.Corrupt (Field.required "hash" blobHash)
        |> Codec.buildUnion

    let snapshotRef: Codec<SnapshotRef> =
        Codec.record {
            let! commit = Codec.field "commit" (fun (s: SnapshotRef) -> s.Commit) Codec.string
            and! tree = Codec.field "tree" (fun s -> s.Tree) Codec.string
            return { Commit = commit; Tree = tree }
        }
        |> Codec.named "SnapshotRef"

    let snapshotError: Codec<SnapshotError> =
        Codec.union "kind" (fun unavailable failed value ->
            match value with
            | Unavailable reason -> unavailable reason
            | GitFailed(command, code, stderr) -> failed command code stderr)
        |> Codec.case1 "unavailable" Unavailable (Field.required "reason" Codec.string)
        |> Codec.case3
            "gitFailed"
            (fun command code stderr -> GitFailed(command, code, stderr))
            (Field.required "command" Codec.string)
            (Field.required "exitCode" Codec.int)
            (Field.required "stderr" Codec.string)
        |> Codec.buildUnion
