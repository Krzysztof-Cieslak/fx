/// The credential-store conformance suite (spec §11.4): what every `CredentialStore` must do, built-in
/// or a plugin's. `fx.store.ephemeral`, `fx.store.file` (over in-memory and real private files) and
/// `fx.store.keyring` (over the in-memory keyring, and the OS keyring where CI provides one) pass it.
///
/// Every test works under ids of its own and deletes what it wrote, so the suite can run against a
/// person's real keyring, whose store holds other credentials too.
module Fx.Auth.Tests.Conformance.CredentialStoreConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Sdk

/// A store under test.
type Subject =
    {
        /// A store, opened anew on each call over the same backing (a file, a keyring).
        Open: unit -> CredentialStore
        /// Whether a store opened again sees what an earlier one wrote (not so for `ephemeral`).
        Persistent: bool
    }

/// Every wait is bounded, so a hang fails the test instead of the job.
let private sync (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

let private none = CancellationToken.None

let private ok (label: string) (result: Result<'T, StoreError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"{label}: {e}"

let private credentialId (name: string) =
    match CredentialId.tryCreate name with
    | Ok id -> id
    | Error e -> failwith e

/// A prefix no other run uses.
let uniquePrefix () =
    "conformance-" + Guid.NewGuid().ToString("N").Substring(0, 12)

let private read (store: CredentialStore) (id: CredentialId) =
    sync (store.Read id none) |> ok ("read " + string id)

let private write (store: CredentialStore) (id: CredentialId) (credential: StoredCredential) =
    sync (store.Write id credential none) |> ok ("write " + string id)

let private ours (prefix: string) (store: CredentialStore) =
    sync (store.List none)
    |> ok "list"
    |> List.filter (fun id -> id.Name.StartsWith(prefix, StringComparison.Ordinal))

let private clean (store: CredentialStore) (prefix: string) =
    match sync (store.List none) with
    | Ok ids ->
        for id in ids do
            if id.Name.StartsWith(prefix, StringComparison.Ordinal) then
                sync (store.Delete id none) |> ignore
    | Error _ -> ()

/// What a credential is, compared: the method, the secret's text and the facts.
let private view (credential: StoredCredential option) =
    credential
    |> Option.map (fun c -> c.Method, c.Secret.Reveal(), c.Meta |> Map.toList)

let private credential (methodKind: string) (secret: string) (facts: (string * string) list) : StoredCredential =
    {
        Method = methodKind
        Secret = SecretText secret
        Meta = Map.ofList facts
    }

let tests (name: string) (subject: Subject) : Test =
    let case (title: string) (body: CredentialStore -> string -> unit) =
        test title {
            let store = subject.Open()
            let prefix = uniquePrefix ()

            try
                body store prefix
            finally
                clean store prefix
        }

    // One test at a time: each opens its own store over the shared backing, and a host opens one
    // store per backing (concurrency within one store is a test of its own).
    testSequenced
    <| testList
        $"CredentialStore conformance ({name})"
        [
            test "the store is ready and names its kind and protection" {
                let store = subject.Open()
                Expect.equal (sync (store.Probe none)) StoreStatus.Ready "probe"

                Expect.isTrue (Text.RegularExpressions.Regex.IsMatch(store.Kind, CredentialStore.KindPattern)) "kind"

                Expect.isFalse (String.IsNullOrWhiteSpace store.Protection) "protection"
            }

            case "a missing credential reads as None, and deleting it says there was none" (fun store prefix ->
                let id = credentialId (prefix + "/nobody")
                Expect.isNone (read store id) "read"
                Expect.isFalse (sync (store.Delete id none) |> ok "delete") "delete"
                Expect.isEmpty (ours prefix store) "list")

            case "a written credential reads back whole, and a second write replaces it" (fun store prefix ->
                let id = credentialId (prefix + "/anthropic")
                let first = credential "api_key" "sk-ant-first" [ "account", "alice@example.com" ]
                write store id first
                Expect.equal (view (read store id)) (view (Some first)) "first"

                let second =
                    credential "oauth_device" "{\"access\":\"a\",\"refresh\":\"r\"}" [ "expires", "1764115200" ]

                write store id second
                Expect.equal (view (read store id)) (view (Some second)) "replaced"
                Expect.equal (ours prefix store) [ id ] "one credential")

            case "delete removes the credential and says whether there was one" (fun store prefix ->
                let id = credentialId (prefix + "/openai")
                write store id (credential "api_key" "sk-x" [])
                Expect.isTrue (sync (store.Delete id none) |> ok "delete") "existed"
                Expect.isNone (read store id) "gone"
                Expect.isFalse (sync (store.Delete id none) |> ok "delete again") "already gone"
                Expect.isEmpty (ours prefix store) "list")

            case "ids are listed in ordinal order, with every character ids may have" (fun store prefix ->
                let names =
                    [
                        prefix + "/b"
                        prefix + "/a.b"
                        prefix + "/a_b"
                        prefix + "/a-b"
                        prefix + "/0"
                        prefix + "/a/b/c"
                    ]

                for n in names do
                    write store (credentialId n) (credential "secret" n [])

                let expected =
                    names
                    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    |> List.map credentialId

                Expect.equal (ours prefix store) expected "ordinal order"

                for n in names do
                    Expect.equal (view (read store (credentialId n))) (Some("secret", n, [])) n)

            case "Unicode, JSON, whitespace and empty secrets and facts round-trip" (fun store prefix ->
                let values =
                    [
                        "unicode", credential "secret" "pässwörd 🔐 日本\twith a tab\nand a line" [ "name", "zoë 🔑" ]
                        "json", credential "oauth_pkce" "{\"access_token\":\"a.b.c\",\"refresh_token\":\"r\"}" []
                        "empty", credential "secret" "" []
                        "facts", credential "api_key" "k" [ "a", ""; "b", "two words"; "c", String('x', 512) ]
                    ]

                for n, c in values do
                    write store (credentialId (prefix + "/" + n)) c

                for n, c in values do
                    Expect.equal (view (read store (credentialId (prefix + "/" + n)))) (view (Some c)) n)

            case "a credential is stored whole or refused as TooLarge, never cut" (fun store prefix ->
                let id = credentialId (prefix + "/large")
                let small = credential "oauth_device" (String('t', 1024)) [ "expires", "1" ]
                write store id small
                Expect.equal (view (read store id)) (view (Some small)) "a kilobyte fits every store"
                let huge = credential "oauth_device" (String('h', 1024 * 1024)) []

                match sync (store.Write id huge none) with
                | Ok() -> Expect.equal (view (read store id)) (view (Some huge)) "whole"
                | Error(StoreError.TooLarge limit) ->
                    Expect.isGreaterThan limit 1024 "the limit fits a kilobyte"
                    Expect.equal (view (read store id)) (view (Some small)) "the old credential stays"
                | Error e -> failtest $"expected Ok or TooLarge, got {e}")

            case "concurrent writes of different ids all land" (fun store prefix ->
                let ids = [ for i in 0..7 -> credentialId (prefix + "/c" + string i) ]

                ids
                |> List.map (fun id -> Task.Run(fun () -> write store id (credential "secret" id.Name [])))
                |> Array.ofList
                |> Task.WaitAll

                Expect.equal (ours prefix store) ids "all listed"

                for id in ids do
                    Expect.equal (view (read store id)) (Some("secret", id.Name, [])) (string id))

            case "a store opened again sees what was written, if the store keeps it" (fun store prefix ->
                let id = credentialId (prefix + "/kept")
                write store id (credential "api_key" "sk-kept" [])
                let again = subject.Open()

                if subject.Persistent then
                    Expect.equal (view (read again id)) (Some("api_key", "sk-kept", [])) "kept"
                    Expect.isTrue (sync (again.Delete id none) |> ok "delete") "deleted through the other"
                    Expect.isNone (read store id) "gone for the first too"
                else
                    Expect.isNone (read again id) "a new store starts empty")

            test "a credential never prints its secret" {
                let c = credential "api_key" "sk-very-secret" [ "account", "a" ]
                Expect.isFalse ((string c).Contains "sk-very-secret") "ToString"
                Expect.isFalse ($"{c}".Contains "sk-very-secret") "interpolation"
            }
        ]
