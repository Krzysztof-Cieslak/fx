namespace Fx.Store.Live

open Fx.Sqlite

/// How hard a store works to keep what it has committed. A store-wide choice, made by the composition
/// root (to become a variant with T106). Measurements: docs/modules/store.md.
[<RequireQualifiedAccess>]
type Durability =
    /// A commit is on disk before it returns: SQLite's `synchronous=FULL` (the write-ahead log is
    /// synced on every commit) and blob files synced before they are renamed into place. Survives
    /// process crashes, OS crashes and power loss. On macOS, `fsync` leaves data in the drive's cache,
    /// so power loss can still take the last commits.
    | Full
    /// Commits survive a process crash, but an OS crash or power loss can take the last ones (never
    /// corrupting the database): `synchronous=NORMAL`, and blob files are not synced.
    | Normal

    override this.ToString() =
        match this with
        | Full -> "full"
        | Normal -> "normal"

/// Why a store could not be opened.
[<RequireQualifiedAccess>]
type OpenError =
    /// SQLite could not open or set up the database: a missing directory, no permission, not a
    /// database, a lock held by another process for too long.
    | Database of SqliteError
    /// The database was written by a newer version of the store.
    | NewerSchema of found: int * supported: int
    /// The path is not usable (relative, or not a directory).
    | InvalidPath of path: string * reason: string

    override this.ToString() =
        match this with
        | Database error -> "database: " + string error
        | NewerSchema(found, supported) ->
            "the database has schema version "
            + string found
            + "; this version of the store reads up to "
            + string supported
        | InvalidPath(path, reason) -> "invalid path (" + reason + "): " + path
