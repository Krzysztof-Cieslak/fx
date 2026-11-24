namespace Fx.Sqlite

#nowarn "9" // native pointers
#nowarn "51" // address-of for out parameters

open System
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop

/// The current row of a running statement. Valid only inside the callback that receives it: the next
/// step overwrites it. Typed readers reject NULL; check `IsNull` or read a `Value` for nullable
/// columns. Columns are numbered from 0.
[<Struct; NoEquality; NoComparison>]
type Row internal (statement: nativeint, count: int) =
    /// The number of columns.
    member _.Count = count

    member private _.Column(column: int) =
        if column < 0 || column >= count then
            raise (ArgumentOutOfRangeException("column", $"column {column} of {count}"))

    member private this.NonNull(column: int) =
        if this.IsNull column then
            invalidOp $"column {column} ({this.Name column}) is NULL"

    /// The column's name: its alias (`AS`), else SQLite's choice.
    member this.Name(column: int) : string =
        this.Column column
        Native.text (Native.sqlite3_column_name (statement, column))

    member this.Type(column: int) : ColumnType =
        this.Column column

        match Native.sqlite3_column_type (statement, column) with
        | Native.SQLITE_INTEGER -> ColumnType.Integer
        | Native.SQLITE_FLOAT -> ColumnType.Real
        | Native.SQLITE_TEXT -> ColumnType.Text
        | Native.SQLITE_BLOB -> ColumnType.Blob
        | _ -> ColumnType.Null

    member this.IsNull(column: int) : bool = this.Type column = ColumnType.Null

    member this.Int64(column: int) : int64 =
        this.NonNull column
        Native.sqlite3_column_int64 (statement, column)

    member this.Double(column: int) : float =
        this.NonNull column
        Native.sqlite3_column_double (statement, column)

    /// The value as UTF-8 text, converted by SQLite if it is not TEXT.
    member this.Text(column: int) : string =
        this.NonNull column
        // Text before bytes: the conversion to text decides the length.
        let text = Native.sqlite3_column_text (statement, column)
        let bytes = Native.sqlite3_column_bytes (statement, column)

        if bytes = 0 then
            ""
        else
            Encoding.UTF8.GetString(NativePtr.ofNativeInt<byte> text, bytes)

    /// The value's bytes. An empty BLOB is an empty array.
    member this.Blob(column: int) : byte array =
        this.NonNull column
        let data = Native.sqlite3_column_blob (statement, column)
        let bytes = Native.sqlite3_column_bytes (statement, column)
        let copy: byte array = Array.zeroCreate bytes

        if bytes > 0 then
            Marshal.Copy(data, copy, 0, bytes)

        copy

    member this.Value(column: int) : Value =
        match this.Type column with
        | ColumnType.Null -> Value.Null
        | ColumnType.Integer -> Value.Integer(this.Int64 column)
        | ColumnType.Real -> Value.Real(this.Double column)
        | ColumnType.Text -> Value.Text(this.Text column)
        | ColumnType.Blob -> Value.Blob(this.Blob column)

/// A prepared statement. Its operations lock its connection, so a statement may be used from any
/// thread, one operation at a time. Dispose statements from `Database.prepare`; the database owns its
/// cached ones.
[<Sealed>]
type Statement internal (connection: Connection, handle: StatementHandle, sql: string) =
    let parameters = Native.sqlite3_bind_parameter_count (handle.DangerousGetHandle())
    let readOnly = Native.sqlite3_stmt_readonly (handle.DangerousGetHandle()) <> 0

    /// The SQL text it was prepared from.
    member _.Sql = sql

    /// How many parameters it takes (`?`, `?NNN`, `:name`, `@name`, `$name`).
    member _.ParameterCount = parameters

    /// Whether it leaves the database unchanged.
    member _.IsReadOnly = readOnly

    member internal _.Connection = connection

    /// Set while it runs, so a nested use of the same statement is caught.
    member val internal Running = false with get, set

    member internal _.Pointer =
        if handle.IsClosed then
            raise (ObjectDisposedException("Statement", "the statement is finalized"))

        handle.DangerousGetHandle()

    member _.Dispose() =
        lock connection.Sync (fun () -> handle.Dispose())

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    override _.ToString() = sql

