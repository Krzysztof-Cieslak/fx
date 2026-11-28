namespace Fx.Sdk

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Grants

/// The grants capability (spec §4.2 `IGrants`, §13): how agents, strategies and extensions ask for
/// permissions. It checks and requests, and never grants: only people grant, through the host's
/// client protocol (`grants/*`), so an environment holding this capability cannot authorize itself
/// (spec §13.2: agents can only request). The host builds it from the grants log
/// (`GrantsService.grants` in Fx.Grants.Host). Data in and out, the cancellation token last.
type Grants =
    {
        /// What the effective rules say about a query now. Consumes nothing and records nothing.
        Check: GrantQuery -> CancellationToken -> Task<Result<GrantCheck, GrantError>>
        /// Settles an ask from the rules if they allow or deny; otherwise records a durable pending
        /// request (spec §5) that a person answers. Asking again with the same key is the same request.
        Request: GrantAsk -> CancellationToken -> Task<Result<AskOutcome, GrantError>>
        /// Waits until a pending request is answered, covered by a later grant, refused, withdrawn or
        /// timed out. Cancelling the wait leaves the request pending.
        Await: RequestId -> CancellationToken -> Task<Result<Resolution, GrantError>>
        /// Withdraws a pending request the requester no longer needs, with a reason.
        Withdraw: RequestId -> string -> CancellationToken -> Task<Result<unit, GrantError>>
    }

    override _.ToString() = "Grants"

/// An environment that can ask for permissions.
type IGrants =
    abstract Grants: Grants

/// One contribution an extension declares: its type and id.
type PackageContribution =
    {
        Type: string
        Id: string
    }

    override this.ToString() = this.Type + "/" + this.Id

/// An extension in effect, as the packages capability describes it: what its manifest says and where
/// it came from. Only active extensions are listed.
type PackageInfo =
    {
        Id: string
        Version: string
        Description: string option
        Runtime: string
        /// Where it came from: `builtin`, `policy`, `host`, `project`.
        Source: string
        Contributions: PackageContribution list
        Methods: string list
    }

    override this.ToString() = this.Id + " " + this.Version

/// A data contribution in effect: whose it is, its id and its JSON form.
type ContributedData =
    {
        Extension: string
        Id: string
        Value: Json
    }

    override this.ToString() = this.Extension + "/" + this.Id

/// Why the packages capability did not list the contributions of a type.
[<RequireQualifiedAccess>]
type PackagesError =
    /// The host's catalog has no contribution type of this name.
    | UnknownType of contributionType: string
    /// The type's values are code, which only the host runs (through its hook points).
    | NotData of contributionType: string * kind: string

    override this.ToString() =
        match this with
        | UnknownType name -> "unknown contribution type " + name
        | NotData(name, kind) -> name + " is not data (its values are " + kind + " code)"

/// The packages capability (spec §4.2 `IPackages`): the extensions in effect and the data
/// contributions of a type, as JSON. Code contributions stay with the host, which runs them through
/// its hook points.
type Packages =
    {
        /// The active extensions, in admission order.
        List: CancellationToken -> Task<PackageInfo list>
        /// The data contributions of a type in effect, in registration order.
        Data: string -> CancellationToken -> Task<Result<ContributedData list, PackagesError>>
    }

    override _.ToString() = "Packages"

/// An environment that can see the extensions in effect.
type IPackages =
    abstract Packages: Packages

/// Extension code used a capability its manifest does not list (spec §4.2, §16.1): a defect of the
/// extension, which the hook that ran it reports as its failure.
type CapabilityDeniedException(extension: string, capability: string) =
    inherit Exception($"{extension} has no capability {capability}: list it in its manifest's capabilities")

    member _.Extension = extension
    member _.Capability = capability

/// The capabilities an extension's manifest may list (`capabilities`). Admission refuses other
/// names (`extension.capability`).
[<RequireQualifiedAccess>]
module Capability =
    /// The clock. Every extension has it; listing it is allowed and changes nothing.
    [<Literal>]
    let Clock = "clock"

    /// Reading the extension's own variant slots (`IVariants`): only the slots it contributes.
    [<Literal>]
    let Variants = "variants"

    /// Listing the extensions in effect and the data contributions of a type (`IPackages`).
    [<Literal>]
    let Packages = "packages"

    /// Checking and requesting permissions (`IGrants`) as the extension: requests name it as the
    /// requester; only people grant.
    [<Literal>]
    let Grants = "grants"

    /// Every capability name the host knows, sorted.
    let known: string list = [ Clock; Grants; Packages; Variants ]

    let isKnown (name: string) : bool = List.contains name known

