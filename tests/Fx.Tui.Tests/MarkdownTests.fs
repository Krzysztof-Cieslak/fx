module Fx.Tui.Tests.MarkdownTests

open Expecto
open Fx.Tui

/// A reply as agents write them: headings, lists, emphasis, inline code, a fenced block, a table.
let sample =
    """## Adding fast mode

I checked it with `fx variants explain`, which picked it up as **model.role.main**. Other ways:

- **One run only:** `fx --model anthropic/claude-opus-5-5`
- **One project only:** the same JSON in `<project>/.fx/config.json`
  - read after `fx trust`
- See [the docs](https://example.com/docs) or <https://example.com>.

1. Add a speed option to the request format (*src/Fx.Models*).
2. Send `speed: "fast"` when it is set.

> Don't use `model.role.fast` for this: it picks
> a separate model.

```fsharp
let config = { Model = "anthropic/claude-opus-5-5" } // the main model
printfn "%d" 42
```

| Slot | Value |
|---|---|
| `model.role.main` | anthropic/claude-opus-5-5 |
| `tui.theme` | dark |

---

Before the first prompt you still need a key: run `fx auth login anthropic`."""

let private texts (rows: Row list) =
    rows |> List.map (fun row -> (Row.text row).TrimEnd())

let private styled (rows: Row list) =
    rows
    |> List.map (fun row ->
        row
        |> List.map (fun span -> "{" + string span.Style + "}" + span.Text)
        |> String.concat "")

[<Tests>]
let tests =
    testList
        "markdown"
        [
            test "inlines: emphasis, strong, code, links, escapes" {
                let parsed =
                    Markdown.inlines "a **b** *c* `d **e**` [f](g) \\*h\\* snake_case_word ~~i~~ <https://x.y>"

                Expect.equal
                    (parsed |> List.map string |> String.concat "")
                    "a **b** *c* `d **e**` [f](g) *h* snake_case_word ~~i~~ [https://x.y](https://x.y)"
                    "parsed"
            }

            test "an unmatched delimiter stays text" {
                Expect.equal (Markdown.inlines "2 * 3 and a ` tick") [ Inline.Plain "2 * 3 and a ` tick" ] "text"
            }

            test "blocks" {
                let blocks = Markdown.parse sample |> List.map string

                Expect.equal
                    blocks
                    [
                        "heading 2"
                        "paragraph 5"
                        "list 3"
                        "ordered 2"
                        "quote 1"
                        "code fsharp"
                        "table 2x2"
                        "rule"
                        "paragraph 3"
                    ]
                    "every block"
            }

            test "an unclosed fence runs to the end (a reply still streaming)" {
                Expect.equal
                    (Markdown.parse "text\n\n```ts\nconst a = 1" |> List.map string)
                    [ "paragraph 1"; "code ts" ]
                    "code"
            }

            test "no row is wider than asked, at any width" {
                for width in [ 4; 10; 24; 40; 80 ] do
                    for row in Markdown.render Theme.ansi16 width sample do
                        Expect.isLessThanOrEqual (Row.width row) width $"width {width}: {Row.text row}"
            }

            test "highlighting: keywords, strings, comments, numbers" {
                Expect.equal
                    (Highlight.line "ts" "const x = \"a\" + 42 // note")
                    [
                        TokenKind.Keyword, "const"
                        TokenKind.Text, " x = "
                        TokenKind.String, "\"a\""
                        TokenKind.Text, " + "
                        TokenKind.Number, "42"
                        TokenKind.Text, " "
                        TokenKind.Comment, "// note"
                    ]
                    "tokens"

                Expect.equal (Highlight.line "text" "let x") [ TokenKind.Text, "let x" ] "unknown language"
                Expect.equal (Highlight.line "sh" "echo $HOME # x").[0] (TokenKind.Keyword, "echo") "shell"
            }

            testList
                "golden"
                [
                    for width in [ 80; 40; 24 ] do
                        test $"the sample at {width} columns" {
                            let rows = Markdown.render Theme.ansi16 width sample
                            Golden.expect $"markdown-{width}.txt" ((texts rows |> String.concat "\n") + "\n")
                        }

                    test "the sample's styles in the dark theme" {
                        let rows = Markdown.render Theme.dark 60 sample
                        Golden.expect "markdown-styles-dark.txt" ((styled rows |> String.concat "\n") + "\n")
                    }
                ]
        ]