module Statement =
    /// Prepares one SQL statement. `persistent` hints that it will be reused many times.
    let internal prepare (connection: Connection) (sql: string) (persistent: bool) : Result<Statement, SqliteError> =
        let bytes = Encoding.UTF8.GetBytes sql

        if String.IsNullOrWhiteSpace sql then
            Error(SqliteError.create 1 "empty SQL")
        else
            let flags = if persistent then Native.SQLITE_PREPARE_PERSISTENT else 0u

            lock connection.Sync (fun () ->
                let pointer = connection.Pointer

                let prepareAt (text: nativeptr<byte>) (offset: int) =
                    let mutable statement = 0n
                    let mutable tail = 0n
                    let start = NativePtr.add text offset

                    let code =
                        Native.sqlite3_prepare_v3 (pointer, start, bytes.Length - offset, flags, &&statement, &&tail)

                    struct (code, statement, int (tail - NativePtr.toNativeInt text))

                let struct (code, statement, more) =
                    Native.pinned bytes (fun text ->
                        let struct (code, statement, used) = prepareAt text 0

                        if code <> Native.SQLITE_OK || statement = 0n || used >= bytes.Length then
                            struct (code, statement, false)
                        else
                            // Whatever follows must be only whitespace, semicolons and comments,
                            // which prepare to no statement at all.
                            let struct (nextCode, next, _) = prepareAt text used

                            if next <> 0n then
                                Native.sqlite3_finalize next |> ignore

                            struct (code, statement, next <> 0n || nextCode <> Native.SQLITE_OK))

                if code <> Native.SQLITE_OK then
                    Error(connection.Error code)
                elif statement = 0n then
                    Error(SqliteError.create 1 "the SQL holds only comments")
                elif more then
                    Native.sqlite3_finalize statement |> ignore
                    Error(SqliteError.create 1 "the SQL holds more than one statement; use Database.exec")
                else
                    Ok(new Statement(connection, new StatementHandle(statement), sql)))

    // A null pointer would bind NULL; `Native.pinned` never passes one, so "" and [||] stay empty
    // TEXT and BLOB values.
    let private bindText (statement: nativeint) (index: int) (text: string) =
        let bytes = Encoding.UTF8.GetBytes text

        Native.pinned bytes (fun data ->
            Native.sqlite3_bind_text (statement, index, data, bytes.Length, Native.transient))

    let private bindBlob (statement: nativeint) (index: int) (blob: byte array) =
        Native.pinned blob (fun data ->
            Native.sqlite3_bind_blob (statement, index, data, blob.Length, Native.transient))

    /// Binds the arguments to the parameters in order. The count must match: a mismatch, a value
    /// over SQLite's size limits or misuse are defects.
    let private bind (statement: Statement) (args: Value list) =
        let pointer = statement.Pointer
        let count = List.length args

        if count <> statement.ParameterCount then
            invalidArg
                "args"
                $"the statement takes {statement.ParameterCount} parameters, {count} given: {statement.Sql}"

        args
        |> List.iteri (fun i value ->
            let index = i + 1

            let code =
                match value with
                | Value.Null -> Native.sqlite3_bind_null (pointer, index)
                | Value.Integer n -> Native.sqlite3_bind_int64 (pointer, index, n)
                | Value.Real r -> Native.sqlite3_bind_double (pointer, index, r)
                | Value.Text t -> bindText pointer index t
                | Value.Blob b -> bindBlob pointer index b

            if code <> Native.SQLITE_OK then
                raise (SqliteException(statement.Connection.Error code, $"binding parameter {index}")))

    /// Runs the statement with the arguments, handing each row to `step` until it says stop. The
    /// statement is reset whatever happens, so it can run again; an exception from `step` propagates.
    let private scan
        (statement: Statement)
        (args: Value list)
        (state: 'S)
        (step: 'S -> Row -> struct ('S * bool))
        : Result<'S, SqliteError> =
        let connection = statement.Connection

        lock connection.Sync (fun () ->
            if statement.Running then
                invalidOp $"the statement is already running (a nested use?): {statement.Sql}"

            let pointer = statement.Pointer
            statement.Running <- true

            try
                bind statement args
                let columns = Native.sqlite3_column_count pointer
                let mutable acc = state
                let mutable outcome = None

                while outcome.IsNone do
                    match Native.sqlite3_step pointer with
                    | Native.SQLITE_ROW ->
                        let struct (next, more) = step acc (Row(pointer, columns))
                        acc <- next

                        if not more then
                            outcome <- Some(Ok acc)
                    | Native.SQLITE_DONE -> outcome <- Some(Ok acc)
                    | code -> outcome <- Some(Error(connection.Error code))

                outcome.Value
            finally
                Native.sqlite3_reset pointer |> ignore
                Native.sqlite3_clear_bindings pointer |> ignore
                statement.Running <- false)

    /// Runs the statement with the arguments and folds over its rows.
    let fold (statement: Statement) (args: Value list) (state: 'S) (folder: 'S -> Row -> 'S) : Result<'S, SqliteError> =
        scan statement args state (fun acc row -> struct (folder acc row, true))

    /// Runs the statement and reads every row.
    let query (statement: Statement) (args: Value list) (read: Row -> 'T) : Result<'T list, SqliteError> =
        fold statement args [] (fun rows row -> read row :: rows) |> Result.map List.rev

    /// Runs the statement and reads the first row, if any.
    let tryFirst (statement: Statement) (args: Value list) (read: Row -> 'T) : Result<'T option, SqliteError> =
        scan statement args None (fun _ row -> struct (Some(read row), false))

    /// Runs the statement to completion and returns how many rows it inserted, updated or deleted
    /// (not counting triggers); 0 for other statements.
    let execute (statement: Statement) (args: Value list) : Result<int64, SqliteError> =
        let connection = statement.Connection

        lock connection.Sync (fun () ->
            // sqlite3_changes64 keeps the count of the last INSERT, UPDATE or DELETE, so it is only
            // this statement's count if the connection's total moved.
            let before = Native.sqlite3_total_changes64 connection.Pointer

            fold statement args () (fun () _ -> ())
            |> Result.map (fun () ->
                if Native.sqlite3_total_changes64 connection.Pointer = before then
                    0L
                else
                    Native.sqlite3_changes64 connection.Pointer))
