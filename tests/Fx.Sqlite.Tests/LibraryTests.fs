module Fx.Sqlite.Tests.LibraryTests

open Expecto
open Fx.Sqlite
open Fx.Sqlite.Tests.Support

[<Tests>]
let tests =
    testList
        "Library"
        [
            test "the loaded library is the pinned one" {
                Library.ensurePinned ()
                Expect.equal (Library.version ()) Library.PinnedVersion "version"
                Expect.equal (Library.sourceId ()) Library.PinnedSourceId "source id"
            }

            test "it is thread-safe" { Expect.isTrue (Library.isThreadsafe ()) "threadsafe" }

            test "it is built with the chosen options" {
                let options = Library.compileOptions ()

                for option in
                    [
                        "THREADSAFE=1"
                        "OMIT_LOAD_EXTENSION"
                        "DQS=0"
                        "ENABLE_API_ARMOR"
                        // Some options are listed by name only.
                        "DEFAULT_FOREIGN_KEYS"
                        "TEMP_STORE=2"
                        "ENABLE_FTS5"
                        "DEFAULT_MEMSTATUS=0"
                        "LIKE_DOESNT_MATCH_BLOBS"
                        "OMIT_DECLTYPE"
                        "OMIT_DEPRECATED"
                        "OMIT_PROGRESS_CALLBACK"
                        "OMIT_SHARED_CACHE"
                        "STRICT_SUBTYPE"
                        "USE_ALLOCA"
                    ] do
                    Expect.contains options option option
            }

            test "connections start with the chosen defaults" {
                use db = Database.memory ()

                for pragma, expected in
                    [
                        "foreign_keys", "1"
                        "trusted_schema", "0"
                        "recursive_triggers", "0"
                        "journal_mode", "memory"
                    ] do
                    Expect.equal (texts db ("PRAGMA " + pragma) []) [ expected ] pragma
            }

            test "PRAGMA compile_options agrees" {
                use db = Database.memory ()
                let pragma = texts db "PRAGMA compile_options" []
                Expect.equal pragma (Library.compileOptions ()) "same list"
            }

            test "JSON is built in" {
                use db = Database.memory ()
                Expect.equal (texts db "SELECT json_extract('{\"a\":[1,2]}', '$.a[1]')" []) [ "2" ] "json"
            }
        ]
