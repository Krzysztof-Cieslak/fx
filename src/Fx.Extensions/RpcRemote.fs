/// The host's `RemotePeer` over a JSON-RPC connection to an extension process.
[<RequireQualifiedAccess>]
module Fx.Extensions.RpcRemote

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Sdk
open Fx.Sdk.Process.ExtensionProtocol

/// The smallest environment a call needs: its scope.
type private CallEnv =
    {
        Scope: Scope
    }

    interface IScope with
        member env.Scope = env.Scope

    override env.ToString() = $"CallEnv({env.Scope})"

let private run (cancellation: CancellationToken) (effect: Eff<CallEnv, 'T>) : Task<'T> =
    Eff.run
        {
            Scope = Scope.ofCancellation cancellation
        }
        effect

let private failure (error: CallError<ProtocolError>) : RemoteFailure =
    match error with
    | CallError.Failed(ProtocolError.UnknownExtension extension) -> RemoteFailure.Unknown("extension " + extension)
    | CallError.Failed(ProtocolError.UnknownContribution address) -> RemoteFailure.Unknown("contribution " + address)
    | CallError.Failed(ProtocolError.UnknownMethod name) -> RemoteFailure.Unknown("method " + name)
    | CallError.Failed(ProtocolError.Invalid issues) -> RemoteFailure.InvalidInput issues
    | CallError.Failed(ProtocolError.NotActive _ as e)
    | CallError.Failed(ProtocolError.ActivationFailed _ as e)
    | CallError.Failed(ProtocolError.InProcessOnly _ as e)
    | CallError.Failed(ProtocolError.MethodFailed _ as e) -> RemoteFailure.Failed(string e)
    | CallError.Rejected e -> RemoteFailure.Failed(string e)
    | CallError.TooLarge _ -> RemoteFailure.Failed(string error)
    | CallError.InvalidReply _
    | CallError.TimedOut _
    | CallError.Disconnected _ -> RemoteFailure.Unreachable(string error)

/// What a call to the method `name` failed with.
let private methodFailure (name: string) (error: CallError<ProtocolError>) : MethodFailure =
    match error with
    | CallError.Failed(ProtocolError.UnknownMethod _) -> MethodFailure.UnknownMethod name
    | CallError.Failed(ProtocolError.Invalid issues) -> MethodFailure.InvalidParams issues
    | CallError.Failed(ProtocolError.MethodFailed(message, data)) -> MethodFailure.Failed(message, data)
    | CallError.Rejected e when e.Code = Protocol.ErrorCode.InternalError -> MethodFailure.Crashed(string e)
    | other -> MethodFailure.Unreachable(string (failure other))

/// The extensions a connection answers `host/*` requests for, with their environments (attenuated to
/// their manifests): those activated through it (`RemotePeer.Attach`), until they are deactivated.
type Attached() =
    let envs =
        ConcurrentDictionary<string, IExtensionEnv>(System.StringComparer.Ordinal)

    member _.Attach(extension: string, env: IExtensionEnv) = envs[extension] <- env

    member _.Detach(extension: string) = envs.TryRemove extension |> ignore

    member _.TryFind(extension: string) : IExtensionEnv option =
        match envs.TryGetValue extension with
        | true, env -> Some env
        | _ -> None

    override _.ToString() = $"Attached({envs.Count})"

/// The host's handlers of the `host/*` requests an extension process sends (T109b): each answers with
/// the named extension's environment, so with exactly the capabilities its manifest lists.
let hostHandlers<'R when 'R :> IScope> (attached: Attached) : Handler<'R> list =
    let granted (extension: string) (capability: string) (run: IExtensionEnv -> Task<Result<'T, HostError>>) =
        match attached.TryFind extension with
        | None -> Task.FromResult(Error(HostError.UnknownExtension extension))
        | Some env when not (env.Capabilities.Granted.Contains capability) ->
            Task.FromResult(Error(HostError.NotGranted(extension, capability)))
        | Some env -> run env

    let grant (outcome: Task<Result<'T, Fx.Grants.GrantError>>) =
        task {
            let! outcome = outcome
            return outcome |> Result.mapError HostError.Grant
        }

    [
        Handler.request hostVariants (fun _ (p: ExtensionRef) ->
            fun (_: 'R) ->
                granted p.Extension Capability.Variants (fun env ->
                    Task.FromResult(Ok { Slots = env.Capabilities.OwnSlots() })))
        Handler.request hostPackages (fun _ (p: ExtensionRef) ->
            fun (env: 'R) ->
                granted p.Extension Capability.Packages (fun ext ->
                    task {
                        let! packages = ext.Packages.List env.Scope.Cancellation
                        return Ok { Packages = packages }
                    }))
        Handler.request hostContributions (fun _ (p: DataParams) ->
            fun (env: 'R) ->
                granted p.Extension Capability.Packages (fun ext ->
                    task {
                        match! ext.Packages.Data p.Type env.Scope.Cancellation with
                        | Ok data -> return Ok { Contributions = data }
                        | Error(PackagesError.UnknownType name) -> return Error(HostError.UnknownType name)
                        | Error(PackagesError.NotData(name, kind)) -> return Error(HostError.NotData(name, kind))
                    }))
        Handler.request hostGrantsCheck (fun _ (p: GrantCheckParams) ->
            fun (env: 'R) ->
                granted p.Extension Capability.Grants (fun ext ->
                    grant (ext.Grants.Check p.Query env.Scope.Cancellation)))
        Handler.request hostGrantsRequest (fun _ (p: GrantRequestParams) ->
            fun (env: 'R) ->
                granted p.Extension Capability.Grants (fun ext ->
                    grant (ext.Grants.Request p.Ask env.Scope.Cancellation)))
        Handler.request hostGrantsAwait (fun _ (p: GrantAwaitParams) ->
            fun (env: 'R) ->
                granted p.Extension Capability.Grants (fun ext ->
                    grant (ext.Grants.Await p.Request env.Scope.Cancellation)))
        Handler.request hostGrantsWithdraw (fun _ (p: GrantWithdrawParams) ->
            fun (env: 'R) ->
                granted p.Extension Capability.Grants (fun ext ->
                    grant (ext.Grants.Withdraw p.Request p.Reason env.Scope.Cancellation)))
    ]

/// The extension protocol over a connection whose server is the extension process; `attached` is
/// what its `host/*` handlers answer with (`hostHandlers`).
let ofPeer (attached: Attached) (peer: Peer) : RemotePeer =
    {
        Activate =
            fun parameters cancellation ->
                task {
                    let! outcome = run cancellation (Rpc.call peer activate parameters)
                    return outcome |> Result.mapError failure
                }
        Invoke =
            fun address input cancellation ->
                task {
                    let! outcome =
                        run
                            cancellation
                            (Rpc.call
                                peer
                                invoke
                                ({
                                    Extension = address.Extension
                                    Type = address.Type
                                    Id = address.Id
                                    Input = input
                                }
                                : InvokeParams))

                    return outcome |> Result.mapError failure
                }
        Call =
            fun extension name parameters cancellation ->
                task {
                    let! outcome =
                        run
                            cancellation
                            (Rpc.call
                                peer
                                call
                                ({
                                    Extension = extension
                                    Method = name
                                    Params = parameters
                                }
                                : CallParams))

                    return outcome |> Result.mapError (methodFailure name)
                }
        Deactivate =
            fun extension cancellation ->
                task {
                    let! outcome =
                        run cancellation (Rpc.call peer deactivate ({ Extension = extension }: ExtensionRef))

                    attached.Detach extension
                    return outcome |> Result.mapError failure
                }
        Attach = fun extension env -> attached.Attach(extension, env)
    }

/// What the host sends in `initialize`.
let initializeMembers: (string * Json) list =
    [ VersionMember, Json.String Versions.Protocol ]
