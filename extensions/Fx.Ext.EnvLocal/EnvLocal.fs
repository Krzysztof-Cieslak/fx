/// `fx.env.local` (spec §3, §9): environments on the host's own machine. The workspace is an
/// existing directory used in place; envd runs in the host's process (embedded mode, the default) or
/// as `<binary> envd --stdio` child of the host (piped). Each environment gets a home of its own
/// under the product's data directory, where envd keeps its state; nothing of the workspace is ever
/// deleted. No sandbox here: drivers come with T113 (the spec's `sandbox` must be `none`). See
/// README.md and docs/modules/environments.md.
module Fx.Ext.EnvLocal.EnvLocal

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// The extension's id: `fx.env.local`.
let id = Product.extensionId "env.local"

/// The provider's kind: what specs name in `provider`.
[<Literal>]
let Kind = "local"

/// The directory under the data directory that holds the environments' homes.
[<Literal>]
let EnvironmentsDir = "environments"

/// The lease, in each home: what `List` finds after a restart.
[<Literal>]
let LeaseFile = "lease.json"

/// In process (embedded mode) first; piped through a child process.
let caps: ProviderCaps =
    {
        Transports = [ TransportKind.InProcess; TransportKind.Piped ]
        Suspend = false
        Fork = false
        Snapshots = []
        Materialize = [ MaterializeKind.Existing ]
        Prebaked = false
        Gpu = false
    }

/// The provider's settings: none, so `{}`.
let config: Codec<Json> =
    Codec.json
    |> Codec.refine (fun json ->
        match json with
        | Json.Object [] -> Ok()
        | other -> Error $"expected {{}}: the local provider has no settings, got {Json.describe other}")
    |> Codec.describe "The local provider's settings: none."

/// Where the environments' homes are: `<data dir>/environments`.
let root (host: HostInfo) : string =
    Path.Combine(host.DataDir, EnvironmentsDir)

/// The home of an environment.
let homeOf (host: HostInfo) (id: string) : string = Path.Combine(root host, id)

/// Environment ids name directories: letters, digits, `-` and `_`.
let private validId (id: string) =
    Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}$")

let private workspaceIssue (message: string) =
    ProviderError.Invalid [ Issue.at [ PathSegment.Key "workspace"; PathSegment.Key "path" ] message ]

/// The workspace's absolute path: the spec's, with `~` for the user's home, normalized.
let workspacePath (host: HostInfo) (workspace: WorkspaceSpec) : Result<string, ProviderError> =
    match workspace.Path with
    | None -> Error(workspaceIssue "the local provider needs the workspace's path")
    | Some path ->
        let expanded =
            if path = "~" then
                host.Home
            elif path.StartsWith "~/" || path.StartsWith "~\\" then
                Path.Combine(host.Home, path.Substring 2)
            else
                path

        if Path.IsPathFullyQualified expanded then
            Ok(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded, expanded)))
        else
            Error(workspaceIssue $"expected an absolute path, or one under ~, got {Json.quote path}")

let private host: Eff<IProviderEnv, HostInfo> =
    Eff.asks (fun (env: IProviderEnv) -> env.HostInfo)

let private failed (error: FsError) = ProviderError.Failed(string error)

let create (request: EnvRequest) : Eff<IProviderEnv, Result<Lease, ProviderError>> =
    eff {
        let! host = host

        if not (validId request.Id) then
            return Error(ProviderError.Invalid [ Issue.create $"{Json.quote request.Id} cannot name a directory" ])
        else
            match workspacePath host request.Spec.Workspace with
            | Error e -> return Error e
            | Ok workspace ->
                match! Fs.stat workspace with
                | Error(NotFound _) -> return Error(workspaceIssue $"the workspace {workspace} does not exist")
                | Error e -> return Error(failed e)
                | Ok entry when entry.Kind <> EntryKind.Directory ->
                    return Error(workspaceIssue $"the workspace {workspace} is not a directory")
                | Ok _ ->
                    let home = homeOf host request.Id

                    let lease =
                        {
                            Id = request.Id
                            Provider = Kind
                            Workspace = workspace
                            Home = home
                            Data = Map.empty
                        }

                    match! Fs.createDirectory home with
                    | Ok()
                    | Error(AlreadyExists _) ->
                        let text = Json.serializeIndented (Codec.encode EnvSpec.lease lease)
                        let! written = Fs.writeText (Path.Combine(home, LeaseFile)) text
                        return written |> Result.map (fun () -> lease) |> Result.mapError failed
                    | Error e -> return Error(failed e)
    }

