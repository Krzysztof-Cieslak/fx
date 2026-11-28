module Fx.TreeSitter.Tests.LibraryTests

open Expecto
open Fx.TreeSitter

[<Tests>]
let tests =
    testList
        "Library"
        [
            test "the loaded library is built from the pinned archives" {
                Library.ensurePinned ()
                Expect.equal (Library.archives ()) Library.PinnedArchives "archives"
                Expect.stringStarts Library.PinnedArchives ("tree-sitter-" + Library.PinnedVersion + " ") "core first"
            }

            test "every grammar is linked in, with an ABI the core accepts" {
                let low, high = Library.abiRange ()
                Expect.equal high 15u "the pinned core's ABI"

                for language in Language.all do
                    let abi = Language.abiVersion language
                    Expect.isTrue (abi >= low && abi <= high) (string language + " ABI " + string abi)
            }

            test "grammars of ABI 15 report their own name" {
                for language in Language.all do
                    if Language.abiVersion language >= 15u then
                        Expect.equal (Language.reportedName language) language.Name (string language)
                    else
                        Expect.equal (Language.reportedName language) "" (string language)
            }

            test "languages by file extension" {
                for path, expected in
                    [
                        "src/A.fs", Some Language.FSharp
                        "build.FSX", Some Language.FSharp
                        "src/A.fsi", Some Language.FSharpSignature
                        "A.cs", Some Language.CSharp
                        "a/b.py", Some Language.Python
                        "stubs.pyi", Some Language.Python
                        "x.ts", Some Language.TypeScript
                        "x.mts", Some Language.TypeScript
                        "x.tsx", Some Language.Tsx
                        "x.js", None
                        "Makefile", None
                        ".fs", None
                        "dir.fs/file", None
                        "C:\\src\\A.cs", Some Language.CSharp
                    ] do
                    Expect.equal (Language.ofPath path) expected path
            }
        ]
