namespace Fx.Effects

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// An expected filesystem failure. Defects (e.g. a disk error mid-write) are exceptions.
type FsError =
    | NotFound of path: string
    | AlreadyExists of path: string
    | NotADirectory of path: string
    | IsADirectory of path: string
    | DirectoryNotEmpty of path: string
    | AccessDenied of path: string
    /// The path is relative, malformed, or outside what the interpreter allows.
    | InvalidPath of path: string * reason: string
    /// Any other I/O failure; the message is for display only.
    | IoFailed of path: string * message: string

    override this.ToString() =
        match this with
        | NotFound path -> $"not found: {path}"
        | AlreadyExists path -> $"already exists: {path}"
        | NotADirectory path -> $"not a directory: {path}"
        | IsADirectory path -> $"is a directory: {path}"
        | DirectoryNotEmpty path -> $"directory not empty: {path}"
        | AccessDenied path -> $"access denied: {path}"
        | InvalidPath(path, reason) -> $"invalid path ({reason}): {path}"
        | IoFailed(path, message) -> $"I/O failed: {path}: {message}"

[<RequireQualifiedAccess>]
type EntryKind =
    | File
    | Directory
    /// A symbolic link (or Windows reparse point); it is not followed.
    | Symlink
    | Other

    override this.ToString() =
        match this with
        | File -> "file"
        | Directory -> "directory"
        | Symlink -> "symlink"
        | Other -> "other"

/// What `Stat` and `List` report about a filesystem entry.
type Entry =
    {
        /// The full path.
        Path: string
        /// The last path segment.
        Name: string
        Kind: EntryKind
        /// The size in bytes of a file; 0 for anything else.
        Size: int64
        /// The last write time, as the filesystem records it.
        Modified: Instant
    }

    override this.ToString() =
        $"{this.Kind} {this.Path} ({this.Size} bytes, modified {this.Modified})"

/// Reading the filesystem. Paths are absolute, in the syntax of the machine the interpreter runs on
/// (the environment's, not necessarily the host's): relative paths fail with `InvalidPath`.
type FsRead =
    {
        /// The whole content of a file.
        ReadFile: string -> CancellationToken -> Task<Result<byte array, FsError>>
        /// The entry at a path, without following a final symlink.
        Stat: string -> CancellationToken -> Task<Result<Entry, FsError>>
        /// The entries of a directory, sorted by name (ordinal).
        List: string -> CancellationToken -> Task<Result<Entry list, FsError>>
    }

    override _.ToString() = "FsRead"

/// Changing the filesystem. Same path rules as `FsRead`.
type Fs =
    {
        /// Creates or replaces a file. The parent directory must exist.
        WriteFile: string -> byte array -> CancellationToken -> Task<Result<unit, FsError>>
        /// Creates a directory and any missing parents; succeeds if it already exists.
        CreateDirectory: string -> CancellationToken -> Task<Result<unit, FsError>>
        /// Deletes a file, or an empty directory.
        Delete: string -> CancellationToken -> Task<Result<unit, FsError>>
        /// Moves (renames) a file or directory. Fails if the destination exists.
        Move: string -> string -> CancellationToken -> Task<Result<unit, FsError>>
    }

    override _.ToString() = "Fs"

/// An environment that can read the filesystem.
type IFsRead =
    abstract FsRead: FsRead

/// An environment that can read and change the filesystem. An environment exposing only `IFsRead`
/// is the read-only attenuation of one exposing `IFs`.
type IFs =
    inherit IFsRead
    abstract Fs: Fs

