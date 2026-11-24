namespace Fx.Sqlite

open System

/// The primary result code of a failure (https://sqlite.org/rescode.html): the low byte of the
/// extended code.
[<RequireQualifiedAccess>]
type ErrorCode =
    | Error
    | Internal
    | Permission
    | Abort
    /// Another connection holds a lock and the busy timeout ran out.
    | Busy
    | Locked
    | NoMemory
    | ReadOnly
    /// `Database.interrupt` or a cancelled token stopped the operation.
    | Interrupt
    | IoError
    | Corrupt
    | NotFound
    | Full
    | CantOpen
    | Protocol
    | Schema
    | TooBig
    /// A UNIQUE, PRIMARY KEY, NOT NULL, CHECK or FOREIGN KEY constraint failed.
    | Constraint
    | Mismatch
    | Misuse
    | NoLfs
    | Auth
    | Range
    | NotADatabase
    | Other of code: int

    override this.ToString() =
        match this with
        | Error -> "error"
        | Internal -> "internal"
        | Permission -> "permission"
        | Abort -> "abort"
        | Busy -> "busy"
        | Locked -> "locked"
        | NoMemory -> "no memory"
        | ReadOnly -> "read-only"
        | Interrupt -> "interrupt"
        | IoError -> "I/O error"
        | Corrupt -> "corrupt"
        | NotFound -> "not found"
        | Full -> "full"
        | CantOpen -> "cannot open"
        | Protocol -> "protocol"
        | Schema -> "schema"
        | TooBig -> "too big"
        | Constraint -> "constraint"
        | Mismatch -> "mismatch"
        | Misuse -> "misuse"
        | NoLfs -> "no large files"
        | Auth -> "authorization"
        | Range -> "range"
        | NotADatabase -> "not a database"
        | Other code -> "code " + string code

module ErrorCode =
    /// The primary code of a (possibly extended) result code.
    let ofResult (code: int) : ErrorCode =
        match code &&& 0xff with
        | 1 -> ErrorCode.Error
        | 2 -> ErrorCode.Internal
        | 3 -> ErrorCode.Permission
        | 4 -> ErrorCode.Abort
        | 5 -> ErrorCode.Busy
        | 6 -> ErrorCode.Locked
        | 7 -> ErrorCode.NoMemory
        | 8 -> ErrorCode.ReadOnly
        | 9 -> ErrorCode.Interrupt
        | 10 -> ErrorCode.IoError
        | 11 -> ErrorCode.Corrupt
        | 12 -> ErrorCode.NotFound
        | 13 -> ErrorCode.Full
        | 14 -> ErrorCode.CantOpen
        | 15 -> ErrorCode.Protocol
        | 17 -> ErrorCode.Schema
        | 18 -> ErrorCode.TooBig
        | 19 -> ErrorCode.Constraint
        | 20 -> ErrorCode.Mismatch
        | 21 -> ErrorCode.Misuse
        | 22 -> ErrorCode.NoLfs
        | 23 -> ErrorCode.Auth
        | 25 -> ErrorCode.Range
        | 26 -> ErrorCode.NotADatabase
        | primary -> ErrorCode.Other primary

/// A failure the caller can expect: a lock held too long, a constraint, a full disk, a corrupt or
/// missing file, an interrupt, or SQL the database rejected.
type SqliteError =
    {
        Code: ErrorCode
        /// The extended result code, e.g. 2067 (SQLITE_CONSTRAINT_UNIQUE).
        ExtendedCode: int
        /// SQLite's message, e.g. "UNIQUE constraint failed: events.key".
        Message: string
    }

    override this.ToString() =
        string this.Code + " (" + string this.ExtendedCode + "): " + this.Message

module SqliteError =
    /// SQLITE_CONSTRAINT_UNIQUE and SQLITE_CONSTRAINT_PRIMARYKEY.
    let isUniqueViolation (error: SqliteError) =
        error.ExtendedCode = 2067 || error.ExtendedCode = 1555

    /// An error made up by the binding rather than reported by SQLite.
    let create (code: int) (message: string) : SqliteError =
        {
            Code = ErrorCode.ofResult code
            ExtendedCode = code
            Message = message
        }

/// A defect: SQLite reported misuse of the API (a bad parameter index, a value over the size limits,
/// a statement used after it was finalized). Never thrown for expected failures.
type SqliteException(error: SqliteError, operation: string) =
    inherit Exception(operation + ": " + string error)

    member _.Error = error
    member _.Operation = operation

/// A value SQLite stores: one of its five fundamental datatypes.
[<RequireQualifiedAccess>]
type Value =
    | Null
    | Integer of int64
    | Real of float
    | Text of string
    | Blob of byte array

    override this.ToString() =
        match this with
        | Null -> "NULL"
        | Integer i -> string i
        | Real r -> r.ToString("R", Globalization.CultureInfo.InvariantCulture)
        | Text t -> "'" + t.Replace("'", "''") + "'"
        | Blob b when b.Length <= 32 -> "X'" + Convert.ToHexString b + "'"
        | Blob b -> "<blob of " + string b.Length + " bytes>"

/// The datatype of a value in a result row.
[<RequireQualifiedAccess>]
type ColumnType =
    | Null
    | Integer
    | Real
    | Text
    | Blob

    override this.ToString() =
        match this with
        | Null -> "NULL"
        | Integer -> "INTEGER"
        | Real -> "REAL"
        | Text -> "TEXT"
        | Blob -> "BLOB"