/// What the host offers every extension, before attenuation: the values of each extension's own
/// variant slots, the packages capability and the grants capability. The host builds one; the
/// environment of each extension is attenuated from it (`ExtensionCapabilities.attenuate`).
type HostServices =
    {
        /// The resolved values of the slots an extension contributes (by its id), in registration order.
        Slots: string -> Resolved list
        Packages: Packages
        Grants: Grants
    }

    override _.ToString() = "HostServices"

/// The capabilities of one extension, attenuated to its manifest: what its environment implements
/// `IVariants`, `IPackages` and `IGrants` with. A capability the manifest does not list throws
/// `CapabilityDeniedException` when used.
type ExtensionCapabilities =
    {
        Extension: string
        /// The names its manifest lists (`Capability.known` only).
        Granted: Set<string>
        /// Its own slots, each with its value and source; throws if `variants` is not granted.
        OwnSlots: unit -> Resolved list
        Variants: Variants
        Packages: Packages
        Grants: Grants
    }

    override this.ToString() =
        let granted = String.Join(", ", this.Granted)
        $"ExtensionCapabilities({this.Extension}: {granted})"

[<RequireQualifiedAccess>]
module ExtensionCapabilities =
    let private denied (extension: string) (capability: string) : 'T =
        raise (CapabilityDeniedException(extension, capability))

    /// What an extension without capabilities beyond the clock gets: every other use throws.
    let none (extension: string) : ExtensionCapabilities =
        {
            Extension = extension
            Granted = Set.empty
            OwnSlots = fun () -> denied extension Capability.Variants
            Variants =
                {
                    Read = fun _ -> denied extension Capability.Variants
                }
            Packages =
                {
                    List = fun _ -> denied extension Capability.Packages
                    Data = fun _ _ -> denied extension Capability.Packages
                }
            Grants =
                {
                    Check = fun _ _ -> denied extension Capability.Grants
                    Request = fun _ _ -> denied extension Capability.Grants
                    Await = fun _ _ -> denied extension Capability.Grants
                    Withdraw = fun _ _ _ -> denied extension Capability.Grants
                }
        }

    /// Variants that read the slots of a list (an extension's own): other slots are not registered.
    let ofSlots (slots: unit -> Resolved list) : Variants =
        {
            Read = fun slot -> slots () |> List.tryFind (fun r -> r.Slot = slot)
        }

    /// The prefix of the idempotency keys of an extension's requests, so that two extensions (or an
    /// extension and a session) never share a request.
    let keyPrefix (extension: string) : string = "ext/" + extension + "/"

    /// Grants as an extension: its requests name it as the requester (on the system's behalf, never
    /// a person's or a session's), their keys are prefixed with `keyPrefix`, and it can wait for and
    /// withdraw only the requests it made through this value.
    let grantsFor (extension: string) (grants: Grants) : Grants =
        let mine = ConcurrentDictionary<RequestId, unit>()

        let owned (id: RequestId) (run: unit -> Task<Result<'T, GrantError>>) =
            if mine.ContainsKey id then
                run ()
            else
                Task.FromResult(Error(GrantError.UnknownRequest id))

        {
            Check = grants.Check
            Request =
                fun ask cancellation ->
                    task {
                        let ask =
                            { ask with
                                Key = keyPrefix extension + ask.Key
                                Requester =
                                    {
                                        Principal = GrantLog.SystemPrincipal
                                        Agent = None
                                        Extension = Some extension
                                    }
                            }

                        let! outcome = grants.Request ask cancellation

                        match outcome with
                        | Ok(AskOutcome.Pending id) -> mine[id] <- ()
                        | _ -> ()

                        return outcome
                    }
            Await = fun id cancellation -> owned id (fun () -> grants.Await id cancellation)
            Withdraw = fun id reason cancellation -> owned id (fun () -> grants.Withdraw id reason cancellation)
        }

    /// The capabilities of an extension whose manifest lists `capabilities`, from what the host offers.
    /// Unknown names grant nothing (admission refuses them).
    let attenuate (services: HostServices) (extension: string) (capabilities: string list) : ExtensionCapabilities =
        let granted = capabilities |> List.filter Capability.isKnown |> Set.ofList
        let none = none extension
        let has name = granted.Contains name
        let ownSlots () = services.Slots extension

        {
            Extension = extension
            Granted = granted
            OwnSlots = if has Capability.Variants then ownSlots else none.OwnSlots
            Variants =
                if has Capability.Variants then
                    ofSlots ownSlots
                else
                    none.Variants
            Packages =
                if has Capability.Packages then
                    services.Packages
                else
                    none.Packages
            Grants =
                if has Capability.Grants then
                    grantsFor extension services.Grants
                else
                    none.Grants
        }