module Fs =
    let private utf8 = UTF8Encoding(false)

    let readFile<'R when 'R :> IFsRead and 'R :> IScope> (path: string) : Eff<'R, Result<byte array, FsError>> =
        fun env -> env.FsRead.ReadFile path env.Scope.Cancellation

    /// A file as UTF-8 text (a byte order mark is dropped; line endings are kept as they are).
    let readText (path: string) : Eff<'R, Result<string, FsError>> =
        readFile path
        |> EffResult.map (fun bytes ->
            let skip =
                if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
                    3
                else
                    0

            utf8.GetString(bytes, skip, bytes.Length - skip))

    let stat<'R when 'R :> IFsRead and 'R :> IScope> (path: string) : Eff<'R, Result<Entry, FsError>> =
        fun env -> env.FsRead.Stat path env.Scope.Cancellation

    /// Whether anything exists at the path. Other failures stay errors.
    let exists (path: string) : Eff<'R, Result<bool, FsError>> =
        stat path
        |> Eff.map (function
            | Ok _ -> Ok true
            | Error(NotFound _) -> Ok false
            | Error e -> Error e)

    let list<'R when 'R :> IFsRead and 'R :> IScope> (path: string) : Eff<'R, Result<Entry list, FsError>> =
        fun env -> env.FsRead.List path env.Scope.Cancellation

    let writeFile<'R when 'R :> IFs and 'R :> IScope>
        (path: string)
        (content: byte array)
        : Eff<'R, Result<unit, FsError>> =
        fun env -> env.Fs.WriteFile path content env.Scope.Cancellation

    /// Writes UTF-8 text without a byte order mark.
    let writeText (path: string) (text: string) : Eff<'R, Result<unit, FsError>> = writeFile path (utf8.GetBytes text)

    let createDirectory<'R when 'R :> IFs and 'R :> IScope> (path: string) : Eff<'R, Result<unit, FsError>> =
        fun env -> env.Fs.CreateDirectory path env.Scope.Cancellation

    let delete<'R when 'R :> IFs and 'R :> IScope> (path: string) : Eff<'R, Result<unit, FsError>> =
        fun env -> env.Fs.Delete path env.Scope.Cancellation

    let move<'R when 'R :> IFs and 'R :> IScope>
        (source: string)
        (destination: string)
        : Eff<'R, Result<unit, FsError>> =
        fun env -> env.Fs.Move source destination env.Scope.Cancellation

    /// Whether paths compare case-insensitively on this OS (Windows and macOS by default).
    let private ignoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()

    /// The path, normalized, if it is absolute and inside `root` (or is `root`).
    let private within (root: string) (path: string) : Result<string, FsError> =
        if not (Path.IsPathFullyQualified path) then
            Error(InvalidPath(path, "not absolute"))
        else
            let full = Path.GetFullPath(path, root)
            let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, root))

            let comparison =
                if ignoreCase then
                    StringComparison.OrdinalIgnoreCase
                else
                    StringComparison.Ordinal

            let prefix =
                if Path.EndsInDirectorySeparator root then
                    root
                else
                    root + string Path.DirectorySeparatorChar

            if
                String.Equals(Path.TrimEndingDirectorySeparator full, root, comparison)
                || full.StartsWith(prefix, comparison)
            then
                Ok full
            else
                Error(AccessDenied path)

    let private guard (root: string) (path: string) (operation: string -> Task<Result<'T, FsError>>) =
        match within root path with
        | Ok full -> operation full
        | Error e -> Task.FromResult(Error e)

    /// Attenuates reading to the subtree at `root` (an absolute path): other paths fail with
    /// `AccessDenied`. Paths are normalized first, so `..` cannot climb out. Symlinks inside the tree
    /// are not resolved, so this is a guardrail for well-behaved code, not a sandbox (spec §18).
    let confineRead (root: string) (fs: FsRead) : FsRead =
        {
            ReadFile = fun path ct -> guard root path (fun p -> fs.ReadFile p ct)
            Stat = fun path ct -> guard root path (fun p -> fs.Stat p ct)
            List = fun path ct -> guard root path (fun p -> fs.List p ct)
        }

    /// Attenuates changes to the subtree at `root`, like `confineRead`.
    let confine (root: string) (fs: Fs) : Fs =
        {
            WriteFile = fun path content ct -> guard root path (fun p -> fs.WriteFile p content ct)
            CreateDirectory = fun path ct -> guard root path (fun p -> fs.CreateDirectory p ct)
            Delete = fun path ct -> guard root path (fun p -> fs.Delete p ct)
            Move =
                fun source destination ct ->
                    guard root source (fun s -> guard root destination (fun d -> fs.Move s d ct))
        }
