namespace Fx.Envd

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Store

/// A piece of a file, read from `Offset`.
type FileChunk =
    {
        Offset: int64
        Content: byte array
        /// The size of the whole file when it was read.
        FileSize: int64
    }

    /// Whether the chunk reaches the end of the file.
    member this.AtEnd = this.Offset + int64 this.Content.Length >= this.FileSize

    override this.ToString() =
        $"{this.Content.Length} bytes at {this.Offset} of {this.FileSize}"

/// Replaces `Count` lines starting at line `Line` (1-based) with `Lines`. `Count = 0` inserts before
/// `Line`; `Line` = the line count + 1 appends. Lines are text without their line endings: replaced
/// lines get the file's line ending (that of its first line, else `\n`), untouched lines keep theirs.
type TextEdit =
    {
        Line: int
        Count: int
        Lines: string list
    }

    override this.ToString() =
        $"lines {this.Line}..{this.Line + this.Count - 1} -> {this.Lines.Length} lines"

/// A change to one file: the unit of `FsOps.ApplyPatch` (a structured edit list, not a unified diff,
/// so anchored edits (spec §6.8, T204) can build on it: ranges plus a digest that detects stale edits).
type FilePatch =
    /// Creates a text file (UTF-8); fails if anything exists at the path.
    | Create of path: string * text: string
    /// Edits a text file. The edits refer to the lines of the file as it is, must not overlap, and are
    /// applied together. With a digest, the file must still have that content (else `Stale`).
    | Edit of path: string * digest: BlobHash option * edits: TextEdit list
    /// Deletes a file; with a digest, only if it still has that content.
    | Delete of path: string * digest: BlobHash option

    member this.Path =
        match this with
        | Create(path, _)
        | Edit(path, _, _)
        | Delete(path, _) -> path

    override this.ToString() =
        match this with
        | Create(path, text) -> $"create {path} ({text.Length} chars)"
        | Edit(path, _, edits) -> $"edit {path} ({edits.Length} edits)"
        | Delete(path, _) -> $"delete {path}"

/// What a patch did to one file.
type PatchedFile =
    {
        Path: string
        /// The SHA-256 of the new content; `None` once deleted.
        Digest: BlobHash option
        /// The number of lines of the new content (0 once deleted).
        Lines: int
    }

    override this.ToString() =
        match this.Digest with
        | Some digest -> $"{this.Path}: {this.Lines} lines, {digest}"
        | None -> $"{this.Path}: deleted"

/// Why a patch was not applied. Nothing was written: every file is checked before any is changed.
type PatchError =
    /// The file no longer has the content the patch was made for.
    | Stale of path: string * expected: BlobHash * actual: BlobHash
    /// The edits do not fit the file: out of range or overlapping.
    | InvalidEdit of path: string * reason: string
    /// The file is not UTF-8 text.
    | NotText of path: string
    | PatchFsError of FsError

    override this.ToString() =
        match this with
        | Stale(path, expected, actual) -> $"stale: {path} has changed (expected {expected}, found {actual})"
        | InvalidEdit(path, reason) -> $"invalid edit of {path}: {reason}"
        | NotText path -> $"not UTF-8 text: {path}"
        | PatchFsError e -> string e

/// What to search: files under `Root` whose relative path (with `/`) matches a glob, and, with a
/// pattern, their lines that match a regular expression. Basic semantics; ripgrep's are T202's.
type SearchQuery =
    {
        /// The directory to search (absolute).
        Root: string
        /// Globs a file must match (any of them); none: every file. A glob without `/` matches the
        /// file name at any depth. `*`, `**`, `?`, `[abc]`, `[!abc]` and `{a,b}` work as in git.
        Include: string list
        /// Globs that exclude files and directories (matched against their relative path and name).
        Exclude: string list
        /// A regular expression (.NET syntax, linear-time engine: no backreferences or lookaround);
        /// `None` lists the matching files only.
        Pattern: string option
        IgnoreCase: bool
        /// At most this many matches (or files, without a pattern); then `Truncated`.
        MaxResults: int
        /// Files larger than this are not searched for the pattern.
        MaxFileBytes: int64
    }

    override this.ToString() =
        let pattern = this.Pattern |> Option.defaultValue "<files>"
        $"search {pattern} in {this.Root}"

