namespace Fx.CodeAct

open System
open System.Security.Cryptography
open System.Text
open Fx.Contracts
open Fx.Prompt

/// What the loop guard says about an action.
[<RequireQualifiedAccess>]
type LoopVerdict =
    | Fine
    /// The same action with the same result happened this often: the next call warns the model.
    | Warn of count: int
    /// This often: the turn stops and asks the user.
    | Stop of count: int

    override this.ToString() =
        match this with
        | Fine -> "fine"
        | Warn count -> $"warn ({count})"
        | Stop count -> $"stop ({count})"

/// The loop guard (spec §4.5): fingerprints each action with its result, and counts repeats within a
/// turn. An agent that keeps doing the same thing and seeing the same result is stuck.
type LoopGuard =
    {
        Counts: Map<string, int>
    }

    override this.ToString() =
        $"loop guard ({this.Counts.Count} actions)"

[<RequireQualifiedAccess>]
module LoopGuard =
    let empty: LoopGuard = { Counts = Map.empty }

    /// The fingerprint of an action and its result: the tool, its arguments (canonical when JSON) and
    /// the receipt's pieces, hashed.
    let fingerprint (tool: string) (arguments: string) (receipt: Piece list) : string =
        let args =
            match Json.parse arguments with
            | Ok json -> Json.serializeCanonical json
            | Error _ -> arguments

        let json =
            Json.Object
                [
                    "tool", Json.String tool
                    "arguments", Json.String args
                    "receipt", Json.Array(receipt |> List.map Piece.codec.Encode)
                ]

        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json.serializeCanonical json)))

    /// Counts an action; `repeats` (0: off) is how many identical ones stop the turn.
    let observe (repeats: int) (print: string) (guard: LoopGuard) : LoopGuard * LoopVerdict =
        let count = (guard.Counts |> Map.tryFind print |> Option.defaultValue 0) + 1

        let guard =
            {
                Counts = guard.Counts |> Map.add print count
            }

        let verdict =
            if repeats <= 0 then
                LoopVerdict.Fine
            elif count >= repeats then
                LoopVerdict.Stop count
            elif count = repeats - 1 && count > 1 then
                LoopVerdict.Warn count
            else
                LoopVerdict.Fine

        guard, verdict
