namespace Fx.Interpreters

open System
open System.Security.Cryptography
open System.Text
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// Why an interpreter's assets could not be made available.
type AssetError =
    | AssetsFs of FsError
    /// An extracted file does not hold what the binary carries, even after extracting it again.
    | Corrupt of path: string

    override this.ToString() =
        match this with
        | AssetsFs e -> string e
        | Corrupt path -> $"{path} does not hold the interpreter's asset"

/// An interpreter's assets (the kernel's own files, carried in the binary) in the environment's
/// filesystem (spec §16.4: extracted with hash checks). Each set lives in its own directory, named
/// by the interpreter and the assets' digest, so versions never mix and a directory is never
/// rewritten in place: a set is written to a staging directory, checked, then renamed into place.
/// Every use checks the files again, and extracts them anew if one was changed or removed.
[<RequireQualifiedAccess>]
module Assets =
    let private utf8 = UTF8Encoding(false)

    let private join (os: Os) (dir: string) (name: string) =
        let separator = if os = Os.Windows then '\\' else '/'

        if dir.EndsWith separator || dir.EndsWith '/' then
            dir + name
        else
            dir + string separator + name

    /// `<root>/<interpreter id>-<the first 16 hex digits of the assets' digest>`.
    let directory (os: Os) (root: string) (provider: InterpreterProvider) : string =
        let digest = InterpreterProvider.assetsDigest provider
        join os root (provider.Id + "-" + digest.Substring("sha256:".Length, 16))

    let private sha256 (bytes: byte array) = SHA256.HashData bytes

    /// Whether every asset is in `dir` with the content the binary carries.
    let private intact<'R when 'R :> IFsRead and 'R :> IScope>
        (os: Os)
        (dir: string)
        (provider: InterpreterProvider)
        : Eff<'R, bool> =
        eff {
            let mutable ok = true

            for asset in provider.Assets do
                if ok then
                    match! Fs.readFile (join os dir asset.Name) with
                    | Ok bytes ->
                        let expected = sha256 (utf8.GetBytes asset.Content)
                        ok <- (sha256 bytes).AsSpan().SequenceEqual(ReadOnlySpan expected)
                    | Error _ -> ok <- false

            return ok
        }

    /// Removes a directory of files (assets are flat).
    let private remove<'R when 'R :> IFs and 'R :> IScope> (dir: string) : Eff<'R, Result<unit, FsError>> =
        effResult {
            let! entries = Fs.list dir

            for entry in entries do
                do! Fs.delete entry.Path

            do! Fs.delete dir
        }

    let private exists<'R when 'R :> IFsRead and 'R :> IScope> (path: string) : Eff<'R, bool> =
        Fs.stat path
        |> Eff.map (fun result ->
            match result with
            | Ok _ -> true
            | Error _ -> false)

    /// The directory with the interpreter's assets under `root`, extracted if they are missing or
    /// changed. `unique` names this caller's staging directory (e.g. the launch's id), so that
    /// concurrent launches never write into one another's; the first rename wins, and the others
    /// check its result.
    let ensure<'R when 'R :> IFs and 'R :> IScope>
        (os: Os)
        (root: string)
        (unique: string)
        (provider: InterpreterProvider)
        : Eff<'R, Result<string, AssetError>> =
        eff {
            let dir = directory os root provider
            let! ready = intact os dir provider

            if ready then
                return Ok dir
            else
                let staging = dir + "." + unique + ".tmp"

                let! written =
                    effResult {
                        do! Fs.createDirectory staging

                        for asset in provider.Assets do
                            do! Fs.writeFile (join os staging asset.Name) (utf8.GetBytes asset.Content)
                    }

                match written with
                | Error e -> return Error(AssetsFs e)
                | Ok() ->
                    let! staged = intact os staging provider

                    if not staged then
                        return Error(Corrupt staging)
                    else
                        // A damaged set: replace it.
                        let! present = exists dir

                        let! removed = if present then remove dir else EffResult.ok ()

                        match removed with
                        | Error e -> return Error(AssetsFs e)
                        | Ok() ->
                            match! Fs.move staging dir with
                            | Ok() -> return Ok dir
                            | Error moveError ->
                                // Another launch renamed its set into place first.
                                let! _ = remove staging
                                let! theirs = intact os dir provider

                                if theirs then
                                    return Ok dir
                                else
                                    return Error(AssetsFs moveError)
        }
