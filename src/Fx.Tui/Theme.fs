namespace Fx.Tui

open System
open Fx.Variants

/// Which theme the TUI draws with (`tui.theme`).
[<RequireQualifiedAccess>]
type ThemeChoice =
    /// `dark` or `light` from what the terminal says of its background (`COLORFGBG`; dark when it
    /// says nothing) where it shows 256 colours or more, else `ansi16`.
    | Auto
    | Dark
    | Light
    /// The terminal's own 16 colours and background: no panels, nothing the terminal's theme does not
    /// map.
    | Ansi16

    override this.ToString() =
        match this with
        | Auto -> "auto"
        | Dark -> "dark"
        | Light -> "light"
        | Ansi16 -> "ansi16"

/// What the terminal can draw: its colour depth, and whether its background is light (when it says).
type Terminal =
    {
        Depth: ColorDepth
        LightBackground: bool option
    }

    override this.ToString() =
        let background =
            match this.LightBackground with
            | Some true -> "light"
            | Some false -> "dark"
            | None -> "unknown"

        $"terminal ({this.Depth} colours, {background} background)"

[<RequireQualifiedAccess>]
module Terminal =
    /// A terminal of the 16 colours that says nothing of its background: what tests draw on.
    let basic: Terminal =
        {
            Depth = ColorDepth.Basic
            LightBackground = None
        }

    /// What the process's variables say of the terminal (`ColorDepth.detect`, and `COLORFGBG`'s
    /// background: `<fg>;<bg>` with the 16 colours' indexes, 7 and 9–15 light).
    let detect (variables: (string * string) list) (windows: bool) : Terminal =
        let light =
            variables
            |> List.tryFind (fun (key, _) -> String.Equals(key, "COLORFGBG", StringComparison.OrdinalIgnoreCase))
            |> Option.bind (fun (_, value) ->
                match
                    value.Split(';')
                    |> Array.tryLast
                    |> Option.map (fun v -> Int32.TryParse(v.Trim()))
                with
                | Some(true, bg) -> Some(bg = 7 || (bg >= 9 && bg <= 15))
                | _ -> None)

        {
            Depth = ColorDepth.detect variables windows
            LightBackground = light
        }

/// The colours of the TUI by what they mean: every style the view draws comes from here.
type Theme =
    {
        Name: string
        /// Painted under everything (`Color.Default`: the terminal's own).
        Background: Color
        /// Under raised blocks: the prompts sent, the prompt being typed, cards (`Color.Default`: none).
        Panel: Color
        /// Under the selected option of a card, and code blocks.
        Element: Color
        Text: Style
        /// What matters less: hints, durations, metadata.
        Muted: Style
        /// The prompt's marker, what is running, links.
        Accent: Style
        /// The bar left of a panel.
        Bar: Style
        Success: Style
        Warning: Style
        Error: Style
        /// Activity lines that change the workspace (`◆`).
        Changes: Style
        /// Activity lines that look or run (`◇`).
        Looks: Style
        Permission: Style
        Question: Style
        Decision: Style
        /// The header bar.
        Header: Style
        /// A warning on every screen (runs unsandboxed).
        Banner: Style
        /// The selected option on a card.
        Selected: Style
        Heading: Style
        Strong: Style
        Emphasis: Style
        /// Inline code.
        Code: Style
        Keyword: Style
        String: Style
        Comment: Style
        Number: Style
        Link: Style
        Quote: Style
    }

    override this.ToString() = "theme " + this.Name

