namespace Fx.CodeAct

open System
open System.Text
open Fx.Contracts

/// The command language of an environment's shell, as `shell.run` hands command lines to it: sh on
/// Linux and macOS, PowerShell 7 on Windows (spec §6.5).
[<RequireQualifiedAccess>]
type ShellDialect =
    | Sh
    | PowerShell

    override this.ToString() =
        match this with
        | Sh -> "sh"
        | PowerShell -> "powershell"

/// A word of a simple command, as written.
type Word =
    {
        /// The word with its quotes and escapes removed. Nothing is expanded: `$HOME` stays `$HOME`, a
        /// command substitution stays as written.
        Text: string
        /// The shell expands part of it when the command runs (a parameter, a command substitution, a
        /// tilde, braces, a bracket glob, ANSI-C quotes; in PowerShell a variable, a subexpression,
        /// splatting): what runs may differ from `Text`. Unquoted `*` and `?` do not count: they match
        /// names in the working directory.
        Expands: bool
    }

    override this.ToString() =
        if this.Expands then this.Text + " (expands)" else this.Text

/// One simple command of a command line: a program and its arguments.
type SimpleCommand =
    {
        /// Its words in order: assignments before the program (`FOO=1 make`), the program, its
        /// arguments. Empty for a line that only redirects (`> file`).
        Words: Word list
        /// Where its output redirections write (`>`, `>>`, `>|`, `&>`, `<>`, `>&file`; `>`, `>>`, `2>`,
        /// `*>` in PowerShell), as written: the null device and duplicated descriptors left out.
        Writes: Word list
    }

    override this.ToString() =
        String.Join(" ", this.Words |> List.map (fun w -> w.Text))
        + (this.Writes |> List.map (fun w -> " > " + w.Text) |> String.concat "")

/// A command line, split so that each part can be checked (docs/modules/codeact.md, "Grants").
[<RequireQualifiedAccess>]
type CommandLine =
    /// Its simple commands, in the order they appear; a command substitution's come before the
    /// command that holds it.
    | Parsed of SimpleCommand list
    /// Not split: syntax the parser does not follow (control flow, functions, here-documents,
    /// script blocks, …) or cannot read safely. The whole line is one command for a person to judge.
    | Opaque of reason: string

    override this.ToString() =
        match this with
        | Parsed commands -> "parsed: " + String.Join(" ; ", commands |> List.map string)
        | Opaque reason -> "opaque: " + reason

