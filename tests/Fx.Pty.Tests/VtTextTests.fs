module Fx.Pty.Tests.VtTextTests

open Expecto
open Fx.Pty

[<Tests>]
let tests =
    testList
        "VtText"
        [
            test "keeps plain text, tabs and line endings" {
                Expect.equal (VtText.strip "a\tb\r\nc\n") "a\tb\r\nc\n" "unchanged"
            }

            test "removes CSI sequences" {
                Expect.equal
                    (VtText.strip "\u001b[31mred\u001b[0m \u001b[?25l\u001b[12;40H!")
                    "red !"
                    "SGR, modes, cursor"

                Expect.equal (VtText.strip "\u009b1mbold") "bold" "8-bit CSI"
            }

            test "removes OSC strings ended by BEL or ST" {
                Expect.equal (VtText.strip "\u001b]0;title\u0007x") "x" "BEL"
                Expect.equal (VtText.strip "\u001b]8;;http://e.x\u001b\\link\u001b]8;;\u001b\\") "link" "ST"
                Expect.equal (VtText.strip "\u009d0;t\u0007y") "y" "8-bit OSC"
            }

            test "removes DCS, APC and character set designations" {
                Expect.equal (VtText.strip "\u001bP1$r0m\u001b\\a\u001b_x\u001b\\b") "ab" "DCS and APC"
                Expect.equal (VtText.strip "\u001b(Bc\u001b7d\u001b8") "cd" "charset, save and restore cursor"
            }

            test "drops other control characters and unterminated sequences" {
                Expect.equal (VtText.strip "a\u0007b\u0008c\u0000") "abc" "BEL, BS, NUL"
                Expect.equal (VtText.strip "x\u001b[12") "x" "unterminated CSI"
                Expect.equal (VtText.strip "x\u001b") "x" "lone ESC"
                Expect.equal (VtText.strip "x\u001b]0;never ended") "x" "unterminated OSC"
            }

            test "plain text of a ConPTY frame" {
                let frame =
                    "\u001b[?9001h\u001b[?1004h\u001b[?25l\u001b[2J\u001b[m\u001b[H@@size=100x30\r\n"
                    + "\u001b]0;C:\\Program Files\\PowerShell\\7\\pwsh.exe\u0007\u001b[?25h"

                Expect.equal (VtText.plain frame) "@@size=100x30\n" "plain"
            }

            test "lines are split on any line ending and trimmed" {
                Expect.equal (VtText.lines "a  \r\nb\rc\n\u001b[1md") [ "a"; "bc"; "d" ] "lines"
            }
        ]
