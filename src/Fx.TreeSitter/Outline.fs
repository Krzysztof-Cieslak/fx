namespace Fx.TreeSitter

open System
open System.Collections.Concurrent
open System.Text

/// One entry of a file's outline: a definition, where it is and what is defined inside it.
type OutlineItem =
    {
        /// What the query calls it: "module", "type", "function", "method", "property", ...
        Kind: string
        Name: string
        /// The source line where its name is, trimmed, whitespace collapsed, at most HeaderMaxChars
        /// characters: `type Retry =`, `public int Count { get; }`, `export function f(x) {`. The name's
        /// line rather than the definition's first, which can be an attribute or a decorator.
        Header: string
        /// One-based, inclusive.
        StartLine: int
        EndLine: int
        Children: OutlineItem list
    }

/// The outline prototype (spec §6.7): definitions found by a query per language (Queries.fs),
/// nested by where they are.
///
/// A query's patterns capture a definition as `@definition.<kind>` and its name as `@name`. The
/// grammars ship tags.scm queries for code navigation, but they leave out much an outline needs
/// (C# properties, records and structs; TypeScript classes and functions live in JavaScript's), so
/// the outline queries are fx's own.
[<RequireQualifiedAccess>]
module Outline =
    [<Literal>]
    let HeaderMaxChars = 100

    let private queries = ConcurrentDictionary<Language, Query>()

    /// The outline query of a language (Tsx shares TypeScript's text, compiled for tsx).
    let source (language: Language) : string =
        match language with
        | Language.FSharp -> Queries.FSharp
        | Language.FSharpSignature -> Queries.FSharpSignature
        | Language.CSharp -> Queries.CSharp
        | Language.Python -> Queries.Python
        | Language.TypeScript
        | Language.Tsx -> Queries.TypeScript

    /// The compiled outline query of a language, compiled once. Throws if it does not compile (a bug
    /// in the embedded query, which the tests catch).
    let query (language: Language) : Query =
        queries.GetOrAdd(
            language,
            fun language ->
                match Query.compile language (source language) with
                | Ok query -> query
                | Error error ->
                    invalidOp (
                        "the outline query for "
                        + string language
                        + " does not compile: "
                        + string error.Kind
                        + " at byte "
                        + string error.Offset
                    )
        )

    let private header (name: Node) : string =
        let source = name.Tree.Source
        let at = min name.StartByte source.Length

        let start =
            if at = 0 then
                0
            else
                match Array.LastIndexOf(source, '\n'B, at - 1) with
                | -1 -> 0
                | i -> i + 1

        let stop =
            match Array.IndexOf(source, '\n'B, at) with
            | -1 -> source.Length
            | i -> i

        let first = Encoding.UTF8.GetString(source, start, stop - start)

        let collapsed =
            String.Join(" ", first.Split([| ' '; '\t'; '\r' |], StringSplitOptions.RemoveEmptyEntries))

        if collapsed.Length <= HeaderMaxChars then
            collapsed
        else
            collapsed.Substring(0, HeaderMaxChars - 1) + "…"

    let private isComment (kind: string) =
        kind = "comment"
        || kind = "line_comment"
        || kind = "block_comment"
        || kind = "xml_doc"

    /// Where a definition's last token that is not a comment ends. tree-sitter-fsharp attaches the
    /// doc comment of a definition to the end of the one before it, which would otherwise end there.
    let rec private contentEnd (node: Node) : Position =
        match node.Children |> List.filter (fun c -> not (isComment c.Kind)) |> List.tryLast with
        | None -> node.End
        | Some last -> contentEnd last

    type private Found =
        {
            Kind: string
            Name: string
            NameNode: Node
            Node: Node
            Pattern: int
            Start: int
            End: int
        }

    /// The outline of a tree: its definitions in source order, each with those inside it.
    let ofTree (tree: Tree) : OutlineItem list =
        let found =
            Query.matches (query tree.Language) tree.Root
            |> List.choose (fun m ->
                let definition =
                    m.Captures
                    |> List.tryFind (fun c -> c.Name.StartsWith("definition.", StringComparison.Ordinal))

                let name = m.Captures |> List.tryFind (fun c -> c.Name = "name")

                match definition, name with
                | Some d, Some n ->
                    Some
                        {
                            Kind = d.Name.Substring "definition.".Length
                            Name = n.Node.Text
                            NameNode = n.Node
                            Node = d.Node
                            Pattern = m.Pattern
                            Start = d.Node.StartByte
                            End = d.Node.EndByte
                        }
                | _ -> None)
            // One name, one item: patterns may match a definition and what wraps it (an export, a
            // decorator), or one node twice. Keep the outermost, then the first pattern.
            |> List.groupBy (fun f -> f.NameNode.StartByte)
            |> List.map (fun (_, same) -> same |> List.minBy (fun f -> f.Start - f.End, f.Pattern))
            |> List.sortBy (fun f -> f.Start, -f.End)

        // Nests by containment: each definition goes under the innermost one before it that spans it.
        let rec build (items: Found list) (within: int) : OutlineItem list * Found list =
            match items with
            | f :: rest when f.Start < within ->
                let children, after = build rest f.End
                let siblings, remaining = build after within

                let item =
                    {
                        Kind = f.Kind
                        Name = f.Name
                        Header = header f.NameNode
                        StartLine = f.Node.Start.Line
                        EndLine = (contentEnd f.Node).Line
                        Children = children
                    }

                item :: siblings, remaining
            | _ -> [], items

        fst (build found Int32.MaxValue)

    /// An outline as text: one line per item, indented by depth, its lines on the right
    /// (`let execute (cfg) =   60–141`).
    let render (items: OutlineItem list) : string =
        let rec flatten depth (items: OutlineItem list) =
            items
            |> List.collect (fun i -> (String(' ', 2 * depth) + i.Header, i) :: flatten (depth + 1) i.Children)

        let lines = flatten 0 items
        let width = lines |> List.fold (fun w (text, _) -> max w text.Length) 0
        let builder = StringBuilder()

        for text, item in lines do
            let range =
                if item.StartLine = item.EndLine then
                    string item.StartLine
                else
                    string item.StartLine + "–" + string item.EndLine

            builder.Append(text.PadRight width).Append("   ").Append(range).Append('\n')
            |> ignore

        builder.ToString()
