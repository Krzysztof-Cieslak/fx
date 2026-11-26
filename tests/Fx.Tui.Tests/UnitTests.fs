module Fx.Tui.Tests.UnitTests

open Expecto
open Fx.Tui

let private keys (input: string) =
    let decoded, rest = Keys.decode "" input
    decoded |> List.map string, rest

[<Tests>]
let tests =
    testList
        "TUI units"
        [
            testList
                "text"
                [
                    test "widths: ASCII, wide CJK, emoji, combining marks, controls" {
                        Expect.equal (Text.width "hello") 5 "ASCII"
                        Expect.equal (Text.width "日本語") 6 "CJK is wide"
                        Expect.equal (Text.width "한국어") 6 "Hangul is wide"
                        Expect.equal (Text.width "😀") 2 "emoji"
                        Expect.equal (Text.width "e\u0301") 1 "a combining accent takes no column"
                        Expect.equal (Text.width "👍🏽") 2 "an emoji with a skin tone is one picture"
                        Expect.equal (Text.width "❤\uFE0F") 2 "an emoji presentation selector"
                        Expect.equal (Text.width "a\u0007b") 2 "a control character is never drawn"
                        Expect.equal (Text.width "◇ ◆ ▸ ✓ ✗") 9 "the activity symbols are narrow"
                    }

                    test "truncating and padding by columns" {
                        Expect.equal (Text.truncate 5 "hello world") "hell…" "cut with an ellipsis"
                        Expect.equal (Text.truncate 11 "hello world") "hello world" "fits"
                        Expect.equal (Text.truncate 4 "日本語") "日…" "a wide character is never cut in half"
                        Expect.equal (Text.pad 6 "日本") "日本  " "padded to the columns"
                        Expect.equal (Text.take 3 "日本語") "日" "the prefix that fits"
                    }

                    test "wrapping at spaces, inside long words, at line ends" {
                        Expect.equal
                            (Text.wrap 10 "the quick brown fox jumps")
                            [ "the quick"; "brown fox"; "jumps" ]
                            "words"

                        Expect.equal (Text.wrap 4 "abcdefghij") [ "abcd"; "efgh"; "ij" ] "a long word"
                        Expect.equal (Text.wrap 10 "one\ntwo\r\nthree") [ "one"; "two"; "three" ] "line ends"
                        Expect.equal (Text.wrap 5 "日本語のテキスト") [ "日本"; "語の"; "テキ"; "スト" ] "wide characters"
                        Expect.equal (Text.wrap 10 "a\tb") [ "a    b" ] "tabs become spaces"
                    }

                    test "rows fit the width and end with an ellipsis" {
                        let row = [ Span.plain "abc"; Span.create Style.bold "defgh" ]
                        Expect.equal (Row.text (Row.fit 6 row)) "abcde…" "cut"
                        Expect.equal (Row.width (Row.fit 6 row)) 6 "six columns"

                        Expect.equal
                            (Row.text (Row.spread 12 [ Span.plain "left" ] [ Span.plain "right" ]))
                            "left   right"
                            "spread"

                        Expect.equal
                            (Row.text (Row.spread 8 [ Span.plain "a long left" ] [ Span.plain "right" ]))
                            "a… right"
                            "the left side gives way"
                    }
                ]

            testList
                "keys"
                [
                    test "printable text, Enter, Tab, Backspace, Ctrl" {
                        Expect.equal (keys "hi\r") ([ "h"; "i"; "Enter" ], "") "text and Enter"
                        Expect.equal (keys "\t\u007f\u0003") ([ "Tab"; "Backspace"; "Ctrl+c" ], "") "controls"
                        Expect.equal (keys "\n") ([ "Ctrl+j" ], "") "Ctrl+J is not Enter"
                        Expect.equal (keys "日😀") ([ "日"; "😀" ], "") "wide characters"
                    }

                    test "escape sequences: arrows, Home/End, Delete, pages, modifiers" {
                        Expect.equal
                            (keys "\u001b[A\u001bOB\u001b[C\u001b[D")
                            ([ "Up"; "Down"; "Right"; "Left" ], "")
                            "arrows"

                        Expect.equal
                            (keys "\u001b[H\u001b[F\u001b[1~\u001b[4~")
                            ([ "Home"; "End"; "Home"; "End" ], "")
                            "Home and End"

                        Expect.equal
                            (keys "\u001b[3~\u001b[5~\u001b[6~")
                            ([ "Delete"; "PageUp"; "PageDown" ], "")
                            "editing keys"

                        Expect.equal
                            (keys "\u001b[1;5C\u001b[1;3D\u001b[Z")
                            ([ "Ctrl+Right"; "Alt+Left"; "Shift+Tab" ], "")
                            "modifiers"

                        Expect.equal (keys "\u001b[13;2u\u001b[13;5u") ([ "Shift+Enter"; "Ctrl+Enter" ], "") "CSI u"
                        Expect.equal (keys "\u001b[27;2;13~") ([ "Shift+Enter" ], "") "modifyOtherKeys"
                        Expect.equal (keys "\u001b\r\u001bb") ([ "Alt+Enter"; "Alt+b" ], "") "Alt"
                        Expect.equal (keys "\u001b\u0003") ([ "Esc"; "Ctrl+c" ], "") "Escape, then Ctrl+C"
                    }

                    test
                        "the mouse wheel: SGR reports with their modifiers, several in a read; other reports are not keys" {
                        Expect.equal
                            (keys "\u001b[<64;10;5M\u001b[<65;10;5M\u001b[<64;1;1M")
                            ([ "WheelUp"; "WheelDown"; "WheelUp" ], "")
                            "notches up and down, several in one read"

                        Expect.equal
                            (keys "\u001b[<68;1;1M\u001b[<73;1;1M\u001b[<80;1;1M")
                            ([ "Shift+WheelUp"; "Alt+WheelDown"; "Ctrl+WheelUp" ], "")
                            "modifiers"

                        Expect.equal
                            (keys
                                "\u001b[<0;3;4M\u001b[<0;3;4m\u001b[<66;3;4M\u001b[<67;3;4M\u001b[<96;3;4M\u001b[<64;3;4m")
                            ([
                                "Unknown(\\e[<0;3;4M)"
                                "Unknown(\\e[<0;3;4m)"
                                "Unknown(\\e[<66;3;4M)"
                                "Unknown(\\e[<67;3;4M)"
                                "Unknown(\\e[<96;3;4M)"
                                "Unknown(\\e[<64;3;4m)"
                             ],
                             "")
                            "a click and its release, the horizontal wheel, motion, a release are not the wheel"

                        Expect.equal
                            (keys "\u001b[M`!!\u001b[Ma!!x")
                            ([ "WheelUp"; "WheelDown"; "x" ], "")
                            "xterm's first encoding (terminals without SGR's)"

                        Expect.equal (keys "\u001b[M !!") ([ "Unknown(\\e[M !!)" ], "") "a click in it"
                    }

                    test "a mouse report cut between reads waits for its rest, also past the Escape wait" {
                        let first, rest = Keys.decode "" "a\u001b[<64;1"
                        Expect.equal (first |> List.map string) [ "a" ] "before the cut"
                        Expect.equal rest "\u001b[<64;1" "pending"
                        Expect.equal (Keys.flush rest) ([], rest) "not typed as text when the wait ends"
                        let second, rest = Keys.decode rest "0;5M"
                        Expect.equal (second |> List.map string) [ "WheelUp" ] "completed"
                        Expect.equal rest "" "nothing left"
                        let first, rest = Keys.decode "" "\u001b[Ma"
                        Expect.isEmpty first "the first encoding, cut"
                        Expect.equal (Keys.flush rest) ([], rest) "waits too"
                        let second, _ = Keys.decode rest "!!"
                        Expect.equal (second |> List.map string) [ "WheelDown" ] "completed"

                        Expect.equal
                            (Keys.flush "\u001b[1;" |> fst |> List.map string)
                            [ "Unknown(\\e[1;)" ]
                            "a key's sequence is not held"
                    }

                    test "a lone Escape waits, then is Escape; a cut sequence waits for the rest" {
                        Expect.equal (keys "\u001b") ([], "\u001b") "pending"
                        Expect.equal (Keys.flush "\u001b" |> fst |> List.map string) [ "Esc" ] "flushed"
                        let first, rest = Keys.decode "" "a\u001b[1;"
                        Expect.equal (first |> List.map string) [ "a" ] "before the cut"
                        let second, rest = Keys.decode rest "5A"
                        Expect.equal (second |> List.map string) [ "Ctrl+Up" ] "completed"
                        Expect.equal rest "" "nothing left"
                    }

                    test "bracketed paste arrives whole, also across reads" {
                        let first, rest = Keys.decode "" "\u001b[200~line one\rline"
                        Expect.isEmpty first "waits for the end"
                        let second, rest = Keys.decode rest " two\u001b[201~x"

                        Expect.equal
                            second
                            [ Keys.key (KeyCode.Paste "line one\rline two"); Keys.char "x" ]
                            "the paste, then a key"

                        Expect.equal rest "" "nothing left"

                        Expect.equal
                            (Keys.flush "\u001b[200~abc")
                            ([], "\u001b[200~abc")
                            "a paste is never flushed early"
                    }

                    test "reports are not keys" {
                        let decoded, _ = Keys.decode "" "\u001b[<0;10;5M\u001b[?1;2c"

                        Expect.isTrue
                            (decoded
                             |> List.forall (fun k ->
                                 match k.Code with
                                 | KeyCode.Unknown _ -> true
                                 | _ -> false))
                            "unknown"
                    }
                ]

            testList
                "colours"
                [
                    test "the depth comes from the process's variables" {
                        let detect vars = ColorDepth.detect vars false

                        Expect.equal
                            (detect [ "NO_COLOR", "1"; "COLORTERM", "truecolor" ])
                            ColorDepth.NoColor
                            "NO_COLOR wins"

                        Expect.equal (detect [ "NO_COLOR", "" ]) ColorDepth.Basic "an empty NO_COLOR is not set"
                        Expect.equal (detect [ "COLORTERM", "24bit" ]) ColorDepth.TrueColor "COLORTERM"
                        Expect.equal (detect [ "TERM_PROGRAM", "iTerm.app" ]) ColorDepth.TrueColor "iTerm2"
                        Expect.equal (detect [ "WT_SESSION", "x" ]) ColorDepth.TrueColor "Windows Terminal"
                        Expect.equal (detect [ "TERM", "xterm-256color" ]) ColorDepth.Indexed "256 colours"
                        Expect.equal (detect [ "TERM", "xterm" ]) ColorDepth.Basic "16"
                        Expect.equal (detect [ "TERM", "dumb" ]) ColorDepth.NoColor "dumb"
                        Expect.equal (ColorDepth.detect [] true) ColorDepth.TrueColor "the Windows console"
                    }

                    test "COLORFGBG says whether the background is light" {
                        Expect.equal (Terminal.detect [ "COLORFGBG", "0;15" ] false).LightBackground (Some true) "light"

                        Expect.equal
                            (Terminal.detect [ "COLORFGBG", "15;default;0" ] false).LightBackground
                            (Some false)
                            "dark"

                        Expect.equal (Terminal.detect [ "COLORFGBG", "x" ] false).LightBackground None "unknown"
                    }

                    test "colours are lowered to what the terminal shows" {
                        let rgb = Color.Rgb(255, 0, 0)
                        Expect.equal (Color.lower ColorDepth.TrueColor rgb) rgb "as is"
                        Expect.equal (Color.lower ColorDepth.Indexed rgb) (Color.Indexed 196) "the cube's red"
                        Expect.equal (Color.lower ColorDepth.Basic rgb) Color.BrightRed "the nearest of 16"
                        Expect.equal (Color.lower ColorDepth.NoColor rgb) Color.Default "none"

                        Expect.equal
                            (Color.lower ColorDepth.Indexed (Color.Rgb(128, 128, 128)))
                            (Color.Indexed 244)
                            "a gray"

                        Expect.equal (Color.lower ColorDepth.Basic (Color.Indexed 1)) Color.Red "the palette's first 16"
                        Expect.equal (Color.lower ColorDepth.NoColor Color.Cyan) Color.Default "no colour at all"
                    }

                    test "SGR for the palette and 24-bit colours" {
                        let style =
                            { Style.plain with
                                Fg = Color.Rgb(1, 2, 3)
                                Bg = Color.Indexed 236
                                Bold = true
                            }

                        Expect.equal (Vt.visible (Vt.sgr style)) "\\e[0;1;38;2;1;2;3;48;5;236m" "codes"
                    }

                    test "a painted theme needs 256 colours" {
                        let on depth light =
                            {
                                Depth = depth
                                LightBackground = light
                            }

                        Expect.equal
                            (Theme.choose ThemeChoice.Auto (on ColorDepth.TrueColor None)).Name
                            "dark"
                            "dark by default"

                        Expect.equal
                            (Theme.choose ThemeChoice.Auto (on ColorDepth.Indexed (Some true))).Name
                            "light"
                            "a light terminal"

                        Expect.equal
                            (Theme.choose ThemeChoice.Light (on ColorDepth.Basic None)).Name
                            "ansi16"
                            "16 colours"

                        Expect.equal
                            (Theme.choose ThemeChoice.Ansi16 (on ColorDepth.TrueColor None)).Name
                            "ansi16"
                            "asked for"
                    }
                ]

            testList
                "editor"
                [
                    test "inserting, moving and deleting over grapheme clusters" {
                        let e = Editor.empty |> Editor.insert "ae\u0301日" |> Editor.left
                        Expect.equal e.Cursor 3 "before the wide character"
                        let e = e |> Editor.left
                        Expect.equal e.Cursor 1 "the accented e is one character"
                        let e = e |> Editor.delete
                        Expect.equal e.Text "a日" "deleted whole"
                        let e = Editor.ofText "x👍🏽" |> Editor.backspace
                        Expect.equal e.Text "x" "the emoji and its modifier go together"
                    }

                    test "lines: newline, up and down by column, home and end, kill" {
                        let e = Editor.ofText "first line" |> Editor.newline |> Editor.insert "two"
                        Expect.equal (Editor.line e) (1, 2) "second of two lines"
                        let e = e |> Editor.up
                        Expect.equal e.Cursor 3 "same column above"
                        let e = e |> Editor.``end``
                        Expect.equal e.Cursor 10 "end of the first line"
                        let e = e |> Editor.down
                        Expect.equal e.Cursor 14 "end of the shorter line"
                        let e = e |> Editor.home |> Editor.killToEnd
                        Expect.equal e.Text "first line\n" "killed to the end"
                        let e = Editor.ofText "one two  three" |> Editor.deleteWord
                        Expect.equal e.Text "one two  " "deleted a word"
                    }

                    test "paste: line ends normalized, controls dropped" {
                        let e = Editor.empty |> Editor.insert "a\r\nb\u001b[31mc"
                        Expect.equal e.Text "a\nb[31mc" "cleaned"
                    }

                    test "layout wraps at the column and places the cursor" {
                        let e = Editor.ofText "abcdefgh"
                        Expect.equal (Editor.layout 4 e) ([ "abcd"; "efgh"; "" ], (2, 0)) "the cursor after a full row"
                        let e = Editor.ofText "日本語" |> Editor.left

                        Expect.equal
                            (Editor.layout 5 e)
                            ([ "日本"; "語" ], (1, 0))
                            "a wide character goes to the next row whole"
                    }
                ]

            testList
                "screen"
                [
                    test "a frame drawn on a terminal shows its rows" {
                        let frame: Frame =
                            {
                                Width = 12
                                Height = 3
                                Rows =
                                    [
                                        [ Span.create Style.reverse "header" ]
                                        [ Span.plain "日本 "; Span.create (Style.fg Color.Green) "✓" ]
                                        []
                                    ]
                                Cursor = Some(2, 4)
                            }

                        let screen = VtScreen(12, 3)
                        screen.Write Vt.enter
                        screen.Write(Vt.diff None frame)
                        Expect.equal screen.Lines (Frame.lines frame) "the rows"
                        Expect.equal screen.Cursor (2, 4) "the cursor"
                        Expect.isTrue screen.CursorVisible "shown"
                        Expect.isTrue screen.OnAlternate "the alternate screen"
                        Expect.isTrue screen.Mouse "mouse reports (the wheel)"
                        Expect.stringContains Vt.enter "\u001b[?1006h\u001b[?1000h" "SGR's encoding before the reports"
                        screen.Write Vt.leave
                        Expect.isFalse screen.OnAlternate "back"
                        Expect.isFalse screen.Mouse "no mouse reports"

                        Expect.stringContains
                            Vt.leave
                            "\u001b[?1000l\u001b[?1006l"
                            "the reports off before the encoding"
                    }

                    test "the difference from the last frame redraws only the rows that changed" {
                        let row (text: string) = [ Span.plain text ]

                        let a: Frame =
                            {
                                Width = 10
                                Height = 3
                                Rows = [ row "one"; row "two"; row "three" ]
                                Cursor = None
                            }

                        let b =
                            { a with
                                Rows = [ row "one"; row "2"; row "three" ]
                            }

                        let text = Vt.diff (Some a) b
                        Expect.stringContains text (Vt.moveTo 1 0) "row 2 moved to"
                        Expect.isFalse (text.Contains(Vt.moveTo 0 0)) "row 1 untouched"
                        Expect.isFalse (text.Contains "three") "row 3 untouched"
                        Expect.equal (Vt.diff (Some b) b) "" "nothing changed, nothing written"
                        let screen = VtScreen(10, 3)
                        screen.Write(Vt.diff None a)
                        screen.Write text
                        Expect.equal screen.Lines [ "one"; "2"; "three" ] "updated in place"
                    }

                    test "styles are SGR codes of the 16 colours" {
                        Expect.equal
                            (Vt.sgr
                                { Style.plain with
                                    Fg = Color.Red
                                    Bold = true
                                })
                            "\u001b[0;1;31m"
                            "bold red"

                        Expect.equal
                            (Vt.sgr (Style.withBg Color.Yellow (Style.fg Color.Black)))
                            "\u001b[0;30;43m"
                            "on yellow"
                    }
                ]
        ]
