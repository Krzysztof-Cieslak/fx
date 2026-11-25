namespace Fx.Envd

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// A snapshot of the workspace (spec §5: a `SnapshotRef` per step): a commit in envd's shadow git
/// repository. Equal trees mean equal workspaces.
type SnapshotRef =
    {
        /// The commit's id (hex).
        Commit: string
        /// The id (hex) of the commit's tree: the workspace's content.
        Tree: string
    }

    override this.ToString() =
        $"snapshot {this.Commit} (tree {this.Tree})"

/// An expected failure of workspace snapshots.
type SnapshotError =
    /// Snapshots cannot be taken here (e.g. git is missing).
    | Unavailable of reason: string
    /// A git command failed.
    | GitFailed of command: string * exitCode: int * stderr: string

    override this.ToString() =
        match this with
        | Unavailable reason -> $"snapshots are unavailable: {reason}"
        | GitFailed(command, code, stderr) -> $"git {command} failed (exit {code}): {stderr.Trim()}"

/// Workspace snapshots (spec §5): record the workspace, compare two records, and put the workspace
/// (or some paths) back. Paths are relative to the workspace, with `/`.
///
/// Interpreters: `GitSnapshots` (a shadow git repository driven through the shell capability) and
/// the envd proxy (`EnvClient`).
type Snapshots =
    {
        /// Records the workspace as it is now, with a message.
        Take: string -> CancellationToken -> Task<Result<SnapshotRef, SnapshotError>>
        /// The files that differ between two snapshots.
        Diff: SnapshotRef -> SnapshotRef -> CancellationToken -> Task<Result<FileChange list, SnapshotError>>
        /// A unified diff (git's format) between two snapshots, of some paths or of everything.
        Patch:
            SnapshotRef -> SnapshotRef -> string list option -> CancellationToken -> Task<Result<string, SnapshotError>>
        /// Puts the workspace, or some paths of it, back as it was in a snapshot: changed files are
        /// rewritten, files that did not exist then are deleted. Ignored files are left alone.
        Restore: SnapshotRef -> string list option -> CancellationToken -> Task<Result<unit, SnapshotError>>
    }

    override _.ToString() = "Snapshots"

/// An environment with workspace snapshots.
type ISnapshots =
    abstract Snapshots: Snapshots

module Snapshot =
    let take<'R when 'R :> ISnapshots and 'R :> IScope>
        (message: string)
        : Eff<'R, Result<SnapshotRef, SnapshotError>> =
        fun env -> env.Snapshots.Take message env.Scope.Cancellation

    let diff<'R when 'R :> ISnapshots and 'R :> IScope>
        (from: SnapshotRef)
        (until: SnapshotRef)
        : Eff<'R, Result<FileChange list, SnapshotError>> =
        fun env -> env.Snapshots.Diff from until env.Scope.Cancellation

    let patch<'R when 'R :> ISnapshots and 'R :> IScope>
        (from: SnapshotRef)
        (until: SnapshotRef)
        (paths: string list option)
        : Eff<'R, Result<string, SnapshotError>> =
        fun env -> env.Snapshots.Patch from until paths env.Scope.Cancellation

    let restore<'R when 'R :> ISnapshots and 'R :> IScope>
        (snapshot: SnapshotRef)
        (paths: string list option)
        : Eff<'R, Result<unit, SnapshotError>> =
        fun env -> env.Snapshots.Restore snapshot paths env.Scope.Cancellation

    /// Snapshots where there are none (e.g. no git): every call fails with `Unavailable`.
    let unavailable (reason: string) : Snapshots =
        let fail () =
            Task.FromResult(Error(Unavailable reason))

        {
            Take = fun _ _ -> fail ()
            Diff = fun _ _ _ -> fail ()
            Patch = fun _ _ _ _ -> fail ()
            Restore = fun _ _ _ -> fail ()
        }

/// Where a shadow repository lives and what it covers.
type GitSnapshotsOptions =
    {
        /// The git program (`git`, found on `PATH`).
        Git: string
        /// The shadow repository (absolute): a bare git directory envd owns, never the user's `.git`.
        GitDir: string
        /// The workspace (absolute).
        WorkTree: string
        /// Paths below the workspace (relative, `/`) never to record, e.g. envd's own state.
        Exclude: string list
    }

    override this.ToString() =
        $"shadow {this.GitDir} of {this.WorkTree}"

