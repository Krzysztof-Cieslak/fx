/// The tree-sitter library itself: which one is loaded, and the grammar ABI it accepts.
[<RequireQualifiedAccess>]
module Fx.TreeSitter.Library

/// The tree-sitter version this build pins (native/tree-sitter/TreeSitter.props), e.g. "0.27.2".
[<Literal>]
let PinnedVersion = Pinned.Version

/// The archives this build pins, space-separated: tree-sitter, then the grammars' releases.
[<Literal>]
let PinnedArchives = Pinned.Archives

/// The archives the loaded library was built from.
let archives () : string = Native.text (Native.fx_ts_pin ())

/// The oldest and newest grammar ABI (LANGUAGE_VERSION) the loaded core accepts.
let abiRange () : uint32 * uint32 =
    Native.fx_ts_min_compatible_language_version (), Native.fx_ts_language_version ()

/// Throws unless the loaded library was built from the pinned archives: another one would mean a
/// broken build (a stale shared library next to the tests, the static library missing from a binary).
let ensurePinned () : unit =
    let loaded = archives ()

    if loaded <> Pinned.Archives then
        invalidOp (
            "loaded tree-sitter is built from "
            + loaded
            + ", not the pinned "
            + Pinned.Archives
        )
