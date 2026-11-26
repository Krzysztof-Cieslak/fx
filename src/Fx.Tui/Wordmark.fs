namespace Fx.Tui

open System

/// The product's name drawn in blocks, five rows tall, for the start screen. Drawn from the name the
/// build gives (`Product.Name`), so a rename redraws it; characters it has no glyph for are blank.
[<RequireQualifiedAccess>]
module Wordmark =
    /// The rows of glyph `c`, `#` for a block.
    let private glyph (c: char) : string list =
        match Char.ToLowerInvariant c with
        | 'a' -> [ " ## "; "#  #"; "####"; "#  #"; "#  #" ]
        | 'b' -> [ "### "; "#  #"; "### "; "#  #"; "### " ]
        | 'c' -> [ " ###"; "#   "; "#   "; "#   "; " ###" ]
        | 'd' -> [ "### "; "#  #"; "#  #"; "#  #"; "### " ]
        | 'e' -> [ "####"; "#   "; "### "; "#   "; "####" ]
        | 'f' -> [ "####"; "#   "; "### "; "#   "; "#   " ]
        | 'g' -> [ " ###"; "#   "; "# ##"; "#  #"; " ###" ]
        | 'h' -> [ "#  #"; "#  #"; "####"; "#  #"; "#  #" ]
        | 'i' -> [ "###"; " # "; " # "; " # "; "###" ]
        | 'j' -> [ "  ##"; "   #"; "   #"; "#  #"; " ## " ]
        | 'k' -> [ "#  #"; "# # "; "##  "; "# # "; "#  #" ]
        | 'l' -> [ "#   "; "#   "; "#   "; "#   "; "####" ]
        | 'm' -> [ "#   #"; "## ##"; "# # #"; "#   #"; "#   #" ]
        | 'n' -> [ "#  #"; "## #"; "# ##"; "#  #"; "#  #" ]
        | 'o'
        | '0' -> [ " ## "; "#  #"; "#  #"; "#  #"; " ## " ]
        | 'p' -> [ "### "; "#  #"; "### "; "#   "; "#   " ]
        | 'q' -> [ " ## "; "#  #"; "#  #"; "# ##"; " ###" ]
        | 'r' -> [ "### "; "#  #"; "### "; "# # "; "#  #" ]
        | 's' -> [ " ###"; "#   "; " ## "; "   #"; "### " ]
        | 't' -> [ "###"; " # "; " # "; " # "; " # " ]
        | 'u' -> [ "#  #"; "#  #"; "#  #"; "#  #"; " ## " ]
        | 'v' -> [ "#   #"; "#   #"; " # # "; " # # "; "  #  " ]
        | 'w' -> [ "#   #"; "#   #"; "# # #"; "## ##"; "#   #" ]
        | 'x' -> [ "#  #"; "#  #"; " ## "; "#  #"; "#  #" ]
        | 'y' -> [ "#  #"; "#  #"; " ###"; "   #"; "### " ]
        | 'z' -> [ "####"; "   #"; " ## "; "#   "; "####" ]
        | '1' -> [ " # "; "## "; " # "; " # "; "###" ]
        | '2' -> [ "### "; "   #"; " ## "; "#   "; "####" ]
        | '3' -> [ "### "; "   #"; " ## "; "   #"; "### " ]
        | '4' -> [ "#  #"; "#  #"; "####"; "   #"; "   #" ]
        | '5' -> [ "####"; "#   "; "### "; "   #"; "### " ]
        | '6' -> [ " ## "; "#   "; "### "; "#  #"; " ## " ]
        | '7' -> [ "####"; "   #"; "  # "; " #  "; " #  " ]
        | '8' -> [ " ## "; "#  #"; " ## "; "#  #"; " ## " ]
        | '9' -> [ " ## "; "#  #"; " ###"; "   #"; " ## " ]
        | '-' -> [ "   "; "   "; "###"; "   "; "   " ]
        | '.' -> [ " "; " "; " "; " "; "#" ]
        | '_' -> [ "    "; "    "; "    "; "    "; "####" ]
        | _ -> [ "  "; "  "; "  "; "  "; "  " ]

    /// How many rows it takes.
    [<Literal>]
    let Height = 5

    /// The name in blocks: five rows, a column between letters; the letters of the name's first half
    /// in `first`, the rest in `second`.
    let draw (first: Style) (second: Style) (name: string) : Row list =
        let letters = name |> Seq.toList
        let half = (letters.Length + 1) / 2

        [
            for row in 0 .. Height - 1 do
                letters
                |> List.mapi (fun i c ->
                    let text = ((glyph c)[row]).Replace('#', '█')
                    let gap = if i = 0 then "" else " "
                    [ Span.plain gap; Span.create (if i < half then first else second) text ])
                |> List.concat
                |> Row.normalize
        ]

    /// The columns the drawn name takes.
    let width (name: string) : int =
        let glyphs = name |> Seq.map (fun c -> (glyph c).Head.Length) |> Seq.toList
        List.sum glyphs + max 0 (glyphs.Length - 1)
