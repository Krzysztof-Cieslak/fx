namespace Fx.Prompt

open Fx.Contracts
open Fx.Models

/// What of one version of a file has entered the context.
type Seen =
    {
        /// The whole file was shown.
        Whole: bool
        /// The lines shown, sorted and merged (`LineRange.normalize`); empty when `Whole`.
        Lines: LineRange list
    }

    override this.ToString() =
        if this.Whole then
            "whole"
        else
            this.Lines |> List.map string |> String.concat ","

/// Which file ranges, of which versions, entered the context (spec §5, §6.8): a pure projection of
/// what a prompt shows, read by the edit guard (T204) to tell edits of lines the agent has seen from
/// edits of lines it has not. Keyed by normalized path, then by the digest of the version.
///
/// Rules:
/// - every `Piece.File` a rendered prompt shows adds its lines (or the whole file) to its path and
///   digest; withheld (excluded) files and pieces hidden by a collapsed range add nothing;
/// - ranges of one version merge (overlapping and adjacent lines join); the whole file covers any
///   range;
/// - versions never mix: lines seen in one digest say nothing about another (an edit makes a new
///   version; the edit's result shows the lines it anchored, which count as seen).
type SeenRanges =
    {
        Files: Map<string, Map<string, Seen>>
    }

    override this.ToString() =
        let versions = this.Files |> Map.toSeq |> Seq.sumBy (fun (_, v) -> v.Count)
        $"seen({this.Files.Count} files, {versions} versions)"

[<RequireQualifiedAccess>]
module SeenRanges =
    let empty: SeenRanges = { Files = Map.empty }

    let private combine (a: Seen) (b: Seen) : Seen =
        if a.Whole || b.Whole then
            { Whole = true; Lines = [] }
        else
            {
                Whole = false
                Lines = LineRange.normalize (a.Lines @ b.Lines)
            }

    /// Records what a file view shows.
    let add (view: FileView) (seen: SeenRanges) : SeenRanges =
        let path = Paths.normalize view.Path

        let shown =
            match view.Lines with
            | [] -> { Whole = true; Lines = [] }
            | lines ->
                {
                    Whole = false
                    Lines = LineRange.normalize lines
                }

        if not shown.Whole && shown.Lines.IsEmpty then
            seen
        else
            let versions = seen.Files |> Map.tryFind path |> Option.defaultValue Map.empty

            let merged =
                match versions |> Map.tryFind view.Digest with
                | Some before -> combine before shown
                | None -> shown

            {
                Files = seen.Files |> Map.add path (versions |> Map.add view.Digest merged)
            }

    /// Records the files the pieces show.
    let addPieces (pieces: Piece list) (seen: SeenRanges) : SeenRanges =
        pieces
        |> List.fold
            (fun seen piece ->
                match piece with
                | Piece.File view -> add view seen
                | _ -> seen)
            seen

    /// Everything either has seen.
    let union (a: SeenRanges) (b: SeenRanges) : SeenRanges =
        b.Files
        |> Map.fold
            (fun (acc: SeenRanges) path versions ->
                versions
                |> Map.fold
                    (fun (acc: SeenRanges) digest seen ->
                        let current = acc.Files |> Map.tryFind path |> Option.defaultValue Map.empty

                        let merged =
                            match current |> Map.tryFind digest with
                            | Some before -> combine before seen
                            | None -> seen

                        {
                            Files = acc.Files |> Map.add path (current |> Map.add digest merged)
                        })
                    acc)
            a

    /// What has been seen of one version of a file.
    let tryFind (path: string) (digest: string) (seen: SeenRanges) : Seen option =
        seen.Files
        |> Map.tryFind (Paths.normalize path)
        |> Option.bind (Map.tryFind digest)

    /// The versions (digests) of a file that have been seen, in digest order.
    let versions (path: string) (seen: SeenRanges) : string list =
        seen.Files
        |> Map.tryFind (Paths.normalize path)
        |> Option.map (fun v -> v |> Map.toList |> List.map fst)
        |> Option.defaultValue []

    /// The lines of `range` in this version that have not been seen, as ranges (empty: all seen).
    let unseen (path: string) (digest: string) (range: LineRange) (seen: SeenRanges) : LineRange list =
        match LineRange.normalize [ range ] with
        | [] -> []
        | [ range ] ->
            match tryFind path digest seen with
            | Some s when s.Whole -> []
            | Some s ->
                let mutable next = range.First
                let gaps = ResizeArray<LineRange>()

                for r in s.Lines do
                    if r.Last >= next && r.First <= range.Last then
                        if r.First > next then
                            gaps.Add
                                {
                                    First = next
                                    Last = min range.Last (r.First - 1)
                                }

                        next <- max next (r.Last + 1)

                if next <= range.Last then
                    gaps.Add { First = next; Last = range.Last }

                List.ofSeq gaps
            | None -> [ range ]
        | _ -> [ range ]

    /// Whether every line of `range` in this version has been seen.
    let covers (path: string) (digest: string) (range: LineRange) (seen: SeenRanges) : bool =
        (unseen path digest range seen).IsEmpty

    let private seenCodec: Codec<Seen> =
        Codec.record {
            let! whole = Codec.defaultField "whole" (fun (s: Seen) -> s.Whole) false Codec.bool

            and! lines =
                Codec.defaultField "lines" (fun s -> s.Lines) [] (Codec.list LineRange.codec)

            return { Whole = whole; Lines = lines }
        }
        |> Codec.named "Seen"

    /// `{"src/a.fs": {"sha256:…": {"whole": false, "lines": [{"first": 1, "last": 20}]}}}`: for the
    /// prompt and seen-range viewer (spec §15).
    let codec: Codec<SeenRanges> =
        Codec.stringMap (Codec.stringMap seenCodec)
        |> Codec.map (fun files -> { Files = files }) (fun seen -> seen.Files)
        |> Codec.describe "The file ranges, per path and version (digest), that entered the context."
        |> Codec.named "SeenRanges"
