namespace Fx.Effects

open System.Threading.Tasks

/// Combinators for effects with expected failures, `Eff<'R, Result<'T, 'E>>`. See `effResult { }`.
module EffResult =
    /// An effect that succeeds with the value.
    let ok (value: 'T) : Eff<'R, Result<'T, 'E>> = fun _ -> Task.FromResult(Ok value)

    /// An effect that fails with the error.
    let error (error: 'E) : Eff<'R, Result<'T, 'E>> = fun _ -> Task.FromResult(Error error)

    let ofResult (result: Result<'T, 'E>) : Eff<'R, Result<'T, 'E>> = fun _ -> Task.FromResult result

    /// An effect that cannot fail in an expected way.
    let ofEff (effect: Eff<'R, 'T>) : Eff<'R, Result<'T, 'E>> = Eff.map Ok effect

    let map (mapping: 'T -> 'U) (effect: Eff<'R, Result<'T, 'E>>) : Eff<'R, Result<'U, 'E>> =
        Eff.map (Result.map mapping) effect

    let mapError (mapping: 'E -> 'F) (effect: Eff<'R, Result<'T, 'E>>) : Eff<'R, Result<'T, 'F>> =
        Eff.map (Result.mapError mapping) effect

    let bind (binder: 'T -> Eff<'R, Result<'U, 'E>>) (effect: Eff<'R, Result<'T, 'E>>) : Eff<'R, Result<'U, 'E>> =
        effResult {
            let! value = effect
            return! binder value
        }

    /// The value, or the fallback computed from the error.
    let defaultWith (fallback: 'E -> 'T) (effect: Eff<'R, Result<'T, 'E>>) : Eff<'R, 'T> =
        Eff.map
            (function
            | Ok value -> value
            | Error e -> fallback e)
            effect

    /// Runs the effects one after another, stopping at the first error.
    let traverse (effect: 'A -> Eff<'R, Result<'T, 'E>>) (items: 'A list) : Eff<'R, Result<'T list, 'E>> =
        effResult {
            let results = ResizeArray()

            for item in items do
                let! result = effect item
                results.Add result

            return List.ofSeq results
        }

    let sequence (effects: Eff<'R, Result<'T, 'E>> list) : Eff<'R, Result<'T list, 'E>> = traverse id effects