/// Workspace snapshots in a shadow git repository: `git --git-dir=<shadow> --work-tree=<workspace>`,
/// so the user's repository (its index, refs, hooks and config) is never touched. Only the git CLI is
/// used, through the shell capability.
///
/// - A snapshot stages everything (`add -A`, which honours the workspace's `.gitignore` files and the
///   user's global excludes), writes the tree and commits it on `refs/<product>/snapshots`, so it
///   stays reachable. Commits have fixed dates, so equal workspaces after equal histories have equal
///   ids.
/// - Content is recorded byte for byte: `info/attributes` turns off line-ending conversion, filters
///   (e.g. LFS) and working-tree encodings, whatever the workspace's `.gitattributes` say.
/// - Hooks, external diff drivers and text conversions never run.
/// - Commands run one at a time (git's index lock).
module GitSnapshots =
    let private refName = "refs/" + Fx.Product.Name + "/snapshots"

    /// Variables that would point git elsewhere or make it ask questions.
    let private environment: EnvVars =
        Inherit
            [
                "GIT_DIR", None
                "GIT_WORK_TREE", None
                "GIT_INDEX_FILE", None
                "GIT_OBJECT_DIRECTORY", None
                "GIT_ALTERNATE_OBJECT_DIRECTORIES", None
                "GIT_NAMESPACE", None
                "GIT_COMMON_DIR", None
                "GIT_CONFIG_PARAMETERS", None
                "GIT_EXTERNAL_DIFF", None
                "GIT_TERMINAL_PROMPT", Some "0"
                "GIT_OPTIONAL_LOCKS", Some "0"
                "GIT_AUTHOR_NAME", Some Fx.Product.Name
                "GIT_AUTHOR_EMAIL", Some(Fx.Product.Name + "@localhost")
                "GIT_AUTHOR_DATE", Some "@0 +0000"
                "GIT_COMMITTER_NAME", Some Fx.Product.Name
                "GIT_COMMITTER_EMAIL", Some(Fx.Product.Name + "@localhost")
                "GIT_COMMITTER_DATE", Some "@0 +0000"
                "LC_ALL", Some "C"
            ]

    let private attributes = "* -text -filter -ident -working-tree-encoding\n"

    let private utf8 = UTF8Encoding(false)

    /// Snapshots of `options.WorkTree`. The shadow repository is created on first use.
    let create (shell: Shell) (fs: Fs) (options: GitSnapshotsOptions) : Snapshots =
        let lockObject = new SemaphoreSlim(1, 1)
        let mutable initialized = false
        let mutable head: string option = None

        let commandOf (args: string list) (stdin: byte array) : Command =
            {
                Spec =
                    {
                        Program = Exec(options.Git, args)
                        Cwd = Some options.WorkTree
                        Env = environment
                    }
                Stdin = stdin
            }

        let globalArgs =
            [
                "--git-dir=" + options.GitDir
                "--work-tree=" + options.WorkTree
                "--literal-pathspecs"
                "-c"
                "core.autocrlf=false"
                "-c"
                "core.safecrlf=false"
                "-c"
                "core.quotepath=false"
                "-c"
                "core.longpaths=true"
                "-c"
                "core.fsmonitor=false"
                "-c"
                "core.hooksPath=" + Path.Combine(options.GitDir, "no-hooks")
                "-c"
                "safe.directory=*"
                "-c"
                "gc.autoDetach=false"
                "-c"
                "commit.gpgSign=false"
            ]

        /// Runs git; its standard output, or why it failed.
        let git
            (args: string list)
            (stdin: byte array)
            (cancellation: CancellationToken)
            : Task<Result<string, SnapshotError>> =
            task {
                match! shell.Run (commandOf (globalArgs @ args) stdin) cancellation with
                | Error e -> return Error(Unavailable(string e))
                | Ok output when output.ExitCode = 0 -> return Ok(Output.stdoutText output)
                | Ok output ->
                    let name = List.tryHead args |> Option.defaultValue ""
                    return Error(GitFailed(name, output.ExitCode, Output.stderrText output))
            }

        let initialize (cancellation: CancellationToken) : Task<Result<unit, SnapshotError>> =
            if initialized then
                Task.FromResult(Ok())
            else
                task {
                    let init =
                        commandOf [ "init"; "--bare"; "--quiet"; "--template="; options.GitDir ] [||]

                    match! shell.Run init cancellation with
                    | Error e -> return Error(Unavailable(string e))
                    | Ok output when output.ExitCode <> 0 ->
                        return Error(GitFailed("init", output.ExitCode, Output.stderrText output))
                    | Ok _ ->
                        let info = Path.Combine(options.GitDir, "info")

                        let exclude =
                            options.Exclude
                            |> List.map (fun p -> "/" + p.TrimStart '/')
                            |> String.concat "\n"

                        let! created = fs.CreateDirectory info cancellation

                        let! written =
                            match created with
                            | Error e -> Task.FromResult(Error e)
                            | Ok() ->
                                task {
                                    match!
                                        fs.WriteFile
                                            (Path.Combine(info, "attributes"))
                                            (utf8.GetBytes attributes)
                                            cancellation
                                    with
                                    | Error e -> return Error e
                                    | Ok() ->
                                        return!
                                            fs.WriteFile
                                                (Path.Combine(info, "exclude"))
                                                (utf8.GetBytes(exclude + "\n"))
                                                cancellation
                                }

                        match written with
                        | Error e -> return Error(Unavailable(string e))
                        | Ok() ->
                            match! git [ "rev-parse"; "--verify"; "--quiet"; refName ] [||] cancellation with
                            | Ok commit -> head <- Some(commit.Trim())
                            | Error _ -> head <- None

                            initialized <- true
                            return Ok()
                }

        /// Runs `body` alone, once the repository exists.
        let exclusive (cancellation: CancellationToken) (body: unit -> Task<Result<'T, SnapshotError>>) =
            task {
                do! lockObject.WaitAsync cancellation

                try
                    match! initialize cancellation with
                    | Error e -> return Error e
                    | Ok() -> return! body ()
                finally
                    lockObject.Release() |> ignore
            }

        let stageAll (cancellation: CancellationToken) =
            git [ "add"; "--all"; "--ignore-errors"; "--"; "." ] [||] cancellation

        let take (message: string) (cancellation: CancellationToken) =
            exclusive cancellation (fun () ->
                task {
                    match! stageAll cancellation with
                    | Error e -> return Error e
                    | Ok _ ->
                        match! git [ "write-tree" ] [||] cancellation with
                        | Error e -> return Error e
                        | Ok tree ->
                            let tree = tree.Trim()

                            let parent =
                                match head with
                                | Some commit -> [ "-p"; commit ]
                                | None -> []

                            let message =
                                if String.IsNullOrWhiteSpace message then
                                    "snapshot"
                                else
                                    message

                            match!
                                git
                                    ([ "commit-tree"; tree ] @ parent @ [ "-F"; "-" ])
                                    (utf8.GetBytes message)
                                    cancellation
                            with
                            | Error e -> return Error e
                            | Ok commit ->
                                let commit = commit.Trim()

                                match! git [ "update-ref"; refName; commit ] [||] cancellation with
                                | Error e -> return Error e
                                | Ok _ ->
                                    head <- Some commit
                                    return Ok { Commit = commit; Tree = tree }
                })

        let parseChanges (output: string) : FileChange list =
            let fields = output.Split('\000', StringSplitOptions.RemoveEmptyEntries)

            [
                for i in 0..2 .. fields.Length - 2 do
                    let kind =
                        match fields[i] with
                        | "A" -> ChangeKind.Added
                        | "D" -> ChangeKind.Deleted
                        | _ -> ChangeKind.Modified

                    { Path = fields[i + 1]; Kind = kind }
            ]

        let diff (from: SnapshotRef) (until: SnapshotRef) (cancellation: CancellationToken) =
            exclusive cancellation (fun () ->
                task {
                    match!
                        git
                            [
                                "diff-tree"
                                "-r"
                                "-z"
                                "--no-renames"
                                "--name-status"
                                from.Tree
                                until.Tree
                            ]
                            [||]
                            cancellation
                    with
                    | Error e -> return Error e
                    | Ok output -> return Ok(parseChanges output)
                })

        let pathspecArgs (paths: string list option) =
            match paths with
            | None
            | Some [] -> [ "--"; "." ], [||]
            | Some paths ->
                [ "--pathspec-from-file=-"; "--pathspec-file-nul" ], utf8.GetBytes(String.Join("\000", paths) + "\000")

        let patch
            (from: SnapshotRef)
            (until: SnapshotRef)
            (paths: string list option)
            (cancellation: CancellationToken)
            =
            exclusive cancellation (fun () ->
                let args, stdin =
                    match paths with
                    | None
                    | Some [] -> [], [||]
                    | Some paths -> [ "--" ] @ paths, [||]

                git
                    ([
                        "diff"
                        "--no-renames"
                        "--no-color"
                        "--no-ext-diff"
                        "--no-textconv"
                        from.Tree
                        until.Tree
                     ]
                     @ args)
                    stdin
                    cancellation)

        let restore (snapshot: SnapshotRef) (paths: string list option) (cancellation: CancellationToken) =
            exclusive cancellation (fun () ->
                task {
                    match! stageAll cancellation with
                    | Error e -> return Error e
                    | Ok _ ->
                        let args, stdin = pathspecArgs paths

                        match!
                            git
                                ([ "restore"; "--source=" + snapshot.Commit; "--staged"; "--worktree" ] @ args)
                                stdin
                                cancellation
                        with
                        | Error e -> return Error e
                        | Ok _ -> return Ok()
                })

        {
            Take = take
            Diff = diff
            Patch = patch
            Restore = restore
        }
