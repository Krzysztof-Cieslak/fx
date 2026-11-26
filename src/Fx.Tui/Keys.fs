namespace Fx.Tui

open System
open System.Text

/// A key a person pressed, or text they pasted.
[<RequireQualifiedAccess>]
type KeyCode =
    /// A printable character (one grapheme cluster).
    | Char of string
    | Enter
    | Tab
    | Backspace
    | Delete
    | Insert
    | Escape
    | Up
    | Down
    | Left
    | Right
    | Home
    | End
    | PageUp
    | PageDown
    /// The mouse wheel (or a trackpad) moved up one notch (an SGR mouse report).
    | WheelUp
    /// The mouse wheel (or a trackpad) moved down one notch.
    | WheelDown
    | F of int
    /// Text pasted at once (bracketed paste): never acted on as keys.
    | Paste of string
    /// A sequence this decoder does not know, as received.
    | Unknown of string

    override this.ToString() =
        match this with
        | Char c -> c
        | Enter -> "Enter"
        | Tab -> "Tab"
        | Backspace -> "Backspace"
        | Delete -> "Delete"
        | Insert -> "Insert"
        | Escape -> "Esc"
        | Up -> "Up"
        | Down -> "Down"
        | Left -> "Left"
        | Right -> "Right"
        | Home -> "Home"
        | End -> "End"
        | PageUp -> "PageUp"
        | PageDown -> "PageDown"
        | WheelUp -> "WheelUp"
        | WheelDown -> "WheelDown"
        | F n -> "F" + string n
        | Paste text -> $"Paste({text.Length})"
        | Unknown text -> "Unknown(" + text.Replace("\u001b", "\\e") + ")"

type Key =
    {
        Code: KeyCode
        Ctrl: bool
        Alt: bool
        Shift: bool
    }

    override this.ToString() =
        (if this.Ctrl then "Ctrl+" else "")
        + (if this.Alt then "Alt+" else "")
        + (if this.Shift then "Shift+" else "")
        + string this.Code