let platform (_: Lease) : Eff<IProviderEnv, Result<EnvMachine, ProviderError>> =
    host
    |> Eff.map (fun host ->
        Ok
            {
                Target = host.Target
                DataDir = host.DataDir
                SameHost = true
            })

let exec (_: Lease, spec: ProcSpec) : Eff<IProviderEnv, Result<Proc, ProviderError>> =
    Shell.spawn spec
    |> Eff.map (Result.mapError (fun e -> ProviderError.Failed(string e)))

let upload (_: Lease, _: Upload) : Eff<IProviderEnv, Result<unit, ProviderError>> =
    Eff.ret (
        Error(ProviderError.Unsupported "uploads: a local environment is the host's machine, which has every file")
    )

/// Deletes a directory and everything in it, depth first, never following links: a link is
/// removed, not what it points to. Gone already is done.
let private deleteTree (path: string) : Eff<IProviderEnv, Result<unit, FsError>> =
    eff {
        let pending = Stack<string * bool>()
        pending.Push((path, false))
        let mutable failure = None

        let remove (path: string) : Eff<IProviderEnv, FsError option> =
            eff {
                match! Fs.delete path with
                | Ok()
                | Error(NotFound _) -> return None
                | Error e -> return Some e
            }

        while failure.IsNone && pending.Count > 0 do
            let path, emptied = pending.Pop()

            if emptied then
                let! removed = remove path
                failure <- removed
            else
                match! Fs.stat path with
                | Error(NotFound _) -> ()
                | Error e -> failure <- Some e
                | Ok entry when entry.Kind = EntryKind.Directory ->
                    match! Fs.list path with
                    | Error(NotFound _) -> ()
                    | Error e -> failure <- Some e
                    | Ok entries ->
                        pending.Push((path, true))

                        for child in entries do
                            pending.Push((child.Path, false))
                | Ok _ ->
                    let! removed = remove path
                    failure <- removed

        return
            match failure with
            | Some e -> Error e
            | None -> Ok()
    }

let private sameDirectory (a: string) (b: string) =
    String.Equals(
        Path.TrimEndingDirectorySeparator a,
        Path.TrimEndingDirectorySeparator b,
        if OperatingSystem.IsLinux() then
            StringComparison.Ordinal
        else
            StringComparison.OrdinalIgnoreCase
    )

/// Deletes the environment's home (envd's state with it). The workspace existed before and stays.
let destroy (lease: Lease) : Eff<IProviderEnv, Result<unit, ProviderError>> =
    eff {
        let! host = host

        if not (validId lease.Id) || not (sameDirectory lease.Home (homeOf host lease.Id)) then
            // Never delete a directory this provider did not create.
            return
                Error(
                    ProviderError.Invalid
                        [
                            Issue.create $"the lease's home {lease.Home} is not this provider's home for {lease.Id}"
                        ]
                )
        else
            let! deleted = deleteTree lease.Home
            return deleted |> Result.mapError failed
    }

/// The leases whose homes exist, read from their lease files.
let list: Eff<IProviderEnv, Result<Lease list, ProviderError>> =
    eff {
        let! host = host

        match! Fs.list (root host) with
        | Error(NotFound _) -> return Ok []
        | Error e -> return Error(failed e)
        | Ok entries ->
            let leases = ResizeArray<Lease>()

            for entry in entries do
                if entry.Kind = EntryKind.Directory then
                    match! Fs.readText (Path.Combine(entry.Path, LeaseFile)) with
                    | Ok text ->
                        match Codec.decodeText EnvSpec.lease text with
                        | Ok lease when lease.Id = entry.Name && lease.Provider = Kind -> leases.Add lease
                        | _ -> ()
                    | Error _ -> ()

            return Ok(List.ofSeq leases)
    }

/// The provider.
let provider: EnvProvider =
    {
        Kind = Kind
        Description = "Environments on the host's own machine: an existing directory, envd in process or as a child."
        Caps = caps
        Config = config
        Create = create
        Platform = platform
        Exec = exec
        Upload = upload
        Connect = None
        Suspend = None
        Resume = None
        Snapshot = None
        Fork = None
        Destroy = destroy
        List = list
    }

/// The package's manifest: native (a provider is code), contributing the `local` provider.
let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description =
            Some "Environments on the host's own machine: the workspace in place, envd in process or as a child."
        Contributions = [ Declare.contribution EnvProviders.contributionType Kind ]
    }

/// The package as a native extension.
let extension: Extension =
    {
        Manifest = manifest
        Activate =
            eff { return Activation.create [ Contribution.create EnvProviders.contributionType Kind provider ] [] }
    }

/// Every issue with the package's data: its manifest and its provider. Empty for what ships.
let check () : Issue list =
    Manifest.check manifest @ EnvProviders.validate provider
