namespace Fx.Host

open System
open System.IO
open System.Text
open Fx.Contracts
open Fx.Effects
open Fx.Components

/// A project the user trusts, and when they said so.
type TrustedProject =
    {
        /// The project's directory: absolute, normalized, without a trailing separator.
        Path: string
        /// Unix microseconds.
        At: int64
    }

    override this.ToString() = this.Path

/// The projects the user trusts on this host (spec §16.5, §18: nothing project-local runs before the
/// project is trusted). Kept host-local, in the data directory, never in the project: a cloned
/// repository cannot trust itself. A directory is trusted when it, or a directory above it, was
/// trusted.
type TrustStore =
    {
        Projects: TrustedProject list
    }

    override this.ToString() =
        $"TrustStore({this.Projects.Length} projects)"

[<RequireQualifiedAccess>]
module TrustStore =
    let empty: TrustStore = { Projects = [] }

    let private project: Codec<TrustedProject> =
        Codec.record {
            let! path = Codec.field "path" (fun (p: TrustedProject) -> p.Path) Codec.string
            and! at = Codec.field "at" (fun (p: TrustedProject) -> p.At) Codec.int64
            return { Path = path; At = at }
        }

    /// `{"projects": [{"path", "at"}]}`.
    let codec: Codec<TrustStore> =
        Codec.record {
            let! projects =
                Codec.field "projects" (fun (s: TrustStore) -> s.Projects) (Codec.list project)

            return { Projects = projects }
        }

    /// Whether paths compare without case on this OS (Windows and macOS by default).
    let ignoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()

    let private comparison (ignoreCase: bool) =
        if ignoreCase then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    /// A directory as the store keeps it: normalized against nothing (it must be absolute), without a
    /// trailing separator (a root keeps its own).
    let normalize (path: string) : Result<string, string> =
        if not (Path.IsPathFullyQualified path) then
            Error $"not an absolute path: {path}"
        else
            Ok(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, path)))

    /// Whether `dir` is `root` or below it.
    let private covers (ignoreCase: bool) (root: string) (dir: string) : bool =
        let comparison = comparison ignoreCase

        if String.Equals(root, dir, comparison) then
            true
        else
            let prefix =
                if Path.EndsInDirectorySeparator root then
                    root
                else
                    root + string Path.DirectorySeparatorChar

            dir.StartsWith(prefix, comparison)

    /// The trusted project that covers a directory, if any.
    let find (ignoreCase: bool) (dir: string) (store: TrustStore) : TrustedProject option =
        match normalize dir with
        | Error _ -> None
        | Ok dir -> store.Projects |> List.tryFind (fun p -> covers ignoreCase p.Path dir)

    let isTrusted (ignoreCase: bool) (dir: string) (store: TrustStore) : bool = (find ignoreCase dir store).IsSome

    /// Trusts a directory (and what is below it). Trusting it again keeps the first time.
    let add (ignoreCase: bool) (dir: string) (at: int64) (store: TrustStore) : Result<TrustStore, string> =
        normalize dir
        |> Result.map (fun dir ->
            if
                store.Projects
                |> List.exists (fun p -> String.Equals(p.Path, dir, comparison ignoreCase))
            then
                store
            else
                {
                    Projects = store.Projects @ [ { Path = dir; At = at } ]
                })

    /// Stops trusting a directory trusted exactly (not one below a trusted directory).
    let remove (ignoreCase: bool) (dir: string) (store: TrustStore) : Result<TrustStore * bool, string> =
        normalize dir
        |> Result.map (fun dir ->
            let kept =
                store.Projects
                |> List.filter (fun p -> not (String.Equals(p.Path, dir, comparison ignoreCase)))

            { Projects = kept }, kept.Length <> store.Projects.Length)

    /// Reads the store: empty if there is none.
    let read<'R when 'R :> IFsRead and 'R :> IScope> (path: string) : Eff<'R, Result<TrustStore, string>> =
        eff {
            match! Fs.readText path with
            | Error(FsError.NotFound _) -> return Ok empty
            | Error e -> return Error(string e)
            | Ok text ->
                match Codec.decodeText codec text with
                | Ok store -> return Ok store
                | Error issues -> return Error($"{path}: " + Issue.renderAtMost 5 issues)
        }

    /// Writes the store, replacing the file whole.
    let write<'R when 'R :> IInstallFiles and 'R :> IScope>
        (path: string)
        (store: TrustStore)
        : Eff<'R, Result<unit, string>> =
        let text = Json.serializeIndented (Codec.encode codec store) + "\n"

        InstallFiles.replaceFile path (UTF8Encoding(false).GetBytes text)
        |> Eff.map (Result.mapError string)
