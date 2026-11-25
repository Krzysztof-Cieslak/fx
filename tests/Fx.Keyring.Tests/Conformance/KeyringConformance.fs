/// The keyring conformance suite: what every `Keyring` interpreter must do. `InMemoryKeyring` passes
/// it everywhere; the local keyring passes it on Windows (Credential Manager) and, with
/// `KEYRING_TESTS=1`, on macOS and Linux where CI prepares a keychain or a Secret Service.
///
/// Every test works under a service of its own and deletes what it wrote, so the suite can run
/// against a user's real keyring and in parallel with itself.
module Fx.Keyring.Tests.Conformance.KeyringConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Effects
open Fx.Keyring

/// Every wait is bounded, so a hang fails the test instead of the job.
let private timeout = TimeSpan.FromSeconds 60.0

let private sync (task: Task<'T>) =
    task.WaitAsync(timeout).GetAwaiter().GetResult()

type private Env =
    {
        Keyring: Keyring
        Scope: Scope
    }

    interface IKeyring with
        member env.Keyring = env.Keyring

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

let private run (keyring: Keyring) (effect: Eff<Env, 'T>) : 'T =
    sync (
        effect
            {
                Keyring = keyring
                Scope = Scope.ofCancellation CancellationToken.None
            }
    )

/// A service nobody else uses.
let uniqueService () =
    match KeyringService.tryCreate (Product.Name + ".conformance." + Guid.NewGuid().ToString("N").Substring(0, 16)) with
    | Ok service -> service
    | Error e -> failwith e

let private key (service: KeyringService) (account: string) =
    match KeyringKey.create service account with
    | Ok key -> key
    | Error e -> failwith e

let private ok (label: string) (result: Result<'T, KeyringError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"{label}: {e}"

let private reveal (value: SecretText option) =
    value |> Option.map (fun v -> v.Reveal())

/// Deletes every account of a service (cleanup after a test).
let private clean (keyring: Keyring) (service: KeyringService) =
    match run keyring (Keyring.list service) with
    | Ok accounts ->
        for account in accounts do
            run keyring (Keyring.delete (key service account)) |> ignore
    | Error _ -> ()

let private withService (keyring: Keyring) (body: KeyringService -> unit) =
    let service = uniqueService ()

    try
        body service
    finally
        clean keyring service

let tests (name: string) (keyring: Keyring) : Test =
    let case (title: string) (body: KeyringService -> unit) = test title { withService keyring body }

    testList
        name
        [
            test "the keyring is ready" { Expect.equal (run keyring Keyring.probe) KeyringStatus.Ready "probe" }

            case "a missing secret reads as None" (fun service ->
                let missing = run keyring (Keyring.read (key service "nobody")) |> ok "read"
                Expect.isNone missing "read"
                Expect.isFalse (run keyring (Keyring.delete (key service "nobody")) |> ok "delete") "delete"
                Expect.isEmpty (run keyring (Keyring.list service) |> ok "list") "list")

            case "a written secret reads back, and a second write replaces it" (fun service ->
                let k = key service "api"
                run keyring (Keyring.write k (SecretText "sk-first")) |> ok "write"
                Expect.equal (run keyring (Keyring.read k) |> ok "read" |> reveal) (Some "sk-first") "first"
                run keyring (Keyring.write k (SecretText "sk-second")) |> ok "rewrite"
                Expect.equal (run keyring (Keyring.read k) |> ok "read" |> reveal) (Some "sk-second") "second"
                Expect.equal (run keyring (Keyring.list service) |> ok "list") [ "api" ] "one item")

            case "delete removes the secret and says whether there was one" (fun service ->
                let k = key service "api"
                run keyring (Keyring.write k (SecretText "sk")) |> ok "write"
                Expect.isTrue (run keyring (Keyring.delete k) |> ok "delete") "existed"
                Expect.isNone (run keyring (Keyring.read k) |> ok "read") "gone"
                Expect.isFalse (run keyring (Keyring.delete k) |> ok "delete again") "already gone")

            case "accounts are listed per service, in ordinal order" (fun service ->
                let other = uniqueService ()

                try
                    for account in [ "beta"; "alpha"; "Gamma" ] do
                        run keyring (Keyring.write (key service account) (SecretText account))
                        |> ok "write"

                    run keyring (Keyring.write (key other "alpha") (SecretText "other"))
                    |> ok "write other"

                    Expect.equal (run keyring (Keyring.list service) |> ok "list") [ "Gamma"; "alpha"; "beta" ] "list"
                    Expect.equal (run keyring (Keyring.list other) |> ok "list other") [ "alpha" ] "other list"

                    Expect.equal
                        (run keyring (Keyring.read (key service "alpha")) |> ok "read" |> reveal)
                        (Some "alpha")
                        "same account, own service"

                    Expect.equal
                        (run keyring (Keyring.read (key other "alpha")) |> ok "read other" |> reveal)
                        (Some "other")
                        "same account, other service"
                finally
                    clean keyring other)

            case "Unicode accounts and values, and empty values, round-trip" (fun service ->
                let values =
                    [
                        "zoë 日本 🔑", "pässwörd 🔐 with spaces\tand a tab\nand a line"
                        "user@example.com", ""
                        "json", """{"access_token":"a.b.c","refresh_token":"r"}"""
                    ]

                for account, value in values do
                    run keyring (Keyring.write (key service account) (SecretText value))
                    |> ok "write"

                for account, value in values do
                    Expect.equal
                        (run keyring (Keyring.read (key service account)) |> ok "read" |> reveal)
                        (Some value)
                        account)

            case "values up to the keyring's limit round-trip; larger ones are TooLarge" (fun service ->
                let k = key service "large"
                let limit = keyring.MaxValueBytes
                let largest = String('x', limit)

                run keyring (Keyring.write k (SecretText largest))
                |> ok "write the largest value"

                Expect.equal (run keyring (Keyring.read k) |> ok "read" |> reveal) (Some largest) "largest"

                match run keyring (Keyring.write k (SecretText(largest + "x"))) with
                | Error(KeyringError.TooLarge l) -> Expect.equal l limit "limit"
                | other -> failtest $"expected TooLarge, got {other}"

                Expect.equal (run keyring (Keyring.read k) |> ok "read" |> reveal) (Some largest) "unchanged")
        ]
