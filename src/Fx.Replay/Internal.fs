namespace Fx.Replay

open System.Runtime.ExceptionServices
open System.Threading.Tasks

/// Replay was asked something the recording does not have (`Fx.Effects.Replay`'s, which variant replay
/// throws too).
type ReplayDivergedException = Fx.Effects.Replay.ReplayDivergedException

module internal Internal =
    /// Rethrows an exception with its stack trace, as an expression of any type.
    let rethrow (e: exn) : 'T =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<'T>

    /// A task as a `Task<unit>`.
    let awaited (work: Task) : Task<unit> = task { do! work }
