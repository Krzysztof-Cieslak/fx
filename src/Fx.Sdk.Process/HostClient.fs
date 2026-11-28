/// The capabilities of an extension served in this process, over its connection to the host
/// (`host/*`, T109b): the host attenuates them to the extension's manifest; this side does the same
/// first, so a capability the manifest does not list never reaches the host. The extension's own slots
/// are asked for once, when it activates (`prefetch`): the host's values do not change while it runs.
[<RequireQualifiedAccess>]
module Fx.Sdk.Process.HostClient

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Sdk
open Fx.Grants
open Fx.Sdk.Process.ExtensionProtocol

/// The smallest environment a call needs: its scope.
type private CallEnv =
    {
        Scope: Scope
    }

    interface IScope with
        member env.Scope = env.Scope

    override env.ToString() = $"CallEnv({env.Scope})"

/// The host could not answer a `host/*` request (the connection is gone, it answered something
/// that does not decode, or it refused): a defect where the extension used the capability.
type HostUnreachableException(message: string) =
    inherit Exception(message)

let private call (peer: Peer) (m: Method<'P, 'R, HostError>) (parameters: 'P) (cancellation: CancellationToken) =
    Eff.run
        {
            Scope = Scope.ofCancellation cancellation
        }
        (Rpc.call peer m parameters)

/// The outcome of a request whose only expected failures are the extension's own (a defect).
let private value (extension: string) (outcome: Result<'R, CallError<HostError>>) : 'R =
    match outcome with
    | Ok value -> value
    | Error(CallError.Failed(HostError.NotGranted(_, capability))) ->
        raise (CapabilityDeniedException(extension, capability))
    | Error e -> raise (HostUnreachableException $"the host did not answer: {e}")

let private grantOutcome (extension: string) (outcome: Result<'R, CallError<HostError>>) : Result<'R, GrantError> =
    match outcome with
    | Ok value -> Ok value
    | Error(CallError.Failed(HostError.Grant error)) -> Error error
    | Error(CallError.Failed(HostError.NotGranted(_, capability))) ->
        raise (CapabilityDeniedException(extension, capability))
    | Error e -> Error(GrantError.Unavailable $"the host did not answer: {e}")

/// The values of the extension's own slots, if its manifest lists `variants`; asked once, at activation.
let prefetch
    (peer: Peer)
    (manifest: Manifest)
    (cancellation: CancellationToken)
    : Task<Result<Fx.Variants.Resolved list, string>> =
    task {
        if not (List.contains Capability.Variants manifest.Capabilities) then
            return Ok []
        else
            match! call peer hostVariants { Extension = manifest.Id } cancellation with
            | Ok slots -> return Ok slots.Slots
            | Error e -> return Error $"the host did not answer host/variants: {e}"
    }

/// The capabilities of the manifest's extension over the connection, with its prefetched slots.
let capabilities (peer: Peer) (manifest: Manifest) (slots: Fx.Variants.Resolved list) : ExtensionCapabilities =
    let id = manifest.Id
    let none = ExtensionCapabilities.none id

    let granted = manifest.Capabilities |> List.filter Capability.isKnown |> Set.ofList

    let has name = granted.Contains name

    {
        Extension = id
        Granted = granted
        OwnSlots =
            if has Capability.Variants then
                (fun () -> slots)
            else
                none.OwnSlots
        Variants =
            if has Capability.Variants then
                ExtensionCapabilities.ofSlots (fun () -> slots)
            else
                none.Variants
        Packages =
            if has Capability.Packages then
                {
                    List =
                        fun cancellation ->
                            task {
                                let! outcome = call peer hostPackages { Extension = id } cancellation
                                return (value id outcome).Packages
                            }
                    Data =
                        fun name cancellation ->
                            task {
                                match! call peer hostContributions { Extension = id; Type = name } cancellation with
                                | Ok data -> return Ok data.Contributions
                                | Error(CallError.Failed(HostError.UnknownType name)) ->
                                    return Error(PackagesError.UnknownType name)
                                | Error(CallError.Failed(HostError.NotData(name, kind))) ->
                                    return Error(PackagesError.NotData(name, kind))
                                | Error e -> return value id (Error e)
                            }
                }
            else
                none.Packages
        Grants =
            if has Capability.Grants then
                {
                    Check =
                        fun query cancellation ->
                            task {
                                let! outcome =
                                    call peer hostGrantsCheck { Extension = id; Query = query } cancellation

                                return grantOutcome id outcome
                            }
                    Request =
                        fun ask cancellation ->
                            task {
                                let! outcome = call peer hostGrantsRequest { Extension = id; Ask = ask } cancellation
                                return grantOutcome id outcome
                            }
                    Await =
                        fun request cancellation ->
                            task {
                                let! outcome =
                                    call peer hostGrantsAwait { Extension = id; Request = request } cancellation

                                return grantOutcome id outcome
                            }
                    Withdraw =
                        fun request reason cancellation ->
                            task {
                                let! outcome =
                                    call
                                        peer
                                        hostGrantsWithdraw
                                        {
                                            Extension = id
                                            Request = request
                                            Reason = reason
                                        }
                                        cancellation

                                return grantOutcome id outcome
                            }
                }
            else
                none.Grants
    }
