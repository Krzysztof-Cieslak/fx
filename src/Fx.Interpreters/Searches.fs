namespace Fx.Interpreters

open System
open Fx.Sdk

/// One line a search printed: where it says the line is, and its text (without its line ending).
type SearchLine =
    {
        /// As printed: relative to the command's working directory, or absolute.
        Path: string
        /// 1-based.
        Line: int
        Text: string
    }

    override this.ToString() = $"{this.Path}:{this.Line}"

/// How a plain search prints its lines (`Searches.recognize`).
[<RequireQualifiedAccess>]
type SearchForm =
    /// `path:line:text`.
    | WithPaths
    /// One operand, which the tool names on each line (`path:line:text`) when it searched a
    /// directory, and leaves out (`line:text`) when it searched that one file: the output says which.
    | OneOperand of path: string

    override this.ToString() =
        match this with
        | WithPaths -> "with paths"
        | OneOperand path -> $"one operand ({path})"

/// Plain searches (spec §6.7, T142): `grep -n`, `rg -n` and `git grep -n` as one simple command,
/// recognized from their words, and their output read conservatively. Pure: what the lines claim is
/// checked against the files by `shell.run` (`CoreBindings`), so a line read wrongly here can only
/// mean no matches, never a wrong one.
[<RequireQualifiedAccess>]
module Searches =
    /// The most lines a search's matches are given for; a search that printed more gets none.
    [<Literal>]
    let MaxMatches = 100

    /// The most files a search's matches may be in (each is read to check its lines).
    [<Literal>]
    let MaxFiles = 20

    /// A search tool's options, as far as the matches need them.
    type private Tool =
        {
            /// Short options that change what is printed (counts, file names only, context,
            /// inverted or partial lines, other formats): none of their lines are file lines.
            Refuses: string
            /// Long options that do the same.
            RefusesLong: string list
            /// Short options whose value is the next word unless it is attached.
            Valued: string
            /// Long options whose value is the next word unless it is attached with `=`.
            ValuedLong: string list
            /// Whether it searches directories by default.
            Recursive: bool
            /// Whether it names the file on every line, whatever it searched.
            AlwaysNames: bool
        }

        override this.ToString() =
            $"Tool(refuses {this.Refuses}, valued {this.Valued})"

    let private grep: Tool =
        {
            Refuses = "cClLoqvABCbZzTuh0123456789"
            RefusesLong =
                [
                    "count"
                    "files-with-matches"
                    "files-without-match"
                    "only-matching"
                    "quiet"
                    "silent"
                    "invert-match"
                    "after-context"
                    "before-context"
                    "context"
                    "byte-offset"
                    "null"
                    "null-data"
                    "initial-tab"
                    "no-filename"
                    "label"
                ]
            Valued = "efmABCdD"
            ValuedLong =
                [
                    "regexp"
                    "file"
                    "max-count"
                    "after-context"
                    "before-context"
                    "context"
                    "devices"
                    "directories"
                    "binary-files"
                    "include"
                    "exclude"
                    "exclude-from"
                    "exclude-dir"
                    "label"
                    "group-separator"
                ]
            Recursive = false
            AlwaysNames = false
        }

    let private rg: Tool =
        {
            Refuses = "cloqvABCbIpr0N"
            RefusesLong =
                [
                    "count"
                    "count-matches"
                    "files"
                    "files-with-matches"
                    "files-without-match"
                    "only-matching"
                    "quiet"
                    "invert-match"
                    "after-context"
                    "before-context"
                    "context"
                    "byte-offset"
                    "null"
                    "null-data"
                    "no-filename"
                    "no-line-number"
                    "replace"
                    "passthru"
                    "pretty"
                    "heading"
                    "column"
                    "vimgrep"
                    "json"
                    "stats"
                    "type-list"
                    "max-columns"
                    "trim"
                ]
            Valued = "efgtTmMABCEjrd"
            ValuedLong =
                [
                    "regexp"
                    "file"
                    "glob"
                    "iglob"
                    "type"
                    "type-not"
                    "type-add"
                    "type-clear"
                    "max-count"
                    "max-columns"
                    "max-depth"
                    "max-filesize"
                    "after-context"
                    "before-context"
                    "context"
                    "context-separator"
                    "encoding"
                    "engine"
                    "threads"
                    "replace"
                    "sort"
                    "sortr"
                    "pre"
                    "pre-glob"
                    "ignore-file"
                    "colors"
                    "path-separator"
                    "field-match-separator"
                    "field-context-separator"
                    "dfa-size-limit"
                    "regex-size-limit"
                    "hostname-bin"
                    "hyperlink-format"
                    "generate"
                ]
            Recursive = true
            AlwaysNames = false
        }

    let private gitGrep: Tool =
        {
            Refuses = "cClLoqvABCWphzO0123456789"
            RefusesLong =
                [
                    "count"
                    "name-only"
                    "files-with-matches"
                    "files-without-match"
                    "only-matching"
                    "quiet"
                    "invert-match"
                    "after-context"
                    "before-context"
                    "context"
                    "function-context"
                    "show-function"
                    "null"
                    "heading"
                    "break"
                    "column"
                    "full-name"
                    "open-files-in-pager"
                    "no-filename"
                ]
            Valued = "efmABC"
            ValuedLong =
                [
                    "regexp"
                    "file"
                    "max-count"
                    "max-depth"
                    "threads"
                    "after-context"
                    "before-context"
                    "context"
                ]
            Recursive = true
            AlwaysNames = true
        }

    /// What the options and operands of a search say.
    type private Reading =
        {
            Numbered: bool
            WithFilename: bool
            Recursive: bool
            PatternGiven: bool
            Operands: string list
            Refused: bool
        }

        override this.ToString() =
            $"Reading(numbered {this.Numbered}, {this.Operands.Length} operands, refused {this.Refused})"

    let private read (tool: Tool) (args: string list) : Reading =
        let mutable reading =
            {
                Numbered = false
                WithFilename = false
                Recursive = tool.Recursive
                PatternGiven = false
                Operands = []
                Refused = false
            }

        let mutable rest = args
        let mutable operandsOnly = false

        let flag (c: char) =
            if tool.Refuses.Contains c then
                reading <- { reading with Refused = true }
            else
                match c with
                | 'n' -> reading <- { reading with Numbered = true }
                | 'H' -> reading <- { reading with WithFilename = true }
                | 'r'
                | 'R' -> reading <- { reading with Recursive = true }
                | _ -> ()

        while not rest.IsEmpty && not reading.Refused do
            let word = rest.Head
            rest <- rest.Tail

            if operandsOnly || word = "-" || not (word.StartsWith '-') then
                // `-` is standard input: there is no file to check.
                if word = "-" then
                    reading <- { reading with Refused = true }
                else
                    reading <-
                        { reading with
                            Operands = reading.Operands @ [ word ]
                        }
            elif word = "--" then
                operandsOnly <- true
            elif word.StartsWith "--" then
                let body = word.Substring 2
                let equals = body.IndexOf '='
                let name = if equals < 0 then body else body.Substring(0, equals)

                let value =
                    if equals >= 0 then
                        Some(body.Substring(equals + 1))
                    elif List.contains name tool.ValuedLong && not rest.IsEmpty then
                        let next = rest.Head
                        rest <- rest.Tail
                        Some next
                    else
                        None

                if List.contains name tool.RefusesLong then
                    reading <- { reading with Refused = true }
                else
                    match name, value with
                    | "line-number", _ -> reading <- { reading with Numbered = true }
                    | "with-filename", _ -> reading <- { reading with WithFilename = true }
                    | ("recursive" | "dereference-recursive"), _ -> reading <- { reading with Recursive = true }
                    | ("regexp" | "file"), _ -> reading <- { reading with PatternGiven = true }
                    | ("color" | "colour"), Some "always" -> reading <- { reading with Refused = true }
                    | _ -> ()
            else
                let cluster = word.Substring 1
                let mutable i = 0

                while i < cluster.Length && not reading.Refused do
                    let c = cluster[i]

                    if tool.Valued.Contains c && not (tool.Refuses.Contains c) then
                        if c = 'e' || c = 'f' then
                            reading <- { reading with PatternGiven = true }

                        // The value is the rest of the word, or the next word.
                        if i = cluster.Length - 1 && not rest.IsEmpty then
                            rest <- rest.Tail

                        i <- cluster.Length
                    else
                        flag c
                        i <- i + 1

        reading

    let private isGlob (word: string) =
        word.IndexOfAny([| '*'; '?'; '['; '{' |]) >= 0

    /// The program a word names: its own name, without `.exe` on Windows.
    let private programName (os: Os) (word: string) =
        if os = Os.Windows then
            let lower = word.ToLowerInvariant()

            if lower.EndsWith ".exe" then
                lower.Substring(0, lower.Length - 4)
            else
                lower
        else
            word

    /// Recognizes a plain search from the words of a simple command (as the shell splits it, nothing
    /// expanded): `grep -n`, `rg -n` or `git grep -n`, with nothing that prints anything but file
    /// lines (counts, file names, context, inverted or partial lines, other formats), reading files
    /// rather than standard input. `None` for anything else.
    let recognize (os: Os) (words: string list) : SearchForm option =
        let tool, args =
            match words with
            | program :: args when programName os program = "grep" -> Some grep, args
            | program :: args when programName os program = "rg" -> Some rg, args
            | program :: "grep" :: args when programName os program = "git" -> Some gitGrep, args
            | _ -> None, []

        match tool with
        | None -> None
        | Some tool ->
            let reading = read tool args

            // The first operand is the pattern, unless `-e` or `-f` gave one.
            let operands =
                if reading.PatternGiven then
                    reading.Operands
                else
                    List.skip (min 1 reading.Operands.Length) reading.Operands

            if reading.Refused || not reading.Numbered then
                None
            elif not reading.PatternGiven && reading.Operands.IsEmpty then
                None
            elif tool.AlwaysNames then
                Some SearchForm.WithPaths
            else
                match operands with
                | [] when reading.Recursive -> Some SearchForm.WithPaths
                // No file: grep reads standard input.
                | [] -> None
                | [ one ] when not reading.WithFilename && not (isGlob one) -> Some(SearchForm.OneOperand one)
                | _ -> Some SearchForm.WithPaths

    let private isDigit (c: char) = c >= '0' && c <= '9'

    /// `line:text`: the line's number and text.
    let private numbered (line: string) : (int * string) option =
        let colon = line.IndexOf ':'

        if colon < 1 || colon > 9 then
            None
        elif Seq.forall isDigit (line.Substring(0, colon)) then
            match Int32.TryParse(line.Substring(0, colon)) with
            | true, n when n >= 1 -> Some(n, line.Substring(colon + 1))
            | _ -> None
        else
            None

    /// Whether a path holds a `:` other than a Windows drive's (`C:\…`, `C:/…`): grep and friends
    /// print it as it is, so where it ends could not be told apart from where the line number starts.
    let private hasColon (path: string) =
        let start =
            if
                path.Length >= 3
                && Char.IsAsciiLetter path[0]
                && path[1] = ':'
                && (path[2] = '\\' || path[2] = '/')
            then
                2
            else
                0

        path.IndexOf(':', start) >= 0

    /// `path:line:text`: the path is everything before the first `:<digits>:`; a path that holds
    /// another `:` is not read.
    let private withPath (line: string) : SearchLine option =
        let mutable found = None
        let mutable i = 1

        while found.IsNone && i < line.Length do
            if line[i] = ':' then
                let mutable j = i + 1

                while j < line.Length && isDigit line[j] do
                    j <- j + 1

                if j > i + 1 && j < line.Length && line[j] = ':' && j - i - 1 <= 9 then
                    found <- Some(i, j)

            i <- i + 1

        match found with
        | Some(i, j) ->
            let path = line.Substring(0, i)

            match Int32.TryParse(line.Substring(i + 1, j - i - 1)) with
            | true, n when n >= 1 && not (hasColon path) ->
                Some
                    {
                        Path = path
                        Line = n
                        Text = line.Substring(j + 1)
                    }
            | _ -> None
        | None -> None

    let private withoutCr (text: string) =
        if text.EndsWith '\r' then
            text.Substring(0, text.Length - 1)
        else
            text

    /// The lines a search printed, in its form: `None` when there are none, more than `MaxMatches`,
    /// or one that does not read as a file line.
    let parse (form: SearchForm) (stdout: string) : SearchLine list option =
        let lines =
            let parts = stdout.Split '\n' |> List.ofArray

            let parts =
                if stdout.EndsWith '\n' then
                    parts |> List.truncate (parts.Length - 1)
                else
                    parts

            parts |> List.map withoutCr

        // One file searched: its lines are `line:text`, and the operand is the file.
        let inFile =
            match form with
            | SearchForm.OneOperand path when lines |> List.forall (fun l -> (numbered l).IsSome) -> Some path
            | _ -> None

        if stdout = "" || lines.Length > MaxMatches then
            None
        else
            let read =
                lines
                |> List.map (fun line ->
                    match inFile with
                    | Some path ->
                        numbered line
                        |> Option.map (fun (n, text) -> { Path = path; Line = n; Text = text })
                    | None -> withPath line)

            if read |> List.forall Option.isSome then
                let found = read |> List.choose id

                if (found |> List.map _.Path |> List.distinct).Length > MaxFiles then
                    None
                else
                    Some found
            else
                None
