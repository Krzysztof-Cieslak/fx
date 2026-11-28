module Fx.TreeSitter.Tests.SyntaxTests

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.TreeSitter
open Fx.TreeSitter.Tests.Support

[<Tests>]
let tests =
    testList
        "Syntax"
        [
            test "every sample parses without errors" {
                for name in samples do
                    use tree = parseSample name
                    Expect.isFalse tree.Root.HasError name
                    Expect.isEmpty (Syntax.errors tree) name
                    Expect.equal tree.Root.StartByte 0 name
                    Expect.equal tree.Root.EndByte (sampleBytes name).Length name
            }

            test "root kinds" {
                let kinds =
                    samples
                    |> List.map (fun name ->
                        use tree = parseSample name
                        name, tree.Root.Kind)

                Expect.equal
                    kinds
                    [
                        "Panel.tsx", "program"
                        "Retry.cs", "compilation_unit"
                        "Retry.fs", "file"
                        "Retry.fsi", "file"
                        "retry.py", "module"
                        "retry.ts", "program"
                    ]
                    "kinds"
            }

            test "positions: one-based lines, byte columns, text by bytes" {
                use tree = parse Language.Python "x = 1\nif x:\n    y = 'é✓'\n"
                let statement = tree.Root.Child 1
                Expect.equal statement.Kind "if_statement" "kind"
                Expect.equal statement.Start { Line = 2; Column = 0 } "start"
                Expect.equal statement.End { Line = 3; Column = 15 } "end: 4 + 4 + quotes, é is 2 bytes, ✓ is 3"
                Expect.equal statement.Text "if x:\n    y = 'é✓'" "text"
            }

            test "CRLF counts lines as LF does" {
                use lf = parse Language.CSharp "class A\n{\n}\n"
                use crlf = parse Language.CSharp "class A\r\n{\r\n}\r\n"
                Expect.equal (lf.Root.Child 0).End.Line 3 "lf"
                Expect.equal (crlf.Root.Child 0).End.Line 3 "crlf"
                Expect.isFalse crlf.Root.HasError "no errors"
            }

            test "empty source" {
                for language in Language.all do
                    use tree = parse language ""
                    Expect.equal tree.Root.EndByte 0 (string language)
                    Expect.isFalse tree.Root.HasError (string language)
            }

            test "broken source parses, with error nodes where it is broken" {
                use tree =
                    parse Language.TypeScript "function ok() {}\nfunction broken( {\n\nconst x = 1;\n"

                Expect.isTrue tree.Root.HasError "has error"
                let errors = Syntax.errors tree
                Expect.isNonEmpty errors "errors"
                Expect.equal errors.Head.Start.Line 2 "first error line"
                Expect.equal (tree.Root.Child 0).Kind "function_declaration" "the good part"
            }

            test "invalid UTF-8 does not stop a parse" {
                let bytes =
                    Array.append (Encoding.UTF8.GetBytes "x = '") [| 0xffuy; 0xfeuy; byte '\''; byte '\n' |]

                use tree = Syntax.parse Language.Python bytes CancellationToken.None |> ok "parse"
                Expect.equal tree.Root.EndByte bytes.Length "spans all bytes"
            }

            test "a token cancelled before the parse" {
                use cancelled = new CancellationTokenSource()
                cancelled.Cancel()
                let result = Syntax.parse Language.FSharp (sampleBytes "Retry.fs") cancelled.Token
                Expect.equal (Result.map ignore result) (Error ParseError.Cancelled) "cancelled"
            }

            test "cancelling stops a long parse promptly, and parsing works afterwards" {
                let large = largeFSharp 20
                use cancel = new CancellationTokenSource()
                cancel.CancelAfter(TimeSpan.FromMilliseconds 100.0)
                let watch = Stopwatch.StartNew()
                let result = Syntax.parse Language.FSharp large cancel.Token
                watch.Stop()
                Expect.equal (Result.map ignore result) (Error ParseError.Cancelled) "cancelled"
                Expect.isLessThan watch.Elapsed (TimeSpan.FromSeconds 2.0) "promptly"

                use tree = parseSample "Retry.fs"
                Expect.isFalse tree.Root.HasError "a later parse"
            }

            test "a disposed tree's nodes throw instead of reading freed memory" {
                let tree = parseSample "retry.py"
                let root = tree.Root
                (tree :> IDisposable).Dispose()
                Expect.throwsT<ObjectDisposedException> (fun () -> root.Kind |> ignore) "kind"
                Expect.throwsT<ObjectDisposedException> (fun () -> tree.Root |> ignore) "root"
            }

            test "parses on many threads at once" {
                let expected =
                    samples
                    |> List.map (fun name ->
                        use tree = parseSample name
                        name, tree.Root.DescendantCount)

                let results =
                    Array.init 64 (fun i ->
                        Task.Run(fun () ->
                            let name, _ = expected[i % expected.Length]
                            use tree = parseSample name
                            name, tree.Root.DescendantCount))
                    |> Task.WhenAll

                for name, count in results.Result do
                    Expect.equal count (List.find (fun (n, _) -> n = name) expected |> snd) name
            }
        ]
