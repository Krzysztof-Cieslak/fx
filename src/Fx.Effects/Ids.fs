namespace Fx.Effects

open System.Threading.Tasks

/// The ids-and-randomness capability.
///
/// Interpreters: `SystemIds` (Fx.Effects.Live, cryptographically strong), `FakeIds` (Fx.Effects.Fakes,
/// seeded), `Record.ids` / `Replay.ids` (Fx.Effects.Replay). Random bytes are journaled like every
/// other capability output, so the journal must be protected like the data it records.
type Ids =
    {
        /// A new id, greater than every id this interpreter returned before.
        NewId: unit -> Id
        /// That many random bytes.
        RandomBytes: int -> byte array
    }

    override _.ToString() = "Ids"

/// An environment with ids and randomness.
type IIds =
    abstract Ids: Ids

module Ids =
    /// A new unique id that sorts after every id generated before it.
    let newId<'R when 'R :> IIds> : Eff<'R, Id> =
        fun env -> Task.FromResult(env.Ids.NewId())

    /// Random bytes, e.g. for nonces.
    let randomBytes<'R when 'R :> IIds> (count: int) : Eff<'R, byte array> =
        fun env -> Task.FromResult(env.Ids.RandomBytes count)
