namespace Fx.TreeSitter

#nowarn "9" // native pointers

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Microsoft.FSharp.NativeInterop

/// A place in the source: a one-based line and a zero-based column in UTF-8 bytes. Lines end at
/// `\n`, so a CRLF file counts as many lines as an LF one.
[<Struct>]
type Position = { Line: int; Column: int }

/// Why a parse gave no tree.
[<RequireQualifiedAccess>]
type ParseError =
    /// The token was cancelled while the parse ran (tree-sitter checks about every hundred steps).
    | Cancelled
    /// The core refused the grammar: its ABI is outside the range the core accepts.
    | Incompatible of abi: uint32

/// Owns a `TSTree*`.
[<Sealed>]
type internal TreeHandle =
    inherit SafeHandle

    new(pointer: nativeint) as this =
        { inherit SafeHandle(0n, true) }
        then this.SetHandle pointer

    override this.IsInvalid = this.handle = 0n

    override this.ReleaseHandle() =
        Native.ts_tree_delete this.handle
        true

/// A syntax tree and the UTF-8 source it was parsed from. Its nodes are valid until it is disposed
/// (using one afterwards throws). A tree may be read from one thread at a time.
[<Sealed>]
type Tree internal (handle: TreeHandle, language: Language, source: byte array) =
    member _.Language = language

    /// The UTF-8 source; node offsets are into it.
    member _.Source = source

    member internal _.Pointer =
        if handle.IsClosed then
            raise (ObjectDisposedException("Tree", "the syntax tree is disposed"))

        handle.DangerousGetHandle()

    member internal _.KeepAlive() = GC.KeepAlive handle

    member this.Root: Node =
        let raw = Native.ts_tree_root_node this.Pointer
        this.KeepAlive()
        Node(raw, this)

    interface IDisposable with
        member _.Dispose() = handle.Dispose()

/// A node of a tree. A struct over tree-sitter's `TSNode`; reading it checks its tree is alive.
and [<Struct; CustomEquality; NoComparison>] Node =
    val internal Raw: Native.TSNode
    val Tree: Tree

    internal new(raw, tree) = { Raw = raw; Tree = tree }

    member private this.Read(f: Native.TSNode -> 'T) : 'T =
        this.Tree.Pointer |> ignore
        let value = f this.Raw
        this.Tree.KeepAlive()
        value

    /// The grammar's name for the node's type, e.g. "function_definition", or "ERROR".
    member this.Kind = this.Read(fun n -> Native.text (Native.ts_node_type n))
    member this.IsNamed = this.Read Native.ts_node_is_named <> 0uy
    member this.IsError = this.Read Native.ts_node_is_error <> 0uy
    /// Inserted by error recovery: absent from the source, zero bytes long.
    member this.IsMissing = this.Read Native.ts_node_is_missing <> 0uy
    /// Whether this node or one under it is an error or missing.
    member this.HasError = this.Read Native.ts_node_has_error <> 0uy
    member this.StartByte = int (this.Read Native.ts_node_start_byte)
    member this.EndByte = int (this.Read Native.ts_node_end_byte)

    member this.Start =
        let p = this.Read Native.ts_node_start_point

        {
            Line = int p.Row + 1
            Column = int p.Column
        }

    member this.End =
        let p = this.Read Native.ts_node_end_point

        {
            Line = int p.Row + 1
            Column = int p.Column
        }

    member this.ChildCount = int (this.Read Native.ts_node_child_count)

    member this.Child(index: int) : Node =
        let raw = this.Read(fun n -> Native.ts_node_child (n, uint32 index))
        Node(raw, this.Tree)

    member this.Children: Node list = List.init this.ChildCount this.Child

    /// How many nodes this one spans, itself included.
    member this.DescendantCount = int (this.Read Native.ts_node_descendant_count)

    /// The node's source text.
    member this.Text =
        Encoding.UTF8.GetString(this.Tree.Source, this.StartByte, this.EndByte - this.StartByte)

    override this.Equals(other) =
        match other with
        | :? Node as n ->
            n.Raw.Id = this.Raw.Id
            && n.Raw.Tree = this.Raw.Tree
            && n.StartByte = this.StartByte
        | _ -> false

    override this.GetHashCode() = this.Raw.Id.GetHashCode()

[<RequireQualifiedAccess>]
module Syntax =
    /// Parses UTF-8 source. Cancelling the token stops the parse within about a hundred steps.
    let parse (language: Language) (source: byte array) (cancel: CancellationToken) : Result<Tree, ParseError> =
        let parser = Native.ts_parser_new ()

        try
            if Native.ts_parser_set_language (parser, Language.pointer language) = 0uy then
                Error(ParseError.Incompatible(Language.abiVersion language))
            else
                // The flag the shim's progress callback reads; native memory, so it never moves.
                let flag =
                    NativePtr.ofVoidPtr<int>(NativeMemory.AllocZeroed(unativeint sizeof<int>))

                try
                    let tree =
                        use _ = cancel.Register(fun () -> Volatile.Write(NativePtr.toByRef flag, 1))

                        if cancel.IsCancellationRequested then
                            0n
                        else
                            Native.pinned source (fun text ->
                                Native.fx_ts_parse_utf8 (parser, text, uint32 source.Length, flag))

                    if tree = 0n then
                        Error ParseError.Cancelled
                    else
                        Ok(new Tree(new TreeHandle(tree), language, source))
                finally
                    NativeMemory.Free(NativePtr.toVoidPtr flag)
        finally
            Native.ts_parser_delete parser

    /// Parses text (as UTF-8).
    let parseText (language: Language) (text: string) (cancel: CancellationToken) : Result<Tree, ParseError> =
        parse language (Encoding.UTF8.GetBytes text) cancel

    /// The error and missing nodes of a tree, outermost first, in source order. Error nodes are not
    /// searched inside.
    let errors (tree: Tree) : Node list =
        let rec walk (node: Node) : Node list =
            if node.IsError || node.IsMissing then [ node ]
            elif node.HasError then node.Children |> List.collect walk
            else []

        walk tree.Root
