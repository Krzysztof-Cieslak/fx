namespace Fx.Prompt

open System
open System.Text
open Fx.Contracts
open Fx.Models

/// A file's content as one piece of context shows it (spec §5, §6.7): what entered the context, so
/// `SeenRanges` can record it and content exclusions can withhold it.
type FileView =
    {
        /// Relative to the workspace, `/`-separated (`Paths.normalize`).
        Path: string
        /// The digest of the whole file when it was read (`sha256:…`): the version the lines belong to.
        Digest: string
        /// The lines shown, or none for the whole file.
        Lines: LineRange list
        /// What the model sees: the lines, a preview or an outline.
        Text: string
    }

    override this.ToString() =
        let lines =
            if this.Lines.IsEmpty then
                ""
            else
                ":" + (this.Lines |> List.map string |> String.concat ",")

        $"{this.Path}{lines} ({this.Text.Length} chars)"

/// A value the model sees by reference (spec §6.6): a blob, a host handle or a kernel variable, with
/// its preview. Values & references (T201) fill these in; the render shows the preview and the name
/// the model passes on.
type Reference =
    {
        /// `blob`, `handle`, `variable`, `output`, …
        Kind: string
        /// What the model uses to name it: a digest, a handle id, a variable name.
        Id: string
        /// Its type, if known (e.g. `Match[]`).
        Type: string option
        Preview: Preview
    }

    override this.ToString() = $"{this.Kind} {this.Id}"

/// Text that may quote files without showing them as such: what a cell printed after it read them
/// through the bridge (docs/modules/egress.md, provenance). It carries their provenance, so the render
/// withholds it and the model-egress hooks redact it when one of them is excluded; it never counts as
/// seen (`SeenRanges`), because what of the files it shows is unknown.
type Quote =
    {
        Text: string
        /// The files it may quote (workspace-relative, `/`-separated).
        Sources: ContentSource list
    }

    override this.ToString() =
        $"quote ({this.Text.Length} chars, {this.Sources.Length} sources)"

/// A piece of context: text, a file's content, a reference or media. Pieces that show a file say so,
/// so the render can withhold excluded files and record what was seen.
[<RequireQualifiedAccess>]
type Piece =
    | Text of string
    | File of FileView
    /// A file's content withheld by content exclusions (spec §18): only its path is shown.
    | Withheld of path: string
    | Ref of Reference
    | Media of MediaPart
    /// Text with the files it may quote (a cell's output after it read them).
    | Quote of Quote

    override this.ToString() =
        match this with
        | Text text -> $"text({text.Length} chars)"
        | File view -> "file " + string view
        | Withheld path -> "withheld " + path
        | Ref reference -> "ref " + string reference
        | Media media -> "media " + string media
        | Quote quote -> string quote

/// A block of context with an id: a section of the prefix (instructions, the interpreter's prompt,
/// agents, a repo map) or a view in the suffix. `Version` tells views whether what they would show has
/// changed since they last showed it.
type Block =
    {
        Id: string
        Version: string option
        Pieces: Piece list
    }

    override this.ToString() =
        $"block {this.Id} ({this.Pieces.Length} pieces)"

/// Workspace paths as context records them.
[<RequireQualifiedAccess>]
module Paths =
    /// `/`-separated, without `./` segments, repeated or trailing separators, or a leading `/`. Never
    /// resolves `..` (a path outside the workspace stays recognisable).
    let normalize (path: string) : string =
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter (fun segment -> segment <> ".")
        |> String.concat "/"

