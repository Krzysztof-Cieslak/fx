namespace Fx.Components

open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Sdk

/// Why a download failed.
type FetchError =
    /// The server answered with an error status.
    | HttpStatus of url: string * status: int
    /// The connection failed or broke off.
    | Network of url: string * reason: string
    /// The download was larger than allowed.
    | TooLarge of url: string * limit: int64
    /// Writing the download failed.
    | WriteFailed of path: string * reason: string

    /// Whether trying again may help: network failures, server errors and rate limits.
    member this.Transient =
        match this with
        | Network _ -> true
        | HttpStatus(_, status) -> status >= 500 || status = 429 || status = 408
        | TooLarge _
        | WriteFailed _ -> false

    override this.ToString() =
        match this with
        | HttpStatus(url, status) -> $"GET {url}: HTTP {status}"
        | Network(url, reason) -> $"GET {url}: {reason}"
        | TooLarge(url, limit) -> $"GET {url}: larger than {limit} bytes"
        | WriteFailed(path, reason) -> $"could not write {path}: {reason}"

/// A download to a file.
type DownloadRequest =
    {
        Url: string
        /// Where to write it (absolute). Replaced if it exists; its directory must exist.
        Destination: string
        MaxBytes: int64
    }

    override this.ToString() = $"{this.Url} -> {this.Destination}"

/// What was downloaded: its size and the SHA-256 of the bytes written.
type FileHash =
    {
        Size: int64
        Digest: Digest
    }

    override this.ToString() = $"{this.Size} bytes, {this.Digest}"

/// Downloading files (spec §6.1 "fetch"). A capability, so that tests serve archives from memory
/// and a host can later push them into an environment (`host-push`, T503).
///
/// Interpreters: `HttpFetch` (Fx.Components.Live), `FakeFetch` (Fx.Components.Fakes).
type Fetch =
    {
        /// Downloads a URL to a file, hashing what it writes. Redirects are followed; a partial file
        /// may be left behind on failure.
        Download: DownloadRequest -> CancellationToken -> Task<Result<FileHash, FetchError>>
    }

    override _.ToString() = "Fetch"

type IFetch =
    abstract Fetch: Fetch

module Fetch =
    let download<'R when 'R :> IFetch and 'R :> IScope>
        (request: DownloadRequest)
        : Eff<'R, Result<FileHash, FetchError>> =
        fun env -> env.Fetch.Download request env.Scope.Cancellation

/// An exclusive lock that other processes see, held until released (or until the process exits).
type FileLock =
    {
        Release: unit -> Task<unit>
    }

    override _.ToString() = "FileLock"

/// Unpacking an archive file into a new directory.
type ExtractRequest =
    {
        Archive: string
        /// A directory that does not exist yet; it is created.
        Destination: string
        Options: ExtractOptions
    }

    override this.ToString() =
        $"{this.Archive} -> {this.Destination} ({this.Options})"

/// The file operations an installer needs beyond `Fs` (spec §6.1): streamed hashing, safe
/// unpacking (`Archive`), deleting trees, atomic replacement, and locks between processes.
///
/// Interpreters: `LocalInstallFiles` (Fx.Components.Live), `InMemoryInstallFiles`
/// (Fx.Components.Fakes, over any `Fs`).
type InstallFiles =
    {
        /// The size and SHA-256 of a file, read as a stream.
        Hash: string -> CancellationToken -> Task<Result<FileHash, FsError>>
        Extract: ExtractRequest -> CancellationToken -> Task<Result<ExtractReport, ArchiveError>>
        /// Deletes a file, a link or a directory tree, never following links. Nothing there is fine.
        DeleteTree: string -> CancellationToken -> Task<Result<unit, FsError>>
        /// Writes a file atomically: a reader sees the old content or the new, never a part.
        ReplaceFile: string -> byte array -> CancellationToken -> Task<Result<unit, FsError>>
        /// Takes the lock named by a file (created with its directory if missing) if no one holds it.
        TryLock: string -> CancellationToken -> Task<Result<FileLock option, FsError>>
    }

    override _.ToString() = "InstallFiles"

type IInstallFiles =
    abstract InstallFiles: InstallFiles

module InstallFiles =
    let hash<'R when 'R :> IInstallFiles and 'R :> IScope> (path: string) : Eff<'R, Result<FileHash, FsError>> =
        fun env -> env.InstallFiles.Hash path env.Scope.Cancellation

    let extract<'R when 'R :> IInstallFiles and 'R :> IScope>
        (request: ExtractRequest)
        : Eff<'R, Result<ExtractReport, ArchiveError>> =
        fun env -> env.InstallFiles.Extract request env.Scope.Cancellation

    let deleteTree<'R when 'R :> IInstallFiles and 'R :> IScope> (path: string) : Eff<'R, Result<unit, FsError>> =
        fun env -> env.InstallFiles.DeleteTree path env.Scope.Cancellation

    let replaceFile<'R when 'R :> IInstallFiles and 'R :> IScope>
        (path: string)
        (content: byte array)
        : Eff<'R, Result<unit, FsError>> =
        fun env -> env.InstallFiles.ReplaceFile path content env.Scope.Cancellation

    let tryLock<'R when 'R :> IInstallFiles and 'R :> IScope>
        (path: string)
        : Eff<'R, Result<FileLock option, FsError>> =
        fun env -> env.InstallFiles.TryLock path env.Scope.Cancellation

    let release (held: FileLock) : Eff<'R, unit> = fun _ -> held.Release()
