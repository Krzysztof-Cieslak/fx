namespace Fx.Pty

open System

/// Turns terminal output into plain text, for previews, logs and tests.
module VtText =
    /// Removes VT escape sequences: CSI (`ESC [ … final`), OSC (`ESC ] … BEL` or `ESC \`), DCS,
    /// SOS, PM and APC strings (`ESC P|X|^|_ … ESC \`), character set designations (`ESC ( B`) and
    /// other two-character escapes, plus the 8-bit C1 forms of CSI and OSC. Other control characters
    /// except tab, carriage return and line feed are dropped. An unterminated sequence at the end is
    /// dropped too.
    let strip (text: string) : string =
        let sb = Text.StringBuilder(text.Length)
        let n = text.Length
        let mutable i = 0

        /// Skips a string terminated by BEL or ST (`ESC \` or U+009C), starting at `start`.
        let skipString (start: int) (belEnds: bool) =
            let mutable j = start
            let mutable ended = false

            while not ended && j < n do
                match text[j] with
                | '\u0007' when belEnds ->
                    j <- j + 1
                    ended <- true
                | '\u009c' ->
                    j <- j + 1
                    ended <- true
                | '\u001b' when j + 1 < n && text[j + 1] = '\\' ->
                    j <- j + 2
                    ended <- true
                | _ -> j <- j + 1

            j

        /// Skips CSI parameters and intermediates up to and including the final byte.
        let skipCsi (start: int) =
            let mutable j = start

            while j < n && (text[j] < '\u0040' || text[j] > '\u007e') do
                j <- j + 1

            min n (j + 1)

        while i < n do
            match text[i] with
            | '\u001b' when i + 1 < n ->
                match text[i + 1] with
                | '[' -> i <- skipCsi (i + 2)
                | ']' -> i <- skipString (i + 2) true
                | 'P'
                | 'X'
                | '^'
                | '_' -> i <- skipString (i + 2) false
                | c when c >= '\u0020' && c <= '\u002f' ->
                    // nF escape: intermediates, then one final byte (e.g. `ESC ( B`).
                    let mutable j = i + 1

                    while j < n && text[j] >= '\u0020' && text[j] <= '\u002f' do
                        j <- j + 1

                    i <- min n (j + 1)
                | _ -> i <- i + 2
            | '\u001b' -> i <- n
            | '\u009b' -> i <- skipCsi (i + 1)
            | '\u009d' -> i <- skipString (i + 1) true
            | '\t'
            | '\r'
            | '\n' as c ->
                sb.Append c |> ignore
                i <- i + 1
            | c when c < ' ' || (c >= '\u007f' && c <= '\u009f') -> i <- i + 1
            | c ->
                sb.Append c |> ignore
                i <- i + 1

        sb.ToString()

    /// Plain text with line endings normalized to `\n` (terminals write `\r\n`; a lone `\r`
    /// returns the cursor and is dropped).
    let plain (text: string) : string =
        (strip text).Replace("\r\n", "\n").Replace("\r", "")

    /// The plain-text lines of terminal output, without trailing spaces.
    let lines (text: string) : string list =
        (plain text).Split('\n') |> Array.map (fun l -> l.TrimEnd()) |> List.ofArray

/// Collects a terminal's output as text (UTF-8, decoded across chunk boundaries, with the VT
/// sequences kept), for code that waits for something to appear: tests, probes, simple automation.
[<Sealed>]
type Transcript(pty: Pty) =
    let decoder = Text.UTF8Encoding(false).GetDecoder()
    let text = Text.StringBuilder()
    let mutable ended = false

    let append (bytes: byte array) =
        let chars = Array.zeroCreate<char>(decoder.GetCharCount(bytes, 0, bytes.Length))
        let count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0)
        text.Append(chars, 0, count) |> ignore

    /// Everything read so far.
    member _.Text = text.ToString()

    /// Everything read so far as plain text (`VtText.plain`).
    member _.Plain = VtText.plain (text.ToString())

    /// Whether the output has ended.
    member _.Ended = ended

    /// Reads until `until` holds for the plain text read so far, or the output ends. Returns whether
    /// it holds.
    member this.ReadUntil (until: string -> bool) (cancellation: Threading.CancellationToken) =
        task {
            let mutable holds = until this.Plain

            while not holds && not ended do
                match! pty.Read cancellation with
                | Some bytes ->
                    append bytes
                    holds <- until this.Plain
                | None -> ended <- true

            return holds
        }

    /// Reads until the output ends.
    member this.ReadToEnd(cancellation: Threading.CancellationToken) =
        task {
            let! _ = this.ReadUntil (fun _ -> false) cancellation
            return ()
        }

    override _.ToString() = $"Transcript({pty})"
