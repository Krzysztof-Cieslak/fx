namespace Fx.Sdk

open Fx.Contracts
open Fx.Effects

/// What an activated extension provides: its contributions and methods, exactly those its manifest
/// declares (the host checks), and how to stop it.
[<NoEquality; NoComparison>]
type Activation =
    {
        Contributions: Contribution list
        Methods: MethodImpl list
        /// Releases what activation acquired. Runs in the extension's environment.
        Deactivate: Eff<IExtensionEnv, unit>
    }

    override this.ToString() =
        $"Activation({this.Contributions.Length} contributions, {this.Methods.Length} methods)"

/// A native extension (spec §16.1 `native` runtime): F# compiled into the binary and registered from
/// a static list of values, never loaded from an assembly at run time. `Activate` builds its
/// contributions (and any state they share) in the extension's environment.
[<NoEquality; NoComparison>]
type Extension =
    {
        Manifest: Manifest
        Activate: Eff<IExtensionEnv, Activation>
    }

    override this.ToString() = string this.Manifest

/// What an extension runtime is asked to load: an admitted package's manifest and directory, and the
/// host's catalog, to load the contributions an extension process sends.
[<NoEquality; NoComparison>]
type ActivationRequest =
    {
        Manifest: Manifest
        Root: string option
        Load: ContributionDecl -> Remote option -> Validation<Contribution, Issue>
    }

    override this.ToString() = "activate " + string this.Manifest

/// An extension runtime (spec §16.1 `ExtensionRuntime`): loads packages whose manifest names it,
/// e.g. `bun` from `fx.exthost.bun`. Contributed under `ContributionTypes.extensionRuntime` with the
/// runtime's name as the contribution id. Extensions it loads start in the bootstrap's last phase.
[<NoEquality; NoComparison>]
type ExtensionRuntime =
    {
        Activate: ActivationRequest -> Eff<IExtensionEnv, Result<Activation, string>>
    }

    override _.ToString() = "ExtensionRuntime"

[<RequireQualifiedAccess>]
module Activation =
    /// An activation with nothing to release.
    let create (contributions: Contribution list) (methods: MethodImpl list) : Activation =
        {
            Contributions = contributions
            Methods = methods
            Deactivate = Eff.unit
        }

    /// What an extension process sends for an activation: the JSON form of each contribution and the
    /// method names.
    let describe (manifest: Manifest) (activation: Activation) : Activated =
        {
            Manifest = manifest
            Contributions = activation.Contributions |> List.map (fun c -> c.Decl)
            Methods = activation.Methods |> List.map (fun m -> m.Name)
        }

/// Typed helpers to write a native extension's manifest.
[<RequireQualifiedAccess>]
module Declare =
    /// Declares a contribution of a type with an id.
    let contribution (contributionType: ContributionType<'T>) (id: string) : ContributionDecl =
        {
            Type = contributionType.Name
            Id = id
            Value = None
        }

    /// Declares a data contribution with its value, for declarative manifests.
    let value (contributionType: ContributionType<'T>) (id: string) (value: 'T) : ContributionDecl =
        {
            Type = contributionType.Name
            Id = id
            Value = contributionType.ToJson value
        }

    /// Declares a method of the RPC contract.
    let method (m: ExtMethod<'P, 'R, 'E>) : MethodDecl = ExtMethod.declare m

/// Activation through an extension process: what the `process` runtime does once the process runs,
/// and what a runtime hosted by a process (e.g. `bun`) does for each package.
[<RequireQualifiedAccess>]
module RemoteActivation =
    let private methodProxy (peer: RemotePeer) (extension: string) (name: string) : MethodImpl =
        {
            Name = name
            Invoke = fun json env -> peer.Call extension name json env.Scope.Cancellation
        }

    /// Asks the process to activate an extension and loads what it provides with the host's catalog:
    /// data as values, code as proxies that call the process. The process must answer for the
    /// manifest's id and version.
    let activate
        (peer: RemotePeer)
        (parameters: ActivateParams)
        (expected: Manifest)
        (load: ContributionDecl -> Remote option -> Validation<Contribution, Issue>)
        : Eff<IExtensionEnv, Result<Activation, string>> =
        fun env ->
            task {
                match! peer.Activate parameters env.Scope.Cancellation with
                | Error failure -> return Error("activation failed: " + string failure)
                | Ok activated when
                    activated.Manifest.Id <> expected.Id
                    || activated.Manifest.Version <> expected.Version
                    ->
                    return
                        Error
                            $"the process activated {activated.Manifest.Id} {activated.Manifest.Version}, not {expected.Id} {expected.Version}"
                | Ok activated ->
                    let loaded =
                        activated.Contributions
                        |> Validation.traverseIndexed (fun i decl ->
                            let remote =
                                {
                                    Peer = peer
                                    Address =
                                        {
                                            Extension = expected.Id
                                            Type = decl.Type
                                            Id = decl.Id
                                        }
                                }

                            load decl (Some remote)
                            |> Result.mapError (
                                List.map (
                                    Issue.under (PathSegment.Index i)
                                    >> Issue.under (PathSegment.Key "contributions")
                                )
                            ))

                    match loaded with
                    | Error issues ->
                        return
                            Error(
                                "the process sent contributions that do not load:\n"
                                + Issue.renderAtMost 10 issues
                            )
                    | Ok contributions ->
                        return
                            Ok
                                {
                                    Contributions = contributions
                                    Methods = activated.Methods |> List.map (methodProxy peer expected.Id)
                                    Deactivate =
                                        fun env ->
                                            task {
                                                let! _ = peer.Deactivate expected.Id env.Scope.Cancellation
                                                return ()
                                            }
                                }
            }

/// The contribution types of the extension system.
[<RequireQualifiedAccess>]
module ExtensionRuntimes =
    /// Extension runtimes, by name (the contribution id). Privileged (spec §16.5). An extension process
    /// can contribute one: the host then activates the runtime's packages through that process.
    let contributionType: ContributionType<ExtensionRuntime> =
        let name = "extension-runtime"

        {
            Name = name
            Description = "An extension runtime: loads packages whose manifest names it."
            Kind = ContributionKind.Code
            Privileged = true
            Schema = None
            ToJson = fun _ -> None
            OfJson =
                fun _ remote ->
                    match remote with
                    | None -> ContributionType.needsCode name
                    | Some remote ->
                        Ok
                            {
                                Activate =
                                    fun request ->
                                        RemoteActivation.activate
                                            remote.Peer
                                            {
                                                Extension = request.Manifest.Id
                                                Manifest = Some request.Manifest
                                                Root = request.Root
                                            }
                                            request.Manifest
                                            request.Load
                            }
            Serve = None
        }

    /// A runtime contributed in process.
    let create (activate: ActivationRequest -> Eff<IExtensionEnv, Result<Activation, string>>) : ExtensionRuntime =
        { Activate = activate }
