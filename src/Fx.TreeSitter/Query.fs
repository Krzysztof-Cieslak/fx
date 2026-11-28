namespace Fx.TreeSitter

#nowarn "9" // native pointers
#nowarn "51" // addresses of locals for out parameters

open System
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop

/// What is wrong with a query (`TSQueryError`).
[<RequireQualifiedAccess>]
type QueryErrorKind =
    | Syntax
    | NodeType
    | Field
    | Capture
    | Structure
    | Language
    | Other of code: int

/// A query the core refused, and the byte offset in the query's UTF-8 source where it noticed.
type QueryError = { Kind: QueryErrorKind; Offset: int }

/// One captured node of a match.
type Capture = { Name: string; Node: Node }

/// One match of a pattern: its index in the query and its captures, in the pattern's order.
type Match =
    { Pattern: int; Captures: Capture list }

/// Owns a `TSQuery*`.
[<Sealed>]
type internal QueryHandle =
    inherit SafeHandle

    new(pointer: nativeint) as this =
        { inherit SafeHandle(0n, true) }
        then this.SetHandle pointer

    override this.IsInvalid = this.handle = 0n

    override this.ReleaseHandle() =
        Native.ts_query_delete this.handle
        true

/// A compiled query for one language. Immutable: one query may run on several threads at once, each
/// with its own tree. Predicates (`#eq?`, `#match?`) are parsed but not applied: the binding applies
/// none, so queries should not rely on them.
[<Sealed>]
type Query internal (handle: QueryHandle, language: Language, captureNames: string array, patterns: int) =
    member _.Language = language
    member _.CaptureNames = captureNames
    member _.PatternCount = patterns

    member internal _.Pointer =
        if handle.IsClosed then
            raise (ObjectDisposedException("Query", "the query is disposed"))

        handle.DangerousGetHandle()

    member internal _.KeepAlive() = GC.KeepAlive handle

    interface IDisposable with
        member _.Dispose() = handle.Dispose()

[<RequireQualifiedAccess>]
module Query =
    let private errorKind (code: int) : QueryErrorKind =
        match code with
        | 1 -> QueryErrorKind.Syntax
        | 2 -> QueryErrorKind.NodeType
        | 3 -> QueryErrorKind.Field
        | 4 -> QueryErrorKind.Capture
        | 5 -> QueryErrorKind.Structure
        | 6 -> QueryErrorKind.Language
        | other -> QueryErrorKind.Other other

    /// Compiles a query (tree-sitter's S-expression pattern language) for a language.
    let compile (language: Language) (source: string) : Result<Query, QueryError> =
        let bytes = Encoding.UTF8.GetBytes source
        let mutable offset = 0u
        let mutable code = 0

        let pointer =
            Native.pinned bytes (fun text ->
                Native.ts_query_new (Language.pointer language, text, uint32 bytes.Length, &&offset, &&code))

        if pointer = 0n then
            Error
                {
                    Kind = errorKind code
                    Offset = int offset
                }
        else
            let names =
                Array.init (int (Native.ts_query_capture_count pointer)) (fun i ->
                    let mutable length = 0u
                    let name = Native.ts_query_capture_name_for_id (pointer, uint32 i, &&length)
                    Native.textOfLength name length)

            let patterns = int (Native.ts_query_pattern_count pointer)
            Ok(new Query(new QueryHandle(pointer), language, names, patterns))

    /// The matches of a query under a node, in the order tree-sitter finds them (by start position).
    let matches (query: Query) (node: Node) : Match list =
        if query.Language <> node.Tree.Language then
            invalidArg
                "node"
                ("a "
                 + string query.Language
                 + " query cannot run on a "
                 + string node.Tree.Language
                 + " tree")

        let tree = node.Tree
        tree.Pointer |> ignore
        let cursor = Native.ts_query_cursor_new ()

        try
            Native.ts_query_cursor_exec (cursor, query.Pointer, node.Raw)
            let mutable current = Unchecked.defaultof<Native.TSQueryMatch>
            let found = ResizeArray()

            while Native.ts_query_cursor_next_match (cursor, &&current) <> 0uy do
                let captures =
                    List.init (int current.CaptureCount) (fun i ->
                        let capture = NativePtr.get current.Captures i

                        {
                            Name = query.CaptureNames[int capture.Index]
                            Node = Node(capture.Node, tree)
                        })

                found.Add
                    {
                        Pattern = int current.PatternIndex
                        Captures = captures
                    }

            List.ofSeq found
        finally
            Native.ts_query_cursor_delete cursor
            query.KeepAlive()
            tree.KeepAlive()
