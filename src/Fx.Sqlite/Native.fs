/// The SQLite C API, as far as the binding uses it (https://sqlite.org/c3ref/intro.html).
///
/// Every signature is blittable: handles and strings are pointers, text is UTF-8 that the caller
/// encodes, and nothing needs a marshalling stub. Runtime marshalling is disabled for the assembly,
/// so a signature that would need one fails on the JIT as well as under Native AOT. Native AOT binds
/// these imports at link time to the static library (DirectPInvoke, native/sqlite/Sqlite.targets);
/// the JIT loads the shared library from the application directory.
module internal Fx.Sqlite.Native

#nowarn "9" // native pointers

open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<assembly: DisableRuntimeMarshalling>]
do ()

[<Literal>]
let private Lib = Pinned.Library

// Result codes (https://sqlite.org/rescode.html).
[<Literal>]
let SQLITE_OK = 0

[<Literal>]
let SQLITE_ROW = 100

[<Literal>]
let SQLITE_DONE = 101

// Open flags.
[<Literal>]
let SQLITE_OPEN_READONLY = 0x00000001

[<Literal>]
let SQLITE_OPEN_READWRITE = 0x00000002

[<Literal>]
let SQLITE_OPEN_CREATE = 0x00000004

[<Literal>]
let SQLITE_OPEN_URI = 0x00000040

[<Literal>]
let SQLITE_OPEN_FULLMUTEX = 0x00010000

[<Literal>]
let SQLITE_OPEN_NOFOLLOW = 0x01000000

[<Literal>]
let SQLITE_OPEN_EXRESCODE = 0x02000000

// sqlite3_db_config options that take an int and an int*.
[<Literal>]
let SQLITE_DBCONFIG_DEFENSIVE = 1010

[<Literal>]
let SQLITE_DBCONFIG_DQS_DML = 1013

[<Literal>]
let SQLITE_DBCONFIG_DQS_DDL = 1014

[<Literal>]
let SQLITE_DBCONFIG_TRUSTED_SCHEMA = 1017

[<Literal>]
let SQLITE_PREPARE_PERSISTENT = 0x01u

// Fundamental datatypes.
[<Literal>]
let SQLITE_INTEGER = 1

[<Literal>]
let SQLITE_FLOAT = 2

[<Literal>]
let SQLITE_TEXT = 3

[<Literal>]
let SQLITE_BLOB = 4

[<Literal>]
let SQLITE_NULL = 5

[<Literal>]
let SQLITE_DESERIALIZE_FREEONCLOSE = 1u

[<Literal>]
let SQLITE_DESERIALIZE_RESIZEABLE = 2u

/// SQLITE_TRANSIENT: SQLite copies the bound value before the call returns.
let transient: nativeint = -1n

// Library.
[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_libversion()

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_libversion_number()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_sourceid()

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_threadsafe()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_compileoption_get(int n)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_errstr(int code)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_malloc64(uint64 size)

[<DllImport(Lib, ExactSpelling = true)>]
extern void sqlite3_free(nativeint pointer)

// Connections.
[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_open_v2(byte* filename, nativeint* db, int flags, nativeint vfs)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_close_v2(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_errmsg(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_extended_errcode(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_busy_timeout(nativeint db, int milliseconds)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3shim_db_config_flag(nativeint db, int op, int value, int* current)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_exec(nativeint db, byte* sql, nativeint callback, nativeint argument, nativeint* errmsg)

[<DllImport(Lib, ExactSpelling = true)>]
extern int64 sqlite3_changes64(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern int64 sqlite3_total_changes64(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern int64 sqlite3_last_insert_rowid(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_get_autocommit(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern void sqlite3_interrupt(nativeint db)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_wal_checkpoint_v2(nativeint db, byte* schema, int mode, int* logFrames, int* checkpointedFrames)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_serialize(nativeint db, byte* schema, int64* size, uint32 flags)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_deserialize(nativeint db, byte* schema, nativeint data, int64 size, int64 capacity, uint32 flags)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_backup_init(nativeint destination, byte* destinationName, nativeint source, byte* sourceName)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_backup_step(nativeint backup, int pages)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_backup_finish(nativeint backup)

// Statements.
[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_prepare_v3(nativeint db, byte* sql, int bytes, uint32 flags, nativeint* statement, nativeint* tail)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_step(nativeint statement)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_reset(nativeint statement)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_clear_bindings(nativeint statement)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_finalize(nativeint statement)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_stmt_readonly(nativeint statement)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_bind_parameter_count(nativeint statement)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_bind_null(nativeint statement, int index)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_bind_int64(nativeint statement, int index, int64 value)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_bind_double(nativeint statement, int index, double value)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_bind_text(nativeint statement, int index, byte* text, int bytes, nativeint destructor)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_bind_blob(nativeint statement, int index, byte* data, int bytes, nativeint destructor)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_bind_zeroblob(nativeint statement, int index, int bytes)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_column_count(nativeint statement)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_column_name(nativeint statement, int column)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_column_type(nativeint statement, int column)

[<DllImport(Lib, ExactSpelling = true)>]
extern int64 sqlite3_column_int64(nativeint statement, int column)

[<DllImport(Lib, ExactSpelling = true)>]
extern double sqlite3_column_double(nativeint statement, int column)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_column_text(nativeint statement, int column)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint sqlite3_column_blob(nativeint statement, int column)

[<DllImport(Lib, ExactSpelling = true)>]
extern int sqlite3_column_bytes(nativeint statement, int column)

/// A NUL-terminated UTF-8 string owned by SQLite, copied.
let text (pointer: nativeint) : string =
    match Marshal.PtrToStringUTF8 pointer with
    | null -> ""
    | s -> s

/// Runs `f` with the array pinned and a pointer to its first element, which is not null even for an
/// empty array. (A GCHandle rather than `use p = fixed array`: the analyzers' typed-tree walk fails on
/// `fixed`.)
let pinned (bytes: byte array) (f: nativeptr<byte> -> 'T) : 'T =
    let handle = GCHandle.Alloc(bytes, GCHandleType.Pinned)

    try
        f (NativePtr.ofNativeInt (handle.AddrOfPinnedObject()))
    finally
        handle.Free()

/// A string as NUL-terminated UTF-8.
let utf8z (s: string) : byte array =
    let count = System.Text.Encoding.UTF8.GetByteCount s
    let bytes = Array.zeroCreate (count + 1)
    System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0) |> ignore
    bytes
