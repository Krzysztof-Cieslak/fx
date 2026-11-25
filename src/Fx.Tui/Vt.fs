namespace Fx.Tui

open System
open System.Text

/// The VT sequences the TUI writes (ECMA-48 and the xterm extensions every current terminal has,
/// Windows' console host included once virtual terminal processing is on): pure functions from
/// frames to the text that draws them.
[<RequireQualifiedAccess>]
module Vt =
    let private esc = "\u001b["

    /// Takes over the terminal: the alternate screen (the shell's scrollback stays as it was),
    /// no automatic wrap at the last column, bracketed paste, the cursor hidden until a frame shows it.
    let enter: string =
        esc
        + "?1049h"
        + esc
        + "?7l"
        + esc
        + "?2004h"
        + esc
        + "?25l"
        + esc
        + "H"
        + esc
        + "2J"

    /// Gives the terminal back as it was: styles reset, wrap and paste modes off, the cursor shown,
    /// the main screen again.
    let leave: string =
        esc + "0m" + esc + "?2004l" + esc + "?7h" + esc + "?25h" + esc + "?1049l"

    let private colorCode (background: bool) (color: Color) : int option =
        let basic =
            match color with
            | Color.Default -> None
            | Color.Black -> Some 30
            | Color.Red -> Some 31
            | Color.Green -> Some 32
            | Color.Yellow -> Some 33
            | Color.Blue -> Some 34
            | Color.Magenta -> Some 35
            | Color.Cyan -> Some 36
            | Color.White -> Some 37
            | Color.Gray -> Some 90
            | Color.BrightRed -> Some 91
            | Color.BrightGreen -> Some 92
            | Color.BrightYellow -> Some 93
            | Color.BrightBlue -> Some 94
            | Color.BrightMagenta -> Some 95
            | Color.BrightCyan -> Some 96
            | Color.BrightWhite -> Some 97

        basic |> Option.map (fun code -> if background then code + 10 else code)

    /// The SGR sequence that sets a style from scratch.
    let sgr (style: Style) : string =
        let codes =
            [
                0
                if style.Bold then
                    1
                if style.Dim then
                    2
                if style.Italic then
                    3
                if style.Underline then
                    4
                if style.Reverse then
                    7
                match colorCode false style.Fg with
                | Some code -> code
                | None -> ()
                match colorCode true style.Bg with
                | Some code -> code
                | None -> ()
            ]

        esc + (codes |> List.map string |> String.concat ";") + "m"

    /// Moves the cursor to a row and column (from 0).
    let moveTo (row: int) (column: int) : string = $"{esc}{row + 1};{column + 1}H"

    let private writeRow (sb: StringBuilder) (index: int) (row: Row) =
        sb.Append(moveTo index 0) |> ignore
        let mutable current = Style.plain
        sb.Append(esc + "0m") |> ignore

        for span in row do
            if span.Style <> current then
                sb.Append(sgr span.Style) |> ignore
                current <- span.Style

            sb.Append span.Text |> ignore

        // Clear the rest of the line in the default style.
        if current <> Style.plain then
            sb.Append(esc + "0m") |> ignore

        sb.Append(esc + "K") |> ignore

    /// What draws `next` over a screen that shows `previous` (`None`: unknown, so everything is drawn
    /// after clearing it): only the rows that changed, inside a synchronized update so the terminal
    /// shows the frame at once. Empty when nothing changed.
    let diff (previous: Frame option) (next: Frame) : string =
        let sb = StringBuilder()

        let full =
            match previous with
            | Some p -> p.Width <> next.Width || p.Height <> next.Height
            | None -> true

        let before =
            match previous with
            | Some p when not full -> Array.ofList p.Rows
            | _ -> [||]

        if full then
            sb.Append(esc + "0m" + esc + "H" + esc + "2J") |> ignore

        next.Rows
        |> List.iteri (fun i row ->
            let changed = full || i >= before.Length || before[i] <> row

            if changed && not (full && row.IsEmpty) then
                writeRow sb i row)

        let cursorChanged =
            match previous with
            | Some p -> p.Cursor <> next.Cursor
            | None -> true

        if sb.Length = 0 && not cursorChanged then
            ""
        else
            let body = sb.ToString()

            let cursor =
                match next.Cursor with
                | Some(row, column) -> moveTo row column + esc + "?25h"
                | None -> esc + "?25l"

            esc + "?2026h" + esc + "?25l" + body + cursor + esc + "?2026l"

    /// A text with escape and control characters shown as `\e`, `\r`, `\n`: for scenarios and logs.
    let visible (text: string) : string =
        let sb = StringBuilder()

        for c in text do
            match c with
            | '\u001b' -> sb.Append "\\e" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | c when Char.IsControl c -> sb.Append("\\x" + (int c).ToString("x2")) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()
