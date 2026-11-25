namespace Fx.Prompt

open Fx.Models

/// Content exclusions in the prompt (spec §5, §18): the content of excluded files never enters a
/// prompt. Matching is the shared matcher, `Fx.Models.Exclusions` (docs/modules/egress.md), so the
/// render, the host bindings and the model-egress hooks exclude exactly the same files; its
/// configuration (policy, host and project patterns, only ever adding up) is `ContentExclusions` in
/// the SDK. The render withholds every excluded file's content and leaves it out of `SeenRanges`.
[<RequireQualifiedAccess>]
module Withhold =
    /// The pieces with the content of excluded files withheld (only their path shows), and whether
    /// any was.
    let pieces (exclusions: Exclusions) (pieces: Piece list) : Piece list * bool =
        let mutable changed = false

        let filtered =
            pieces
            |> List.map (fun piece ->
                match piece with
                | Piece.File view when Exclusions.excludes exclusions view.Path ->
                    changed <- true
                    Piece.Withheld(Paths.normalize view.Path)
                | piece -> piece)

        (if changed then filtered else pieces), changed

    /// A block with the content of excluded files withheld, and whether any was.
    let block (exclusions: Exclusions) (block: Block) : Block * bool =
        match pieces exclusions block.Pieces with
        | _, false -> block, false
        | filtered, true -> { block with Pieces = filtered }, true
