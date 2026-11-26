namespace Fx.Components

open System
open System.IO
open System.Text
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// Where things live in an install root. Everything the installer writes is under it; it never
/// touches `PATH` or installs of the user's (spec §6.1).
///
/// ```
/// <root>/<id>/<version>-<hash>/     an active install, with its receipt; renamed into place whole
/// <root>/.staging/<id>@<version>-<hash>/
/// <root>/.downloads/<id>@<version>-<hash>[.partial]
/// <root>/.locks/<id>@<version>-<hash>.lock, root.lock
/// <root>/.roots/<installer>.json   the installs each installer's holders use
/// <root>/.trash/                    what a prune or repair is deleting
/// ```
module InstallRoot =
    /// The directory under the per-user data directory (`~/.fx/runtimes`).
    [<Literal>]
    let DirName = "runtimes"

    /// The default root on a machine, from values the composition root reads: `~/.fx/runtimes`, or
    /// `%LOCALAPPDATA%\fx\runtimes` on Windows (names from `Fx.Product`).
    let defaultFor (os: Os) (home: string) (localAppData: string) : string =
        match os with
        | Os.Windows -> Path.Combine(localAppData, Product.windowsDataDirName, DirName)
        | Os.Linux
        | Os.MacOs -> Path.Combine(home, Product.homeDirName, DirName)

    /// The install's receipt, inside its directory: written before activation, so an install
    /// directory with a matching receipt is complete.
    let receiptName = "." + Product.Name + "-receipt.json"

    let installDir (root: string) (key: InstallKey) = Path.Combine(root, key.Id, key.Dir)

    let staging (root: string) (key: InstallKey) =
        Path.Combine(root, ".staging", key.Name)

    let download (root: string) (key: InstallKey) =
        Path.Combine(root, ".downloads", key.Name)

    let partial (root: string) (key: InstallKey) = download root key + ".partial"
    let locks (root: string) = Path.Combine(root, ".locks")

    let keyLock (root: string) (name: string) =
        Path.Combine(locks root, name + ".lock")

    let rootLock (root: string) = Path.Combine(locks root, "root.lock")
    let roots (root: string) = Path.Combine(root, ".roots")

    let rootsFile (root: string) (installer: string) =
        Path.Combine(roots root, installer + ".json")

    let trash (root: string) = Path.Combine(root, ".trash")

    /// A `/`-separated path in an install directory, as a native path.
    let file (dir: string) (relative: string) =
        Path.Combine(Array.ofList (dir :: List.ofArray (relative.Split '/')))

/// What an install directory says about itself.
type Receipt =
    {
        Id: string
        Version: string
        Hash: string
        Target: string
        Files: int
        Bytes: int64
    }

    override this.ToString() =
        $"{this.Id}@{this.Version}-{this.Hash} for {this.Target}"

module Receipt =
    let codec: Codec<Receipt> =
        Codec.record {
            let! id = Codec.field "id" (fun (r: Receipt) -> r.Id) Codec.string
            and! version = Codec.field "version" (fun r -> r.Version) Codec.string
            and! hash = Codec.field "hash" (fun r -> r.Hash) Codec.string
            and! target = Codec.field "target" (fun r -> r.Target) Codec.string
            and! files = Codec.field "files" (fun r -> r.Files) Codec.int
            and! bytes = Codec.field "bytes" (fun r -> r.Bytes) Codec.int64

            return
                {
                    Id = id
                    Version = version
                    Hash = hash
                    Target = target
                    Files = files
                    Bytes = bytes
                }
        }

    let matches (key: InstallKey) (receipt: Receipt) =
        receipt.Id = key.Id && receipt.Version = key.Version && receipt.Hash = key.Hash

/// How an installer runs.
[<NoEquality; NoComparison>]
type InstallerSettings<'R> =
    {
        /// The install root (absolute); `InstallRoot.defaultFor` gives the usual one.
        Root: string
        /// This installer's name among the installers sharing the root (its roots file), e.g. the
        /// host's or environment's id. Two installers running at once must not share a name.
        Name: string
        /// Answers the first-install grant (spec §13) of an install attempt. The caller decides: ask a
        /// person (`Approvals.viaGrants`), apply a policy, or approve (tests).
        Approve: ApprovalRequest -> Eff<'R, ApprovalAnswer>
        Limits: ExtractLimits
        MaxDownloadBytes: int64
        /// Tries per download when failures are transient (network errors, HTTP 5xx and 429).
        DownloadAttempts: int
        /// The wait before the second try; it doubles for each later one.
        RetryDelay: TimeSpan
        /// How long to wait for a lock another installer holds.
        LockTimeout: TimeSpan
        ProbeTimeout: TimeSpan
        /// The probe's environment. By default the installer's own, without runtime injection
        /// vectors (`Manifest.deniedEnvironment`).
        ProbeEnv: EnvVars
    }

    override this.ToString() =
        $"InstallerSettings({this.Root}, {this.Name})"

