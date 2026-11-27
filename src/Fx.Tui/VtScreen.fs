namespace Fx.Tui

open System
open System.Text

/// A small terminal: enough of VT to show what the TUI draws (cursor moves, erasing, SGR, the
/// alternate screen, text with wide characters), for the fake terminal, tests and scenarios.
/// Deterministic; not thread-safe (its owner serializes writes).
[<Sealed>]
type VtScreen(width: int, height: int) =
    let mutable width = width
    let mutable height = height

    let blank () =
        Array.init height (fun _ -> Array.create width " ")

    let mutable main = blank ()
    let mutable alternate = blank ()
    let mutable onAlternate = false
    let mutable row = 0
    let mutable column = 0
    let mutable cursorVisible = true
    let mutable mouse = false
    let mutable pending = ""

    let grid () = if onAlternate then alternate else main

    let clampCursor () =
        row <- max 0 (min (height - 1) row)
        column <- max 0 (min (width - 1) column)

    let scrollUp () =
        let g = grid ()

        for r in 0 .. height - 2 do
            g[r] <- g[r + 1]

        g[height - 1] <- Array.create width " "

    let newline () =
        if row = height - 1 then scrollUp () else row <- row + 1

    let put (cluster: string) =
        let w = Text.clusterWidth cluster

        if w > 0 then
            if column + w > width then
                // No automatic wrap is assumed (the TUI turns it off): a wide character with no room
                // left is dropped.
                ()
            else
                let g = grid ()

                // Overwriting half of a wide character blanks its other half.
                if g[row][column] = "" && column > 0 then
                    g[row][column - 1] <- " "

                let after = column + w

                if after < width && g[row][after] = "" then
                    g[row][after] <- " "

                g[row][column] <- cluster

                if w = 2 then
                    g[row][column + 1] <- ""

                // As a terminal with wrap off does, the cursor stays on the last column once a
                // character is drawn there: the next one overwrites it, and erasing the rest of the
                // line (EL) erases it too.
                column <- min after (width - 1)

    let eraseLine (mode: int) =
        let g = grid ()

        let first, last =
            match mode with
            | 1 -> 0, column
            | 2 -> 0, width - 1
            | _ -> column, width - 1

        for c in first .. min last (width - 1) do
            g[row][c] <- " "

    let eraseDisplay (mode: int) =
        let g = grid ()

        match mode with
        | 2
        | 3 ->
            for r in 0 .. height - 1 do
                g[r] <- Array.create width " "
        | 1 ->
            for r in 0 .. row - 1 do
                g[r] <- Array.create width " "

            eraseLine 1
        | _ ->
            eraseLine 0

            for r in row + 1 .. height - 1 do
                g[r] <- Array.create width " "

    let numbers (parameters: string) : int list =
        if parameters = "" then
            []
        else
            parameters.Split(';')
            |> Array.map (fun p ->
                match
                    Int32.TryParse(p, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
                with
                | true, n -> n
                | _ -> 0)
            |> List.ofArray

    let csi (parameters: string) (final: char) =
        let privateMode = parameters.StartsWith "?"
        let ps = numbers (parameters.TrimStart('?'))

        let first (fallback: int) =
            match ps with
            | n :: _ when n > 0 -> n
            | _ -> fallback

        match final with
        | 'H'
        | 'f' ->
            let r =
                match ps with
                | n :: _ when n > 0 -> n
                | _ -> 1

            let c =
                match ps with
                | _ :: n :: _ when n > 0 -> n
                | _ -> 1

            row <- r - 1
            column <- c - 1
            clampCursor ()
        | 'A' ->
            row <- row - first 1
            clampCursor ()
        | 'B' ->
            row <- row + first 1
            clampCursor ()
        | 'C' ->
            column <- column + first 1
            clampCursor ()
        | 'D' ->
            column <- column - first 1
            clampCursor ()
        | 'G' ->
            column <- first 1 - 1
            clampCursor ()
        | 'K' -> eraseLine (List.tryHead ps |> Option.defaultValue 0)
        | 'J' -> eraseDisplay (List.tryHead ps |> Option.defaultValue 0)
        | 'h'
        | 'l' when privateMode ->
            let on = final = 'h'

            for mode in ps do
                match mode with
                | 25 -> cursorVisible <- on
                | 1000 -> mouse <- on
                | 1049 ->
                    if on && not onAlternate then
                        alternate <- blank ()
                        onAlternate <- true
                    elif not on && onAlternate then
                        onAlternate <- false
                | _ -> ()
        | _ -> ()

    /// Feeds output to the screen. A sequence cut at the end waits for the next write.
    member _.Write(text: string) =
        let text = pending + text
        pending <- ""
        let n = text.Length
        let mutable i = 0

        while i < n do
            let c = text[i]

            if c = '\u001b' then
                if i + 1 >= n then
                    pending <- text.Substring i
                    i <- n
                elif text[i + 1] = '[' then
                    let mutable j = i + 2

                    while j < n && (Char.IsDigit text[j] || text[j] = ';' || text[j] = '?') do
                        j <- j + 1

                    if j >= n then
                        pending <- text.Substring i
                        i <- n
                    else
                        csi (text.Substring(i + 2, j - i - 2)) text[j]
                        i <- j + 1
                elif text[i + 1] = ']' then
                    // OSC: up to BEL or ST.
                    let mutable j = i + 2

                    while j < n
                          && text[j] <> '\u0007'
                          && not (text[j] = '\u001b' && j + 1 < n && text[j + 1] = '\\') do
                        j <- j + 1

                    if j >= n then
                        pending <- text.Substring i
                        i <- n
                    else
                        i <- if text[j] = '\u0007' then j + 1 else j + 2
                else
                    i <- i + 2
            elif c = '\r' then
                column <- 0
                i <- i + 1
            elif c = '\n' then
                newline ()
                i <- i + 1
            elif Char.IsControl c then
                i <- i + 1
            else
                let length = max 1 (Globalization.StringInfo.GetNextTextElementLength(text, i))
                let length = min length (n - i)
                put (text.Substring(i, length))
                i <- i + length

    /// Changes the size: what fits stays.
    member _.Resize(columns: int, rows: int) =
        let copy (g: string array array) =
            Array.init rows (fun r -> Array.init columns (fun c -> if r < height && c < width then g[r][c] else " "))

        main <- copy main
        alternate <- copy alternate
        width <- columns
        height <- rows
        clampCursor ()

    member _.Width = width
    member _.Height = height

    /// Whether the alternate screen is shown.
    member _.OnAlternate = onAlternate

    member _.CursorVisible = cursorVisible

    /// Whether the program asked for mouse reports (and the terminal would send the wheel as them).
    member _.Mouse = mouse

    member _.Cursor = row, column

    /// The rows shown, trailing spaces removed.
    member _.Lines: string list =
        grid ()
        |> Array.map (fun cells -> (String.Join("", cells)).TrimEnd())
        |> List.ofArray

    /// The main screen's rows (what stays after the TUI leaves), trailing spaces removed.
    member _.MainLines: string list =
        main
        |> Array.map (fun cells -> (String.Join("", cells)).TrimEnd())
        |> List.ofArray

    /// The rows shown, as one text.
    member this.Text: string = String.Join("\n", this.Lines)

    override _.ToString() = $"VtScreen({width}x{height})"
