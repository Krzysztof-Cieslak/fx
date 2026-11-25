namespace Fx.Sessions.Host

open Fx.Contracts
open Fx.Effects
open Fx.Store
open Fx.Variants

/// A profile resolved for a session and stored where anyone with the log can load it.
type BoundProfile =
    {
        /// The profile id (`sha256:…`).
        Id: string
        /// Every slot's value, by slot id.
        Selections: Map<string, Json>
    }

    override this.ToString() = this.Id

/// Resolves the profile a session runs under from its overrides (the `Session` layer) and stores it
/// before the session binds it (docs/modules/variants.md: profiles are stored before they are bound).
/// The composition root builds one from its configuration (`ProfileBinder.ofVariants`); tests use
/// `ProfileBinder.constant`. A failure lists what is wrong, one issue per line.
type ProfileBinder<'R> =
    {
        Bind: (string * Json) list -> Eff<'R, Result<BoundProfile, string list>>
    }

    override _.ToString() = "ProfileBinder"

module ProfileBinder =
    /// Resolves the layers below and above the session's (host, project, explicit) with the overrides
    /// as the `Session` layer, under the policies; `describe` makes the profile (adding extensions,
    /// conformance and the lockfile), which is stored in the blob store under its id.
    let ofVariants<'R when 'R :> IBlobs and 'R :> IScope>
        (registry: SlotRegistry)
        (policies: Policy list)
        (layers: Selections list)
        (describe: Resolution -> Profile)
        : ProfileBinder<'R> =
        {
            Bind =
                fun overrides ->
                    eff {
                        let session =
                            Selections.create (Source.create Layer.Session "session overrides") overrides

                        match Resolution.resolve registry policies (layers @ [ session ]) with
                        | Error issues -> return Error(issues |> List.map string)
                        | Ok resolution ->
                            let profile = describe resolution
                            let! _ = Blobs.put (Profile.bytes profile)

                            return
                                Ok
                                    {
                                        Id = string (Profile.id profile)
                                        Selections =
                                            profile.Selections
                                            |> Map.toList
                                            |> List.map (fun (slot, value) -> slot.Value, value)
                                            |> Map.ofList
                                    }
                    }
        }

    /// A binder whose profile id is a function of the overrides, with nothing stored: for tests.
    let constant<'R> (id: (string * Json) list -> string) : ProfileBinder<'R> =
        {
            Bind =
                fun overrides ->
                    Eff.ret (
                        Ok
                            {
                                Id = id overrides
                                Selections = Map.ofList overrides
                            }
                    )
        }

    /// The slots whose values differ between two bound profiles.
    let changed (before: BoundProfile) (after: BoundProfile) : string list =
        Seq.append (Map.keys before.Selections) (Map.keys after.Selections)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.filter (fun slot -> before.Selections.TryFind slot <> after.Selections.TryFind slot)
        |> List.ofSeq
