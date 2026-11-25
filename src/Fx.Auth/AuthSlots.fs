namespace Fx.Auth

open Fx
open Fx.Contracts
open Fx.Variants

/// The service credentials' variant slots (spec §15): core slots defined next to the code that reads
/// them; the host registers `AuthSlots.all` with `Slots.all`. Both are privileged: a project must not
/// move credentials or choose where secrets come from.
[<RequireQualifiedAccess>]
module AuthSlots =
    /// What `credentials.store` takes besides a store's kind: pick the OS keyring when it can be
    /// used, else the file store.
    [<Literal>]
    let Auto = "auto"

    /// `credentials.store`: where the host keeps credentials.
    let store: Slot<string> =
        Slot.create
            "credentials.store"
            "Where the host keeps credentials: auto (the OS keyring when it can be used here, else a file only you can read), or a store's kind: keyring, file, ephemeral (memory, never written), or a plugin's. A store that cannot be used is an error, never a silent fallback."
            (Codec.string |> Codec.pattern "^[a-z][a-z0-9-]*$")
            Auto
        |> Slot.privileged

    /// `secrets.sources`: where `secret:` references are looked up, in order.
    let sources: Slot<string list> =
        Slot.create
            "secrets.sources"
            ("Where secret: references are looked up, in order: store (the credential store), env ("
             + Product.envVar "SECRET_<NAME>"
             + " variables), or a plugin's source.")
            (Codec.list (Codec.string |> Codec.pattern "^[a-z][a-z0-9-]*$")
             |> Codec.minItems 1)
            [ "store"; "env" ]
        |> Slot.privileged

    /// The slots, for the host's registry.
    let all: SlotInfo list = [ Slot.info store; Slot.info sources ]