[<RequireQualifiedAccess>]
module FileView =
    let codec: Codec<FileView> =
        Codec.record {
            let! path =
                Codec.field "path" (fun (v: FileView) -> v.Path) (Codec.string |> Codec.nonEmpty)

            and! digest = Codec.field "digest" (fun v -> v.Digest) Codec.string

            and! lines =
                Codec.defaultField "lines" (fun v -> v.Lines) [] (Codec.list LineRange.codec)

            and! text = Codec.field "text" (fun v -> v.Text) Codec.string

            return
                {
                    Path = path
                    Digest = digest
                    Lines = lines
                    Text = text
                }
        }
        |> Codec.describe "A file's content as context shows it: path, version (digest), lines and text."
        |> Codec.named "FileView"

    /// The header line the render puts above a file's text: `src/a.fs:10-20,31 (sha256:9f86d081884c)`.
    let header (view: FileView) : string =
        let lines =
            match LineRange.normalize view.Lines with
            | [] -> ""
            | ranges -> ":" + (ranges |> List.map string |> String.concat ",")

        let digest =
            // `sha256:` and 12 hex digits identify the version without the noise of all 64.
            let colon = view.Digest.IndexOf ':'
            let short = colon + 13

            if view.Digest.Length > short then
                view.Digest.Substring(0, short)
            else
                view.Digest

        if digest = "" then
            view.Path + lines
        else
            $"{view.Path}{lines} ({digest})"

[<RequireQualifiedAccess>]
module Reference =
    let codec: Codec<Reference> =
        Codec.record {
            let! kind =
                Codec.field "kind" (fun (r: Reference) -> r.Kind) (Codec.string |> Codec.nonEmpty)

            and! id = Codec.field "id" (fun r -> r.Id) Codec.string
            and! typeName = Codec.optionalField "type" (fun r -> r.Type) Codec.string
            and! preview = Codec.field "preview" (fun r -> r.Preview) Codecs.preview

            return
                {
                    Kind = kind
                    Id = id
                    Type = typeName
                    Preview = preview
                }
        }
        |> Codec.describe "A value shown by reference: its kind, the name the model passes, its type and preview."
        |> Codec.named "Reference"

[<RequireQualifiedAccess>]
module Quote =
    let codec: Codec<Quote> =
        Codec.record {
            let! text = Codec.field "text" (fun (q: Quote) -> q.Text) Codec.string

            and! sources =
                Codec.defaultField "sources" (fun q -> q.Sources) [] (Codec.list ContentSource.codec)

            return { Text = text; Sources = sources }
        }
        |> Codec.describe "Text with the files it may quote, e.g. a cell's output after it read them."
        |> Codec.named "Quote"

