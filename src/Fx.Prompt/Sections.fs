namespace Fx.Prompt


/// An agent the model can call (spec §7.3), as the prefix lists it.
type AgentSummary =
    {
        Name: string
        Description: string
    }

    override this.ToString() = "agent " + this.Name

/// The prefix's sections (spec §5, §6.2, §6.7, §7.3): static, cacheable content, in a fixed order. The
/// strategy builds them from their sources, `Transcript.reconcile` records the frame, and changes later
/// in the conversation become updates at the end of the history, never a different prefix.
[<RequireQualifiedAccess>]
module Sections =
    /// The system instructions.
    let instructions (text: string) : Block =
        Block.create "instructions" [ Piece.Text text ]

    /// The interpreter's prompt pack, its declarations included (`PromptPack.render`).
    let interpreter (prompt: string) : Block =
        Block.create "interpreter" [ Piece.Text prompt ]

    /// The agents the model can call, by name.
    let agents (agents: AgentSummary list) : Block =
        let lines =
            agents
            |> List.sortBy _.Name
            |> List.map (fun a -> $"- {Fx.Product.kernelGlobal}.agents.{a.Name}: {a.Description}")

        Block.create
            "agents"
            [
                if not lines.IsEmpty then
                    Piece.Text("Agents you can call:\n" + String.concat "\n" lines)
            ]

    /// A map of the repository (spec §6.7), when `prompt.repoMap` is on.
    let repoMap (pieces: Piece list) : Block = Block.create "repo-map" pieces

    /// A file pinned into the prefix by configuration (e.g. a project's agent instructions): what it
    /// was when the conversation began; later versions arrive as updates.
    let file (view: FileView) : Block =
        Block.create ("file:" + Paths.normalize view.Path) [ Piece.File view ]

    /// The sections in their order, leaving out empty ones.
    let ordered (sections: Block list) : Block list =
        sections |> List.filter (fun s -> not s.Pieces.IsEmpty)