/// Reads command lines as a shell would split them (docs/modules/codeact.md, "Grants"). A guardrail,
/// not a boundary (spec §18): it never runs anything, and what it cannot follow it gives up on
/// (`Opaque`), so the whole line goes to a person.
[<RequireQualifiedAccess>]
module ShellSyntax =
    /// The longest line split into parts; a longer one is opaque.
    [<Literal>]
    let MaxLength = 65536

    /// The most simple commands in a line; more make it opaque.
    [<Literal>]
    let MaxCommands = 256

    /// How deep command substitutions may nest.
    [<Literal>]
    let MaxDepth = 8

    let private isBlank (c: char) = c = ' ' || c = '\t' || c = '\r'

    let private isNameStart (c: char) = Char.IsAsciiLetter c || c = '_'

    let private isNameChar (c: char) = Char.IsAsciiLetterOrDigit c || c = '_'

    /// The parser's state over one line; `Failure` set ends it.
    type private Cursor(text: string) =
        member val Pos = 0 with get, set
        member val Failure: string option = None with get, set
        member val Commands = ResizeArray<SimpleCommand>()
        member _.Text = text

        member this.Ok = this.Failure.IsNone

        member this.Fail(reason: string) =
            if this.Failure.IsNone then
                this.Failure <- Some reason

        member this.AtEnd = this.Pos >= text.Length

        /// The character `k` ahead, or `\000` past the end.
        member this.Peek(k: int) =
            let at = this.Pos + k
            if at < text.Length then text[at] else '\000'

        /// Appends the text from `start` to here, as written.
        member this.Slice(word: StringBuilder, start: int) =
            if this.Ok && start <= this.Pos && this.Pos <= text.Length then
                word.Append(text, start, this.Pos - start) |> ignore

        member this.Add(command: SimpleCommand) =
            if this.Commands.Count >= MaxCommands then
                this.Fail "too many commands"
            else
                this.Commands.Add command

    // ---- sh ---------------------------------------------------------------------------------------

    let private shReserved =
        set
            [
                "if"
                "then"
                "else"
                "elif"
                "fi"
                "case"
                "esac"
                "for"
                "select"
                "while"
                "until"
                "do"
                "done"
                "function"
                "coproc"
                "[["
                "]]"
                "{"
                "}"
            ]

    let private shNull = set [ "/dev/null"; "/dev/stdout"; "/dev/stderr"; "/dev/tty" ]

    /// Ends an unquoted sh word.
    let private shEnds (c: char) =
        c = '\000'
        || isBlank c
        || c = '\n'
        || c = ';'
        || c = '&'
        || c = '|'
        || c = '<'
        || c = '>'
        || c = ')'

    type private Sh(cursor: Cursor, depth: int) =
        let text = cursor.Text

        /// Skips blanks and line continuations.
        member _.SkipBlanks() =
            let mutable go = true

            while go && cursor.Ok do
                let c = cursor.Peek 0

                if isBlank c then
                    cursor.Pos <- cursor.Pos + 1
                elif c = '\\' && cursor.Peek 1 = '\n' then
                    cursor.Pos <- cursor.Pos + 2
                else
                    go <- false

        member _.SkipComment() =
            while not cursor.AtEnd && cursor.Peek 0 <> '\n' do
                cursor.Pos <- cursor.Pos + 1

        /// Skips blanks, newlines and comments.
        member this.SkipLines() =
            let mutable go = true

            while go && cursor.Ok do
                this.SkipBlanks()

                match cursor.Peek 0 with
                | '\n' -> cursor.Pos <- cursor.Pos + 1
                | '#' -> this.SkipComment()
                | _ -> go <- false

        /// A command substitution's text after `$(`, parsed in place up to its `)`.
        member this.Substitution() =
            if depth >= MaxDepth then
                cursor.Fail "command substitutions nested too deep"
            else
                let inner = Sh(cursor, depth + 1)
                inner.List(Some ')')

                if cursor.Ok then
                    if cursor.Peek 0 = ')' then
                        cursor.Pos <- cursor.Pos + 1
                    else
                        cursor.Fail "an unclosed command substitution"

        /// A backquoted command substitution: its text unescaped, parsed on its own.
        member _.Backquoted() =
            // At the opening backquote.
            cursor.Pos <- cursor.Pos + 1
            let inner = StringBuilder()
            let mutable closed = false

            while not closed && cursor.Ok do
                match cursor.Peek 0 with
                | '\000' when cursor.AtEnd -> cursor.Fail "an unclosed backquote"
                | '`' ->
                    cursor.Pos <- cursor.Pos + 1
                    closed <- true
                | '\\' when cursor.Pos + 1 >= text.Length -> cursor.Fail "an unclosed backquote"
                | '\\' ->
                    match cursor.Peek 1 with
                    | '`' -> cursor.Fail "nested backquotes"
                    | '$'
                    | '\\' as c ->
                        inner.Append c |> ignore
                        cursor.Pos <- cursor.Pos + 2
                    | c ->
                        inner.Append('\\').Append(c) |> ignore
                        cursor.Pos <- cursor.Pos + 2
                | c ->
                    inner.Append c |> ignore
                    cursor.Pos <- cursor.Pos + 1

            if cursor.Ok then
                if depth >= MaxDepth then
                    cursor.Fail "command substitutions nested too deep"
                else
                    let nested = Cursor(inner.ToString())
                    let parser = Sh(nested, depth + 1)
                    parser.Line()

                    match nested.Failure with
                    | Some reason -> cursor.Fail reason
                    | None ->
                        for command in nested.Commands do
                            cursor.Add command

        /// `$…`: appends it as written; whether it expands.
        member this.Dollar(word: StringBuilder, quoted: bool) : bool =
            let start = cursor.Pos

            match cursor.Peek 1 with
            | '(' when cursor.Peek 2 = '(' ->
                cursor.Fail "an arithmetic expansion"
                true
            | '(' ->
                cursor.Pos <- cursor.Pos + 2
                this.Substitution()

                cursor.Slice(word, start)

                true
            | '{' ->
                let close = text.IndexOf('}', cursor.Pos + 2)

                if close < 0 then
                    cursor.Fail "an unclosed parameter expansion"
                else
                    let inner = text.Substring(cursor.Pos + 2, close - cursor.Pos - 2)

                    if
                        inner.Contains '$'
                        || inner.Contains '`'
                        || inner.Contains '{'
                        || inner.Contains '\''
                        || inner.Contains '"'
                    then
                        cursor.Fail "a parameter expansion with a nested expansion"
                    else
                        word.Append(text, start, close + 1 - start) |> ignore
                        cursor.Pos <- close + 1

                true
            | '\'' when not quoted ->
                // ANSI-C quoting: backslash escapes up to the closing quote.
                cursor.Pos <- cursor.Pos + 2
                let mutable closed = false

                while not closed && cursor.Ok do
                    match cursor.Peek 0 with
                    | '\000' when cursor.AtEnd -> cursor.Fail "an unclosed quote"
                    | '\'' ->
                        cursor.Pos <- cursor.Pos + 1
                        closed <- true
                    | '\\' when cursor.Pos + 1 < text.Length ->
                        word.Append(text, cursor.Pos, 2) |> ignore
                        cursor.Pos <- cursor.Pos + 2
                    | c ->
                        word.Append c |> ignore
                        cursor.Pos <- cursor.Pos + 1

                true
            | '"' when not quoted ->
                cursor.Pos <- cursor.Pos + 1
                this.Double word |> ignore
                true
            | c when isNameStart c ->
                cursor.Pos <- cursor.Pos + 1

                while isNameChar (cursor.Peek 0) do
                    cursor.Pos <- cursor.Pos + 1

                cursor.Slice(word, start)
                true
            | c when Char.IsAsciiDigit c || "@*#?$!-".Contains c ->
                word.Append(text, start, 2) |> ignore
                cursor.Pos <- cursor.Pos + 2
                true
            | _ ->
                word.Append '$' |> ignore
                cursor.Pos <- cursor.Pos + 1
                false

        /// The inside of double quotes, from after the opening quote; whether it expands.
        member this.Double(word: StringBuilder) : bool =
            cursor.Pos <- cursor.Pos + 1
            let mutable expands = false
            let mutable closed = false

            while not closed && cursor.Ok do
                match cursor.Peek 0 with
                | '\000' when cursor.AtEnd -> cursor.Fail "an unclosed quote"
                | '"' ->
                    cursor.Pos <- cursor.Pos + 1
                    closed <- true
                | '\\' ->
                    match cursor.Peek 1 with
                    | '$'
                    | '`'
                    | '"'
                    | '\\' as c ->
                        word.Append c |> ignore
                        cursor.Pos <- cursor.Pos + 2
                    | '\n' -> cursor.Pos <- cursor.Pos + 2
                    | _ ->
                        word.Append '\\' |> ignore
                        cursor.Pos <- cursor.Pos + 1
                | '$' ->
                    if this.Dollar(word, true) then
                        expands <- true
                | '`' ->
                    let start = cursor.Pos
                    this.Backquoted()

                    cursor.Slice(word, start)

                    expands <- true
                | c ->
                    word.Append c |> ignore
                    cursor.Pos <- cursor.Pos + 1

            expands

        /// A word, at its first character.
        member this.Word() : Word =
            let word = StringBuilder()
            let mutable expands = false
            let mutable go = true

            while go && cursor.Ok do
                match cursor.Peek 0 with
                | '\'' ->
                    let close = text.IndexOf('\'', cursor.Pos + 1)

                    if close < 0 then
                        cursor.Fail "an unclosed quote"
                    else
                        word.Append(text, cursor.Pos + 1, close - cursor.Pos - 1) |> ignore
                        cursor.Pos <- close + 1
                | '"' ->
                    if this.Double word then
                        expands <- true
                | '\\' ->
                    match cursor.Peek 1 with
                    | '\n' -> cursor.Pos <- cursor.Pos + 2
                    | '\000' when cursor.Pos + 1 >= text.Length -> cursor.Fail "a backslash at the end"
                    | c ->
                        word.Append c |> ignore
                        cursor.Pos <- cursor.Pos + 2
                | '$' ->
                    if this.Dollar(word, false) then
                        expands <- true
                | '`' ->
                    let start = cursor.Pos
                    this.Backquoted()

                    cursor.Slice(word, start)

                    expands <- true
                | '(' -> cursor.Fail "a parenthesis inside a word (a function, an array or a pattern)"
                | ('<' | '>') when cursor.Peek 1 = '(' -> cursor.Fail "a process substitution"
                | c when shEnds c -> go <- false
                | c ->
                    if c = '[' || c = '{' || c = '}' || c = '~' then
                        expands <- true

                    word.Append c |> ignore
                    cursor.Pos <- cursor.Pos + 1

            {
                Text = word.ToString()
                Expands = expands
            }

        /// A redirection's target.
        member this.Target() : Word option =
            this.SkipBlanks()

            match cursor.Peek 0 with
            | c when shEnds c || c = '\000' ->
                cursor.Fail "a redirection without a target"
                None
            | _ -> Some(this.Word())

        /// A redirection, at its operator (after any descriptor number); adds what it writes.
        member this.Redirect(writes: ResizeArray<Word>) =
            let write (target: Word option) =
                match target with
                | Some target when
                    target.Expands
                    || not (
                        shNull.Contains target.Text
                        || target.Text.StartsWith("/dev/fd/", StringComparison.Ordinal)
                    )
                    ->
                    writes.Add target
                | _ -> ()

            let duplicate () =
                // `>&n`, `<&n`, `>&-`: a descriptor; anything else after `>&` is a file.
                match this.Target() with
                | Some target when
                    not target.Expands
                    && target.Text <> ""
                    && (target.Text = "-" || target.Text |> Seq.forall Char.IsAsciiDigit)
                    ->
                    None
                | other -> other

            match cursor.Peek 0, cursor.Peek 1, cursor.Peek 2 with
            | ('<' | '>'), '(', _ -> cursor.Fail "a process substitution"
            | '&', '>', '>' ->
                cursor.Pos <- cursor.Pos + 3
                write (this.Target())
            | '&', '>', _ ->
                cursor.Pos <- cursor.Pos + 2
                write (this.Target())
            | '>', '>', _
            | '>', '|', _ ->
                cursor.Pos <- cursor.Pos + 2
                write (this.Target())
            | '>', '&', _ ->
                cursor.Pos <- cursor.Pos + 2
                write (duplicate ())
            | '>', _, _ ->
                cursor.Pos <- cursor.Pos + 1
                write (this.Target())
            | '<', '<', '<' ->
                cursor.Pos <- cursor.Pos + 3
                this.Target() |> ignore
            | '<', '<', _ -> cursor.Fail "a here-document"
            | '<', '>', _ ->
                cursor.Pos <- cursor.Pos + 2
                write (this.Target())
            | '<', '&', _ ->
                cursor.Pos <- cursor.Pos + 2

                match duplicate () with
                | Some _ -> cursor.Fail "an input redirection from a file with <&"
                | None -> ()
            | '<', _, _ ->
                cursor.Pos <- cursor.Pos + 1
                this.Target() |> ignore
            | _ -> cursor.Fail "an unexpected redirection"

        /// Whether a redirection starts here: an operator, or digits right before one.
        member _.AtRedirect() =
            match cursor.Peek 0 with
            | '<'
            | '>' -> true
            | '&' -> cursor.Peek 1 = '>'
            | c when Char.IsAsciiDigit c ->
                let mutable k = 0

                while Char.IsAsciiDigit(cursor.Peek k) do
                    k <- k + 1

                let next = cursor.Peek k
                next = '<' || next = '>'
            | _ -> false

        /// A simple command: words, assignments and redirections.
        member this.Simple() =
            let words = ResizeArray<Word>()
            let writes = ResizeArray<Word>()
            let mutable go = true

            while go && cursor.Ok do
                this.SkipBlanks()

                match cursor.Peek 0 with
                | '#' -> go <- false
                | '(' -> cursor.Fail "a parenthesis after a command (a function definition)"
                | _ when this.AtRedirect() ->
                    while Char.IsAsciiDigit(cursor.Peek 0) do
                        cursor.Pos <- cursor.Pos + 1

                    this.Redirect writes
                | c when shEnds c -> go <- false
                | _ ->
                    let word = this.Word()

                    if cursor.Ok && words.Count = 0 && shReserved.Contains word.Text then
                        cursor.Fail $"control flow ({word.Text})"
                    else
                        words.Add word

            if cursor.Ok then
                if words.Count = 0 && writes.Count = 0 then
                    cursor.Fail "an empty command"
                else
                    cursor.Add
                        {
                            Words = List.ofSeq words
                            Writes = List.ofSeq writes
                        }

        /// After a subshell or a group: redirections of the whole group are not followed.
        member this.AfterGroup() =
            this.SkipBlanks()

            if this.AtRedirect() then
                cursor.Fail "a redirection of a group of commands"

        /// A command: a subshell, a group, or a simple command.
        member this.Command() =
            this.SkipBlanks()

            match cursor.Peek 0, cursor.Peek 1 with
            | '(', '(' -> cursor.Fail "an arithmetic command"
            | '(', _ ->
                cursor.Pos <- cursor.Pos + 1
                this.List(Some ')')

                if cursor.Ok then
                    if cursor.Peek 0 = ')' then
                        cursor.Pos <- cursor.Pos + 1
                        this.AfterGroup()
                    else
                        cursor.Fail "an unclosed subshell"
            | '{', c when isBlank c || c = '\n' ->
                cursor.Pos <- cursor.Pos + 1
                this.List(Some '}')

                if cursor.Ok then
                    if cursor.Peek 0 = '}' then
                        cursor.Pos <- cursor.Pos + 1
                        this.AfterGroup()
                    else
                        cursor.Fail "an unclosed group"
            | _ -> this.Simple()

        /// A pipeline: `!`, then commands joined by `|` or `|&`.
        member this.Pipeline() =
            this.SkipBlanks()

            if cursor.Peek 0 = '!' && (isBlank (cursor.Peek 1) || cursor.Peek 1 = '\n') then
                cursor.Pos <- cursor.Pos + 1

            this.Command()
            let mutable go = true

            while go && cursor.Ok do
                this.SkipBlanks()

                match cursor.Peek 0, cursor.Peek 1 with
                | '|', '|' -> go <- false
                | '|', '&' ->
                    cursor.Pos <- cursor.Pos + 2
                    this.SkipLines()
                    this.Command()
                | '|', _ ->
                    cursor.Pos <- cursor.Pos + 1
                    this.SkipLines()
                    this.Command()
                | _ -> go <- false

        /// Whether the list ends here: the end of the line, or the closer of a subshell or group.
        member _.Closes(closer: char option) =
            match closer with
            | _ when cursor.AtEnd -> true
            | Some ')' -> cursor.Peek 0 = ')'
            | Some '}' ->
                cursor.Peek 0 = '}'
                && (let next = cursor.Peek 1

                    next = '\000'
                    || isBlank next
                    || next = '\n'
                    || next = ';'
                    || next = ')'
                    || next = '&'
                    || next = '|')
            | _ -> false

        /// Pipelines joined by `;`, `&`, `&&`, `||` and newlines, up to the end or the closer.
        member this.List(closer: char option) =
            this.SkipLines()
            let mutable go = not (this.Closes closer)

            while go && cursor.Ok do
                this.Pipeline()

                if cursor.Ok then
                    this.SkipBlanks()

                    match cursor.Peek 0, cursor.Peek 1 with
                    | '#', _ ->
                        this.SkipLines()
                        go <- not (this.Closes closer)
                    | ';', ';' -> cursor.Fail "a case clause"
                    | '&', '&'
                    | '|', '|' ->
                        cursor.Pos <- cursor.Pos + 2
                        this.SkipLines()

                        if this.Closes closer then
                            cursor.Fail "a command missing after && or ||"
                    | ';', _
                    | '&', _
                    | '\n', _ ->
                        cursor.Pos <- cursor.Pos + 1
                        this.SkipLines()
                        go <- not (this.Closes closer)
                    | _ when this.Closes closer -> go <- false
                    | c, _ -> cursor.Fail $"an unexpected {Json.quote (string c)}"

        /// The whole line.
        member this.Line() =
            this.List None

            if cursor.Ok && not cursor.AtEnd then
                cursor.Fail $"an unexpected {Json.quote (string (cursor.Peek 0))}"

    // ---- PowerShell -------------------------------------------------------------------------------

    let private psKeywords =
        set
            [
                "begin"
                "break"
                "catch"
                "class"
                "clean"
                "configuration"
                "continue"
                "data"
                "define"
                "do"
                "dynamicparam"
                "else"
                "elseif"
                "end"
                "enum"
                "exit"
                "filter"
                "finally"
                "for"
                "foreach"
                "from"
                "function"
                "hidden"
                "if"
                "in"
                "inlinescript"
                "parallel"
                "param"
                "process"
                "return"
                "sequence"
                "static"
                "switch"
                "throw"
                "trap"
                "try"
                "until"
                "using"
                "var"
                "while"
                "workflow"
            ]

    let private psNull = set [ "$null"; "nul"; "/dev/null" ]

    /// Typographic quotes PowerShell reads as quotes.
    let private psQuotes = "‘’‚‛“”„"

    /// Ends an unquoted PowerShell word.
    let private psEnds (c: char) =
        c = '\000'
        || isBlank c
        || c = '\n'
        || c = ';'
        || c = '&'
        || c = '|'
        || c = '>'
        || c = ')'

    type private Ps(cursor: Cursor, depth: int) =
        let text = cursor.Text

        member _.SkipBlanks() =
            let mutable go = true

            while go && cursor.Ok do
                let c = cursor.Peek 0

                if isBlank c then
                    cursor.Pos <- cursor.Pos + 1
                elif
                    c = '`'
                    && (cursor.Peek 1 = '\n' || (cursor.Peek 1 = '\r' && cursor.Peek 2 = '\n'))
                then
                    cursor.Pos <- cursor.Pos + (if cursor.Peek 1 = '\n' then 2 else 3)
                elif c = '<' && cursor.Peek 1 = '#' then
                    let close = text.IndexOf("#>", cursor.Pos + 2, StringComparison.Ordinal)

                    if close < 0 then
                        cursor.Fail "an unclosed block comment"
                    else
                        cursor.Pos <- close + 2
                else
                    go <- false

        member _.SkipComment() =
            while not cursor.AtEnd && cursor.Peek 0 <> '\n' do
                cursor.Pos <- cursor.Pos + 1

        member this.SkipLines() =
            let mutable go = true

            while go && cursor.Ok do
                this.SkipBlanks()

                match cursor.Peek 0 with
                | '\n' -> cursor.Pos <- cursor.Pos + 1
                | '#' -> this.SkipComment()
                | _ -> go <- false

        /// A subexpression's text after `$(`, `@(` or `(`, parsed in place up to its `)`.
        member this.Subexpression() =
            if depth >= MaxDepth then
                cursor.Fail "subexpressions nested too deep"
            else
                let inner = Ps(cursor, depth + 1)
                inner.List(Some ')')

                if cursor.Ok then
                    if cursor.Peek 0 = ')' then
                        cursor.Pos <- cursor.Pos + 1

                        // `(…).Member` and `(…)[i]` call into .NET.
                        match cursor.Peek 0 with
                        | '.'
                        | '['
                        | '(' -> cursor.Fail "a member access"
                        | _ -> ()
                    else
                        cursor.Fail "an unclosed subexpression"

        /// `$…`: appends it as written; whether it expands. Outside strings, `$x.Member` and `$x[i]`
        /// evaluate expressions; inside them only the variable expands.
        member this.Dollar(word: StringBuilder, quoted: bool) : bool =
            let start = cursor.Pos

            match cursor.Peek 1 with
            | '(' ->
                cursor.Pos <- cursor.Pos + 2
                this.Subexpression()

                cursor.Slice(word, start)

                true
            | '{' ->
                let close = text.IndexOf('}', cursor.Pos + 2)

                if close < 0 then
                    cursor.Fail "an unclosed variable"
                else
                    word.Append(text, start, close + 1 - start) |> ignore
                    cursor.Pos <- close + 1

                true
            | c when isNameChar c || c = '?' || c = '$' || c = '^' ->
                cursor.Pos <- cursor.Pos + 1

                if c = '?' || c = '$' || c = '^' then
                    cursor.Pos <- cursor.Pos + 1
                else
                    while isNameChar (cursor.Peek 0)
                          || (cursor.Peek 0 = ':' && isNameStart (cursor.Peek 1)) do
                        cursor.Pos <- cursor.Pos + 1

                cursor.Slice(word, start)

                if not quoted then
                    match cursor.Peek 0 with
                    | '.' when isNameStart (cursor.Peek 1) -> cursor.Fail "a member access"
                    | '['
                    | '(' -> cursor.Fail "a member access"
                    | _ -> ()

                true
            | _ ->
                word.Append '$' |> ignore
                cursor.Pos <- cursor.Pos + 1
                false

        /// The inside of double quotes, from the opening quote; whether it expands.
        member this.Double(word: StringBuilder) : bool =
            cursor.Pos <- cursor.Pos + 1
            let mutable expands = false
            let mutable closed = false

            while not closed && cursor.Ok do
                match cursor.Peek 0 with
                | '\000' when cursor.AtEnd -> cursor.Fail "an unclosed quote"
                | '"' when cursor.Peek 1 = '"' ->
                    word.Append '"' |> ignore
                    cursor.Pos <- cursor.Pos + 2
                | '"' ->
                    cursor.Pos <- cursor.Pos + 1
                    closed <- true
                | '`' when cursor.Pos + 1 < text.Length ->
                    word.Append(cursor.Peek 1) |> ignore
                    cursor.Pos <- cursor.Pos + 2
                | '`' -> cursor.Fail "an escape at the end"
                | '$' ->
                    // A subexpression in a string runs; a variable expands.
                    if this.Dollar(word, true) then
                        expands <- true
                | c when psQuotes.Contains c -> cursor.Fail "a typographic quote"
                | c ->
                    word.Append c |> ignore
                    cursor.Pos <- cursor.Pos + 1

            expands

        /// A word in argument mode, at its first character.
        member this.Word() : Word =
            let word = StringBuilder()
            let mutable expands = false
            let mutable go = true
            let start = cursor.Pos

            if cursor.Peek 0 = '-' && cursor.Peek 1 = '-' && cursor.Peek 2 = '%' then
                cursor.Fail "the stop-parsing token (--%)"

            while go && cursor.Ok do
                let first = cursor.Pos = start

                match cursor.Peek 0 with
                | '\'' ->
                    cursor.Pos <- cursor.Pos + 1
                    let mutable closed = false

                    while not closed && cursor.Ok do
                        match cursor.Peek 0 with
                        | '\000' when cursor.AtEnd -> cursor.Fail "an unclosed quote"
                        | '\'' when cursor.Peek 1 = '\'' ->
                            word.Append '\'' |> ignore
                            cursor.Pos <- cursor.Pos + 2
                        | '\'' ->
                            cursor.Pos <- cursor.Pos + 1
                            closed <- true
                        | c when psQuotes.Contains c -> cursor.Fail "a typographic quote"
                        | c ->
                            word.Append c |> ignore
                            cursor.Pos <- cursor.Pos + 1
                | '"' ->
                    if this.Double word then
                        expands <- true
                | '`' ->
                    match cursor.Peek 1 with
                    | '\n' -> cursor.Pos <- cursor.Pos + 2
                    | '\000' when cursor.Pos + 1 >= text.Length -> cursor.Fail "an escape at the end"
                    | c ->
                        word.Append c |> ignore
                        cursor.Pos <- cursor.Pos + 2
                | '$' ->
                    if this.Dollar(word, false) then
                        expands <- true
                | '@' when first ->
                    match cursor.Peek 1 with
                    | '(' ->
                        cursor.Pos <- cursor.Pos + 2
                        this.Subexpression()
                        cursor.Slice(word, start)
                        expands <- true
                    | '{' -> cursor.Fail "a hashtable"
                    | '"'
                    | '\'' -> cursor.Fail "a here-string"
                    | c when isNameStart c ->
                        cursor.Pos <- cursor.Pos + 1

                        while isNameChar (cursor.Peek 0) do
                            cursor.Pos <- cursor.Pos + 1

                        cursor.Slice(word, start)
                        expands <- true
                    | _ ->
                        word.Append '@' |> ignore
                        cursor.Pos <- cursor.Pos + 1
                | '(' when first ->
                    cursor.Pos <- cursor.Pos + 1
                    this.Subexpression()
                    cursor.Slice(word, start)
                    expands <- true
                | '(' -> cursor.Fail "a parenthesis inside a word"
                | '{'
                | '}' -> cursor.Fail "a script block"
                | '[' when first -> cursor.Fail "a type or an attribute"
                | '<' -> cursor.Fail "the < operator"
                | c when psQuotes.Contains c -> cursor.Fail "a typographic quote"
                | c when psEnds c -> go <- false
                | c ->
                    if c = '[' || c = '~' then
                        expands <- true

                    word.Append c |> ignore
                    cursor.Pos <- cursor.Pos + 1

            {
                Text = word.ToString()
                Expands = expands
            }

        member _.AtRedirect() =
            match cursor.Peek 0, cursor.Peek 1 with
            | '>', _ -> true
            | '*', '>' -> true
            | c, '>' when c >= '1' && c <= '6' -> true
            | _ -> false

        member this.Redirect(writes: ResizeArray<Word>) =
            if cursor.Peek 0 <> '>' then
                cursor.Pos <- cursor.Pos + 1

            cursor.Pos <- cursor.Pos + 1

            match cursor.Peek 0, cursor.Peek 1 with
            | '&', c when c >= '1' && c <= '6' -> cursor.Pos <- cursor.Pos + 2
            | '&', _ -> cursor.Fail "a redirection to a stream that does not exist"
            | _ ->
                if cursor.Peek 0 = '>' then
                    cursor.Pos <- cursor.Pos + 1

                this.SkipBlanks()

                match cursor.Peek 0 with
                | c when psEnds c -> cursor.Fail "a redirection without a target"
                | _ ->
                    let start = cursor.Pos
                    let target = this.Word()
                    let raw = text.Substring(start, cursor.Pos - start)

                    if cursor.Ok && not (psNull.Contains(raw.ToLowerInvariant())) then
                        writes.Add target

        /// A command: a name (or `&`/`.` and what they invoke), its arguments and redirections.
        member this.Command() =
            this.SkipBlanks()
            let words = ResizeArray<Word>()
            let writes = ResizeArray<Word>()

            // What may start a command: a bare name, or an invocation operator.
            match cursor.Peek 0, cursor.Peek 1 with
            | ('&' | '.'), c when isBlank c ->
                words.Add
                    {
                        Text = string (cursor.Peek 0)
                        Expands = false
                    }

                cursor.Pos <- cursor.Pos + 1
                this.SkipBlanks()

                match cursor.Peek 0 with
                | '{' -> cursor.Fail "a script block"
                | '(' -> cursor.Fail "an expression"
                | _ -> ()
            | c, _ when
                Char.IsLetter c
                || c = '_'
                || c = '.'
                || c = '\\'
                || c = '/'
                || c = '~'
                || c = '%'
                || c = '?'
                ->
                ()
            | c, _ when c = '\000' || psEnds c -> cursor.Fail "an empty command"
            | _ -> cursor.Fail "an expression where a command was expected"

            let mutable go = true

            while go && cursor.Ok do
                this.SkipBlanks()

                match cursor.Peek 0 with
                | '#' -> go <- false
                | _ when this.AtRedirect() -> this.Redirect writes
                | c when psEnds c -> go <- false
                | _ ->
                    let word = this.Word()

                    if cursor.Ok then
                        if words.Count = 0 && psKeywords.Contains(word.Text.ToLowerInvariant()) then
                            cursor.Fail $"a statement ({word.Text})"
                        else
                            words.Add word

            if cursor.Ok then
                if words.Count = 0 then
                    cursor.Fail "an empty command"
                else
                    cursor.Add
                        {
                            Words = List.ofSeq words
                            Writes = List.ofSeq writes
                        }

        member this.Pipeline() =
            this.Command()
            let mutable go = true

            while go && cursor.Ok do
                this.SkipBlanks()

                match cursor.Peek 0, cursor.Peek 1 with
                | '|', '|' -> go <- false
                | '|', _ ->
                    cursor.Pos <- cursor.Pos + 1
                    this.SkipLines()
                    this.Command()
                | _ -> go <- false

        member _.Closes(closer: char option) =
            cursor.AtEnd || (closer = Some ')' && cursor.Peek 0 = ')')

        member this.List(closer: char option) =
            this.SkipLines()
            let mutable go = not (this.Closes closer)

            while go && cursor.Ok do
                this.Pipeline()

                if cursor.Ok then
                    this.SkipBlanks()

                    match cursor.Peek 0, cursor.Peek 1 with
                    | '#', _ ->
                        this.SkipLines()
                        go <- not (this.Closes closer)
                    | '&', '&'
                    | '|', '|' ->
                        cursor.Pos <- cursor.Pos + 2
                        this.SkipLines()

                        if this.Closes closer then
                            cursor.Fail "a command missing after && or ||"
                    | ';', _
                    | '&', _
                    | '\n', _ ->
                        cursor.Pos <- cursor.Pos + 1
                        this.SkipLines()
                        go <- not (this.Closes closer)
                    | _ when this.Closes closer -> go <- false
                    | c, _ -> cursor.Fail $"an unexpected {Json.quote (string c)}"

        member this.Line() =
            this.List None

            if cursor.Ok && not cursor.AtEnd then
                cursor.Fail $"an unexpected {Json.quote (string (cursor.Peek 0))}"

    // ---- The parsers ------------------------------------------------------------------------------

    /// Splits a command line into simple commands as the dialect's shell would, or gives up (`Opaque`).
    let parse (dialect: ShellDialect) (text: string) : CommandLine =
        if text.Length > MaxLength then
            CommandLine.Opaque "too long to split"
        elif text.Contains '\000' then
            CommandLine.Opaque "a NUL character"
        else
            let cursor = Cursor(text)

            match dialect with
            | ShellDialect.Sh -> Sh(cursor, 0).Line()
            | ShellDialect.PowerShell -> Ps(cursor, 0).Line()

            match cursor.Failure with
            | Some reason -> CommandLine.Opaque reason
            | None -> CommandLine.Parsed(List.ofSeq cursor.Commands)

    /// Text as one line of words: control characters and runs of white space become single spaces.
    let normalize (text: string) : string =
        let builder = StringBuilder(text.Length)
        let mutable space = false

        for c in text do
            if Char.IsWhiteSpace c || Char.IsControl c then
                space <- builder.Length > 0
            else
                if space then
                    builder.Append ' ' |> ignore
                    space <- false

                builder.Append c |> ignore

        builder.ToString()

    /// A simple command as a grant's `command` resource (docs/modules/grants.md): its words joined by
    /// single spaces, white space inside a word splitting it, no control characters. `None` for a
    /// command with no words.
    let resource (words: Word list) : string option =
        match normalize (String.Join(" ", words |> List.map (fun w -> w.Text))) with
        | "" -> None
        | text -> Some text
