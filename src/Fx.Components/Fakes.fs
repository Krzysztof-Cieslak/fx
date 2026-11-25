namespace Fx.Components.Fakes

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Fx.Components
open Fx.Effects
open Fx.Sdk

/// Serves downloads from memory and writes them with a filesystem capability (an `InMemoryFs`, or
/// the local one in tests that unpack to disk). A URL it does not serve is HTTP 404. Tests can make
/// the next download of a URL fail, and hold downloads until they release them.
[<Sealed>]
type FakeFetch(write: string -> byte array -> CancellationToken -> Task<Result<unit, FsError>>) =
    let gate = obj ()
    let served = Dictionary<string, byte array>(StringComparer.Ordinal)
    let failures = Dictionary<string, Queue<FetchError>>(StringComparer.Ordinal)
    let holds = Dictionary<string, TaskCompletionSource<unit>>(StringComparer.Ordinal)
    let requests = ResizeArray<string>()

    let download (request: DownloadRequest) (cancellation: CancellationToken) : Task<Result<FileHash, FetchError>> =
        task {
            let failure, hold, content =
                lock gate (fun () ->
                    requests.Add request.Url

                    let failure =
                        match failures.TryGetValue request.Url with
                        | true, queue when queue.Count > 0 -> Some(queue.Dequeue())
                        | _ -> None

                    let hold =
                        match holds.TryGetValue request.Url with
                        | true, source -> Some source.Task
                        | false, _ -> None

                    let content =
                        match served.TryGetValue request.Url with
                        | true, bytes -> Some bytes
                        | false, _ -> None

                    failure, hold, content)

            match hold with
            | Some hold -> do! hold.WaitAsync cancellation
            | None -> ()

            match failure, content with
            | Some failure, _ -> return Error failure
            | None, None -> return Error(FetchError.HttpStatus(request.Url, 404))
            | None, Some bytes when int64 bytes.Length > request.MaxBytes ->
                return Error(FetchError.TooLarge(request.Url, request.MaxBytes))
            | None, Some bytes ->
                match! write request.Destination bytes cancellation with
                | Error e -> return Error(FetchError.WriteFailed(request.Destination, string e))
                | Ok() ->
                    return
                        Ok
                            {
                                Size = int64 bytes.Length
                                Digest = Sha256(Convert.ToHexStringLower(SHA256.HashData bytes))
                            }
        }

    /// Over an `Fs` capability.
    new(fs: Fs) = FakeFetch(fs.WriteFile)

    member val Fetch: Fetch = { Download = download }

    /// Serves these bytes at a URL.
    member _.Serve(url: string, content: byte array) =
        lock gate (fun () -> served[url] <- content)

    /// The next download of the URL fails with this error (queued: call it again for later ones).
    member _.FailNext(url: string, error: FetchError) =
        lock gate (fun () ->
            match failures.TryGetValue url with
            | true, queue -> queue.Enqueue error
            | false, _ -> failures[url] <- Queue [ error ])

    /// Downloads of the URL wait until the returned source completes.
    member _.Hold(url: string) : TaskCompletionSource<unit> =
        lock gate (fun () ->
            let source =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            holds[url] <- source
            source)

    /// Releases held downloads of the URL.
    member _.Release(url: string) =
        lock gate (fun () ->
            match holds.TryGetValue url with
            | true, source ->
                holds.Remove url |> ignore
                source.TrySetResult() |> ignore
            | false, _ -> ())

    /// Every URL requested, in order.
    member _.Requests: string list = lock gate (fun () -> List.ofSeq requests)

    override _.ToString() = "FakeFetch"

