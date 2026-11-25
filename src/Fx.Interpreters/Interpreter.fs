namespace Fx.Interpreters

open System
open System.Security.Cryptography
open System.Text
open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Sdk

/// What a kernel launch needs, read once by the caller (the environment's launcher, spec §3: kernels
/// run in the environment as separate processes).
[<NoEquality; NoComparison>]
type InterpreterSession =
    {
        Provider: InterpreterProvider
        /// The runtime the provider names (`Provider.Launch.Runtime`), from the `runtime`
        /// contributions in effect.
        Runtime: RuntimeSpec
        /// What `Provider.Requires` resolved to in the environment (`Components.ensure`); the
        /// runtime's component among them.
        Installed: InstalledComponent list
        /// Where interpreters' assets are extracted in the environment: under the product's home,
        /// never in the workspace.
        AssetsRoot: string
        /// The target, this kernel's own fresh directories (`OwnedDirs.under`), the workspace, trust,
        /// and the launcher's environment.
        Launch: LaunchSession
    }

    override this.ToString() =
        $"{this.Provider.Id} on {this.Runtime} ({this.Launch})"

/// Why a kernel was not started.
[<RequireQualifiedAccess>]
type InterpreterError =
    /// The provider or its launch is invalid; the issues say why.
    | Invalid of Issue list
    /// The runtime's component is not among the installed ones.
    | NotInstalled of componentId: string
    | Assets of AssetError
    /// The owned directories or the binding files could not be written.
    | Prepare of FsError
    | Start of StartError

    override this.ToString() =
        match this with
        | Invalid issues -> "invalid interpreter launch:\n" + Issue.renderAtMost 10 issues
        | NotInstalled id -> $"the component {id} is not installed"
        | Assets e -> "assets: " + string e
        | Prepare e -> "preparing the launch: " + string e
        | Start e -> string e

/// Starting an interpreter's kernel in an environment (docs/modules/interpreters.md, "Launching"):
/// the assets extracted (checked), the runtime's owned directories prepared, the binding files
/// written, the process resolved by the runtime's hardened launch (`RuntimeSpec.launch`), then
/// started through the environment's shell capability and connected over its stdio.
[<RequireQualifiedAccess>]
module Interpreter =
    let private join (os: Os) (dir: string) (name: string) =
        let separator = if os = Os.Windows then '\\' else '/'

        if dir.EndsWith separator || dir.EndsWith '/' then
            dir + name
        else
            dir + string separator + name

    /// Where a launch's binding files go: `bindings` in its owned root.
    let bindingsDir (launch: LaunchSession) : string =
        join launch.Target.Os launch.Owned.Root "bindings"

    /// A name for this launch's staging directory: its owned root is its own.
    let private launchName (launch: LaunchSession) =
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes launch.Owned.Root)

        Convert.ToHexStringLower(digest).Substring(0, 16)

    /// Everything up to the process: checks the provider and the runtime, extracts the assets, prepares
    /// the owned directories and writes the binding files for `surface`. Runs on the environment's
    /// filesystem; the result goes to `Kernel.start`.
    let prepare<'R when 'R :> IFs and 'R :> IScope>
        (session: InterpreterSession)
        (surface: BindingSurface)
        : Eff<'R, Result<KernelLaunch, InterpreterError>> =
        eff {
            let provider = session.Provider
            let runtime = session.Runtime
            let os = session.Launch.Target.Os

            let issues =
                [
                    yield!
                        InterpreterProvider.validate provider
                        |> List.map (Issue.under (PathSegment.Key "provider"))
                    if provider.Launch.Runtime <> runtime.Component.Id then
                        Issue.at
                            [ PathSegment.Key "runtime" ]
                            $"the interpreter runs on {Json.quote provider.Launch.Runtime}, not {Json.quote runtime.Component.Id}"
                ]

            match
                issues,
                session.Installed
                |> List.tryFind (fun installed -> installed.Id = runtime.Component.Id)
            with
            | _ :: _, _ -> return Error(InterpreterError.Invalid issues)
            | [], None -> return Error(InterpreterError.NotInstalled runtime.Component.Id)
            | [], Some installed ->
                match! Assets.ensure os session.AssetsRoot (launchName session.Launch) provider with
                | Error e -> return Error(InterpreterError.Assets e)
                | Ok assets ->
                    let files = provider.Bindings surface
                    let bindings = bindingsDir session.Launch

                    let! prepared =
                        effResult {
                            do! RuntimeSpec.prepare runtime session.Launch.Owned
                            do! Fs.createDirectory bindings

                            for file in files.Files do
                                do! Fs.writeText (join os bindings file.Name) file.Content
                        }

                    match prepared with
                    | Error e -> return Error(InterpreterError.Prepare e)
                    | Ok() ->
                        let launched =
                            InterpreterProvider.program provider os assets bindings
                            |> Result.bind (RuntimeSpec.launch runtime installed session.Launch)

                        match launched with
                        | Error issues -> return Error(InterpreterError.Invalid issues)
                        | Ok proc ->
                            return
                                Ok
                                    {
                                        Process = proc
                                        EnvDeny = runtime.Launch.EnvDeny
                                        IgnoreEnvCase = (os = Os.Windows)
                                    }
        }

    /// Prepares the launch and starts the kernel, in one environment with both the filesystem and
    /// the shell of the environment the kernel runs in.
    let start<'R when 'R :> IFs and 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (options: KernelOptions)
        (host: KernelHost<'R>)
        (setup: KernelSetup)
        (session: InterpreterSession)
        (surface: BindingSurface)
        : Eff<'R, Result<Kernel, InterpreterError>> =
        eff {
            match! prepare session surface with
            | Error e -> return Error e
            | Ok launch ->
                match! Kernel.start options host setup launch with
                | Ok kernel -> return Ok kernel
                | Error e -> return Error(InterpreterError.Start e)
        }

    /// A kernel of a bridge: its setup, host and surface from the bridge.
    let startBridge<'R when 'R :> IFs and 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (options: KernelOptions)
        (bridge: Bridge<'R>)
        (output: CellOutput -> Eff<'R, unit>)
        (activity: CellActivity -> Eff<'R, unit>)
        (limits: KernelLimits)
        (session: InterpreterSession)
        : Eff<'R, Result<Kernel, InterpreterError>> =
        match Bridge.validate bridge with
        | _ :: _ as issues -> EffResult.error (InterpreterError.Invalid issues)
        | [] ->
            start
                options
                (Bridge.host bridge output activity)
                (Bridge.setup bridge limits session.Launch.Workspace)
                session
                (Bridge.surface bridge)
