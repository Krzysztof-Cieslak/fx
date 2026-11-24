module Fx.Analyzers.Tests.ConfigTests

open System.IO
open Expecto
open Fx.Analyzers

let private codes = Rules.all |> List.map (fun r -> r.Code)

let private entry codes files : Config.Entry =
    {
        Codes = codes
        Files = files
        Reason = "test"
    }

let private parseErrors json =
    match Config.parse codes json with
    | Ok config -> failwith $"expected errors, parsed {config}"
    | Error errors -> errors

[<Tests>]
let tests =
    testList
        "Config"
        [
            testList
                "globs"
                [
                    test "** spans directories, * and ? stay within one" {
                        let cases =
                            [
                                "src/**", "src/Fx.Cli/Program.fs", true
                                "src/**", "src/a.fs", true
                                "src/**", "srcx/a.fs", false
                                "src/**", "tests/src/a.fs", false
                                "**/*.fs", "a.fs", true
                                "**/*.fs", "src/x/a.fs", true
                                "**/*.fs", "src/x/a.fsx", false
                                "src/*.fs", "src/a.fs", true
                                "src/*.fs", "src/x/a.fs", false
                                "src/?.fs", "src/a.fs", true
                                "src/?.fs", "src/ab.fs", false
                                "src/Fx.Cli/Program.fs", "src/Fx.Cli/Program.fs", true
                                "src/Fx.Cli/Program.fs", "src/Fx.Cli/Program.fsi", false
                            ]

                        for glob, path, expected in cases do
                            Expect.equal (Config.globMatches glob path) expected $"{glob} vs {path}"
                    }

                    test "matching ignores case" { Expect.isTrue (Config.globMatches "src/**" "SRC/A.fs") "case" }
                ]

            test "code patterns are exact or a prefix with *" {
                Expect.isTrue (Config.codeMatches "EFF*" "EFF005") "prefix"
                Expect.isTrue (Config.codeMatches "EFF005" "EFF005") "exact"
                Expect.isFalse (Config.codeMatches "EFF005" "EFF001") "other code"
                Expect.isFalse (Config.codeMatches "EFF*" "AOT001") "other prefix"
            }

            testList
                "reports"
                [
                    let config: Config.Config =
                        {
                            Scope = [ entry [ "EFF*" ] [ "src/**" ] ]
                            Allow = [ entry [ "EFF005" ] [ "src/Main.fs" ] ]
                        }

                    test "a rule no scope entry names applies everywhere" {
                        Expect.isTrue (Config.reports config "AOT001" "tests/A.fs") "tests"
                        Expect.isTrue (Config.reports config "AOT001" "src/A.fs") "src"
                    }

                    test "a scoped rule applies only inside its scope" {
                        Expect.isTrue (Config.reports config "EFF001" "src/A.fs") "inside"
                        Expect.isFalse (Config.reports config "EFF001" "tests/A.fs") "outside"
                    }

                    test "an allow entry suppresses only its codes in its files" {
                        Expect.isFalse (Config.reports config "EFF005" "src/Main.fs") "allowed"
                        Expect.isTrue (Config.reports config "EFF001" "src/Main.fs") "other code"
                        Expect.isTrue (Config.reports config "EFF005" "src/Other.fs") "other file"
                    }

                    test "without a config every rule is reported" {
                        Expect.isTrue (Config.reports Config.empty "EFF005" "anywhere.fs") "empty"
                    }
                ]

            testList
                "parse"
                [
                    test "reads scope and allow entries" {
                        let json =
                            """{ "scope": [ { "codes": ["EFF*"], "files": ["src/**"] } ],
                                 "allow": [ { "codes": ["EFF005"], "files": ["src/Main.fs"], "reason": "entry point" } ] }"""

                        let expected: Config.Config =
                            {
                                Scope =
                                    [
                                        { entry [ "EFF*" ] [ "src/**" ] with
                                            Reason = ""
                                        }
                                    ]
                                Allow =
                                    [
                                        { entry [ "EFF005" ] [ "src/Main.fs" ] with
                                            Reason = "entry point"
                                        }
                                    ]
                            }

                        Expect.equal (Config.parse codes json) (Ok expected) "config"
                    }

                    test "an empty object is the empty config" {
                        Expect.equal (Config.parse codes "{}") (Ok Config.empty) "empty"
                    }

                    test "allow entries need a reason" {
                        let errors =
                            parseErrors """{ "allow": [ { "codes": ["EFF005"], "files": ["a.fs"] } ] }"""

                        Expect.exists errors (fun e -> e.Contains "\"reason\" is required") "reason"
                    }

                    test "unknown codes are rejected, so typos cannot allow nothing" {
                        let errors =
                            parseErrors """{ "allow": [ { "codes": ["EFF05"], "files": ["a.fs"], "reason": "x" } ] }"""

                        Expect.equal errors [ "unknown rule code \"EFF05\"" ] "errors"
                    }

                    test "every problem is reported at once" {
                        let errors =
                            parseErrors """{ "allow": [ { "codes": [], "file": ["a.fs"] } ], "extra": 1 }"""

                        Expect.equal
                            errors
                            [
                                "allow[0]: \"codes\" must not be empty"
                                "allow[0]: \"files\" is required"
                                "allow[0]: \"reason\" is required: say why this code may break the rule"
                                "allow[0]: unknown property \"file\""
                                "unknown property \"extra\""
                            ]
                            "errors"
                    }

                    test "invalid JSON is an error, not an exception" {
                        let errors = parseErrors "{ \"allow\": "
                        Expect.isTrue (errors.Head.StartsWith "invalid JSON") "message"
                    }
                ]

            testList
                "the repository's analyzers.json"
                [
                    let config =
                        lazy
                            (match Config.parse codes (File.ReadAllText(Path.Combine(Repo.root, Config.FileName))) with
                             | Ok config -> config
                             | Error errors -> failwith (String.concat "; " errors))

                    test "is valid" { Expect.isNonEmpty config.Value.Allow "allow entries" }

                    test "every glob matches a file, so stale entries do not linger" {
                        let files = Repo.files ()

                        for e in config.Value.Scope @ config.Value.Allow do
                            for glob in e.Files do
                                Expect.exists files (Config.globMatches glob) $"{glob} matches no file"
                    }

                    test "production code is in scope of every rule" {
                        for rule in Rules.all do
                            Expect.isTrue
                                (Config.reports config.Value rule.Code "src/Fx.Prelude/Product.fs")
                                $"{rule.Code} in src/"
                    }

                    test "EFF rules do not apply to tests and tools" {
                        Expect.isFalse
                            (Config.reports config.Value Rules.filesystem.Code "tests/Fx.Cli.Tests/CliTests.fs")
                            "tests"

                        Expect.isFalse
                            (Config.reports config.Value Rules.filesystem.Code "tools/Fx.Analyzers/Config.fs")
                            "tools"
                    }
                ]
        ]
