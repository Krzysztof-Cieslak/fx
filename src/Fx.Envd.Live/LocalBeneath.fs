namespace Fx.Envd.Live

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd

/// envd's file capabilities beneath roots (T113b, `EnvdServices.Beneath`): a path under one of the
/// roots is resolved beneath the outermost of them without following symbolic links
/// (`BeneathPosix` on Linux and macOS, `BeneathWindows` on Windows); the root itself and paths outside
/// every root go to `LocalFs` and `LocalFsOps` as before. The roots are where a sandboxed process can
/// write, hence make links; above them nothing it can change decides where a path leads.
module LocalBeneath =
    let private comparison =
        if OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let private separators =
        [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]

    let private normalize (path: string) =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, path))

    let private under (root: string) (path: string) =
        let prefix =
            if Path.EndsInDirectorySeparator root then
                root
            else
                root + string Path.DirectorySeparatorChar

        path.Length > prefix.Length && path.StartsWith(prefix, comparison)

    /// The target of an absolute path beneath the outermost root that contains it (not the root
    /// itself); `None` for any other path.
    let internal targetWith (fast: bool) (roots: string list) (path: string) : BeneathTarget option =
        if String.IsNullOrEmpty path || not (Path.IsPathFullyQualified path) then
            None
        else
            let full = normalize path

            roots
            |> List.filter Path.IsPathFullyQualified
            |> List.map normalize
            |> List.filter (fun root -> under root full)
            |> List.sortBy (fun root -> root.Length)
            |> List.tryHead
            |> Option.map (fun root ->
                let start =
                    if Path.EndsInDirectorySeparator root then
                        root.Length
                    else
                        root.Length + 1

                let components =
                    full.Substring(start).Split(separators, StringSplitOptions.RemoveEmptyEntries)
                    |> List.ofArray

                {
                    Root = root
                    Directories = List.take (components.Length - 1) components
                    Name = List.last components
                    Path = full
                    Fast = fast
                })

    let internal target = targetWith true

    let private windows = OperatingSystem.IsWindows()

    let private sync (work: unit -> Result<'T, FsError>) : Task<Result<'T, FsError>> = Task.FromResult(work ())

    let private read (target: BeneathTarget) (offset: int64) (count: int option) (cancellation: CancellationToken) =
        if windows then
            BeneathWindows.read target offset count cancellation
        else
            sync (fun () -> BeneathPosix.read target offset count)

    /// The file capabilities with paths under `roots` resolved beneath them, opening directories with
    /// one call where the OS can (`fast`) or one per component; `ids` names temporary files and
    /// `clock` debounces watches.
    let filesWith (fast: bool) (clock: Clock) (ids: Ids) (roots: string list) : EnvdFiles =
        let target = targetWith fast

        let outside = LocalFsOps.create clock ids
        let random () = ids.RandomBytes 6

        let at
            (path: string)
            (beneath: BeneathTarget -> Task<Result<'T, FsError>>)
            (otherwise: unit -> Task<Result<'T, FsError>>)
            =
            match target roots path with
            | Some target -> beneath target
            | None -> otherwise ()

        let fsRead: FsRead =
            {
                ReadFile =
                    fun path ct ->
                        at
                            path
                            (fun t ->
                                task {
                                    let! read = read t 0L None ct
                                    return read |> Result.map fst
                                })
                            (fun () -> LocalFs.read.ReadFile path ct)
                Stat =
                    fun path ct ->
                        at
                            path
                            (fun t ->
                                if windows then
                                    BeneathWindows.stat t ct
                                else
                                    sync (fun () -> BeneathPosix.stat t))
                            (fun () -> LocalFs.read.Stat path ct)
                List =
                    fun path ct ->
                        at
                            path
                            (fun t ->
                                if windows then
                                    BeneathWindows.list t ct
                                else
                                    sync (fun () -> BeneathPosix.list t))
                            (fun () -> LocalFs.read.List path ct)
            }

        let writeAtomic (path: string) (content: byte array) (ct: CancellationToken) =
            at
                path
                (fun t ->
                    if windows then
                        BeneathWindows.writeAtomic t random content ct
                    else
                        sync (fun () -> BeneathPosix.writeAtomic t random content))
                (fun () -> outside.WriteAtomic path content ct)

        let fs: Fs =
            {
                WriteFile =
                    fun path content ct ->
                        at
                            path
                            (fun t ->
                                if windows then
                                    BeneathWindows.writeFile t content ct
                                else
                                    sync (fun () -> BeneathPosix.writeFile t content))
                            (fun () -> LocalFs.write.WriteFile path content ct)
                CreateDirectory =
                    fun path ct ->
                        at
                            path
                            (fun t ->
                                task {
                                    // The root may not exist yet (an environment's owned directory);
                                    // its own path is trusted, so it is made as anywhere else.
                                    match! LocalFs.write.CreateDirectory t.Root ct with
                                    | Error e -> return Error e
                                    | Ok() ->
                                        if windows then
                                            return! BeneathWindows.createDirectory t ct
                                        else
                                            return BeneathPosix.createDirectory t
                                })
                            (fun () -> LocalFs.write.CreateDirectory path ct)
                Delete =
                    fun path ct ->
                        at
                            path
                            (fun t ->
                                if windows then
                                    BeneathWindows.delete t ct
                                else
                                    sync (fun () -> BeneathPosix.delete t))
                            (fun () -> LocalFs.write.Delete path ct)
                Move =
                    fun source destination ct ->
                        match target roots source, target roots destination with
                        | Some s, Some d when under s.Path d.Path ->
                            Task.FromResult(Error(InvalidPath(destination, "inside the source")))
                        | Some s, Some d ->
                            if windows then
                                BeneathWindows.move s d ct
                            else
                                sync (fun () -> BeneathPosix.move s d)
                        | None, None -> LocalFs.write.Move source destination ct
                        | _ when not (Path.IsPathFullyQualified source && Path.IsPathFullyQualified destination) ->
                            // LocalFs refuses the relative one.
                            LocalFs.write.Move source destination ct
                        | Some _, None
                        | None, Some _ ->
                            Task.FromResult(
                                Error(
                                    InvalidPath(destination, "a move into or out of a root that paths resolve beneath")
                                )
                            )
            }

        {
            FsRead = fsRead
            Fs = fs
            FsOps =
                {
                    StatMany = FsOpsOver.statMany fsRead
                    ReadRange =
                        fun path offset count ct ->
                            FsOps.checkRange offset count

                            at
                                path
                                (fun t ->
                                    task {
                                        let! read = read t offset (Some count) ct

                                        return
                                            read
                                            |> Result.map (fun (content, size) ->
                                                {
                                                    Offset = offset
                                                    Content = content
                                                    FileSize = size
                                                })
                                    })
                                (fun () -> outside.ReadRange path offset count ct)
                    WriteAtomic = writeAtomic
                    ApplyPatch = FsOpsOver.applyPatch fsRead writeAtomic fs.Delete
                    Search = Search.run fsRead
                    Watch = outside.Watch
                }
        }

    /// The file capabilities with paths under `roots` resolved beneath them (`filesWith true`).
    let files (clock: Clock) (ids: Ids) (roots: string list) : EnvdFiles = filesWith true clock ids roots