[<RequireQualifiedAccess>]
module Theme =
    let private fg (color: Color) = Style.fg color

    /// The terminal's own 16 colours: the look of every terminal theme.
    let ansi16: Theme =
        {
            Name = "ansi16"
            Background = Color.Default
            Panel = Color.Default
            Element = Color.Default
            Text = Style.plain
            Muted = Style.dim
            Accent = fg Color.Cyan
            Bar = fg Color.Cyan
            Success = fg Color.Green
            Warning = fg Color.Yellow
            Error = fg Color.Red
            Changes = fg Color.Yellow
            Looks = fg Color.Cyan
            Permission = fg Color.Magenta
            Question = fg Color.Cyan
            Decision = fg Color.Yellow
            Header = Style.reverse
            Banner =
                { Style.plain with
                    Fg = Color.Black
                    Bg = Color.Yellow
                    Bold = true
                }
            Selected = Style.reverse
            Heading = fg Color.Cyan |> Style.withBold
            Strong = Style.bold
            Emphasis = Style.withItalic Style.plain
            Code = fg Color.Green
            Keyword = fg Color.Magenta
            String = fg Color.Green
            Comment = Style.dim
            Number = fg Color.Yellow
            Link = fg Color.Cyan |> Style.withUnderline
            Quote = Style.dim |> Style.withItalic
        }

    let private rgb (r: int) (g: int) (b: int) = Color.Rgb(r, g, b)

    /// 24-bit colours on a dark background the theme paints itself.
    let dark: Theme =
        let background = rgb 24 25 33
        let panel = rgb 34 36 48
        let element = rgb 46 49 66
        let text = rgb 220 223 228
        let muted = rgb 128 132 150
        let blue = rgb 122 162 247
        let cyan = rgb 125 207 255
        let green = rgb 158 206 106
        let amber = rgb 224 175 104
        let red = rgb 247 118 142
        let purple = rgb 187 154 247
        let orange = rgb 255 158 100

        {
            Name = "dark"
            Background = background
            Panel = panel
            Element = element
            Text = fg text
            Muted = fg muted
            Accent = fg cyan
            Bar = fg blue
            Success = fg green
            Warning = fg amber
            Error = fg red
            Changes = fg amber
            Looks = fg cyan
            Permission = fg purple
            Question = fg cyan
            Decision = fg amber
            Header = fg muted |> Style.withBg panel
            Banner =
                { fg background with
                    Bg = amber
                    Bold = true
                }
            Selected = fg text |> Style.withBg element |> Style.withBold
            Heading = fg blue |> Style.withBold
            Strong = fg orange |> Style.withBold
            Emphasis = fg text |> Style.withItalic
            Code = fg green
            Keyword = fg purple
            String = fg green
            Comment = fg muted |> Style.withItalic
            Number = fg orange
            Link = fg cyan |> Style.withUnderline
            Quote = fg muted |> Style.withItalic
        }

    /// 24-bit colours on a light background the theme paints itself.
    let light: Theme =
        let background = rgb 250 250 250
        let panel = rgb 236 238 242
        let element = rgb 222 225 232
        let text = rgb 40 42 54
        let muted = rgb 110 114 130
        let blue = rgb 52 101 164
        let cyan = rgb 0 122 163
        let green = rgb 56 125 46
        let amber = rgb 152 104 1
        let red = rgb 196 42 62
        let purple = rgb 122 62 157
        let orange = rgb 176 82 0

        {
            Name = "light"
            Background = background
            Panel = panel
            Element = element
            Text = fg text
            Muted = fg muted
            Accent = fg cyan
            Bar = fg blue
            Success = fg green
            Warning = fg amber
            Error = fg red
            Changes = fg amber
            Looks = fg cyan
            Permission = fg purple
            Question = fg cyan
            Decision = fg amber
            Header = fg muted |> Style.withBg panel
            Banner =
                { fg background with
                    Bg = amber
                    Bold = true
                }
            Selected = fg text |> Style.withBg element |> Style.withBold
            Heading = fg blue |> Style.withBold
            Strong = fg orange |> Style.withBold
            Emphasis = fg text |> Style.withItalic
            Code = fg green
            Keyword = fg purple
            String = fg green
            Comment = fg muted |> Style.withItalic
            Number = fg orange
            Link = fg cyan |> Style.withUnderline
            Quote = fg muted |> Style.withItalic
        }

    /// The theme a choice gives on a terminal: the painted themes need 256 colours or more (with
    /// fewer they would be lowered to colours a terminal's theme maps anywhere), so below that every
    /// choice is `ansi16`.
    let choose (choice: ThemeChoice) (terminal: Terminal) : Theme =
        match terminal.Depth with
        | ColorDepth.NoColor
        | ColorDepth.Basic -> ansi16
        | ColorDepth.Indexed
        | ColorDepth.TrueColor ->
            match choice with
            | ThemeChoice.Ansi16 -> ansi16
            | ThemeChoice.Dark -> dark
            | ThemeChoice.Light -> light
            | ThemeChoice.Auto when terminal.LightBackground = Some true -> light
            | ThemeChoice.Auto -> dark

/// The TUI's variant slots (spec §19.6: the TUI style is a variant).
[<RequireQualifiedAccess>]
module TuiSlots =
    /// `tui.theme`: `auto`, `dark`, `light` or `ansi16`.
    let theme: Slot<ThemeChoice> =
        Slot.enum
            "tui.theme"
            "The TUI's colours: auto (dark or light from what the terminal says of its background, the terminal's 16 colours where it shows fewer than 256), dark, light, or ansi16 (the terminal's own colours and background)."
            [
                ThemeChoice.Auto, "auto"
                ThemeChoice.Dark, "dark"
                ThemeChoice.Light, "light"
                ThemeChoice.Ansi16, "ansi16"
            ]
            ThemeChoice.Auto

    let all: SlotInfo list = [ Slot.info theme ]
