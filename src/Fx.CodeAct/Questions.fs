namespace Fx.CodeAct

open Fx.Contracts
open Fx.Sessions

/// A question the agent asks the user: the text of a reply without a tool call, or why a turn stopped
/// (the loop guard, a budget).
type Question =
    {
        Question: string
        /// Why the turn asks: `reply` (the model asked), `loop` (the loop guard stopped it), `budget`.
        Reason: string
    }

    override this.ToString() =
        $"question ({this.Reason}): {this.Question}"

/// The request kind of the agent's questions (`agent.question`): the turn ends `NeedInput` on it, and
/// the answer is the next turn's input (spec §4.5, §5). Register it with the session host's kinds
/// (`SessionSettings.Kinds`).
[<RequireQualifiedAccess>]
module Questions =
    [<Literal>]
    let KindId = "agent.question"

    /// The most characters of a question kept in its request (events stay small).
    [<Literal>]
    let MaxChars = 8000

    let payload: Codec<Question> =
        Codec.record {
            let! question =
                Codec.field "question" (fun (q: Question) -> q.Question) (Codec.string |> Codec.nonEmpty)

            and! reason = Codec.defaultField "reason" (fun q -> q.Reason) "reply" Codec.string
            return { Question = question; Reason = reason }
        }
        |> Codec.describe "A question from the agent: its text, and why it asks (reply, loop, budget)."
        |> Codec.named "AgentQuestion"

    /// Answered with free text.
    let kind: RequestKind<Question, string> =
        RequestKind.create KindId "A question from the agent, answered with text." payload Codec.string

    /// The kinds a session host needs for CodeAct turns: the built-in ones and the agent's questions.
    let kinds: RequestKinds =
        RequestKinds.create
            [
                RequestKind.info RequestKinds.confirm
                RequestKind.info RequestKinds.choice
                RequestKind.info kind
            ]

    let private bound (text: string) =
        if text.Length <= MaxChars then
            text
        else
            let cut =
                if System.Char.IsHighSurrogate text[MaxChars - 2] then
                    MaxChars - 2
                else
                    MaxChars - 1

            text.Substring(0, cut) + "…"

    /// The request of a question: answered as input (the next turn's), open across turns until
    /// someone answers it.
    let request (id: string) (question: Question) : RequestSpec =
        let question =
            { question with
                Question = bound question.Question
            }

        let title =
            match question.Reason with
            | "reply" -> "The agent asks"
            | "loop" -> "The agent repeats itself"
            | "budget" -> "The turn used its budget"
            | other -> "The agent asks (" + other + ")"

        RequestKind.request kind id title (RequestOrigin.Turn 0) Reply.AsInput Lifetime.Session question
