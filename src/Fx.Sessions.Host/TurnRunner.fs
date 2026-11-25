namespace Fx.Sessions.Host

open Fx.Effects
open Fx.Machines
open Fx.Sessions

/// What the strategy that runs a turn (T127) gets from its session: the turn's inputs, and the
/// session's side of the turn as effects. Every function sends a command to the session's actor with
/// a key derived from the turn and what it asks, so a retry is deduplicated.
type TurnContext<'R> =
    {
        Turn: TurnStart
        /// A step boundary (spec §5, §19.2: steering is delivered at step boundaries). Takes what waits
        /// in the session's inbox (steered prompts, requests settled as input, finished jobs) and
        /// records it as delivered to this step, exactly once: asked again for the same step, it gives
        /// the same inputs. Steps count from 1.
        Boundary: int -> Eff<'R, Input list>
        /// Opens a request on behalf of the turn (its origin is the turn), and gives the sequence number
        /// of the event that opened it, or why the session refused it.
        Open: RequestSpec -> Eff<'R, Result<int64, Rejection>>
        /// Waits for a request's resolution, recorded after a sequence number (the one `Open` gave).
        Await: string -> int64 -> Eff<'R, Resolution>
    }

    override this.ToString() = "TurnContext(" + string this.Turn + ")"

/// Runs turns (the boundary between the session and its strategy). Expected failures, such as a model
/// that cannot be reached, are an `Error` with a reason; the session records them as a failed turn,
/// and so it does an exception's type (never its message). An interrupt cancels the effect's scope.
type TurnRunner<'R> =
    {
        Run: TurnContext<'R> -> Eff<'R, Result<TurnResult, string>>
    }

    override _.ToString() = "TurnRunner"

module TurnContext =
    /// Opens a request and waits for its resolution: a tool call waiting for a grant, a form the agent
    /// needs filled in.
    let ask<'R> (context: TurnContext<'R>) (request: RequestSpec) : Eff<'R, Result<Resolution, Rejection>> =
        eff {
            match! context.Open request with
            | Error rejection -> return Error rejection
            | Ok opened ->
                let! resolution = context.Await request.Id opened
                return Ok resolution
        }