module SearchQuery =
    /// Every file under `root`, at most 1000 results, files up to 4 MiB.
    let files (root: string) : SearchQuery =
        {
            Root = root
            Include = []
            Exclude = []
            Pattern = None
            IgnoreCase = false
            MaxResults = 1000
            MaxFileBytes = 4L * 1024L * 1024L
        }

    /// Lines matching `pattern` in files under `root`.
    let lines (root: string) (pattern: string) : SearchQuery =
        { files root with
            Pattern = Some pattern
        }

/// A line that matched.
type SearchMatch =
    {
        /// The file, relative to the root, with `/` separators.
        Path: string
        /// 1-based line number.
        Line: int
        /// 1-based column (UTF-16 code units) of the match's start.
        Column: int
        /// The line, cut to at most 500 characters.
        Text: string
    }

    override this.ToString() =
        $"{this.Path}:{this.Line}:{this.Column}: {this.Text}"

type SearchResult =
    {
        /// The files that matched (relative, `/` separators), in walk order (sorted by name).
        Files: string list
        /// The matching lines, in file and line order; empty without a pattern.
        Matches: SearchMatch list
        /// Whether a limit cut the results short.
        Truncated: bool
    }

    override this.ToString() =
        $"{this.Files.Length} files, {this.Matches.Length} matches (truncated: {this.Truncated})"

type SearchError =
    /// The glob or pattern is invalid.
    | InvalidQuery of reason: string
    | SearchFsError of FsError

    override this.ToString() =
        match this with
        | InvalidQuery reason -> $"invalid query: {reason}"
        | SearchFsError e -> string e

/// What happened to a path.
[<RequireQualifiedAccess>]
type ChangeKind =
    | Added
    | Modified
    | Deleted

    override this.ToString() =
        match this with
        | Added -> "added"
        | Modified -> "modified"
        | Deleted -> "deleted"

/// A change to a file or directory: in a watch batch (absolute path) or a snapshot diff (path
/// relative to the workspace, with `/`).
type FileChange =
    {
        Path: string
        Kind: ChangeKind
    }

    override this.ToString() = $"{this.Kind} {this.Path}"

/// What to watch.
type WatchSpec =
    {
        /// A directory (absolute).
        Root: string
        Recursive: bool
        /// Changes are collected for this long after the first one, then delivered as one batch.
        Debounce: TimeSpan
    }

    override this.ToString() =
        $"watch {this.Root} (recursive: {this.Recursive}, debounce {this.Debounce.TotalMilliseconds} ms)"

/// Debounced changes. Renames are a deletion and an addition.
type WatchBatch =
    {
        /// Coalesced per path, in the order the paths first changed.
        Changes: FileChange list
        /// Some changes were lost (the OS's or the watcher's buffer overflowed): rescan.
        Overflow: bool
    }

    override this.ToString() =
        $"{this.Changes.Length} changes (overflow: {this.Overflow})"

/// A running watch, a record of functions like `Proc`.
type Watcher =
    {
        /// The next batch; `None` once the watcher is closed.
        Next: CancellationToken -> Task<WatchBatch option>
        /// Stops watching. Idempotent.
        Close: unit -> Task<unit>
    }

    override _.ToString() = "Watcher"

