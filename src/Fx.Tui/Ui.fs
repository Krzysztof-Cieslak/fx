namespace Fx.Tui

open System

/// A colour: one of the 16 every terminal has (SGR 30–37 and 90–97, drawn as the terminal's theme
/// maps them), one of the 256 of the xterm palette, or a 24-bit one. What a terminal cannot show is
/// lowered to what it can (`Color.lower`) before it is drawn.
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
    /// One of the xterm palette's 256 colours (16–231: a 6×6×6 cube, 232–255: grays).
    | Indexed of index: int
    | Rgb of r: int * g: int * b: int

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
        | Indexed index -> "indexed-" + string index
        | Rgb(r, g, b) -> "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2")

/// How many colours a terminal shows.
[<RequireQualifiedAccess>]
type ColorDepth =
    /// None (`NO_COLOR`): bold, dim, italics, underline and reverse only.
    | NoColor
    /// The 16 colours.
    | Basic
    /// The xterm palette's 256.
    | Indexed
    /// 24-bit colour.
    | TrueColor

    override this.ToString() =
        match this with
        | NoColor -> "none"
        | Basic -> "16"
        | Indexed -> "256"
        | TrueColor -> "truecolor"

[<RequireQualifiedAccess>]
module ColorDepth =
    /// How many colours the terminal shows, from the process's variables: `NO_COLOR` (any value)
    /// turns them off; `COLORTERM` (`truecolor`, `24bit`) and the terminals known to draw 24-bit
    /// colour (Windows Terminal, iTerm2, WezTerm, kitty, Ghostty, VS Code) give 24 bits; a `TERM`
    /// with `256color` the palette; otherwise the 16. `windows`: the Windows console draws 24-bit
    /// colour once virtual terminal processing is on (Windows 10 1703 and later).
    let detect (variables: (string * string) list) (windows: bool) : ColorDepth =
        let get (name: string) =
            variables
            |> List.tryFind (fun (key, _) -> String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            |> Option.map snd

        let lower (name: string) =
            get name |> Option.map (fun value -> value.Trim().ToLowerInvariant())

        let set (name: string) =
            match get name with
            | Some value -> value <> ""
            | None -> false

        if set "NO_COLOR" then
            ColorDepth.NoColor
        elif lower "COLORTERM" = Some "truecolor" || lower "COLORTERM" = Some "24bit" then
            ColorDepth.TrueColor
        elif
            set "WT_SESSION"
            || set "KITTY_WINDOW_ID"
            || set "WEZTERM_EXECUTABLE"
            || (match lower "TERM_PROGRAM" with
                | Some("iterm.app" | "wezterm" | "vscode" | "ghostty") -> true
                | _ -> false)
        then
            ColorDepth.TrueColor
        else
            match lower "TERM" with
            | Some "dumb" -> ColorDepth.NoColor
            | Some term when term.Contains "truecolor" || term.Contains "direct" -> ColorDepth.TrueColor
            | Some term when term.Contains "256color" -> ColorDepth.Indexed
            | _ when windows -> ColorDepth.TrueColor
            | _ -> ColorDepth.Basic

[<RequireQualifiedAccess>]
module Color =
    /// The 16 colours as xterm draws them, to find the nearest one.
    let private basic: (Color * (int * int * int)) list =
        [
            Color.Black, (0, 0, 0)
            Color.Red, (205, 0, 0)
            Color.Green, (0, 205, 0)
            Color.Yellow, (205, 205, 0)
            Color.Blue, (0, 0, 238)
            Color.Magenta, (205, 0, 205)
            Color.Cyan, (0, 205, 205)
            Color.White, (229, 229, 229)
            Color.Gray, (127, 127, 127)
            Color.BrightRed, (255, 0, 0)
            Color.BrightGreen, (0, 255, 0)
            Color.BrightYellow, (255, 255, 0)
            Color.BrightBlue, (92, 92, 255)
            Color.BrightMagenta, (255, 0, 255)
            Color.BrightCyan, (0, 255, 255)
            Color.BrightWhite, (255, 255, 255)
        ]

    let private levels = [| 0; 95; 135; 175; 215; 255 |]

    /// The colour of a palette index (16 and up).
    let private ofIndex (index: int) : int * int * int =
        if index >= 232 then
            let v = 8 + (index - 232) * 10
            v, v, v
        else
            let i = max 0 (index - 16)
            levels[i / 36], levels[(i / 6) % 6], levels[i % 6]

    let private distance ((r1, g1, b1): int * int * int) ((r2, g2, b2): int * int * int) =
        // Weighted for how the eye sees: green most, blue least.
        let dr = r1 - r2
        let dg = g1 - g2
        let db = b1 - b2
        2 * dr * dr + 4 * dg * dg + 3 * db * db

    /// The palette index nearest a 24-bit colour: the cube's or the grays'.
    let toIndexed (r: int, g: int, b: int) : int =
        let step (v: int) =
            levels
            |> Array.mapi (fun i level -> i, abs (level - v))
            |> Array.minBy snd
            |> fst

        let cube = 16 + 36 * step r + 6 * step g + step b
        let average = (r + g + b) / 3
        let gray = 232 + (max 0 (min 23 ((average - 3) / 10)))

        if distance (r, g, b) (ofIndex gray) < distance (r, g, b) (ofIndex cube) then
            gray
        else
            cube

    /// The nearest of the 16 colours.
    let toBasic (rgb: int * int * int) : Color =
        basic |> List.minBy (fun (_, value) -> distance rgb value) |> fst

    /// The colour as a terminal of this depth can draw it.
    let lower (depth: ColorDepth) (color: Color) : Color =
        match depth, color with
        | _, Color.Default -> Color.Default
        | ColorDepth.NoColor, _ -> Color.Default
        | ColorDepth.TrueColor, _ -> color
        | ColorDepth.Indexed, Color.Rgb(r, g, b) -> Color.Indexed(toIndexed (r, g, b))
        | ColorDepth.Indexed, _ -> color
        | ColorDepth.Basic, Color.Rgb(r, g, b) -> toBasic (r, g, b)
        | ColorDepth.Basic, Color.Indexed index when index < 16 ->
            basic
            |> List.tryItem index
            |> Option.map fst
            |> Option.defaultValue Color.Default
        | ColorDepth.Basic, Color.Indexed index -> toBasic (ofIndex index)
        | ColorDepth.Basic, _ -> color

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
    let withFg (color: Color) (style: Style) = { style with Fg = color }
    let withItalic (style: Style) = { style with Italic = true }
    let withUnderline (style: Style) = { style with Underline = true }

    /// The style as a terminal of this depth can draw it.
    let lower (depth: ColorDepth) (style: Style) : Style =
        { style with
            Fg = Color.lower depth style.Fg
            Bg = Color.lower depth style.Bg
        }

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

    /// The frame as a terminal of this depth can draw it.
    let lower (depth: ColorDepth) (frame: Frame) : Frame =
        if depth = ColorDepth.TrueColor then
            frame
        else
            { frame with
                Rows =
                    frame.Rows
                    |> List.map (fun row ->
                        row
                        |> List.map (fun span ->
                            { span with
                                Style = Style.lower depth span.Style
                            })
                        |> Row.normalize)
            }

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
