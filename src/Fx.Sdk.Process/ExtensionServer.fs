/// The extension process's side of the extension protocol, for F# extensions: serves native
/// `Extension` values over a transport (stdio in a child process), so the same extension runs in the
/// host (`native` runtime) or out of it (`process` runtime). The transport-parity tests run one
/// sample extension both ways.
[<RequireQualifiedAccess>]
module Fx.Sdk.Process.ExtensionServer

open System.Collections.Generic
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Sdk
open Fx.Sdk.Process.ExtensionProtocol

[<NoEquality; NoComparison>]
type private Served =
    {
        Manifest: Manifest
        Env: IExtensionEnv
        Activation: Activation
    }

    override this.ToString() = string this.Manifest

/// Extensions served in one process, by id; the activated ones.
type private Table(extensions: Extension list) =
    let statics = extensions |> List.map (fun e -> e.Manifest.Id, e) |> Map.ofList
    let active = Dictionary<string, Served>()
    let gate = obj ()

    member _.Static(id: string) = statics.TryFind id

    member _.Ids = extensions |> List.map (fun e -> e.Manifest.Id)

    member _.TryActive(id: string) : Served option =
        lock gate (fun () ->
            match active.TryGetValue id with
            | true, served -> Some served
            | _ -> None)

    member _.Add(served: Served) =
        lock gate (fun () -> active[served.Manifest.Id] <- served)

    member _.Remove(id: string) : Served option =
        lock gate (fun () ->
            match active.TryGetValue id with
            | true, served ->
                active.Remove id |> ignore
                Some served
            | _ -> None)

    /// A runtime with this name contributed by an active extension, with its extension's environment.
    member _.Runtime(name: string) : (ExtensionRuntime * IExtensionEnv) option =
        lock gate (fun () ->
            active.Values
            |> Seq.tryPick (fun served ->
                served.Activation.Contributions
                |> List.tryPick (fun c ->
                    if c.Id = name then
                        Contribution.tryValue ExtensionRuntimes.contributionType c
                        |> Option.map (fun runtime -> runtime, served.Env)
                    else
                        None)))

let private noCatalog: ContributionDecl -> Remote option -> Validation<Contribution, Issue> =
    fun _ _ ->
        Error
            [
                Issue.create "an extension process has no catalog: provide contributions as values"
            ]