/// envd's filesystem operations beyond `FsRead`/`Fs` (spec §3: batch stat, read ranges, atomic
/// writes, apply-patch, search, watch). Same path rules as `FsRead`: absolute, in the environment's
/// syntax.
///
/// Interpreters: `FsOps.over` (generic, over any `FsRead`/`Fs`; writes are not atomic and watching
/// is not supported, as for `InMemoryFs`), `LocalFsOps` (Fx.Envd.Live) and the envd proxy
/// (`EnvClient`). All pass `FsOpsConformance`.
type FsOps =
    {
        /// The entries at several paths, one result per path, in order.
        StatMany: string list -> CancellationToken -> Task<Result<Entry, FsError> list>
        /// At most `count` bytes of a file from `offset`: fewer at the end, none past it. With
        /// `FileSize`, this also reads a file up to a size limit.
        ReadRange: string -> int64 -> int -> CancellationToken -> Task<Result<FileChunk, FsError>>
        /// Creates or replaces a file so that readers see the old content or the new, never a mix (a
        /// temporary file in the same directory, renamed over it); keeps the file's mode on Unix.
        WriteAtomic: string -> byte array -> CancellationToken -> Task<Result<unit, FsError>>
        /// Applies patches to files: all are checked first, then written (each atomically).
        ApplyPatch: FilePatch list -> CancellationToken -> Task<Result<PatchedFile list, PatchError>>
        /// Finds files by glob and lines by regular expression, within limits.
        Search: SearchQuery -> CancellationToken -> Task<Result<SearchResult, SearchError>>
        /// Watches a directory. The caller must `Close` the watcher.
        Watch: WatchSpec -> CancellationToken -> Task<Result<Watcher, FsError>>
    }

    override _.ToString() = "FsOps"

/// An environment with envd's filesystem operations.
type IFsOps =
    abstract FsOps: FsOps

module FsOps =
    let statMany<'R when 'R :> IFsOps and 'R :> IScope> (paths: string list) : Eff<'R, Result<Entry, FsError> list> =
        fun env -> env.FsOps.StatMany paths env.Scope.Cancellation

    let readRange<'R when 'R :> IFsOps and 'R :> IScope>
        (path: string)
        (offset: int64)
        (count: int)
        : Eff<'R, Result<FileChunk, FsError>> =
        fun env -> env.FsOps.ReadRange path offset count env.Scope.Cancellation

    let writeAtomic<'R when 'R :> IFsOps and 'R :> IScope>
        (path: string)
        (content: byte array)
        : Eff<'R, Result<unit, FsError>> =
        fun env -> env.FsOps.WriteAtomic path content env.Scope.Cancellation

    let applyPatch<'R when 'R :> IFsOps and 'R :> IScope>
        (patches: FilePatch list)
        : Eff<'R, Result<PatchedFile list, PatchError>> =
        fun env -> env.FsOps.ApplyPatch patches env.Scope.Cancellation

    let search<'R when 'R :> IFsOps and 'R :> IScope>
        (query: SearchQuery)
        : Eff<'R, Result<SearchResult, SearchError>> =
        fun env -> env.FsOps.Search query env.Scope.Cancellation

    let watch<'R when 'R :> IFsOps and 'R :> IScope> (spec: WatchSpec) : Eff<'R, Result<Watcher, FsError>> =
        fun env -> env.FsOps.Watch spec env.Scope.Cancellation

    /// Checks the arguments of a ranged read; interpreters call it first.
    let checkRange (offset: int64) (count: int) =
        if offset < 0L then
            invalidArg (nameof offset) "The offset must not be negative."

        if count < 0 then
            invalidArg (nameof count) "The count must not be negative."

    /// The read-only attenuation: writes and patches fail with `AccessDenied`.
    let readOnly (ops: FsOps) : FsOps =
        { ops with
            WriteAtomic = fun path _ _ -> Task.FromResult(Error(AccessDenied path))
            ApplyPatch =
                fun patches _ ->
                    let path =
                        match patches with
                        | first :: _ -> first.Path
                        | [] -> ""

                    Task.FromResult(Error(PatchFsError(AccessDenied path)))
        }
