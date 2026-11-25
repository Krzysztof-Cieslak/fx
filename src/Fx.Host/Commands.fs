namespace Fx.Host

open System
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Extensions
open Fx.Components
open Fx.Auth

/// What `fx variants` shows.
[<RequireQualifiedAccess>]
type VariantsView =
    /// Every slot: id, default, choices, whether privileged, and what it changes.
    | List
    /// The value of every slot here, and where it comes from (config, flags, policy, default).
    | Explain
    /// The JSON Schema of a config file's `variants` member, for editors.
    | Schema

    override this.ToString() =
        match this with
        | List -> "list"
        | Explain -> "explain"
        | Schema -> "schema"

/// The host's commands that need no running host: they read config and load the built-ins, but open
/// no store and take no lock.
[<RequireQualifiedAccess>]
module Commands =
    let private run (options: HostOptions) (effect: Eff<HostEnv, 'T>) : Task<'T> =
        Eff.run (Host.environment options Scope.root) effect

    /// Trusts a directory (absolute) and what is below it: its project config, extensions and setup
    /// may then run (spec §18). Recorded in the host's data directory, never in the project.
    let trust (options: HostOptions) (dir: string) : Task<Result<string, string>> =
        run
            options
            (effResult {
                let! store = TrustStore.read options.Paths.Trust
                let! now = Clock.now
                let! updated = TrustStore.add TrustStore.ignoreCase dir now.UnixMicroseconds store
                do! Fs.createDirectory options.Paths.Data |> EffResult.mapError string
                do! TrustStore.write options.Paths.Trust updated
                let! normalized = TrustStore.normalize dir
                return $"trusted {normalized}"
            })

    /// Stops trusting a directory that was trusted.
    let untrust (options: HostOptions) (dir: string) : Task<Result<string, string>> =
        run
            options
            (effResult {
                let! store = TrustStore.read options.Paths.Trust
                let! updated, removed = TrustStore.remove TrustStore.ignoreCase dir store
                let! normalized = TrustStore.normalize dir

                if removed then
                    do! TrustStore.write options.Paths.Trust updated
                    return $"no longer trusted: {normalized}"
                else
                    return $"{normalized} was not trusted (a directory above it may be)"
            })

    /// The trusted directories, one per line.
    let trusted (options: HostOptions) : Task<Result<string, string>> =
        run
            options
            (effResult {
                let! store = TrustStore.read options.Paths.Trust

                return store.Projects |> List.map (fun p -> p.Path + "\n") |> String.concat ""
            })

    /// The variant registry with the built-ins loaded, and the host's resolution in the workspace.
    let variants (options: HostOptions) (view: VariantsView) : Task<Result<string, string list>> =
        run
            options
            (effResult {
                let! config = Setup.readConfig options

                let! extensions =
                    Setup.bootstrap options config (Eff.ret Fx.Sdk.ContentExclusions.none)

                let! registry = Setup.registry extensions

                match view with
                | VariantsView.Schema ->
                    return
                        Json.serializeIndented (Schema.toJsonSchema (SlotRegistry.configSchema registry))
                        + "\n"
                | VariantsView.List ->
                    return
                        registry.Slots
                        |> List.map (fun slot ->
                            let privileged = if slot.Privileged then " (privileged)" else ""

                            let choices =
                                match slot.Choices with
                                | Some choices ->
                                    " [" + (choices |> List.map Json.serialize |> String.concat ", ") + "]"
                                | None -> ""

                            $"{slot.Id} = {Json.serialize slot.Default}{choices}{privileged}\n    {slot.Description}\n")
                        |> String.concat ""
                | VariantsView.Explain ->
                    let! resolution = Setup.resolve registry config

                    let trust =
                        if config.ProjectTrusted then
                            ""
                        else
                            $"# {options.Workspace} is not trusted: its config is not read (trust it with `{Fx.Product.binaryName} trust`)\n"

                    return
                        trust
                        + (Resolution.explain resolution
                           |> List.map (fun l -> l + "\n")
                           |> String.concat "")
            })

    /// `fx auth …` (auth.md): runs the command against the service credentials the host would start
    /// with, under the data directory's lock (one writer of the credential stores at a time). The exit
    /// code and the lines to print, which never hold a secret; `runFlow` runs a login's flow at the
    /// terminal.
    let auth
        (options: HostOptions)
        (runFlow: FlowRunner)
        (command: AuthCommand)
        : Task<Result<int * string list, string list>> =
        let paths = options.Paths

        let withExtensions (config: HostConfig) (extensions: Fx.Extensions.Host) =
            effResult {
                let! registry = Setup.registry extensions
                let! resolution = Setup.resolve registry config
                let! env = Eff.ask<HostEnv>

                let resolved =
                    { env with
                        Variants = Variants.ofResolution resolution
                    }

                let! service =
                    Eff.provide resolved (Setup.auth options extensions)
                    |> EffResult.mapError List.singleton

                return!
                    Eff.provide resolved (AuthCli.run service (Setup.providers extensions) runFlow command)
                    |> Eff.map Ok
            }

        let locked: Eff<HostEnv, Result<int * string list, string list>> =
            effResult {
                let! config = Setup.readConfig options

                return!
                    Eff.bracket
                        (Setup.bootstrap options config (Eff.ret Fx.Sdk.ContentExclusions.none))
                        ExtensionHost.stop
                        (withExtensions config)
            }

        run
            options
            (effResult {
                do!
                    Fs.createDirectory paths.Data
                    |> EffResult.mapError (fun e -> [ $"the data directory {paths.Data} could not be created: {e}" ])

                let! held =
                    InstallFiles.tryLock paths.Lock
                    |> EffResult.mapError (fun e -> [ $"the host's lock {paths.Lock} could not be taken: {e}" ])

                match held with
                | None ->
                    return!
                        EffResult.error
                            [
                                $"a host is running on {paths.Data}: stop it first, or set {HostPaths.homeVariable} to another directory"
                            ]
                | Some held -> return! Eff.bracket (Eff.ret held) InstallFiles.release (fun _ -> locked)
            })
