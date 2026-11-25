namespace Fx.Effects

open System
open System.Threading

/// Which structured child an effect runs in: "" at the root, then the children's indexes, `0`, `1.2`,
/// … as `Eff.all`, `both` and `race` (and so `timeout`) start them. The same program gives the same
/// flows on every run, so record and replay tell concurrent calls apart by it (docs/modules/replay.md):
/// two children reading the clock get their own readings back, whichever resumes first.
///
/// Ambient, like the scope's cancellation: it flows into everything a child starts. Read it only to
/// key what is recorded; never branch on it.
[<RequireQualifiedAccess>]
module Flow =
    let private current = AsyncLocal<string>()

    /// The current flow.
    let path () : string =
        let path = current.Value
        if String.IsNullOrEmpty path then "" else path

    let private within (path: string) (start: unit -> 'T) : 'T =
        let saved = current.Value
        current.Value <- path

        try
            start ()
        finally
            current.Value <- saved

    /// Starts a structured child (synchronously, up to its first incomplete await): its flow is its
    /// parent's with its index.
    let child (index: int) (start: unit -> 'T) : 'T =
        let parent = path ()

        within
            (if parent = "" then
                 string index
             else
                 parent + "." + string index)
            start

    /// Starts something at the root flow, whoever starts it: where a recorded unit of work (a turn)
    /// begins, so its flows do not depend on what started it.
    let root (start: unit -> 'T) : 'T = within "" start
