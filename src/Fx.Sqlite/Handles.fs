namespace Fx.Sqlite

open System
open System.Runtime.InteropServices

/// Owns a `sqlite3*`. `sqlite3_close_v2` defers the close until every statement of the connection
/// is finalized, so connection and statement handles may be released in any order (finalizers).
[<Sealed>]
type internal ConnectionHandle =
    inherit SafeHandle

    new(pointer: nativeint) as this =
        { inherit SafeHandle(0n, true) }
        then this.SetHandle pointer

    override this.IsInvalid = this.handle = 0n

    override this.ReleaseHandle() =
        Native.sqlite3_close_v2 this.handle |> ignore
        true

/// Owns a `sqlite3_stmt*`.
[<Sealed>]
type internal StatementHandle =
    inherit SafeHandle

    new(pointer: nativeint) as this =
        { inherit SafeHandle(0n, true) }
        then this.SetHandle pointer

    override this.IsInvalid = this.handle = 0n

    override this.ReleaseHandle() =
        // Returns the error of the last step, not a failure to finalize.
        Native.sqlite3_finalize this.handle |> ignore
        true

/// A connection and the lock that serializes its use. SQLite's own mutex keeps the library safe; this
/// lock keeps the binding's state (statement cache, savepoint depth) consistent and makes a
/// transaction own the connection until it ends, so no other thread's statement lands in it.
[<Sealed>]
type internal Connection(handle: ConnectionHandle, name: string) =
    member val Sync = obj ()
    member _.Handle = handle
    member _.Name = name
    member val SavepointDepth = 0 with get, set

    /// The `sqlite3*`, for a call made while holding `Sync`.
    member _.Pointer =
        if handle.IsClosed then
            raise (ObjectDisposedException("Database", "the database connection is closed"))

        handle.DangerousGetHandle()

    /// The error SQLite reported for the last failed call, with its result code. Read while holding
    /// `Sync`, right after the call: the message belongs to the connection, not the call.
    member this.Error(code: int) : SqliteError =
        {
            Code = ErrorCode.ofResult code
            ExtendedCode = code
            Message = Native.text (Native.sqlite3_errmsg this.Pointer)
        }

    override _.ToString() = name
