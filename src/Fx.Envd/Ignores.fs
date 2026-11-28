namespace Fx.Envd

open System
open System.Text.RegularExpressions

/// `.gitignore` rules as git reads them (gitignore(5)), for searches that respect them:
///
/// - blank lines and lines starting with `#` are skipped; `\#` and `\!` start a pattern with `#` or
///   `!`; trailing spaces go unless escaped (`\ `);
/// - `!` negates: a later match of it re-includes what an earlier rule ignored (but nothing below an
///   ignored directory, which a walk never enters);
/// - a trailing `/` matches directories only;
/// - a pattern with a `/` before its end is anchored at the directory of its file; one without
///   matches a name at any depth below it;
/// - `*`, `?`, `[...]` and `**` as in `Glob` (which also reads `{a,b}`, which git does not).
///
/// The last rule that matches decides, rules of deeper files coming later. Matching is case-sensitive
/// (git's default on Linux). Patterns `Glob` cannot read are skipped, as git skips them.
[<RequireQualifiedAccess>]
module Ignores =
    /// One pattern of an ignore file.
    type Rule =
        {
            Pattern: string
            Regex: Regex
            Negated: bool
            DirectoryOnly: bool
        }

        override this.ToString() =
            (if this.Negated then "!" else "")
            + this.Pattern
            + (if this.DirectoryOnly then "/" else "")

    /// The rules of one ignore file, and the directory they are relative to (relative to the walk's
    /// base, with `/`; "" for the base itself).
    type Rules =
        {
            Directory: string
            Rules: Rule list
        }

        override this.ToString() =
            $"{this.Rules.Length} rules in {this.Directory}/"

    /// Trailing spaces go, unless escaped with a backslash.
    let private trimTrailingSpaces (line: string) =
        let mutable stop = line.Length

        while stop > 0 && line[stop - 1] = ' ' && not (stop > 1 && line[stop - 2] = '\\') do
            stop <- stop - 1

        line.Substring(0, stop)

    let private rule (line: string) : Rule option =
        let line = trimTrailingSpaces (line.TrimEnd '\r')

        if line = "" || line.StartsWith '#' then
            None
        else
            let negated = line.StartsWith '!'
            let body = if negated then line.Substring 1 else line

            let body =
                if body.StartsWith "\\#" || body.StartsWith "\\!" then
                    body.Substring 1
                else
                    body

            let directoryOnly = body.EndsWith '/'
            let body = body.TrimEnd '/'

            if body = "" then
                None
            else
                match Glob.toRegex body with
                | Error _ -> None
                | Ok regex ->
                    Some
                        {
                            Pattern = body
                            Regex = Regex(regex, RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking)
                            Negated = negated
                            DirectoryOnly = directoryOnly
                        }

    /// The rules of an ignore file's text.
    let parse (text: string) : Rule list =
        text.Split '\n' |> Array.choose rule |> List.ofArray

    /// The rules of an ignore file in `directory` (relative to the base, "" for the base).
    let ofText (directory: string) (text: string) : Rules =
        {
            Directory = directory
            Rules = parse text
        }

    /// Whether a path (relative to the base, with `/`) is ignored by rules, outermost first.
    let isIgnored (rules: Rules list) (path: string) (isDirectory: bool) : bool =
        let mutable ignored = false

        for set in rules do
            let relative =
                if set.Directory = "" then
                    Some path
                elif path.StartsWith(set.Directory + "/", StringComparison.Ordinal) then
                    Some(path.Substring(set.Directory.Length + 1))
                else
                    None

            match relative with
            | None -> ()
            | Some relative ->
                for r in set.Rules do
                    if (isDirectory || not r.DirectoryOnly) && r.Regex.IsMatch relative then
                        ignored <- not r.Negated

        ignored
