namespace Fx.Host

open Fx.Contracts
open Fx.Variants
open Fx.Prompt
open Fx.CodeAct
open Fx.Identity.Host
open Fx.Auth

/// How hard the host's stores work to keep what they committed (`Durability` of Fx.Store.Live).
[<RequireQualifiedAccess>]
type StoreDurability =
    | Full
    | Normal

    override this.ToString() =
        match this with
        | Full -> "full"
        | Normal -> "normal"

/// The host's own variant slots: what the composition root reads when it opens its stores and starts
/// its sessions (docs/modules/host.md). Read from the host's resolution (host config, explicit flags,
/// policy), never from a session's profile.
[<RequireQualifiedAccess>]
module HostSlots =
    /// `host.store.durability`: `full` (commits on disk before they return) or `normal` (commits
    /// survive a process crash, not an OS crash). Privileged: a project must not weaken it.
    let durability: Slot<StoreDurability> =
        Slot.enum
            "host.store.durability"
            "How hard the host's event and blob stores work to keep what they committed: full (synced on every commit) or normal (survives a process crash, not power loss)."
            [ StoreDurability.Full, "full"; StoreDurability.Normal, "normal" ]
            StoreDurability.Full
        |> Slot.privileged

    /// `sessions.snapshotEvery`: a session's snapshot is saved once this many events were appended.
    let snapshotEvery: Slot<int> =
        Slot.create
            "sessions.snapshotEvery"
            "A session's state is saved as a snapshot once this many events were appended after the last one."
            (Codec.int |> Codec.between 1 1_000_000)
            256

    /// `sessions.subscribeMaxGap`: a subscriber further behind gets the latest snapshot first.
    let subscribeMaxGap: Slot<int> =
        Slot.create
            "sessions.subscribeMaxGap"
            "A client that subscribes more than this many events behind gets the session's latest snapshot first."
            (Codec.int |> Codec.between 1 1_000_000)
            1024

    /// `sessions.profileAttempts`: how often an override is resolved again when another client
    /// changed the session's profile meanwhile.
    let profileAttempts: Slot<int> =
        Slot.create
            "sessions.profileAttempts"
            "How often a session override is resolved again when another client changed the session's profile meanwhile."
            (Codec.int |> Codec.between 1 20)
            3

    /// `agent.interpreter`: the interpreter cells run on (an `interpreter` contribution's id).
    let interpreter: Slot<string> =
        Slot.create
            "agent.interpreter"
            "The interpreter the agent's cells run on: the id of an interpreter an extension contributes (bun-ts)."
            (Codec.string |> Codec.pattern "^[a-z][a-z0-9-]*$")
            "bun-ts"

    /// The host's slots.
    let all: SlotInfo list =
        [
            Slot.info durability
            Slot.info snapshotEvery
            Slot.info subscribeMaxGap
            Slot.info profileAttempts
            Slot.info interpreter
        ]

    /// Every core slot a host registers: the variants module's, the prompt render's, the CodeAct
    /// strategy's, identity's, service credentials', the sandbox's and the host's. Extensions
    /// contribute theirs (content exclusions among them, from `fx.egress.exclusions`).
    let core: SlotInfo list =
        Slots.all
        @ PromptSlots.all
        @ CodeActSlots.all
        @ IdentitySlots.all
        @ AuthSlots.all
        @ Fx.Environments.SandboxSlots.all
        @ all
