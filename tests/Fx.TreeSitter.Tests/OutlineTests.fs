module Fx.TreeSitter.Tests.OutlineTests

open System.IO
open System.Text
open System.Threading
open Expecto
open Fx.TreeSitter
open Fx.TreeSitter.Tests.Support

[<Tests>]
let tests =
    testList
        "Outline"
        [
            test "every outline query compiles" {
                for language in Language.all do
                    Expect.isGreaterThan (Outline.query language).PatternCount 0 (string language)
            }

            testList
                "samples"
                [
                    for name in samples ->
                        test name {
                            use tree = parseSample name
                            let expected = File.ReadAllText(Path.Combine(samplesDir, name + ".outline"))
                            Expect.equal (Outline.render (Outline.ofTree tree)) expected "outline"
                        }
                ]

            test "CRLF sources give the same outline" {
                for name in samples do
                    let lf = Encoding.UTF8.GetString(sampleBytes name)
                    let crlf = Encoding.UTF8.GetBytes(lf.Replace("\n", "\r\n"))
                    use a = parseSample name

                    use b = Syntax.parse (sampleLanguage name) crlf CancellationToken.None |> ok "parse"

                    Expect.equal (Outline.render (Outline.ofTree b)) (Outline.render (Outline.ofTree a)) name
            }

            test "items: kinds, names, lines, nesting" {
                use tree = parseSample "Retry.fs"
                let items = Outline.ofTree tree
                let top = List.exactlyOne items
                Expect.equal (top.Kind, top.Name, top.StartLine) ("module", "Billing.Retry", 2) "module"

                let clock = top.Children |> List.find (fun i -> i.Name = "Clock")
                Expect.equal clock.Kind "type" "type"

                Expect.equal
                    (clock.Children |> List.map (fun i -> i.Kind, i.Name, i.StartLine))
                    [ "member", "Now", 31; "member", "Wait", 32; "member", "Default", 33 ]
                    "members"
            }

            test "long headers are cut" {
                use tree =
                    parse Language.Python ("def f(" + String.replicate 200 "a, " + "z): pass\n")

                let item = Outline.ofTree tree |> List.exactlyOne
                Expect.equal item.Header.Length Outline.HeaderMaxChars "length"
                Expect.stringEnds item.Header "…" "ellipsis"
            }

            test "a broken file still has an outline of what parses" {
                // The unclosed class becomes an ERROR node; what is inside it still parses.
                use tree = parse Language.CSharp "class A {\n  void M() {\n}\nclass B { int x; }\n"
                Expect.isTrue tree.Root.HasError "broken"
                let items = Outline.ofTree tree

                let names =
                    items |> List.collect (fun i -> i.Name :: (i.Children |> List.map _.Name))

                Expect.equal names [ "M"; "B"; "x" ] "names"
            }
        ]
