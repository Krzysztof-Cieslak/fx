namespace Fx.Host

open System
open Fx.Grants

/// A grant request as a terminal shows it, and the person's reply as an answer (spec §13, §19.4:
/// people answer; `fx run` asks at the terminal). Pure: the CLI reads and writes the console.
[<RequireQualifiedAccess>]
module GrantPrompts =
    /// The most pieces of evidence shown, and characters of each.
    [<Literal>]
    let MaxEvidence = 6

    [<Literal>]
    let MaxLine = 200

    let private line (text: string) =
        let first =
            text.Split('\n')
            |> Array.map (fun l -> l.TrimEnd('\r'))
            |> Array.tryFind (fun l -> l.Trim() <> "")
            |> Option.defaultValue ""

        let more = text.Contains '\n' && first.Length < text.TrimEnd().Length

        if first.Length > MaxLine then
            first.Substring(0, MaxLine - 1) + "…"
        elif more then
            first + " …"
        else
            first

    /// Whether the request lets an answer cover its session.
    let offersSession (request: GrantRequest) : bool =
        List.contains ScopeKind.Session request.Offer

    /// What the person reads: the summary, the action and resource, and the evidence (each on one
    /// line, bounded), then the question.
    let lines (request: GrantRequest) : string list =
        [
            yield "? " + line request.Summary
            yield "  " + request.Action.Value + " " + line request.Resource

            for evidence in request.Evidence |> List.truncate MaxEvidence do
                yield "  " + evidence.Label + ": " + line evidence.Text

            if request.Evidence.Length > MaxEvidence then
                yield $"  (and {request.Evidence.Length - MaxEvidence} more)"
        ]

    /// Whether the request lets an answer cover the whole host.
    let offersHost (request: GrantRequest) : bool =
        List.contains ScopeKind.Host request.Offer

    /// The question, with the answers the request offers.
    let question (request: GrantRequest) : string =
        let choices =
            [
                "[y] once"
                if offersSession request then
                    "[s] for this session"
                if offersHost request then
                    "[a] always on this host"
                "[N] no"
            ]

        "  Allow? " + String.Join(", ", choices) + ": "

    let private answer (effect: GrantEffect) (scope: ScopeChoice) (reason: string option) : GrantAnswer =
        {
            Effect = effect
            Scope = scope
            Resource = None
            Expires = None
            Reason = reason
        }

    /// The person's reply as an answer: `y` once, `s` for the session and `a` for the host (when
    /// offered), anything else denies once.
    let parse (request: GrantRequest) (reply: string) : GrantAnswer =
        match reply.Trim().ToLowerInvariant() with
        | "y"
        | "yes"
        | "once" -> answer GrantEffect.Allow ScopeChoice.Once None
        | "s"
        | "session" when offersSession request -> answer GrantEffect.Allow ScopeChoice.Session None
        | "a"
        | "always" when offersHost request -> answer GrantEffect.Allow ScopeChoice.Host None
        | _ -> answer GrantEffect.Deny ScopeChoice.Once (Some "denied at the terminal")

    /// The answer when nobody can answer (no terminal): denied once, saying why.
    let unanswered: GrantAnswer =
        answer GrantEffect.Deny ScopeChoice.Once (Some "nobody could answer: not run from a terminal")

    /// What the run shows when it denies a request nobody can answer.
    let deniedUnasked (request: GrantRequest) : string =
        $"  ! {line request.Summary} ({request.Action.Value} {line request.Resource}) needs a person's approval, and nobody can answer here (input is not a terminal): denied once. Grant it beforehand, or run from a terminal."
