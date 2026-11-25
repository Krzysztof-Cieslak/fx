/// The host's `RemotePeer` over a JSON-RPC connection to an extension process.
[<RequireQualifiedAccess>]
module Fx.Extensions.RpcRemote

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

/// The extension protocol over a connection whose server is the extension process.
let ofPeer (peer: Peer) : RemotePeer =
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

                    return outcome |> Result.mapError failure
                }
    }

/// What the host sends in `initialize`.
let initializeMembers: (string * Json) list =
    [ VersionMember, Json.String Versions.Protocol ]
