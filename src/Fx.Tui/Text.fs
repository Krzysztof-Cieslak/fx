namespace Fx.Tui

open System
open System.Globalization
open System.Text

/// Text as a terminal shows it: grapheme clusters (what a person sees as one character) and how
/// many columns each takes. East Asian wide and fullwidth characters and emoji take two columns,
/// combining marks and other zero-width characters none; control characters are never drawn.
[<RequireQualifiedAccess>]
module Text =
    /// Code points that take two columns: East Asian Wide (W) and Fullwidth (F), and emoji shown as
    /// pictures. Sorted, inclusive ranges (Unicode 16, simplified where the gaps are unassigned).
    let private wide: struct (int * int) array =
        [|
            struct (0x1100, 0x115F)
            struct (0x231A, 0x231B)
            struct (0x2329, 0x232A)
            struct (0x23E9, 0x23EC)
            struct (0x23F0, 0x23F0)
            struct (0x23F3, 0x23F3)
            struct (0x25FD, 0x25FE)
            struct (0x2614, 0x2615)
            struct (0x2648, 0x2653)
            struct (0x267F, 0x267F)
            struct (0x2693, 0x2693)
            struct (0x26A1, 0x26A1)
            struct (0x26AA, 0x26AB)
            struct (0x26BD, 0x26BE)
            struct (0x26C4, 0x26C5)
            struct (0x26CE, 0x26CE)
            struct (0x26D4, 0x26D4)
            struct (0x26EA, 0x26EA)
            struct (0x26F2, 0x26F3)
            struct (0x26F5, 0x26F5)
            struct (0x26FA, 0x26FA)
            struct (0x26FD, 0x26FD)
            struct (0x2705, 0x2705)
            struct (0x270A, 0x270B)
            struct (0x2728, 0x2728)
            struct (0x274C, 0x274C)
            struct (0x274E, 0x274E)
            struct (0x2753, 0x2755)
            struct (0x2757, 0x2757)
            struct (0x2795, 0x2797)
            struct (0x27B0, 0x27B0)
            struct (0x27BF, 0x27BF)
            struct (0x2B1B, 0x2B1C)
            struct (0x2B50, 0x2B50)
            struct (0x2B55, 0x2B55)
            struct (0x2E80, 0x303E)
            struct (0x3041, 0x33FF)
            struct (0x3400, 0x4DBF)
            struct (0x4E00, 0x9FFF)
            struct (0xA000, 0xA4CF)
            struct (0xA960, 0xA97F)
            struct (0xAC00, 0xD7A3)
            struct (0xF900, 0xFAFF)
            struct (0xFE10, 0xFE19)
            struct (0xFE30, 0xFE6F)
            struct (0xFF00, 0xFF60)
            struct (0xFFE0, 0xFFE6)
            struct (0x16FE0, 0x16FE4)
            struct (0x17000, 0x18CFF)
            struct (0x1B000, 0x1B2FF)
            struct (0x1F004, 0x1F004)
            struct (0x1F0CF, 0x1F0CF)
            struct (0x1F18E, 0x1F18E)
            struct (0x1F191, 0x1F19A)
            struct (0x1F200, 0x1F2FF)
            struct (0x1F300, 0x1F64F)
            struct (0x1F680, 0x1F6FF)
            struct (0x1F7E0, 0x1F7EB)
            struct (0x1F7F0, 0x1F7F0)
            struct (0x1F90C, 0x1F9FF)
            struct (0x1FA70, 0x1FAFF)
            struct (0x20000, 0x2FFFD)
            struct (0x30000, 0x3FFFD)
        |]

    let private isWide (code: int) : bool =
        let mutable lo = 0
        let mutable hi = wide.Length - 1
        let mutable found = false

        while not found && lo <= hi do
            let mid = (lo + hi) / 2
            let struct (first, last) = wide[mid]

            if code < first then hi <- mid - 1
            elif code > last then lo <- mid + 1
            else found <- true

        found

    /// The columns one code point takes on its own: 0 for controls, combining marks and other
    /// zero-width characters, 2 for wide ones, 1 otherwise.
    let runeWidth (rune: Rune) : int =
        let code = rune.Value

        if code < 0x20 || (code >= 0x7F && code < 0xA0) then
            0
        elif code < 0x300 then
            1
        elif
            code = 0x200B
            || code = 0x200C
            || code = 0x200D
            || code = 0x2060
            || code = 0xFEFF
        then
            0
        elif code >= 0xFE00 && code <= 0xFE0F then
            0
        elif code >= 0x1160 && code <= 0x11FF then
            0
        elif code >= 0xE0000 && code <= 0xE0FFF then
            0
        else
            match Rune.GetUnicodeCategory rune with
            | UnicodeCategory.NonSpacingMark
            | UnicodeCategory.EnclosingMark
            | UnicodeCategory.Format -> 0
            | _ -> if isWide code then 2 else 1

    /// The columns a grapheme cluster takes: its widest code point, and two for an emoji asked for
    /// as a picture (U+FE0F).
    let clusterWidth (cluster: string) : int =
        let mutable width = 0
        let mutable picture = false

        for rune in cluster.EnumerateRunes() do
            if rune.Value = 0xFE0F then
                picture <- true

            width <- max width (runeWidth rune)

        if picture && width = 1 then 2 else width

    /// The grapheme clusters of a text, in order.
    let clusters (text: string) : string list =
        let result = ResizeArray<string>()
        let mutable i = 0

        while i < text.Length do
            let length = StringInfo.GetNextTextElementLength(text, i)
            let length = if length <= 0 then 1 else length
            result.Add(text.Substring(i, length))
            i <- i + length

        List.ofSeq result

    /// The columns a text takes on one line.
    let width (text: string) : int =
        let mutable total = 0
        let mutable i = 0

        while i < text.Length do
            let length = max 1 (StringInfo.GetNextTextElementLength(text, i))
            total <- total + clusterWidth (text.Substring(i, length))
            i <- i + length

        total

    /// The text with what a terminal would act on instead of drawing removed or replaced: tabs
    /// become spaces, other control characters (escape sequences included) are dropped, line ends
    /// are kept as `\n`.
    let clean (text: string) : string =
        let sb = StringBuilder(text.Length)

        for c in text.Replace("\r\n", "\n").Replace('\r', '\n') do
            if c = '\n' then sb.Append '\n' |> ignore
            elif c = '\t' then sb.Append "    " |> ignore
            elif Char.IsControl c then ()
            else sb.Append c |> ignore

        sb.ToString()

    /// The longest prefix of a text (one line) that fits in `columns`.
    let take (columns: int) (text: string) : string =
        let sb = StringBuilder()
        let mutable used = 0
        let mutable i = 0
        let mutable full = false

        while not full && i < text.Length do
            let length = max 1 (StringInfo.GetNextTextElementLength(text, i))
            let cluster = text.Substring(i, length)
            let w = clusterWidth cluster

            if used + w > columns then
                full <- true
            else
                sb.Append cluster |> ignore
                used <- used + w
                i <- i + length

        sb.ToString()

    /// A text cut to `columns`, ending with `…` when it was longer.
    let truncate (columns: int) (text: string) : string =
        if columns <= 0 then ""
        elif width text <= columns then text
        else take (columns - 1) text + "…"

    /// A text padded with spaces to `columns` (cut first if longer).
    let pad (columns: int) (text: string) : string =
        let cut = truncate columns text
        cut + String(' ', max 0 (columns - width cut))

    /// One paragraph (no line ends) broken into lines of at most `columns`: at spaces where it can,
    /// inside words that are longer than a line.
    let private wrapLine (columns: int) (line: string) : string list =
        if columns <= 0 then
            [ "" ]
        elif width line <= columns then
            [ line ]
        else
            let lines = ResizeArray<string>()
            let current = StringBuilder()
            let mutable used = 0

            let flush () =
                lines.Add(current.ToString().TrimEnd(' '))
                current.Clear() |> ignore
                used <- 0

            let words = line.Split(' ')

            for index in 0 .. words.Length - 1 do
                let word = words[index]
                let w = width word
                let gap = if used = 0 then 0 else 1

                if used + gap + w <= columns then
                    if gap = 1 then
                        current.Append ' ' |> ignore

                    current.Append word |> ignore
                    used <- used + gap + w
                elif w <= columns then
                    flush ()
                    current.Append word |> ignore
                    used <- w
                else
                    // A word longer than a line: fill this line, then whole lines of it.
                    if used > 0 && used + 1 < columns then
                        current.Append ' ' |> ignore
                        used <- used + 1
                    elif used > 0 then
                        flush ()

                    for cluster in clusters word do
                        let cw = clusterWidth cluster

                        if used + cw > columns then
                            flush ()

                        current.Append cluster |> ignore
                        used <- used + cw

            if current.Length > 0 || lines.Count = 0 then
                lines.Add(current.ToString())

            List.ofSeq lines

    /// A text broken into lines of at most `columns`: at its own line ends, then at spaces.
    let wrap (columns: int) (text: string) : string list =
        (clean text).Split('\n') |> List.ofArray |> List.collect (wrapLine columns)

    /// The text's lines (at most `count`, the first ones), with `…` on the last when some were left out.
    let firstLines (count: int) (text: string) : string list =
        let lines = (clean text).Split('\n') |> List.ofArray

        if lines.Length <= count then
            lines
        else
            List.truncate count lines @ [ $"… {lines.Length - count} more lines" ]

    /// The first non-blank line of a text.
    let firstLine (text: string) : string =
        (clean text).Split('\n')
        |> Array.map (fun l -> l.Trim())
        |> Array.tryFind (fun l -> l <> "")
        |> Option.defaultValue ""
