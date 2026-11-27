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
    /// no automatic wrap at the last column, bracketed paste, mouse reports in the SGR encoding (for
    /// the wheel: the alternate screen has no scrollback, and terminals would send the wheel as `↑`
    /// `↓`; selecting text takes Shift), the cursor hidden until a frame shows it.
    let enter: string =
        esc
        + "?1049h"
        + esc
        + "?7l"
        + esc
        + "?2004h"
        // The encoding first, so no report comes in the old one.
        + esc
        + "?1006h"
        + esc
        + "?1000h"
        + esc
        + "?25l"
        + esc
        + "H"
        + esc
        + "2J"

    /// Gives the terminal back as it was: styles reset, mouse reports, wrap and paste modes off, the
    /// cursor shown, the main screen again.
    let leave: string =
        esc
        + "0m"
        + esc
        + "?1000l"
        + esc
        + "?1006l"
        + esc
        + "?2004l"
        + esc
        + "?7h"
        + esc
        + "?25h"
        + esc
        + "?1049l"

    let private colorCodes (background: bool) (color: Color) : int list =
        let basic (code: int) =
            [ (if background then code + 10 else code) ]

        let extended = if background then 48 else 38

        match color with
        | Color.Default -> []
        | Color.Black -> basic 30
        | Color.Red -> basic 31
        | Color.Green -> basic 32
        | Color.Yellow -> basic 33
        | Color.Blue -> basic 34
        | Color.Magenta -> basic 35
        | Color.Cyan -> basic 36
        | Color.White -> basic 37
        | Color.Gray -> basic 90
        | Color.BrightRed -> basic 91
        | Color.BrightGreen -> basic 92
        | Color.BrightYellow -> basic 93
        | Color.BrightBlue -> basic 94
        | Color.BrightMagenta -> basic 95
        | Color.BrightCyan -> basic 96
        | Color.BrightWhite -> basic 97
        | Color.Indexed index -> [ extended; 5; max 0 (min 255 index) ]
        | Color.Rgb(r, g, b) ->
            let clamp (v: int) = max 0 (min 255 v)
            [ extended; 2; clamp r; clamp g; clamp b ]

    /// The SGR sequence that sets a style from scratch (lower it to the terminal's depth first:
    /// `Style.lower`).
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
                yield! colorCodes false style.Fg
                yield! colorCodes true style.Bg
            ]

        esc + (codes |> List.map string |> String.concat ";") + "m"

    /// Moves the cursor to a row and column (from 0).
    let moveTo (row: int) (column: int) : string = $"{esc}{row + 1};{column + 1}H"

    let private writeRow (sb: StringBuilder) (width: int) (index: int) (row: Row) =
        sb.Append(moveTo index 0) |> ignore
        let mutable current = Style.plain
        sb.Append(esc + "0m") |> ignore

        for span in row do
            if span.Style <> current then
                sb.Append(sgr span.Style) |> ignore
                current <- span.Style

            sb.Append span.Text |> ignore

        // Clear the rest of the line in the default style, if the row leaves any: with wrap off the
        // cursor stays on the last column once a row fills it, and EL would erase that column too.
        if current <> Style.plain then
            sb.Append(esc + "0m") |> ignore

        if Row.width row < width then
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
                writeRow sb next.Width i row)

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
