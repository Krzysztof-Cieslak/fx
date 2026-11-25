/// The built-in credential stores: the conformance suite on each (over fakes everywhere, over this
/// machine's private files everywhere, and over its keyring on Windows and where CI provides one with
/// `KEYRING_TESTS=1`), and what each does of its own.
module Fx.Auth.Tests.StoreTests

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Keyring
open Fx.Sdk
open Fx.Ext.StoreEphemeral
open Fx.Ext.StoreFile
open Fx.Ext.StoreKeyring
open Fx.Auth.Tests.Support
open Fx.Auth.Tests.Conformance

let private none = CancellationToken.None

let private variable (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

/// The live keyring runs on Windows always, elsewhere when CI prepared one.
let private liveKeyring =
    OperatingSystem.IsWindows() || variable "KEYRING_TESTS" = Some "1"

let private tempRoot () =
    Path.Combine(Path.GetTempPath(), Product.Name + "-auth-tests-" + Guid.NewGuid().ToString("N"), "credentials")

[<Tests>]
let conformance =
    let memoryKeyring = InMemoryKeyring()
    let memoryFiles = InMemoryPrivateFiles()
    let liveRoot = tempRoot ()
    let liveFiles = LocalPrivateFiles.create liveRoot Product.Name

    testList
        "credential stores"
        [
            CredentialStoreConformance.tests
                "ephemeral"
                {
                    Open = fun () -> StoreEphemeral.create ()
                    Persistent = false
                }
            CredentialStoreConformance.tests
                "file over in-memory private files"
                {
                    Open = fun () -> StoreFile.create memoryFiles.PrivateFiles
                    Persistent = true
                }
            CredentialStoreConformance.tests
                "file over this machine's private files"
                {
                    Open = fun () -> StoreFile.create liveFiles
                    Persistent = true
                }
            CredentialStoreConformance.tests
                "keyring over the in-memory keyring"
                {
                    Open = fun () -> StoreKeyring.create memoryKeyring.Keyring
                    Persistent = true
                }
            CredentialStoreConformance.tests
                "keyring over a Windows-sized in-memory keyring"
                {
                    Open = fun () -> StoreKeyring.create (InMemoryKeyring 2560).Keyring
                    Persistent = false
                }
            if liveKeyring then
                let facts = LocalHostFacts.read ()

                let keyring =
                    LocalKeyring.create
                        facts.Os
                        { LocalKeyring.settings facts with
                            Prompts = Prompts.Never
                        }

                CredentialStoreConformance.tests
                    "keyring over this machine's keyring"
                    {
                        Open = fun () -> StoreKeyring.create keyring
                        Persistent = true
                    }
        ]

let private storedText (credential: StoredCredential) = StoredCredential.toText credential

[<Tests>]
let tests =
    testList
        "stores"
        [
            test "the file is canonical JSON with a version, and reads back" {
                let credentials =
                    Map.ofList
                        [
                            "openai", StoredCredential.create "api_key" (SecretText "sk-o")
                            "anthropic",
                            { StoredCredential.create "api_key" (SecretText "sk-a") with
                                Meta = Map.ofList [ "account", "a" ]
                            }
                        ]

                let bytes = StoreFile.render credentials

                Expect.equal
                    (Encoding.UTF8.GetString bytes)
                    """{"credentials":{"anthropic":{"meta":{"account":"a"},"method":"api_key","secret":"sk-a"},"openai":{"meta":{},"method":"api_key","secret":"sk-o"}},"version":1}"""
                    "canonical"

                match StoreFile.parse bytes with
                | Ok parsed ->
                    Expect.equal
                        (parsed |> Map.map (fun _ c -> storedText c))
                        (credentials |> Map.map (fun _ c -> storedText c))
                        "read back"
                | Error e -> failtest e
            }

            test "a damaged file is reported and never overwritten" {
                let files = InMemoryPrivateFiles()
                files.Set StoreFile.FileName "{not json"B
                let store = StoreFile.create files.PrivateFiles

                match sync (store.Read (credentialId "anthropic") none) with
                | Error(StoreError.Corrupt reason) -> Expect.stringContains reason StoreFile.FileName "names the file"
                | other -> failtest $"expected Corrupt, got {other}"

                match sync (store.Probe none) with
                | StoreStatus.Unavailable _ -> ()
                | other -> failtest $"expected Unavailable, got {other}"

                Expect.isError
                    (sync (store.Write (credentialId "x") (StoredCredential.ofSecret (SecretText "x")) none))
                    "refuses to write"

                Expect.equal files.Files[StoreFile.FileName] "{not json"B "the damaged file is kept"

                for bad in
                    [
                        """{"version":2,"credentials":{}}"""
                        """{"version":1,"credentials":{"Upper":{"method":"secret","secret":"x"}}}"""
                    ] do
                    files.Set StoreFile.FileName (Encoding.UTF8.GetBytes bad)
                    Expect.isError (sync (store.List none)) bad
            }

            test "a store's failure to write is the store's error" {
                let files = InMemoryPrivateFiles()
                let store = StoreFile.create files.PrivateFiles
                files.Failure <- Some(FsError.AccessDenied StoreFile.FileName)

                match sync (store.Write (credentialId "a") (StoredCredential.ofSecret (SecretText "x")) none) with
                | Error(StoreError.Failed reason) -> Expect.stringContains reason "access denied" "reason"
                | other -> failtest $"expected Failed, got {other}"
            }

            test "the file is only the user's on this machine, and sealed with DPAPI on Windows" {
                let root = tempRoot ()
                let files = LocalPrivateFiles.create root Product.Name
                let store = StoreFile.create files
                let secret = "sk-ant-sealed-" + Guid.NewGuid().ToString("N")

                match
                    sync (
                        store.Write
                            (credentialId "anthropic")
                            (StoredCredential.create "api_key" (SecretText secret))
                            none
                    )
                with
                | Ok() -> ()
                | Error e -> failtest (string e)

                let path = Path.Combine(root, StoreFile.FileName)
                Expect.isTrue (LocalPrivateFiles.isOwnerOnly path) "owner-only"
                let onDisk = Encoding.UTF8.GetString(File.ReadAllBytes path)

                if OperatingSystem.IsWindows() then
                    Expect.equal store.Protection "dpapi" "protection"
                    Expect.isFalse (onDisk.Contains secret) "encrypted at rest"
                else
                    Expect.equal store.Protection "owner-only" "protection"

                    Expect.equal
                        (File.GetUnixFileMode root)
                        (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                        "the directory is 0700"

                Expect.isFalse (File.Exists(path + ".new")) "no staging file is left"
            }

            test "keyring items are the product's service and the credential's id" {
                let keyring = InMemoryKeyring()
                let store = StoreKeyring.create keyring.Keyring
                let credential = StoredCredential.create "api_key" (SecretText "sk-a")
                sync (store.Write (credentialId "anthropic") credential none) |> ignore

                let key =
                    match KeyringKey.tryCreate Product.Name "anthropic" with
                    | Ok key -> key
                    | Error e -> failwith e

                match sync (keyring.Keyring.Read key none) with
                | Ok(Some value) -> Expect.equal (value.Reveal()) (storedText credential) "the credential's JSON"
                | other -> failtest $"expected the item, got {other}"
            }

            test "a value stored in the keyring by hand reads as a plain secret" {
                let keyring = InMemoryKeyring()

                let key =
                    match KeyringKey.tryCreate Product.Name "github-token" with
                    | Ok key -> key
                    | Error e -> failwith e

                sync (keyring.Keyring.Write key (SecretText "ghp_by_hand") none) |> ignore
                let store = StoreKeyring.create keyring.Keyring

                match sync (store.Read (credentialId "github-token") none) with
                | Ok(Some credential) ->
                    Expect.equal credential.Method StoredCredential.PlainSecret "plain"
                    Expect.equal (credential.Secret.Reveal()) "ghp_by_hand" "value"
                | other -> failtest $"expected the secret, got {other}"
            }

            test "keyring states become store states with what to do" {
                let keyring = InMemoryKeyring()
                let store = StoreKeyring.create keyring.Keyring
                keyring.Status <- KeyringStatus.Locked

                match sync (store.Probe none), sync (store.Read (credentialId "a") none) with
                | StoreStatus.Locked hint, Error(StoreError.Locked _) ->
                    Expect.stringContains hint "credentials.store" "hint"
                | other -> failtest $"expected locked, got {other}"

                keyring.Status <- KeyringStatus.Unavailable KeyringUnavailable.NoSecretService

                match sync (store.Probe none) with
                | StoreStatus.Unavailable reason -> Expect.stringContains reason "gnome-keyring" "says how to get one"
                | other -> failtest $"expected unavailable, got {other}"

                for unavailable in
                    [
                        KeyringUnavailable.NoSessionBus
                        KeyringUnavailable.BusUnreachable "refused"
                        KeyringUnavailable.NoSecretService
                        KeyringUnavailable.NoDefaultCollection
                        KeyringUnavailable.NoKeychain
                        KeyringUnavailable.NoLogonSession
                        KeyringUnavailable.NotSupported "plan9"
                    ] do
                    Expect.stringContains (StoreKeyring.reason unavailable) "credentials.store" (string unavailable)
            }

            test "two ephemeral stores share nothing" {
                let a = StoreEphemeral.create ()
                let b = StoreEphemeral.create ()

                sync (a.Write (credentialId "x") (StoredCredential.ofSecret (SecretText "1")) none)
                |> ignore

                Expect.equal (sync (b.Read (credentialId "x") none) |> Result.map Option.isSome) (Ok false) "empty"
            }

            test "credential ids are lowercase names, and secret names map to them" {
                for good in [ "anthropic"; "a/b.c_d-e"; "0"; String('a', 128) ] do
                    Expect.isOk (CredentialId.tryCreate good) good

                for bad in [ ""; "Anthropic"; "-a"; "a b"; "a:b"; String('a', 129) ] do
                    Expect.isError (CredentialId.tryCreate bad) bad

                Expect.equal (CredentialId.ofSecret (secretRef "secret:GitHub/Token")).Name "github/token" "lowercase"
            }
        ]