[<RequireQualifiedAccess>]
module Piece =
    let codec: Codec<Piece> =
        Codec.union "kind" {
            let! text = Codec.case1 "text" Piece.Text (Field.required "text" Codec.string)
            and! file = Codec.caseOf "file" Piece.File FileView.codec

            and! withheld =
                Codec.case1 "withheld" Piece.Withheld (Field.required "path" Codec.string)

            and! reference = Codec.case1 "ref" Piece.Ref (Field.required "ref" Reference.codec)
            and! media = Codec.caseOf "media" Piece.Media MediaPart.codec
            and! quote = Codec.caseOf "quote" Piece.Quote Quote.codec

            return
                fun value ->
                    match value with
                    | Piece.Text t -> text t
                    | Piece.File v -> file v
                    | Piece.Withheld path -> withheld path
                    | Piece.Ref r -> reference r
                    | Piece.Media m -> media m
                    | Piece.Quote q -> quote q
        }
        |> Codec.describe
            "A piece of context: text, a file's content, a withheld file, a reference, media, or text quoting files."
        |> Codec.named "Piece"

    let text (text: string) : Piece = Piece.Text text

    /// Text cut to at most `maxChars` characters: the head and the tail, with how much was left out
    /// between them (command output keeps its last lines, where errors usually are).
    let output (maxChars: int) (text: string) : Piece =
        if text.Length <= maxChars then
            Piece.Text text
        else
            let omitted (n: int) = $"\n… {n} characters omitted …\n"
            // The marker's length depends on the count; a count of the text's length is an upper bound.
            let room = max 0 (maxChars - (omitted text.Length).Length)
            let head = room * 2 / 3
            let tail = room - head

            let safeEnd (n: int) =
                if n > 0 && Char.IsHighSurrogate text[n - 1] then
                    n - 1
                else
                    n

            let safeStart (n: int) =
                if n < text.Length && n > 0 && Char.IsLowSurrogate text[n] then
                    n + 1
                else
                    n

            let headEnd = safeEnd head
            let tailStart = safeStart (text.Length - tail)

            Piece.Text(
                text.Substring(0, headEnd)
                + omitted (tailStart - headEnd)
                + text.Substring tailStart
            )

    /// A named value's preview: `name = preview`.
    let preview (name: string) (preview: Preview) : Piece = Piece.Text(name + " = " + preview.Text)

    /// A value within limits, previewed through its codec.
    let value (limits: Limits) (name: string) (codec: Codec<'T>) (value: 'T) : Piece =
        preview name (codec.Preview limits value)

    /// The text a piece shows where only text can go (the system prompt, a joined block).
    let render (piece: Piece) : string =
        match piece with
        | Piece.Text text -> text
        | Piece.Quote quote -> quote.Text
        | Piece.File view -> FileView.header view + "\n" + view.Text
        | Piece.Withheld path -> $"[{path}: withheld by content exclusions]"
        | Piece.Ref r ->
            let typeName =
                match r.Type with
                | Some t -> ": " + t
                | None -> ""

            $"[{r.Kind} {r.Id}{typeName}] {r.Preview.Text}"
        | Piece.Media m ->
            let name =
                match m.Name with
                | Some name -> " " + name
                | None -> ""

            $"[{m.Kind} {m.MediaType}{name}]"

    /// The files a piece quotes (provenance for the model-egress hooks, docs/modules/egress.md): a
    /// file's lines (each range, or the whole file), media's and quotes' own sources; nothing for text,
    /// withheld files and references.
    let sources (piece: Piece) : ContentSource list =
        match piece with
        | Piece.File view ->
            match LineRange.normalize view.Lines with
            | [] -> [ ContentSource.file view.Path ]
            | ranges -> ranges |> List.map (fun range -> { Path = view.Path; Lines = Some range })
        | Piece.Media media -> media.Sources
        | Piece.Quote quote -> quote.Sources
        | Piece.Text _
        | Piece.Withheld _
        | Piece.Ref _ -> []

    /// Pieces as parts of a message: consecutive pieces joined by line breaks into one text part, media
    /// as media parts. Empty text pieces are left out. A text part quotes (`Sources`) the files of the
    /// pieces it joins.
    let parts (pieces: Piece list) : Part list =
        let result = ResizeArray<Part>()
        let text = StringBuilder()
        let quoted = ResizeArray<ContentSource>()
        let mutable pending = false

        let flush () =
            if pending && text.Length > 0 then
                result.Add(Part.quoting (List.ofSeq quoted) (text.ToString()))

            text.Clear() |> ignore
            quoted.Clear()
            pending <- false

        for piece in pieces do
            match piece with
            | Piece.Text "" -> ()
            | Piece.Media media ->
                flush ()
                result.Add(Part.Media media)
            | piece ->
                if pending then
                    text.Append('\n') |> ignore

                text.Append(render piece) |> ignore
                quoted.AddRange(sources piece)
                pending <- true

        flush ()
        List.ofSeq result

    /// Pieces as the content of a tool result.
    let toolContent (pieces: Piece list) : ToolContent list =
        parts pieces
        |> List.choose (fun part ->
            match part with
            | Part.Text t -> Some(ToolContent.Text t.Text)
            | Part.Media m -> Some(ToolContent.Media m)
            | _ -> None)

[<RequireQualifiedAccess>]
module Block =
    let create (id: string) (pieces: Piece list) : Block =
        {
            Id = id
            Version = None
            Pieces = pieces
        }

    let codec: Codec<Block> =
        Codec.record {
            let! id = Codec.field "id" (fun (b: Block) -> b.Id) (Codec.string |> Codec.nonEmpty)
            and! version = Codec.optionalField "version" (fun b -> b.Version) Codec.string
            and! pieces = Codec.field "pieces" (fun b -> b.Pieces) (Codec.list Piece.codec)

            return
                {
                    Id = id
                    Version = version
                    Pieces = pieces
                }
        }
        |> Codec.describe "A block of context: a section of the prefix or a view in the suffix."
        |> Codec.named "Block"

    /// The block as text: its pieces joined by line breaks.
    let render (block: Block) : string =
        block.Pieces |> List.map Piece.render |> String.concat "\n"
