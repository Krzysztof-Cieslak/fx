/// The SQLite library itself: which one is loaded, and how it was built.
[<RequireQualifiedAccess>]
module Fx.Sqlite.Library

/// The version this build pins (native/sqlite/Sqlite.props), e.g. "3.54.1".
[<Literal>]
let PinnedVersion = Pinned.Version

/// The source id this build pins: the check-in date and hash of the release.
[<Literal>]
let PinnedSourceId = Pinned.SourceId

/// The version of the loaded library, e.g. "3.54.1".
let version () : string =
    Native.text (Native.sqlite3_libversion ())

/// The version as a number, e.g. 3054001.
let versionNumber () : int = Native.sqlite3_libversion_number ()

/// The source id of the loaded library.
let sourceId () : string =
    Native.text (Native.sqlite3_sourceid ())

/// Whether the library was built thread-safe (connections may be used from any thread).
let isThreadsafe () : bool = Native.sqlite3_threadsafe () <> 0

/// The compile-time options of the loaded library without the `SQLITE_` prefix, e.g. "DQS=0", as
/// `PRAGMA compile_options` lists them.
let compileOptions () : string list =
    Seq.unfold
        (fun i ->
            match Native.sqlite3_compileoption_get i with
            | 0n -> None
            | option -> Some(Native.text option, i + 1))
        0
    |> List.ofSeq

/// Throws unless the loaded library is the pinned one. Another SQLite would mean a broken build:
/// the static library missing from a native binary, or a stale shared library next to the tests.
let ensurePinned () : unit =
    let loaded = sourceId ()

    if versionNumber () <> Pinned.VersionNumber || loaded <> Pinned.SourceId then
        invalidOp (
            "loaded SQLite "
            + version ()
            + " ("
            + loaded
            + ") is not the pinned "
            + Pinned.Version
            + " ("
            + Pinned.SourceId
            + ")"
        )
