namespace Fx.Host

open System
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Extensions
open Fx.Components
open Fx.Auth
open Fx.Replay

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

/// The host's commands that need no running host: they read config and load the built-ins; `auth` and
/// `replay` take the data directory's lock, and `replay` reads its stores.
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

    /// Runs `locked` under the data directory's lock: no host runs on it meanwhile.
    let private underLock (options: HostOptions) (locked: Eff<HostEnv, Result<'T, string list>>) =
        let paths = options.Paths

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

    /// `fx replay <session>` (replay.md): replays a recorded session of the data directory exactly, under
    /// its lock (the stores are only read), with the interpreters and model catalog of the extensions in
    /// effect. What to print and the exit code: 0 when replay matched the recording, 1 when it diverged.
    let replay (options: HostOptions) (args: ReplayArgs) : Task<Result<ReplayOutput, string list>> =
        let paths = options.Paths

        let replayed (config: HostConfig) (extensions: Fx.Extensions.Host) =
            effResult {
                let! registry = Setup.registry extensions
                let! resolution = Setup.resolve registry config
                let durability = Setup.value HostSlots.durability (Variants.ofResolution resolution)
                let! stores = options.OpenStores paths durability |> Result.mapError List.singleton
                let providers = Setup.providers extensions

                let settings: ReplaySettings =
                    {
                        // The host's kernels are its interpreters' (their launches are journaled).
                        Interpreter =
                            fun info ->
                                {
                                    Language = info.Language
                                    Prompt = fun _ -> ""
                                    Start =
                                        fun _ ->
                                            EffResult.error
                                                $"the session's {info.Language} kernels are not an interpreter's: this host cannot start them"
                                    Launches = None
                                }
                        Provider = Setup.interpreter extensions
                        KernelOptions = Setup.kernelOptions
                        Catalog =
                            fun model ->
                                providers
                                |> List.tryPick (fun p -> p.Catalog.Models |> List.tryFind (fun m -> m.Ref = model))
                        StallAfter = TimeSpan.FromSeconds(float args.StallSeconds)
                    }

                let recording: RecordedSession =
                    {
                        Log = stores.Log
                        Blobs = stores.Blobs
                    }

                return!
                    Eff.bracket (Eff.ret stores) (fun stores -> eff { stores.Close() }) (fun _ ->
                        ReplayCommand.replay settings recording args |> Eff.map Ok)
            }

        underLock
            options
            (effResult {
                // Replay opens no store a host has not made.
                let! recorded = Fs.exists paths.Events |> EffResult.mapError (fun e -> [ string e ])

                if not recorded then
                    return! EffResult.error [ $"no session is recorded in {paths.Data}" ]
                else
                    let! config = Setup.readConfig options

                    return!
                        Eff.bracket
                            (Setup.bootstrap options config (Eff.ret Fx.Sdk.ContentExclusions.none))
                            ExtensionHost.stop
                            (replayed config)
            })

    /// `fx grants …` (host.md): the person who runs it — the OS user, signed in in process through
    /// `local-os-user`, as embedded mode signs them in — lists, makes or revokes grants on the data
    /// directory's grants log, under its lock (as `fx auth`: beside a running host it fails). Only a
    /// person who owns the host grants (spec §13); nothing here is reachable by an agent or extension.
    /// The lines to print.
    let grants (options: HostOptions) (command: GrantsCommand) : Task<Result<string list, string list>> =
        let paths = options.Paths

        let withExtensions (config: HostConfig) (extensions: Fx.Extensions.Host) =
            effResult {
                let! registry = Setup.registry extensions
                let! resolution = Setup.resolve registry config
                let variants = Variants.ofResolution resolution
                let! env0 = Eff.ask<HostEnv>
                let env = { env0 with Variants = variants }

                let! actions =
                    Fx.Grants.Actions.withContributed (
                        ExtensionHost.contributions Fx.Sdk.GrantActions.contributionType extensions
                        |> List.map _.Value
                    )

                // Who runs this: the person embedded mode signs in.
                let! machine = Host.machineId paths.MachineId |> EffResult.mapError List.singleton

                let authenticator =
                    Fx.Identity.Host.Authenticator.create
                        (ExtensionHost.contributions Fx.Sdk.IdentityProviders.contributionType extensions)
                        {
                            Machine = machine
                            Account = options.Account
                        }
                        (Fx.Identity.Host.SessionTokens())

                let! signedIn =
                    Eff.provide
                        env
                        (Fx.Identity.Host.Authenticator.signIn
                            authenticator
                            Fx.Identity.Host.ConnectionFacts.inProcess
                            "cli"
                            (Some(
                                Fx.Identity.SignIn.Provider(
                                    Fx.Ext.IdentityLocalOsUser.LocalOsUser.ProviderId,
                                    Json.Null
                                )
                            )))
                    |> EffResult.mapError (fun failure -> [ $"signing in failed: {failure}" ])

                let session = signedIn.Session

                do!
                    if Fx.Identity.Role.allows Fx.Identity.Role.Owner session.Roles then
                        Ok()
                    else
                        Error
                            [
                                $"{session.Principal.Id.Value} does not own this host: only its owner grants here"
                            ]

                let by = Fx.Identity.Host.ClientSession.attribution session

                do!
                    Fs.createDirectory (IO.Path.GetDirectoryName paths.Events |> nonNull)
                    |> EffResult.mapError (fun e -> [ $"the event store's directory could not be created: {e}" ])

                do!
                    Fs.createDirectory paths.Blobs
                    |> EffResult.mapError (fun e -> [ $"the blob store's directory could not be created: {e}" ])

                let! stores =
                    options.OpenStores paths (Setup.value HostSlots.durability variants)
                    |> Result.mapError List.singleton

                let granting
                    (service: Fx.Grants.Host.GrantsService<HostEnv>)
                    : Eff<HostEnv, Result<string list, string list>> =
                    effResult {
                        match command with
                        | GrantsCommand.List all ->
                            return GrantsCommand.render (Fx.Grants.Host.GrantsService.list service all).Grants
                        | GrantsCommand.Grant(effect, action, pattern, scope, reason) ->
                            let! scope =
                                match scope with
                                | GrantsScope.Host -> EffResult.ok Fx.Grants.GrantScope.Host
                                | GrantsScope.Repo ->
                                    EffResult.ok (
                                        Fx.Grants.GrantScope.Repo
                                            {
                                                Repo = GrantsCommand.repo options.Workspace
                                                Path = None
                                            }
                                    )
                                | GrantsScope.Session id ->
                                    eff {
                                        let! indexed = SessionIndex.entries (SessionIndex.typed stores.Log)

                                        if indexed |> List.exists (fun e -> e.Session = id) then
                                            return Ok(Fx.Grants.GrantScope.Session id)
                                        else
                                            return Error [ $"no session {id} on this host" ]
                                    }

                            let! draft =
                                GrantsCommand.draft actions effect action pattern scope reason
                                |> Result.mapError List.singleton

                            let! grant =
                                Fx.Grants.Host.GrantsService.grant service by draft
                                |> EffResult.mapError (fun e -> [ e.Message ])

                            return
                                [
                                    GrantsCommand.line
                                        {
                                            Grant = grant
                                            Status = Fx.Grants.GrantStatus.Active
                                        }
                                ]
                        | GrantsCommand.Revoke(id, reason) ->
                            do!
                                Fx.Grants.Host.GrantsService.revoke service by (Fx.Grants.GrantId id) reason
                                |> EffResult.mapError (fun e -> [ e.Message ])

                            return [ $"revoked {id}" ]
                    }

                return!
                    Eff.bracket (Eff.ret stores) (fun stores -> eff { stores.Close() }) (fun _ ->
                        Eff.provide
                            env
                            (Eff.bracket
                                (Fx.Grants.Host.GrantsService.start
                                    { Fx.Grants.Host.GrantsSettings.create (
                                          Fx.Grants.Host.GrantStoreLog.hostLocal stores.Log
                                      ) with
                                        Actions = actions
                                    })
                                Fx.Grants.Host.GrantsService.stop
                                granting))
            }

        underLock
            options
            (effResult {
                let! config = Setup.readConfig options

                return!
                    Eff.bracket
                        (Setup.bootstrap options config (Eff.ret Fx.Sdk.ContentExclusions.none))
                        ExtensionHost.stop
                        (withExtensions config)
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

        underLock options locked
