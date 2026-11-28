/// The tree-sitter C API (lib/include/tree_sitter/api.h), as far as the binding uses it, the
/// grammars' language functions and the shim in native/tree-sitter/library.c.
///
/// Every signature is blittable. `TSNode` and `TSPoint` cross by value as plain structs; C's `bool`
/// is declared as `byte` (one byte on every target ABI) and enums as `int`. Runtime marshalling is
/// disabled for the assembly, so a signature that would need a stub fails on the JIT as well as
/// under Native AOT. Native AOT binds these imports at link time to the static library
/// (DirectPInvoke, native/tree-sitter/TreeSitter.targets); the JIT loads the shared library from the
/// application directory.
module internal Fx.TreeSitter.Native

#nowarn "9" // native pointers

open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<assembly: DisableRuntimeMarshalling>]
do ()

[<Literal>]
let private Lib = Pinned.Library

/// `TSPoint`: a zero-based row and a byte column.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type TSPoint =
    val Row: uint32
    val Column: uint32

/// `TSNode`: `uint32_t context[4]`, then the subtree and tree pointers. Valid while its tree lives.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type TSNode =
    val Context0: uint32
    val Context1: uint32
    val Context2: uint32
    val Context3: uint32
    val Id: nativeint
    val Tree: nativeint

/// `TSQueryCapture`: a node and the index of its capture name.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type TSQueryCapture =
    val Node: TSNode
    val Index: uint32

/// `TSQueryMatch`: the captures pointer is valid until the cursor moves on.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type TSQueryMatch =
    val Id: uint32
    val PatternIndex: uint16
    val CaptureCount: uint16
    val Captures: nativeptr<TSQueryCapture>

// The shim (library.c).
[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint fx_ts_pin()

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 fx_ts_language_version()

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 fx_ts_min_compatible_language_version()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint fx_ts_parse_utf8(nativeint parser, byte* text, uint32 length, int* cancel)

// The grammars.
[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint tree_sitter_fsharp()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint tree_sitter_fsharp_signature()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint tree_sitter_c_sharp()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint tree_sitter_python()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint tree_sitter_typescript()

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint tree_sitter_tsx()

// Languages.
[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 ts_language_abi_version(nativeint language)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint ts_language_name(nativeint language)

// Parsers.
[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint ts_parser_new()

[<DllImport(Lib, ExactSpelling = true)>]
extern void ts_parser_delete(nativeint parser)

[<DllImport(Lib, ExactSpelling = true)>]
extern byte ts_parser_set_language(nativeint parser, nativeint language)

// Trees.
[<DllImport(Lib, ExactSpelling = true)>]
extern void ts_tree_delete(nativeint tree)

[<DllImport(Lib, ExactSpelling = true)>]
extern TSNode ts_tree_root_node(nativeint tree)

// Nodes.
[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint ts_node_type(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 ts_node_start_byte(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 ts_node_end_byte(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern TSPoint ts_node_start_point(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern TSPoint ts_node_end_point(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern byte ts_node_is_named(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern byte ts_node_is_missing(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern byte ts_node_is_error(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern byte ts_node_has_error(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 ts_node_child_count(TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern TSNode ts_node_child(TSNode node, uint32 index)

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 ts_node_descendant_count(TSNode node)

// Queries.
[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint ts_query_new(nativeint language, byte* source, uint32 length, uint32* errorOffset, int* errorType)

[<DllImport(Lib, ExactSpelling = true)>]
extern void ts_query_delete(nativeint query)

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 ts_query_pattern_count(nativeint query)

[<DllImport(Lib, ExactSpelling = true)>]
extern uint32 ts_query_capture_count(nativeint query)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint ts_query_capture_name_for_id(nativeint query, uint32 index, uint32* length)

[<DllImport(Lib, ExactSpelling = true)>]
extern nativeint ts_query_cursor_new()

[<DllImport(Lib, ExactSpelling = true)>]
extern void ts_query_cursor_delete(nativeint cursor)

[<DllImport(Lib, ExactSpelling = true)>]
extern void ts_query_cursor_set_match_limit(nativeint cursor, uint32 limit)

[<DllImport(Lib, ExactSpelling = true)>]
extern byte ts_query_cursor_did_exceed_match_limit(nativeint cursor)

[<DllImport(Lib, ExactSpelling = true)>]
extern void ts_query_cursor_exec(nativeint cursor, nativeint query, TSNode node)

[<DllImport(Lib, ExactSpelling = true)>]
extern byte ts_query_cursor_next_match(nativeint cursor, TSQueryMatch* matched)

/// A NUL-terminated UTF-8 string owned by tree-sitter (static: node types, names), copied.
let text (pointer: nativeint) : string =
    match Marshal.PtrToStringUTF8 pointer with
    | null -> ""
    | s -> s

/// UTF-8 of the given length owned by tree-sitter, copied.
let textOfLength (pointer: nativeint) (length: uint32) : string =
    if pointer = 0n then
        ""
    else
        Marshal.PtrToStringUTF8(pointer, int length)

/// Runs `f` with the array pinned and a pointer to its first element, which is not null even for an
/// empty array. (A GCHandle rather than `use p = fixed array`: the analyzers' typed-tree walk fails on
/// `fixed`.)
let pinned (bytes: byte array) (f: nativeptr<byte> -> 'T) : 'T =
    let bytes = if bytes.Length = 0 then [| 0uy |] else bytes
    let handle = GCHandle.Alloc(bytes, GCHandleType.Pinned)

    try
        f (NativePtr.ofNativeInt (handle.AddrOfPinnedObject()))
    finally
        handle.Free()
