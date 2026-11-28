module Fx.TreeSitter.Tests.QueryTests

open Expecto
open Fx.TreeSitter
open Fx.TreeSitter.Tests.Support

let private compileError (language: Language) (source: string) : QueryError =
    match Query.compile language source with
    | Ok _ -> failtest ("compiled: " + source)
    | Error error -> error

[<Tests>]
let tests =
    testList
        "Query"
        [
            test "errors say what is wrong and where" {
                Expect.equal
                    (compileError Language.Python "(function_definition name: (identifier) @name")
                    {
                        Kind = QueryErrorKind.Syntax
                        Offset = 45
                    }
                    "unclosed"

                Expect.equal
                    (compileError Language.Python "(class_definition) (no_such_node)")
                    {
                        Kind = QueryErrorKind.NodeType
                        Offset = 20
                    }
                    "node type"

                Expect.equal
                    (compileError Language.Python "(class_definition nope: (identifier))")
                    {
                        Kind = QueryErrorKind.Field
                        Offset = 18
                    }
                    "field"
            }

            test "capture names and pattern count" {
                use query =
                    Query.compile Language.Python "(class_definition name: (_) @name) @class\n(function_definition) @fn"
                    |> ok "compile"

                Expect.equal query.CaptureNames [| "name"; "class"; "fn" |] "names"
                Expect.equal query.PatternCount 2 "patterns"
            }

            test "matches in source order, with their captures in node order" {
                use tree = parse Language.Python "def a(): pass\nclass B:\n    def c(self): pass\n"

                use query =
                    Query.compile
                        Language.Python
                        "(function_definition name: (_) @name) @fn\n(class_definition name: (_) @name) @cls"
                    |> ok "compile"

                let found =
                    Query.matches query tree.Root
                    |> List.map (fun m ->
                        m.Pattern, m.Captures |> List.map (fun c -> c.Name, c.Node.Text.Split('\n')[0]))

                Expect.equal
                    found
                    [
                        0, [ "fn", "def a(): pass"; "name", "a" ]
                        1, [ "cls", "class B:"; "name", "B" ]
                        0, [ "fn", "def c(self): pass"; "name", "c" ]
                    ]
                    "matches"
            }

            test "a query runs on its own language only" {
                use tree = parse Language.TypeScript "let x = 1;"
                use query = Query.compile Language.Tsx "(program) @p" |> ok "compile"
                Expect.throwsT<System.ArgumentException> (fun () -> Query.matches query tree.Root |> ignore) "mismatch"
            }
        ]