/// Input as terminals send it (xterm conventions, also Windows' console with virtual terminal
/// input), decoded into keys: pure, with what a read cut in the middle of a sequence carried over.
[<RequireQualifiedAccess>]
module Keys =
    let key (code: KeyCode) : Key =
        {
            Code = code
            Ctrl = false
            Alt = false
            Shift = false
        }

    let ctrl (c: char) : Key =
        { key (KeyCode.Char(string c)) with
            Ctrl = true
        }

    let alt (k: Key) : Key = { k with Alt = true }
    let char (c: string) : Key = key (KeyCode.Char c)

    let private pasteStart = "\u001b[200~"
    let private pasteEnd = "\u001b[201~"

    /// The modifiers of an xterm parameter (`1 + bits`: shift 1, alt 2, ctrl 4).
    let private withModifiers (parameter: int) (k: Key) : Key =
        let bits = max 0 (parameter - 1)

        { k with
            Shift = k.Shift || bits &&& 1 <> 0
            Alt = k.Alt || bits &&& 2 <> 0
            Ctrl = k.Ctrl || bits &&& 4 <> 0
        }

    let private parseInt (text: string) : int option =
        match Int32.TryParse(text, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
        | true, n -> Some n
        | _ -> None

    /// An SGR mouse report (`ESC [ < button ; column ; row M|m`): the wheel's notches (buttons 64
    /// and 65, with any modifiers), anything else as received.
    let private mouse (parameters: string) (final: char) (raw: string) : KeyCode =
        let button = parameters.Substring(1).Split(';') |> Array.tryHead |> Option.bind parseInt

        match button, final with
        | Some b, 'M' when (b &&& 64) <> 0 && (b &&& 128) = 0 && (b &&& 3) = 0 -> KeyCode.WheelUp
        | Some b, 'M' when (b &&& 64) <> 0 && (b &&& 128) = 0 && (b &&& 3) = 1 -> KeyCode.WheelDown
        | _ -> KeyCode.Unknown raw

    /// A CSI sequence's key (`ESC [ params final`).
    let private csi (parameters: string) (final: char) (raw: string) : Key =
        let ps = parameters.Split(';') |> Array.map parseInt
        let p (i: int) = if i < ps.Length then ps[i] else None
        let modifiers = p 1 |> Option.defaultValue 1

        let simple (code: KeyCode) = key code |> withModifiers modifiers

        match final with
        | 'A' -> simple KeyCode.Up
        | 'B' -> simple KeyCode.Down
        | 'C' -> simple KeyCode.Right
        | 'D' -> simple KeyCode.Left
        | 'H' -> simple KeyCode.Home
        | 'F' -> simple KeyCode.End
        | 'Z' -> { key KeyCode.Tab with Shift = true }
        | 'P' -> simple (KeyCode.F 1)
        | 'Q' -> simple (KeyCode.F 2)
        | 'R' -> simple (KeyCode.F 3)
        | 'S' -> simple (KeyCode.F 4)
        | '~' ->
            let code =
                match p 0 with
                | Some 1
                | Some 7 -> Some KeyCode.Home
                | Some 2 -> Some KeyCode.Insert
                | Some 3 -> Some KeyCode.Delete
                | Some 4
                | Some 8 -> Some KeyCode.End
                | Some 5 -> Some KeyCode.PageUp
                | Some 6 -> Some KeyCode.PageDown
                | Some 11 -> Some(KeyCode.F 1)
                | Some 12 -> Some(KeyCode.F 2)
                | Some 13 -> Some(KeyCode.F 3)
                | Some 14 -> Some(KeyCode.F 4)
                | Some 15 -> Some(KeyCode.F 5)
                | Some 17 -> Some(KeyCode.F 6)
                | Some 18 -> Some(KeyCode.F 7)
                | Some 19 -> Some(KeyCode.F 8)
                | Some 20 -> Some(KeyCode.F 9)
                | Some 21 -> Some(KeyCode.F 10)
                | Some 23 -> Some(KeyCode.F 11)
                | Some 24 -> Some(KeyCode.F 12)
                // xterm's modifyOtherKeys: ESC [ 27 ; mods ; code ~
                | Some 27 ->
                    match p 2 with
                    | Some 13 -> Some KeyCode.Enter
                    | Some 9 -> Some KeyCode.Tab
                    | Some 27 -> Some KeyCode.Escape
                    | Some 127 -> Some KeyCode.Backspace
                    | Some c when c >= 32 && c < 0x110000 -> Some(KeyCode.Char(Char.ConvertFromUtf32 c))
                    | _ -> None
                | _ -> None

            match code with
            | Some code -> simple code
            | None -> key (KeyCode.Unknown raw)
        // CSI u (the kitty keyboard protocol and fixterms): ESC [ code ; mods u
        | 'u' ->
            let code =
                match p 0 with
                | Some 13 -> Some KeyCode.Enter
                | Some 9 -> Some KeyCode.Tab
                | Some 27 -> Some KeyCode.Escape
                | Some 127 -> Some KeyCode.Backspace
                | Some c when c >= 32 && c < 0x110000 && not (c >= 0xD800 && c <= 0xDFFF) ->
                    Some(KeyCode.Char(Char.ConvertFromUtf32 c))
                | _ -> None

            match code with
            | Some code -> simple code
            | None -> key (KeyCode.Unknown raw)
        | _ -> key (KeyCode.Unknown raw)

    /// A control character's key.
    let private control (c: char) : Key =
        match c with
        | '\r' -> key KeyCode.Enter
        | '\t' -> key KeyCode.Tab
        | '\u007f'
        | '\b' -> key KeyCode.Backspace
        | '\u0000' -> { char " " with Ctrl = true }
        | c when c >= '\u0001' && c <= '\u001a' -> ctrl (Operators.char (int 'a' + int c - 1))
        | '\u001c' -> ctrl '\\'
        | '\u001d' -> ctrl ']'
        | '\u001e' -> ctrl '^'
        | '\u001f' -> ctrl '_'
        | c -> key (KeyCode.Unknown(string c))

    /// Where a CSI sequence that starts at `start` (after `ESC [`) ends: the index of its final
    /// character, or `None` if the input ends first.
    let private csiEnd (input: string) (start: int) : int option =
        let mutable j = start

        while j < input.Length && input[j] >= '\u0030' && input[j] <= '\u003f' do
            j <- j + 1

        while j < input.Length && input[j] >= '\u0020' && input[j] <= '\u002f' do
            j <- j + 1

        if j < input.Length then Some j else None

    /// Decodes what arrived, after what was left over last time (`pending`). Returns the keys and
    /// what is left: the start of a sequence the input ends in the middle of. A lone `ESC` stays
    /// pending too: the caller waits briefly for more, then calls `flush`.
    let decode (pending: string) (input: string) : Key list * string =
        let text = pending + input
        let keys = ResizeArray<Key>()
        let n = text.Length
        let mutable i = 0
        let mutable rest = ""

        while i < n do
            let c = text[i]

            if
                c = '\u001b'
                && n - i >= pasteStart.Length
                && String.CompareOrdinal(text, i, pasteStart, 0, pasteStart.Length) = 0
            then
                let from = i + pasteStart.Length
                let stop = text.IndexOf(pasteEnd, from, StringComparison.Ordinal)

                if stop < 0 then
                    rest <- text.Substring i
                    i <- n
                else
                    keys.Add(key (KeyCode.Paste(text.Substring(from, stop - from))))
                    i <- stop + pasteEnd.Length
            elif c = '\u001b' then
                if i + 1 >= n then
                    rest <- text.Substring i
                    i <- n
                else
                    match text[i + 1] with
                    | '[' ->
                        match csiEnd text (i + 2) with
                        | None ->
                            rest <- text.Substring i
                            i <- n
                        | Some j ->
                            let raw = text.Substring(i, j - i + 1)
                            let parameters = text.Substring(i + 2, j - i - 2)
                            // A leading private marker (`<`, `?`) is a report (mouse, modes), not a key;
                            // of the mouse, only the wheel is acted on.
                            if parameters.StartsWith "<" then
                                keys.Add(key (mouse parameters text[j] raw))
                            elif parameters.StartsWith "?" then
                                keys.Add(key (KeyCode.Unknown raw))
                            else
                                keys.Add(csi parameters text[j] raw)

                            i <- j + 1
                    | 'O' ->
                        if i + 2 >= n then
                            rest <- text.Substring i
                            i <- n
                        else
                            let raw = text.Substring(i, 3)
                            keys.Add(csi "" text[i + 2] raw)
                            i <- i + 3
                    | '\u001b' ->
                        // ESC ESC: a lone Escape, then whatever the second starts.
                        keys.Add(key KeyCode.Escape)
                        i <- i + 1
                    | '\r'
                    | '\t'
                    | '\b'
                    | '\u007f' ->
                        keys.Add(alt (control text[i + 1]))
                        i <- i + 2
                    | next when Char.IsControl next ->
                        // Escape, then a control key typed right after it (Alt with Ctrl is not a key).
                        keys.Add(key KeyCode.Escape)
                        i <- i + 1
                    | _ ->
                        let length = max 1 (Globalization.StringInfo.GetNextTextElementLength(text, i + 1))
                        keys.Add(alt (char (text.Substring(i + 1, length))))
                        i <- i + 1 + length
            elif Char.IsControl c then
                keys.Add(control c)
                i <- i + 1
            elif Char.IsHighSurrogate c && i + 1 >= n then
                // Half of a character: wait for the rest.
                rest <- text.Substring i
                i <- n
            else
                let length = max 1 (Globalization.StringInfo.GetNextTextElementLength(text, i))
                keys.Add(char (text.Substring(i, length)))
                i <- i + length

        List.ofSeq keys, rest

    /// What is still pending once no more input came: a lone `ESC` is Escape; the start of a
    /// sequence that never finished is passed on as unknown.
    /// A paste still arriving stays pending.
    let flush (pending: string) : Key list * string =
        if pending = "" then
            [], ""
        elif pending = "\u001b" then
            [ key KeyCode.Escape ], ""
        elif pending.StartsWith(pasteStart, StringComparison.Ordinal) then
            [], pending
        else
            [ key (KeyCode.Unknown pending) ], ""

/// Decodes UTF-8 bytes from a terminal into text, keeping a character cut between two reads for the
/// next one.
[<Sealed>]
type Utf8Input() =
    let decoder = UTF8Encoding(false, false).GetDecoder()

    member _.Decode(bytes: byte array, count: int) : string =
        let chars = Array.zeroCreate<char>(decoder.GetCharCount(bytes, 0, count, false))
        let n = decoder.GetChars(bytes, 0, count, chars, 0, false)
        String(chars, 0, n)

    override _.ToString() = "Utf8Input"
