/// Fx.TreeSitter and the pinned tree-sitter (T008, docs/spikes/tree-sitter.md): linked statically into
/// the native binary, loaded as a shared library on the JIT. The scenarios parse the binding tests'
/// samples (copied next to the binary, tree-sitter-samples/) and print what tree-sitter returned: kinds, positions, query results, outlines.
module Fx.Aot.Scenarios.TreeSitter

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.TreeSitter

let private samplesDir =
    Path.Combine(AppContext.BaseDirectory, "tree-sitter-samples")

let private sampleNames =
    Directory.GetFiles samplesDir
    |> Array.map Path.GetFileName
    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> List.ofArray

let private sample (name: string) : byte array =
    File.ReadAllBytes(Path.Combine(samplesDir, name))

let private languageOf (name: string) : Language =
    match Language.ofPath name with
    | Some language -> language
    | None -> failwith ("no language for " + name)

let private parse (language: Language) (bytes: byte array) : Tree =
    match Syntax.parse language bytes CancellationToken.None with
    | Ok tree -> tree
    | Error error -> failwith ("parse: " + string error)

let private parseText (language: Language) (text: string) =
    parse language (Encoding.UTF8.GetBytes text)

let private position (p: Position) = string p.Line + ":" + string p.Column

let private node (n: Node) =
    n.Kind + " " + position n.Start + "-" + position n.End + " " + escape n.Text

let private parseError (error: ParseError) =
    match error with
    | ParseError.Cancelled -> "cancelled"
    | ParseError.Incompatible abi -> "incompatible " + string abi

let private library () : string list =
    let low, high = Library.abiRange ()

    [
        yield "pinned " + string (Library.archives () = Library.PinnedArchives)
        yield "archives " + Library.archives ()
        yield "abi " + string low + ".." + string high
        for language in Language.all do
            yield
                "language "
                + language.Name
                + " abi "
                + string (Language.abiVersion language)
                + " reports "
                + orNull (Language.reportedName language)
    ]

let private syntax () : string list =
    [
        for name in sampleNames do
            use tree = parse (languageOf name) (sample name)
            let root = tree.Root

            yield
                name
                + " root "
                + root.Kind
                + " nodes "
                + string root.DescendantCount
                + " children "
                + string root.ChildCount
                + " errors "
                + string (Syntax.errors tree).Length
                + " end "
                + position root.End

            for child in root.Children |> List.truncate 3 do
                yield "  " + child.Kind + " " + position child.Start + "-" + position child.End

        use utf8 = parseText Language.Python "x = 1\nif x:\n    y = '\u00e9\u2713'\n"
        yield "utf8 " + node (utf8.Root.Child 1)

        use crlf = parseText Language.CSharp "class A\r\n{\r\n}\r\n"
        yield "crlf " + node (crlf.Root.Child 0)

        use broken =
            parseText Language.TypeScript "function ok() {}\nfunction broken( {\n\nconst x = 1;\n"

        for error in Syntax.errors broken do
            yield "broken " + string error.IsMissing + " " + node error

        for language in Language.all do
            use empty = parseText language ""

            yield
                "empty "
                + language.Name
                + " "
                + empty.Root.Kind
                + " "
                + string empty.Root.EndByte

        let disposed = parseText Language.Python "x = 1"
        let root = disposed.Root
        (disposed :> IDisposable).Dispose()

        yield
            "disposed "
            + (try
                root.Kind
               with :? ObjectDisposedException as e ->
                   e.GetType().Name)
    ]

let private queries () : string list =
    let compile language source =
        match Query.compile language source with
        | Ok query ->
            use query = query

            "ok patterns "
            + string query.PatternCount
            + " captures "
            + String.Join(",", query.CaptureNames)
        | Error error -> "error " + string error.Kind + " at " + string error.Offset

    [
        yield
            "unclosed "
            + compile Language.Python "(function_definition name: (identifier) @name"
        yield "node type " + compile Language.Python "(class_definition) (no_such_node)"
        yield "field " + compile Language.Python "(class_definition nope: (identifier))"
        yield
            "good "
            + compile Language.Python "(class_definition name: (_) @name) @class (function_definition) @fn"

        for language in Language.all do
            yield
                "outline query "
                + language.Name
                + " patterns "
                + string (Outline.query language).PatternCount

        use tree =
            parseText Language.Python "def a(): pass\nclass B:\n    def c(self): pass\n"

        match
            Query.compile
                Language.Python
                "(function_definition name: (_) @name) @fn (class_definition name: (_) @name) @cls"
        with
        | Error error -> yield "compile failed " + string error.Kind
        | Ok query ->
            use query = query

            for m in Query.matches query tree.Root do
                yield
                    "match "
                    + string m.Pattern
                    + " "
                    + String.Join(" ", m.Captures |> List.map (fun c -> c.Name + "=" + escape c.Node.Text))
    ]

let private outlines () : string list =
    [
        for name in sampleNames do
            use tree = parse (languageOf name) (sample name)
            yield "== " + name

            for line in (Outline.render (Outline.ofTree tree)).TrimEnd('\n').Split '\n' do
                yield escape line
    ]

/// The F# sample repeated as modules up to about the given size.
let private large (megabytes: int) : byte array =
    let text =
        Encoding.UTF8.GetString(sample "Retry.fs").Replace("module Billing.Retry", "")

    let builder = StringBuilder()
    let mutable i = 0

    while builder.Length < megabytes * 1_000_000 do
        builder.Append("module M").Append(i).Append(" =\n") |> ignore

        for line in text.Split '\n' do
            builder.Append("    ").Append(line).Append('\n') |> ignore

        i <- i + 1

    Encoding.UTF8.GetBytes(builder.ToString())

let private cancellation () : string list =
    use before = new CancellationTokenSource()
    before.Cancel()

    let first =
        match Syntax.parse Language.FSharp (sample "Retry.fs") before.Token with
        | Ok _ -> "parsed"
        | Error error -> parseError error

    // About ten seconds of parsing, stopped after a tenth of one.
    use during = new CancellationTokenSource()
    during.CancelAfter(TimeSpan.FromMilliseconds 100.0)

    let second =
        match Syntax.parse Language.FSharp (large 20) during.Token with
        | Ok tree ->
            (tree :> IDisposable).Dispose()
            "parsed"
        | Error error -> parseError error

    use after = parse Language.FSharp (sample "Retry.fs")

    [
        "cancelled before: " + first
        "cancelled during: " + second
        "after: errors "
        + string (Syntax.errors after).Length
        + " nodes "
        + string after.Root.DescendantCount
    ]

let private threads () : Task<string list> =
    task {
        let names = Array.ofList sampleNames

        let! counts =
            Array.init 48 (fun i ->
                Task.Run(fun () ->
                    let name = names[i % names.Length]
                    use tree = parse (languageOf name) (sample name)

                    name
                    + " "
                    + string tree.Root.DescendantCount
                    + " "
                    + string (Outline.ofTree tree).Length))
            |> Task.WhenAll

        return counts |> Array.distinct |> Array.sort |> List.ofArray
    }

let scenarios: Scenario list =
    [
        Scenario.lines "tree-sitter-library" library
        Scenario.lines "tree-sitter-syntax" syntax
        Scenario.lines "tree-sitter-queries" queries
        Scenario.lines "tree-sitter-outline" outlines
        Scenario.lines "tree-sitter-cancel" cancellation
        Scenario.task "tree-sitter-threads" threads
    ]
