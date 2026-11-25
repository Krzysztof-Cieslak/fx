/// `fx.store.file` (spec §11.4, §16.4): credentials in one file only the user can read, through the
/// host's private files (`IPrivateFiles`: mode `0600` in a `0700` directory on Unix, an owner-only ACL
/// and DPAPI on Windows; written atomically). The store for machines without a usable keyring:
/// containers, servers, SSH sessions (docs/spikes/keychains.md). A native extension built only on
/// the public SDK. See README.md and docs/modules/auth.md.
module Fx.Ext.StoreFile.StoreFile

open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// What `credentials.store` selects.
[<Literal>]
let Kind = "file"

/// The file's name among the host's private files.
[<Literal>]
let FileName = "credentials.json"

/// The largest credential the store keeps, in bytes of its JSON.
[<Literal>]
let MaxCredentialBytes = 65536

/// The file's format version.
[<Literal>]
let Version = 1

let private utf8 = UTF8Encoding(false)

/// The file: `{"version": 1, "credentials": {"<id>": <StoredCredential>, …}}`, canonical JSON.
let fileCodec: Codec<Map<string, StoredCredential>> =
    Codec.record {
        let! _ =
            Codec.field
                "version"
                (fun (_: Map<string, StoredCredential>) -> Version)
                (Codec.int |> Codec.between Version Version)

        and! credentials =
            Codec.field
                "credentials"
                id
                (Codec.stringMap StoredCredential.codec
                 |> Codec.refine (fun credentials ->
                     match
                         credentials
                         |> Map.toList
                         |> List.tryPick (fun (name, _) ->
                             match CredentialId.tryCreate name with
                             | Ok _ -> None
                             | Error e -> Some(name + ": " + e))
                     with
                     | Some problem -> Error problem
                     | None -> Ok()))

        return credentials
    }

/// The file's bytes for these credentials.
let render (credentials: Map<string, StoredCredential>) : byte array =
    utf8.GetBytes(Json.serializeCanonical (fileCodec.Encode credentials))

/// The credentials in a file's bytes, or why they cannot be read.
let parse (bytes: byte array) : Result<Map<string, StoredCredential>, string> =
    let text =
        try
            Ok(UTF8Encoding(false, true).GetString bytes)
        with :? DecoderFallbackException ->
            Error(FileName + " is not UTF-8")

    match text with
    | Error e -> Error e
    | Ok text ->
        match Codec.decodeText fileCodec text with
        | Ok credentials -> Ok credentials
        | Error issues -> Error(FileName + " is not a credentials file: " + Issue.renderAtMost 3 issues)

/// A store over private files. Writes and deletes run one at a time (read, change, write the whole
/// file); one host writes a data directory at a time, so nothing else writes the file.
let create (files: PrivateFiles) : CredentialStore =
    let gate = new SemaphoreSlim(1, 1)

    let load (cancellation: CancellationToken) : Task<Result<Map<string, StoredCredential>, StoreError>> =
        task {
            match! files.Read FileName cancellation with
            | Error e -> return Error(StoreError.Failed(string e))
            | Ok None -> return Ok Map.empty
            | Ok(Some bytes) -> return parse bytes |> Result.mapError StoreError.Corrupt
        }

    let save (credentials: Map<string, StoredCredential>) (cancellation: CancellationToken) =
        task {
            match! files.Write FileName (render credentials) cancellation with
            | Ok() -> return Ok()
            | Error e -> return Error(StoreError.Failed(string e))
        }

    let exclusively (cancellation: CancellationToken) (body: unit -> Task<Result<'T, StoreError>>) =
        task {
            do! gate.WaitAsync cancellation

            try
                return! body ()
            finally
                gate.Release() |> ignore
        }

    {
        Kind = Kind
        Protection = files.Protection
        Probe =
            fun cancellation ->
                task {
                    match! load cancellation with
                    | Ok _ -> return StoreStatus.Ready
                    | Error e -> return StoreStatus.Unavailable(string e)
                }
        Read =
            fun id cancellation ->
                task {
                    match! load cancellation with
                    | Ok credentials -> return Ok(credentials.TryFind id.Name)
                    | Error e -> return Error e
                }
        Write =
            fun id credential cancellation ->
                if utf8.GetByteCount(StoredCredential.toText credential) > MaxCredentialBytes then
                    Task.FromResult(Error(StoreError.TooLarge MaxCredentialBytes))
                else
                    exclusively cancellation (fun () ->
                        task {
                            match! load cancellation with
                            | Error e -> return Error e
                            | Ok credentials -> return! save (credentials.Add(id.Name, credential)) cancellation
                        })
        Delete =
            fun id cancellation ->
                exclusively cancellation (fun () ->
                    task {
                        match! load cancellation with
                        | Error e -> return Error e
                        | Ok credentials when not (credentials.ContainsKey id.Name) -> return Ok false
                        | Ok credentials ->
                            match! save (credentials.Remove id.Name) cancellation with
                            | Ok() -> return Ok true
                            | Error e -> return Error e
                    })
        List =
            fun cancellation ->
                task {
                    match! load cancellation with
                    | Error e -> return Error e
                    | Ok credentials ->
                        return
                            credentials
                            |> Map.toList
                            |> List.choose (fun (name, _) -> CredentialId.tryCreate name |> Result.toOption)
                            |> Ok
                }
    }

let def: CredentialStoreDef =
    {
        Kind = Kind
        Description =
            "Keeps credentials in one file only the user can read (mode 0600; DPAPI and an owner-only ACL on Windows)."
        Open = fun env -> Task.FromResult(create env.PrivateFiles)
    }

/// The extension's id: `fx.store.file`.
let id = Product.extensionId "store.file"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "A credential store in one file only the user can read."
        Contributions = [ Declare.contribution CredentialStores.contributionType Kind ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate = Eff.ret (Activation.create [ Contribution.create CredentialStores.contributionType Kind def ] [])
    }
