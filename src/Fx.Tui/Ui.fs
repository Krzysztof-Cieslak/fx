namespace Fx.Tui

open System

/// The 16 colours every terminal has (SGR 30–37 and 90–97), so the TUI looks the same in any theme
/// that maps them.
[<RequireQualifiedAccess>]
type Color =
    | Default
    | Black
    | Red
    | Green
    | Yellow
    | Blue
    | Magenta
    | Cyan
    | White
    | Gray
    | BrightRed
    | BrightGreen
    | BrightYellow
    | BrightBlue
    | BrightMagenta
    | BrightCyan
    | BrightWhite

    override this.ToString() =
        match this with
        | Default -> "default"
        | Black -> "black"
        | Red -> "red"
        | Green -> "green"
        | Yellow -> "yellow"
        | Blue -> "blue"
        | Magenta -> "magenta"
        | Cyan -> "cyan"
        | White -> "white"
        | Gray -> "gray"
        | BrightRed -> "bright-red"
        | BrightGreen -> "bright-green"
        | BrightYellow -> "bright-yellow"
        | BrightBlue -> "bright-blue"
        | BrightMagenta -> "bright-magenta"
        | BrightCyan -> "bright-cyan"
        | BrightWhite -> "bright-white"

/// How a span of text is drawn.
type Style =
    {
        Fg: Color
        Bg: Color
        Bold: bool
        Dim: bool
        Italic: bool
        Underline: bool
        Reverse: bool
    }

    override this.ToString() =
        let flags =
            [
                if this.Bold then
                    "bold"
                if this.Dim then
                    "dim"
                if this.Italic then
                    "italic"
                if this.Underline then
                    "underline"
                if this.Reverse then
                    "reverse"
            ]

        String.concat " " ([ $"fg={this.Fg}"; $"bg={this.Bg}" ] @ flags)

module Style =
    let plain: Style =
        {
            Fg = Color.Default
            Bg = Color.Default
            Bold = false
            Dim = false
            Italic = false
            Underline = false
            Reverse = false
        }

    let fg (color: Color) = { plain with Fg = color }
    let bold = { plain with Bold = true }
    let dim = { plain with Dim = true }
    let reverse = { plain with Reverse = true }
    let withBold (style: Style) = { style with Bold = true }
    let withDim (style: Style) = { style with Dim = true }
    let withBg (color: Color) (style: Style) = { style with Bg = color }

/// Text in one style. Its text is one line without control characters.
type Span =
    {
        Text: string
        Style: Style
    }

    override this.ToString() = this.Text

/// One row of the screen: spans, at most the screen's width in total.
type Row = Span list

module Span =
    let create (style: Style) (text: string) : Span = { Text = text; Style = style }
    let plain (text: string) : Span = create Style.plain text

module Row =
    let empty: Row = []

    /// The columns a row takes.
    let width (row: Row) : int =
        row |> List.sumBy (fun span -> Text.width span.Text)

    /// The row's text without styles.
    let text (row: Row) : string =
        row |> List.map (fun span -> span.Text) |> String.concat ""

    /// The row cut to `columns`: the last span that does not fit is cut and ends with `…`.
    let fit (columns: int) (row: Row) : Row =
        if width row <= columns then
            row
        else
            // The span that reaches the edge while more follows is cut and ends with the ellipsis.
            let rec go (used: int) (spans: Row) : Row =
                match spans with
                | [] -> []
                | span :: rest ->
                    let w = Text.width span.Text

                    if used + w < columns then
                        span :: go (used + w) rest
                    else
                        [
                            { span with
                                Text = Text.truncate (columns - used) span.Text
                            }
                        ]

            go 0 row |> List.filter (fun span -> span.Text <> "")

    /// Merges neighbouring spans of the same style.
    let normalize (row: Row) : Row =
        let rec go (spans: Row) : Row =
            match spans with
            | a :: b :: rest when a.Style = b.Style -> go ({ a with Text = a.Text + b.Text } :: rest)
            | a :: rest when a.Text = "" -> go rest
            | a :: rest -> a :: go rest
            | [] -> []

        go row

    /// A row with `left` at the start and `right` at the end of `columns`, cutting `left` first.
    let spread (columns: int) (left: Row) (right: Row) : Row =
        let rw = width right

        if rw >= columns then
            fit columns right
        else
            let left = fit (max 0 (columns - rw - 1)) left
            let gap = columns - width left - rw
            left @ [ Span.plain (String(' ', max 1 gap)) ] @ right

    /// A row padded to `columns` with spaces in a style (a bar's background).
    let fill (columns: int) (style: Style) (row: Row) : Row =
        let row = fit columns row
        let w = width row

        if w < columns then
            row @ [ Span.create style (String(' ', columns - w)) ]
        else
            row

/// What the screen shows: exactly `Height` rows of at most `Width` columns, and where the cursor is
/// (row and column, from 0), if it is shown.
type Frame =
    {
        Width: int
        Height: int
        Rows: Row list
        Cursor: (int * int) option
    }

    override this.ToString() = $"frame {this.Width}x{this.Height}"

module Frame =
    /// The frame's rows as plain text, trailing spaces removed: what tests compare.
    let lines (frame: Frame) : string list =
        frame.Rows |> List.map (fun row -> (Row.text row).TrimEnd())

    /// The frame as plain text, one line per row.
    let text (frame: Frame) : string = lines frame |> String.concat "\n"

/// Building rows from text that wraps: paragraphs with a hanging indent and a style.
[<RequireQualifiedAccess>]
module Paragraph =
    /// A text wrapped to `columns`, every line after the first indented like the first (`prefix`
    /// is drawn on the first line and `indent` spaces on the others).
    let wrap (columns: int) (prefix: Row) (style: Style) (text: string) : Row list =
        let lead = Row.width prefix
        let available = max 1 (columns - lead)

        Text.wrap available text
        |> List.mapi (fun i line ->
            let head = if i = 0 then prefix else [ Span.plain (String(' ', lead)) ]

            Row.fit columns (head @ [ Span.create style line ]))
