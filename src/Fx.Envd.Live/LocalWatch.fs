namespace Fx.Envd.Live

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Envd

/// Watching directories on this machine with `FileSystemWatcher` (inotify on Linux, FSEvents on macOS,
/// ReadDirectoryChangesW on Windows). Events go into a `ChangeQueue`, which debounces and coalesces
/// them; a full OS buffer or queue becomes an overflow (rescan). The only file that watches files.
module LocalWatch =
    /// Pending changes per watch before they count as an overflow.
    let private capacity = 16 * 1024

    let watch (clock: Clock) (spec: WatchSpec) (_: CancellationToken) : Task<Result<Watcher, FsError>> =
        let root = spec.Root

        if String.IsNullOrEmpty root || not (Path.IsPathFullyQualified root) then
            Task.FromResult(Error(InvalidPath(root, "not absolute")))
        elif File.Exists root then
            Task.FromResult(Error(NotADirectory root))
        elif not (Directory.Exists root) then
            Task.FromResult(Error(NotFound root))
        else
            let queue = ChangeQueue capacity

            let watcher =
                new FileSystemWatcher(
                    root,
                    IncludeSubdirectories = spec.Recursive,
                    NotifyFilter =
                        (NotifyFilters.FileName
                         ||| NotifyFilters.DirectoryName
                         ||| NotifyFilters.LastWrite
                         ||| NotifyFilters.Size),
                    InternalBufferSize = 64 * 1024
                )

            let add (kind: ChangeKind) (path: string) = queue.Add { Path = path; Kind = kind }

            watcher.Created.Add(fun e -> add ChangeKind.Added e.FullPath)
            watcher.Changed.Add(fun e -> add ChangeKind.Modified e.FullPath)
            watcher.Deleted.Add(fun e -> add ChangeKind.Deleted e.FullPath)

            watcher.Renamed.Add(fun e ->
                add ChangeKind.Deleted e.OldFullPath
                add ChangeKind.Added e.FullPath)

            watcher.Error.Add(fun _ -> queue.Overflowed())
            watcher.EnableRaisingEvents <- true
            let closed = ref 0

            Task.FromResult(
                Ok
                    {
                        Next = fun cancellation -> queue.Next(clock, spec.Debounce, cancellation)
                        Close =
                            fun () ->
                                if Interlocked.Exchange(&closed.contents, 1) = 0 then
                                    watcher.EnableRaisingEvents <- false
                                    watcher.Dispose()
                                    queue.Complete()

                                Task.FromResult()
                    }
            )
