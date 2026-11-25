/// The recording contract of docs/modules/variants.md, end to end: a stream binds its profile by id in
/// its first event and when a session override changes it; profiles are stored content-addressed in
/// the blob store; the reads of each binding are journaled and replay reproduces them.
module Fx.Variants.Tests.RecordingTests

open System.Threading
open Expecto
open Fx.Machines
open Fx.Store
open Fx.Store.Fakes
open Fx.Variants
open Fx.Variants.Tests.Support

/// A run's events, as a machine would define them: profile ids travel as their text, so the events
/// stay FSharp.Core types (spec §4.4).
type private RunEvent =
    | Started of profile: string * task: string
    | ProfileChanged of profile: string * changed: string list
    | Stepped of note: string

    override this.ToString() =
        match this with
        | Started(profile, task) -> $"started {task} under {profile}"
        | ProfileChanged(profile, _) -> $"now under {profile}"
        | Stepped note -> $"stepped {note}"

type private RunState =
    {
        Profile: string option
        /// Each step with the profile it ran under.
        Steps: (string * string) list
    }

    override this.ToString() = $"RunState({this.Steps.Length} steps)"

let private machine: Machine<unit, RunEvent, RunState, unit> =
    {
        Initial = { Profile = None; Steps = [] }
        Evolve =
            fun state event ->
                match event with
                | Started(profile, _)
                | ProfileChanged(profile, _) -> { state with Profile = Some profile }
                | Stepped note ->
                    { state with
                        Steps = state.Steps @ [ note, state.Profile.Value ]
                    }
        Handle = fun _ () -> Ok []
        React = fun _ _ -> []
    }

let private store (blobs: Blobs) (profile: Profile) : BlobRef =
    blobs.Put (Profile.bytes profile) CancellationToken.None |> _.Result

let private load (blobs: Blobs) (id: ProfileId) : Profile =
    match BlobHash.tryParse (string id) with
    | None -> failtest $"not a blob hash: {id}"
    | Some hash ->
        match blobs.Get hash CancellationToken.None |> _.Result with
        | Error error -> failtest (string error)
        | Ok bytes ->
            match Profile.ofBytes id bytes with
            | Ok profile -> profile
            | Error error -> failtest error

[<Tests>]
let tests =
    test "a stream binds its profiles, the blob store keeps them, and replay reproduces the reads" {
        let blobs = InMemoryBlobs().Blobs
        let host = layer Layer.Host "~/host.json" [ "test.depth", num "6" ]

        // The run starts under the profile of the host's config.
        let first = resolveOk [] [ host ]

        let firstProfile =
            Profile.ofResolution "0.1.0" first
            |> Profile.withExtension "acme.tools" { Version = "1.0.0"; Hash = None }

        let firstId = Profile.id firstProfile
        Expect.equal (string (store blobs firstProfile).Hash) (string firstId) "the blob hash is the profile id"

        let firstReads = VariantReads()
        let variants = Variants.record firstReads (Variants.ofResolution first)
        let firstStep = runIn (env variants) (Variants.get depth)

        // A session override changes a slot: a new resolution, a new profile, a new binding.
        let second =
            resolveOk [] [ host; layer Layer.Session "/mode" [ "test.mode", str "careful" ] ]

        let secondProfile =
            { firstProfile with
                Selections = second.Selections
            }

        let secondId = Profile.id secondProfile
        store blobs secondProfile |> ignore

        let changed =
            Profile.diff firstProfile secondProfile
            |> List.choose (function
                | SelectionChanged(slot, _, _) -> Some slot.Value
                | _ -> None)

        let events =
            [
                Started(string firstId, "fix the build")
                Stepped "a"
                ProfileChanged(string secondId, changed)
                Stepped "b"
            ]

        let state = Machine.replay machine events
        Expect.equal state.Steps [ "a", string firstId; "b", string secondId ] "each event's profile"
        Expect.equal changed [ "test.mode" ] "what changed"

        // Anyone with the log and the blob store can tell how each step was configured.
        for _, id in state.Steps do
            let profile = load blobs (ProfileId.tryParse id).Value
            Expect.equal (string (Profile.id profile)) id "loaded by id"

        Expect.equal (load blobs secondId) secondProfile "the second profile"

        // Exact replay answers from the reads journaled for the binding, not from today's config.
        let replayed = runIn (env (Variants.replay firstReads.Reads)) (Variants.get depth)
        Expect.equal replayed firstStep "replayed"
        Expect.equal firstReads.Reads [ (first.TryFind depth.Id).Value ] "journaled reads"
    }