/// `InstallFiles` over any `Fs`/`FsRead` (an `InMemoryFs` in tests and `fx test`): unpacking runs
/// the same `Archive` code as the live interpreter, in memory. Files have no modes and links are
/// not created, so it records both for tests to inspect. Locks are held in this object: installers
/// that share a root share one instance.
[<Sealed>]
type InMemoryInstallFiles(fs: Fs, read: FsRead) =
    let gate = obj ()
    let locks = HashSet<string>(StringComparer.Ordinal)
    let executables = HashSet<string>(StringComparer.Ordinal)
    let links = Dictionary<string, string>(StringComparer.Ordinal)

    let under (root: string) (segments: string list) =
        Path.Combine(Array.ofList (root :: segments))

    let hash (path: string) (cancellation: CancellationToken) =
        task {
            match! read.ReadFile path cancellation with
            | Error e -> return Error e
            | Ok bytes ->
                return
                    Ok
                        {
                            Size = int64 bytes.Length
                            Digest = Sha256(Convert.ToHexStringLower(SHA256.HashData bytes))
                        }
        }

    let message (result: Result<unit, FsError>) = result |> Result.mapError string

    let sink (root: string) (cancellation: CancellationToken) : ExtractSink =
        {
            CreateDirectory =
                fun path ->
                    task {
                        let! created = fs.CreateDirectory (under root path) cancellation
                        return message created
                    }
            WriteFile =
                fun path executable content ->
                    task {
                        use buffer = new MemoryStream()
                        do! content.CopyToAsync(buffer, cancellation)
                        let target = under root path

                        if executable then
                            lock gate (fun () -> executables.Add target |> ignore)

                        let! written = fs.WriteFile target (buffer.ToArray()) cancellation
                        return message written
                    }
            CreateSymlink =
                fun path target ->
                    lock gate (fun () -> links[under root path] <- target)
                    Task.FromResult(Ok())
            CopyFile =
                fun path source executable ->
                    task {
                        match! read.ReadFile (under root source) cancellation with
                        | Error e -> return Error(string e)
                        | Ok bytes ->
                            let target = under root path

                            if executable then
                                lock gate (fun () -> executables.Add target |> ignore)

                            let! written = fs.WriteFile target bytes cancellation
                            return message written
                    }
        }

    let extract (request: ExtractRequest) (cancellation: CancellationToken) =
        task {
            match! read.Stat request.Destination cancellation with
            | Ok _ -> return Error(ArchiveError.WriteFailed(request.Destination, "it exists already"))
            | Error(FsError.NotFound _) ->
                match! read.ReadFile request.Archive cancellation with
                | Error e -> return Error(ArchiveError.WriteFailed(request.Archive, string e))
                | Ok bytes ->
                    match! fs.CreateDirectory request.Destination cancellation with
                    | Error e -> return Error(ArchiveError.WriteFailed(request.Destination, string e))
                    | Ok() ->
                        use stream = new MemoryStream(bytes, false)

                        return!
                            Archive.extract request.Options stream (sink request.Destination cancellation) cancellation
            | Error e -> return Error(ArchiveError.WriteFailed(request.Destination, string e))
        }

    let rec deleteTree (path: string) (cancellation: CancellationToken) : Task<Result<unit, FsError>> =
        task {
            match! read.Stat path cancellation with
            | Error(FsError.NotFound _) -> return Ok()
            | Error e -> return Error e
            | Ok entry when entry.Kind = EntryKind.Directory ->
                match! read.List path cancellation with
                | Error e -> return Error e
                | Ok children ->
                    let mutable result = Ok()

                    for child in children do
                        if Result.isOk result then
                            let! deleted = deleteTree child.Path cancellation
                            result <- deleted

                    match result with
                    | Error e -> return Error e
                    | Ok() -> return! fs.Delete path cancellation
            | Ok _ ->
                lock gate (fun () ->
                    executables.Remove path |> ignore
                    links.Remove path |> ignore)

                return! fs.Delete path cancellation
        }

    let tryLock (path: string) (_: CancellationToken) =
        let taken = lock gate (fun () -> locks.Add path)

        if taken then
            Task.FromResult(
                Ok(
                    Some
                        {
                            Release =
                                fun () ->
                                    lock gate (fun () -> locks.Remove path |> ignore)
                                    Task.FromResult()
                        }
                )
            )
        else
            Task.FromResult(Ok None)

    member val InstallFiles: InstallFiles =
        {
            Hash = hash
            Extract = extract
            DeleteTree = deleteTree
            ReplaceFile = fs.WriteFile
            TryLock = tryLock
        }

    /// Files unpacked as executable, by full path.
    member _.Executables: string list =
        lock gate (fun () -> executables |> Seq.sort |> List.ofSeq)

    /// Symbolic links unpacked: full path and target.
    member _.Links: (string * string) list =
        lock gate (fun () -> links |> Seq.map (fun p -> p.Key, p.Value) |> Seq.sortBy fst |> List.ofSeq)

    /// The locks held now.
    member _.Held: string list = lock gate (fun () -> locks |> Seq.sort |> List.ofSeq)

    override _.ToString() = "InMemoryInstallFiles"