module InstallerSettings =
    /// Settings with the default limits and timeouts.
    let create
        (root: string)
        (name: string)
        (approve: ApprovalRequest -> Eff<'R, ApprovalAnswer>)
        : InstallerSettings<'R> =
        {
            Root = root
            Name = name
            Approve = approve
            Limits = ExtractLimits.defaults
            MaxDownloadBytes = 2L * 1024L * 1024L * 1024L
            DownloadAttempts = 3
            RetryDelay = TimeSpan.FromSeconds 1.0
            LockTimeout = TimeSpan.FromMinutes 10.0
            ProbeTimeout = TimeSpan.FromMinutes 1.0
            ProbeEnv =
                Inherit(
                    Manifest.deniedEnvironment
                    |> List.filter (fun name -> not (name.EndsWith '*'))
                    |> List.map (fun name -> name, None)
                )
        }

/// The installer's interpreter: runs `InstallerIntent`s on the install root with the environment's
/// capabilities, and turns their outcomes (expected failures included) into result commands.
///
/// Coordination with installers in other processes that share the root:
/// - a lock per install (`.locks/<name>.lock`) around fetching and building it, so only one
///   downloads and unpacks it at a time; the others find it installed;
/// - the root lock (`.locks/root.lock`) around publishing roots, activating and pruning, so a prune
///   never removes an install that a holder uses or that is being activated;
/// - activation is one rename of a complete, probed staging directory: an install directory either
///   does not exist or is complete, never half there.
module Pipeline =

    let private utf8 = UTF8Encoding(false)

    let private io (result: Eff<'R, Result<'T, FsError>>) : Eff<'R, Result<'T, InstallFailure>> =
        EffResult.mapError (fun (e: FsError) -> InstallFailure.IoFailed(string e)) result

    /// Creates a directory, failing as an `InstallFailure`.
    let private directory<'R when 'R :> IFs and 'R :> IScope> (path: string) : Eff<'R, Result<unit, InstallFailure>> =
        io (Fs.createDirectory path)

    /// Holds a lock for the body: waits (with backoff on the clock) while another installer holds
    /// it, up to the lock timeout.
    let withLock<'R, 'T when 'R :> IInstallFiles and 'R :> IFs and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (path: string)
        (body: Eff<'R, Result<'T, InstallFailure>>)
        : Eff<'R, Result<'T, InstallFailure>> =
        eff {
            let mutable held = None
            let mutable failure = None
            let mutable waited = TimeSpan.Zero
            let mutable delay = TimeSpan.FromMilliseconds 10.0

            match! directory (Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue path) with
            | Error e -> failure <- Some e
            | Ok() -> ()

            while held.IsNone && failure.IsNone do
                match! InstallFiles.tryLock path with
                | Error e -> failure <- Some(InstallFailure.IoFailed(string e))
                | Ok(Some taken) -> held <- Some taken
                | Ok None when waited >= settings.LockTimeout ->
                    failure <-
                        Some(
                            InstallFailure.IoFailed
                                $"{path} stayed locked by another installer for {settings.LockTimeout}"
                        )
                | Ok None ->
                    do! Clock.sleep delay
                    waited <- waited + delay
                    delay <- TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2.0, 500.0))

            match held, failure with
            | Some taken, _ -> return! Eff.bracket (Eff.ret taken) InstallFiles.release (fun _ -> body)
            | None, Some failure -> return Error failure
            | None, None -> return Error(InstallFailure.IoFailed $"{path} was not locked")
        }

    /// The install in a directory, if its receipt says it is this one.
    let inspectDir<'R when 'R :> IFs and 'R :> IScope> (dir: string) (key: InstallKey) : Eff<'R, InstalledInfo option> =
        eff {
            match! Fs.readFile (Path.Combine(dir, InstallRoot.receiptName)) with
            | Ok bytes ->
                match Codec.decodeText Receipt.codec (utf8.GetString bytes) with
                | Ok receipt when Receipt.matches key receipt ->
                    return
                        Some
                            {
                                Dir = dir
                                Files = receipt.Files
                                Bytes = receipt.Bytes
                                Reused = true
                            }
                | _ -> return None
            | Error _ -> return None
        }

    let private publish<'R when 'R :> IInstallFiles and 'R :> IFs and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (keys: InstallKey list)
        : Eff<'R, InstallerCommand list> =
        eff {
            let names = keys |> List.map (fun k -> k.Id + "/" + k.Dir) |> List.sort
            let json = Json.serializeIndented (Json.Array(names |> List.map Json.String)) + "\n"

            let! written =
                withLock
                    settings
                    (InstallRoot.rootLock settings.Root)
                    (effResult {
                        do! directory (InstallRoot.roots settings.Root)

                        return!
                            io (
                                InstallFiles.replaceFile
                                    (InstallRoot.rootsFile settings.Root settings.Name)
                                    (utf8.GetBytes json)
                            )
                    })

            match written with
            | Ok() -> return []
            // Without its roots, an installer cannot keep others from pruning what it uses.
            | Error failure -> return invalidOp $"could not publish the installer's roots: {failure}"
        }

    let private inspect<'R when 'R :> IFs and 'R :> IScope>
        (settings: InstallerSettings<'R>)
        (spec: InstallSpec)
        : Eff<'R, InstallerCommand list> =
        eff {
            let! found = inspectDir (InstallRoot.installDir settings.Root spec.Key) spec.Key
            return [ InstallerCommand.Inspected(spec.Key, found) ]
        }

    let private ask<'R> (settings: InstallerSettings<'R>) (request: ApprovalRequest) : Eff<'R, InstallerCommand list> =
        eff {
            let! answer = settings.Approve request
            return [ InstallerCommand.Answered(request.Spec.Key, answer) ]
        }

    /// Downloads into `.partial`, checks the digest, then renames it into place. Retries transient
    /// failures with a doubling delay; a wrong digest is never retried.
    let private download<'R
        when 'R :> IInstallFiles and 'R :> IFs and 'R :> IFetch and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (spec: InstallSpec)
        (archive: ArchiveSpec)
        (path: string)
        : Eff<'R, Result<FetchedArchive, InstallFailure>> =
        eff {
            let partial = InstallRoot.partial settings.Root spec.Key
            let mutable attempt = 0
            let mutable outcome = None

            while outcome.IsNone do
                attempt <- attempt + 1
                let! _ = InstallFiles.deleteTree partial

                let! downloaded =
                    Fetch.download
                        {
                            Url = archive.Url
                            Destination = partial
                            MaxBytes = settings.MaxDownloadBytes
                        }

                match downloaded with
                | Ok hash when hash.Digest = archive.Digest ->
                    match! Fs.move partial path with
                    | Ok() -> outcome <- Some(Ok { Path = path; Size = hash.Size })
                    | Error e -> outcome <- Some(Error(InstallFailure.IoFailed(string e)))
                | Ok hash ->
                    let! _ = InstallFiles.deleteTree partial
                    outcome <- Some(Error(DigestMismatch(string archive.Digest, string hash.Digest)))
                | Error e when e.Transient && attempt < settings.DownloadAttempts ->
                    do! Clock.sleep (settings.RetryDelay * Math.Pow(2.0, float (attempt - 1)))
                | Error e -> outcome <- Some(Error(DownloadFailed(string e)))

            return Option.get outcome
        }

    let private fetch<'R
        when 'R :> IInstallFiles and 'R :> IFs and 'R :> IFetch and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (spec: InstallSpec)
        : Eff<'R, InstallerCommand list> =
        eff {
            match spec.Method with
            | Archive archive ->
                let path = InstallRoot.download settings.Root spec.Key

                let final = InstallRoot.installDir settings.Root spec.Key

                let! result =
                    withLock
                        settings
                        (InstallRoot.keyLock settings.Root spec.Key.Name)
                        (eff {
                            // Another installer may have installed it while this one waited for the lock.
                            match! inspectDir final spec.Key with
                            | Some info -> return Ok(Choice2Of2 info)
                            | None ->
                                match! directory (Path.Combine(settings.Root, ".downloads")) with
                                | Error e -> return Error e
                                | Ok() ->
                                    // An archive verified by an earlier attempt (or a crashed run) is reused.
                                    match! InstallFiles.hash path with
                                    | Ok hash when hash.Digest = archive.Digest ->
                                        return Ok(Choice1Of2 { Path = path; Size = hash.Size })
                                    | Ok _
                                    | Error(NotFound _) ->
                                        let! _ = InstallFiles.deleteTree path
                                        let! downloaded = download settings spec archive path
                                        return downloaded |> Result.map Choice1Of2
                                    | Error e -> return Error(InstallFailure.IoFailed(string e))
                        })

                match result with
                | Ok(Choice2Of2 info) -> return [ InstallerCommand.Inspected(spec.Key, Some info) ]
                | Ok(Choice1Of2 archive) -> return [ InstallerCommand.Fetched(spec.Key, Ok archive) ]
                | Error failure -> return [ InstallerCommand.Fetched(spec.Key, Error failure) ]
            | other ->
                return
                    [
                        InstallerCommand.Fetched(
                            spec.Key,
                            Error(
                                InstallFailure.Unsupported
                                    $"the {other.Kind} install method is not implemented yet (T211)"
                            )
                        )
                    ]
        }

    let private expandPath (spec: InstallSpec) (path: string) : Result<string, InstallFailure> =
        Component.expand [ "exeSuffix", TargetTriple.exeSuffix spec.Target ] path
        |> Result.mapError BadArchive

    /// Runs the probe in a directory.
    let probe<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (spec: InstallSpec)
        (dir: string)
        : Eff<'R, Result<unit, InstallFailure>> =
        effResult {
            match spec.Probe with
            | None -> return ()
            | Some probe ->
                let! program = expandPath spec (List.head probe.Argv)

                let! expected =
                    match probe.Expect with
                    | Some expect ->
                        Component.expand [ "version", string spec.Version ] expect
                        |> Result.map Some
                        |> Result.mapError ProbeFailed
                    | None -> Ok None

                let command =
                    {
                        Spec =
                            {
                                Program = Exec(InstallRoot.file dir program, List.tail probe.Argv)
                                Cwd = Some dir
                                Env = settings.ProbeEnv
                            }
                        Stdin = [||]
                    }

                let! outcome = Eff.timeout settings.ProbeTimeout (Shell.run command)

                match outcome with
                | None -> return! Error(ProbeFailed $"{program} did not finish within {settings.ProbeTimeout}")
                | Some(Error e) -> return! Error(ProbeFailed(string e))
                | Some(Ok output) when output.ExitCode <> 0 ->
                    let errors = (Output.stderrText output).Trim()

                    let errors =
                        if errors.Length > 200 then
                            errors.Substring(0, 200) + "…"
                        else
                            errors

                    return! Error(ProbeFailed $"{program} exited with {output.ExitCode}: {errors}")
                | Some(Ok output) ->
                    let printed = (Output.stdoutText output).Trim()

                    match expected with
                    | Some expected when printed <> expected.Trim() ->
                        return!
                            Error(
                                ProbeFailed
                                    $"{program} printed {Json.quote printed}, expected {Json.quote (expected.Trim())}"
                            )
                    | _ -> return ()
        }

    /// Unpacks, checks and probes an install in its staging directory, and writes its receipt.
    let private stage<'R
        when 'R :> IInstallFiles and 'R :> IFs and 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (spec: InstallSpec)
        (archive: ArchiveSpec)
        (fetched: FetchedArchive)
        (staging: string)
        : Eff<'R, Result<ExtractReport, InstallFailure>> =
        effResult {
            // Verify again: the archive sat on disk since it was fetched.
            let! hash = io (InstallFiles.hash fetched.Path)

            if hash.Digest <> archive.Digest then
                do! Error(DigestMismatch(string archive.Digest, string hash.Digest))

            let executables = ResizeArray<string>()

            for path in spec.Layout.Executables do
                let! expanded = expandPath spec path
                executables.Add expanded

            let! report =
                InstallFiles.extract
                    {
                        Archive = fetched.Path
                        Destination = staging
                        Options =
                            {
                                Format = archive.Format
                                Strip = archive.Strip
                                Limits = settings.Limits
                                Executables = List.ofSeq executables
                            }
                    }
                |> EffResult.mapError (fun e -> BadArchive(string e))

            // Everything the component declares must be there.
            let declared =
                (spec.Exports.Bin |> List.map (fun (name, path) -> path, $"exports as {name}"))
                @ (spec.Layout.Executables |> List.map (fun path -> path, "lists as executable"))

            for path, what in declared do
                let! relative = expandPath spec path

                let! exists = io (Fs.exists (InstallRoot.file staging relative))

                if not exists then
                    do! Error(BadArchive $"it has no {relative}, which the component {what}")

            let receipt =
                Codec.encodeText
                    Receipt.codec
                    {
                        Id = spec.Key.Id
                        Version = spec.Key.Version
                        Hash = spec.Key.Hash
                        Target = string spec.Target
                        Files = report.Files
                        Bytes = report.Bytes
                    }

            do! io (Fs.writeFile (Path.Combine(staging, InstallRoot.receiptName)) (utf8.GetBytes receipt))
            do! probe settings spec staging
            return report
        }

    /// Renames a directory, trying again a few times when it fails: on Windows a virus scanner or the
    /// search indexer may hold a file it has just seen being written (often a new executable), and
    /// the rename fails until it lets go.
    let private rename<'R when 'R :> IFs and 'R :> IClock and 'R :> IScope>
        (source: string)
        (destination: string)
        : Eff<'R, Result<unit, InstallFailure>> =
        eff {
            let mutable attempt = 0
            let mutable outcome = None

            while outcome.IsNone do
                attempt <- attempt + 1

                match! Fs.move source destination with
                | Ok() -> outcome <- Some(Ok())
                | Error(AccessDenied _ | FsError.IoFailed _) when attempt < 6 ->
                    do! Clock.sleep (TimeSpan.FromMilliseconds(50.0 * Math.Pow(2.0, float attempt)))
                | Error e -> outcome <- Some(Error(InstallFailure.IoFailed(string e)))

            return Option.get outcome
        }

    /// Moves a directory out of the way and deletes it.
    let private discard<'R when 'R :> IInstallFiles and 'R :> IFs and 'R :> IClock and 'R :> IScope>
        (settings: InstallerSettings<'R>)
        (dir: string)
        (name: string)
        : Eff<'R, Result<unit, InstallFailure>> =
        effResult {
            let trash = InstallRoot.trash settings.Root
            do! directory trash
            let target = Path.Combine(trash, name)
            do! io (InstallFiles.deleteTree target)
            do! rename dir target
            do! io (InstallFiles.deleteTree target)
        }

    /// Renames the staging directory into place, under the root lock. An install directory that is
    /// already there is kept if its receipt matches (another installer won), and replaced if not (a
    /// broken install: repair).
    let private activate<'R when 'R :> IInstallFiles and 'R :> IFs and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (spec: InstallSpec)
        (staging: string)
        (report: ExtractReport)
        : Eff<'R, Result<InstalledInfo, InstallFailure>> =
        let final = InstallRoot.installDir settings.Root spec.Key

        withLock
            settings
            (InstallRoot.rootLock settings.Root)
            (effResult {
                do! directory (Path.Combine(settings.Root, spec.Key.Id))
                let! existing = inspectDir final spec.Key

                match existing with
                | Some info ->
                    do! io (InstallFiles.deleteTree staging)
                    return info
                | None ->
                    let! broken = io (Fs.exists final)

                    if broken then
                        do! discard settings final ("broken-" + spec.Key.Name)

                    do! rename staging final

                    return
                        {
                            Dir = final
                            Files = report.Files
                            Bytes = report.Bytes
                            Reused = false
                        }
            })

    let private build<'R
        when 'R :> IInstallFiles and 'R :> IFs and 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (spec: InstallSpec)
        (fetched: FetchedArchive)
        : Eff<'R, InstallerCommand list> =
        eff {
            let result =
                match spec.Method with
                | Archive archive ->
                    let staging = InstallRoot.staging settings.Root spec.Key
                    let final = InstallRoot.installDir settings.Root spec.Key

                    withLock
                        settings
                        (InstallRoot.keyLock settings.Root spec.Key.Name)
                        (eff {
                            match! inspectDir final spec.Key with
                            | Some info -> return Ok info
                            | None ->
                                // Whatever a crashed attempt left in staging goes first.
                                let! cleared =
                                    effResult {
                                        do! io (InstallFiles.deleteTree staging)
                                        do! directory (Path.Combine(settings.Root, ".staging"))
                                    }

                                match cleared with
                                | Error e -> return Error e
                                | Ok() ->
                                    let! staged = stage settings spec archive fetched staging

                                    let! activated =
                                        match staged with
                                        | Ok report -> activate settings spec staging report
                                        | Error e -> EffResult.error e

                                    match activated with
                                    | Ok info ->
                                        let! _ = InstallFiles.deleteTree fetched.Path
                                        return Ok info
                                    | Error e ->
                                        // A failed install leaves nothing behind (its archive included: it
                                        // would fail the same way); the active installs are untouched.
                                        let! _ = InstallFiles.deleteTree staging
                                        let! _ = InstallFiles.deleteTree fetched.Path
                                        return Error e
                        })
                | other ->
                    EffResult.error (
                        InstallFailure.Unsupported $"the {other.Kind} install method is not implemented yet (T211)"
                    )

            let! result = result
            return [ InstallerCommand.Built(spec.Key, result) ]
        }

    /// The installs other installers' roots files name, or `None` if one cannot be read (then
    /// nothing is pruned).
    let private allRoots<'R when 'R :> IFs and 'R :> IScope>
        (settings: InstallerSettings<'R>)
        : Eff<'R, Set<string> option> =
        eff {
            match! Fs.list (InstallRoot.roots settings.Root) with
            | Error(NotFound _) -> return Some Set.empty
            | Error _ -> return None
            | Ok entries ->
                let mutable names = Some Set.empty

                for entry in entries do
                    if names.IsSome && entry.Name.EndsWith(".json", StringComparison.Ordinal) then
                        match! Fs.readFile entry.Path with
                        | Ok bytes ->
                            match Codec.decodeText (Codec.list Codec.string) (utf8.GetString bytes) with
                            | Ok listed -> names <- names |> Option.map (fun set -> set + Set.ofList listed)
                            | Error _ -> names <- None
                        | Error _ -> names <- None

                return names
        }

    /// Deletes what crashed installers left in a working directory, if no one holds its lock.
    let private sweepLeftovers<'R when 'R :> IInstallFiles and 'R :> IFs and 'R :> IScope>
        (settings: InstallerSettings<'R>)
        (dir: string)
        : Eff<'R, unit> =
        eff {
            match! Fs.list dir with
            | Error _ -> ()
            | Ok entries ->
                for entry in entries do
                    let name =
                        if entry.Name.EndsWith(".partial", StringComparison.Ordinal) then
                            entry.Name.Substring(0, entry.Name.Length - ".partial".Length)
                        else
                            entry.Name

                    match! InstallFiles.tryLock (InstallRoot.keyLock settings.Root name) with
                    | Ok(Some taken) ->
                        let! _ = InstallFiles.deleteTree entry.Path
                        do! InstallFiles.release taken
                    | _ -> ()
        }

    let private sweep<'R when 'R :> IInstallFiles and 'R :> IFs and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (round: int)
        : Eff<'R, InstallerCommand list> =
        eff {
            let root = settings.Root

            let! swept =
                withLock
                    settings
                    (InstallRoot.rootLock root)
                    (eff {
                        let removed = ResizeArray<string>()

                        match! allRoots settings with
                        | None -> ()
                        | Some used ->
                            match! Fs.list root with
                            | Error _ -> ()
                            | Ok components ->
                                for comp in components do
                                    if comp.Kind = EntryKind.Directory && not (comp.Name.StartsWith '.') then
                                        match! Fs.list comp.Path with
                                        | Error _ -> ()
                                        | Ok versions ->
                                            for version in versions do
                                                let name = comp.Name + "/" + version.Name

                                                if not (used.Contains name) then
                                                    match!
                                                        discard settings version.Path (comp.Name + "@" + version.Name)
                                                    with
                                                    | Ok() -> removed.Add name
                                                    | Error _ -> ()

                                            // An empty component directory goes too; a busy one stays.
                                            let! _ = Fs.delete comp.Path
                                            ()

                        do! sweepLeftovers settings (Path.Combine(root, ".staging"))
                        do! sweepLeftovers settings (Path.Combine(root, ".downloads"))
                        let! _ = InstallFiles.deleteTree (InstallRoot.trash root)
                        return Ok(List.ofSeq removed)
                    })

            match swept with
            | Ok removed -> return [ InstallerCommand.Swept(round, removed) ]
            | Error _ -> return [ InstallerCommand.Swept(round, []) ]
        }

    /// Runs an intent.
    let interpret<'R
        when 'R :> IInstallFiles
        and 'R :> IFs
        and 'R :> IFetch
        and 'R :> IShell
        and 'R :> IClock
        and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (intent: InstallerIntent)
        : Eff<'R, InstallerCommand list> =
        match intent with
        | InstallerIntent.Publish keys -> publish settings keys
        | InstallerIntent.Inspect spec -> inspect settings spec
        | InstallerIntent.Ask request -> ask settings request
        | InstallerIntent.Fetch spec -> fetch settings spec
        | InstallerIntent.Build(spec, archive) -> build settings spec archive
        | InstallerIntent.Sweep round -> sweep settings round