/// The protocol's handlers over these extensions. `envFor` builds the environment each extension's
/// code runs in (in a child process: its own clock, …); requests give it their scope.
let handlers<'R when 'R :> IScope>
    (extensions: Extension list)
    (envFor: Manifest -> IExtensionEnv)
    : string list * Handler<'R> list =
    let table = Table extensions

    let scoped (served: Served) (env: 'R) = served.Env.WithScope env.Scope

    let activateHandler =
        Handler.request activate (fun _ parameters ->
            fun (env: 'R) ->
                task {
                    match table.TryActive parameters.Extension with
                    | Some served -> return Ok(Activation.describe served.Manifest served.Activation)
                    | None ->
                        match table.Static parameters.Extension, parameters.Manifest with
                        | Some extension, _ ->
                            let extensionEnv = envFor extension.Manifest
                            let! activation = extension.Activate(extensionEnv.WithScope env.Scope)

                            table.Add
                                {
                                    Manifest = extension.Manifest
                                    Env = extensionEnv
                                    Activation = activation
                                }

                            return Ok(Activation.describe extension.Manifest activation)
                        | None, Some manifest when manifest.Id = parameters.Extension ->
                            match table.Runtime manifest.Runtime with
                            | None ->
                                return
                                    Error(
                                        ProtocolError.ActivationFailed(
                                            manifest.Id,
                                            $"this process has no extension runtime {manifest.Runtime}"
                                        )
                                    )
                            | Some(runtime, runtimeEnv) ->
                                let request =
                                    {
                                        Manifest = manifest
                                        Root = parameters.Root
                                        Load = noCatalog
                                    }

                                match! runtime.Activate request (runtimeEnv.WithScope env.Scope) with
                                | Error reason -> return Error(ProtocolError.ActivationFailed(manifest.Id, reason))
                                | Ok activation ->
                                    table.Add
                                        {
                                            Manifest = manifest
                                            Env = envFor manifest
                                            Activation = activation
                                        }

                                    return Ok(Activation.describe manifest activation)
                        | None, _ -> return Error(ProtocolError.UnknownExtension parameters.Extension)
                })

    let invokeHandler =
        Handler.request invoke (fun _ (parameters: InvokeParams) ->
            fun (env: 'R) ->
                task {
                    let address = $"{parameters.Extension}/{parameters.Type}/{parameters.Id}"

                    match table.TryActive parameters.Extension with
                    | None -> return Error(ProtocolError.NotActive parameters.Extension)
                    | Some served ->
                        match
                            served.Activation.Contributions
                            |> List.tryFind (fun c -> c.Type = parameters.Type && c.Id = parameters.Id)
                        with
                        | None -> return Error(ProtocolError.UnknownContribution address)
                        | Some contribution ->
                            match contribution.Serve with
                            | None -> return Error(ProtocolError.InProcessOnly address)
                            | Some serve ->
                                match! serve parameters.Input (scoped served env) with
                                | Ok output -> return Ok output
                                | Error issues -> return Error(ProtocolError.Invalid issues)
                })

    let callHandler =
        Handler.request call (fun _ (parameters: CallParams) ->
            fun (env: 'R) ->
                task {
                    match table.TryActive parameters.Extension with
                    | None -> return Error(ProtocolError.NotActive parameters.Extension)
                    | Some served ->
                        match served.Activation.Methods |> List.tryFind (fun m -> m.Name = parameters.Method) with
                        | None -> return Error(ProtocolError.UnknownMethod parameters.Method)
                        | Some m ->
                            match! m.Invoke parameters.Params (scoped served env) with
                            | Ok result -> return Ok result
                            | Error(MethodFailure.UnknownMethod name) -> return Error(ProtocolError.UnknownMethod name)
                            | Error(MethodFailure.InvalidParams issues) -> return Error(ProtocolError.Invalid issues)
                            | Error(MethodFailure.Failed(message, data)) ->
                                return Error(ProtocolError.MethodFailed(message, data))
                            | Error(MethodFailure.Crashed reason)
                            | Error(MethodFailure.Unreachable reason) ->
                                return
                                    raise (
                                        ContributionFailedException(
                                            {
                                                Extension = parameters.Extension
                                                Type = "method"
                                                Id = parameters.Method
                                            },
                                            reason,
                                            null
                                        )
                                    )
                })

    let deactivateHandler =
        Handler.request deactivate (fun _ (parameters: ExtensionRef) ->
            fun (env: 'R) ->
                task {
                    match table.Remove parameters.Extension with
                    | None -> return Error(ProtocolError.NotActive parameters.Extension)
                    | Some served ->
                        do! served.Activation.Deactivate(scoped served env)
                        return Ok()
                })

    table.Ids, [ activateHandler; invokeHandler; callHandler; deactivateHandler ]

/// A service that serves the extensions. It refuses hosts that do not speak its protocol version.
let service<'R when 'R :> IScope>
    (info: PeerInfo)
    (extensions: Extension list)
    (envFor: Manifest -> IExtensionEnv)
    : Service<'R> =
    let served, handlers = handlers extensions envFor

    Service.create info handlers
    |> Service.withAccept (fun remote ->
        let version =
            match remote.Members with
            | Json.Object members -> members |> List.tryFind (fun (key, _) -> key = VersionMember)
            | _ -> None

        match version with
        | Some(_, Json.String v) when List.contains v versions ->
            EffResult.ok [ ServesMember, Json.Array(served |> List.map Json.String) ]
        | _ ->
            let supported = String.concat ", " versions

            EffResult.error (
                RpcError.create
                    Protocol.ErrorCode.InvalidRequest
                    $"the host must speak extension protocol {supported} (initialize member {VersionMember})"
            ))

/// Serves the extensions on a transport (the server side of `initialize`), until the host closes it.
let serve<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
    (info: PeerInfo)
    (extensions: Extension list)
    (envFor: Manifest -> IExtensionEnv)
    (transport: Transport)
    : Eff<'R, Result<Peer, ConnectError>> =
    Rpc.accept (service info extensions envFor) transport
