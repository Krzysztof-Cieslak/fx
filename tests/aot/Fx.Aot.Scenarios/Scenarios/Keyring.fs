/// Fx.Keyring (T007, docs/spikes/keychains.md): the D-Bus wire format and the Secret Service's
/// session crypto (pure, everywhere), the headless decisions, the probe of this machine's keyring,
/// and a round trip through the real keyring: the Credential Manager and DPAPI on Windows, and, with
/// `KEYRING_TESTS=1` (CI's keyring job), the Keychain on macOS and the Secret Service on Linux.
///
/// Facts are checked against the values they must have, so a keyring that misbehaves the same way on
/// the JIT and natively still fails. `keyring-detect` also checks `KEYRING_EXPECT` when it is set,
/// which is how CI proves the detection of each headless state.
module Fx.Aot.Scenarios.Keyring

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Effects
open Fx.Keyring
open Fx.Keyring.DBus
open Fx.Keyring.SecretService

let private requested = Environment.GetEnvironmentVariable "KEYRING_TESTS" = "1"

let private hex (bytes: byte array) = Convert.ToHexStringLower bytes

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

/// `label = actual`, failing the scenario if it is not `expected`.
let private fact (label: string) (expected: string) (actual: string) : string =
    if actual <> expected then
        failwith $"{label}: expected {expected}, got {actual}"

    label + " = " + Show.escape actual

let private dbusWire =
    Scenario.lines "keyring-dbus-wire" (fun () ->
        let hello =
            { Message.call "org.freedesktop.DBus" "/org/freedesktop/DBus" "org.freedesktop.DBus" "Hello" [] with
                Serial = 1u
            }

        let values =
            [
                DBusValue.Byte 0xffuy
                DBusValue.Boolean true
                DBusValue.Int16 -2s
                DBusValue.UInt16 65535us
                DBusValue.Int32 -3
                DBusValue.UInt32 4000000000u
                DBusValue.Int64 -5L
                DBusValue.UInt64 18000000000000000000UL
                DBusValue.Double 2.5
                DBusValue.String "zoë 🔑"
                DBusValue.ObjectPath "/org/freedesktop/secrets/collection/login/1"
                DBusValue.Signature "a{sv}(oayays)"
                DBusValue.Variant(DBusValue.Variant(DBusValue.UInt64 9UL))
                DBusValue.Bytes [| 0uy; 1uy; 2uy |]
                DBusValue.vardict [ "Label", DBusValue.String "x"; "Locked", DBusValue.Boolean false ]
                DBusValue.stringDict [ "service", "fx"; "account", "a" ]
                DBusValue.Struct
                    [
                        DBusValue.ObjectPath "/s/1"
                        DBusValue.Bytes [| 1uy |]
                        DBusValue.Bytes [| 2uy; 3uy |]
                        DBusValue.String "text/plain"
                    ]
                DBusValue.paths [ "/"; "/a/b_c" ]
            ]

        let message =
            { Message.call "org.example" "/org/example" "org.example.I" "M" values with
                Serial = 42u
            }

        let encoded = Wire.encode message

        // A reply to call 7 with one uint32, as a big-endian peer sends it.
        let bigEndian =
            Convert.FromHexString(
                "42020001"
                + "00000004"
                + "00000009"
                + "0000000f"
                + "05017500"
                + "00000007"
                + "08016700"
                + "017500"
                + "00"
                + "01020304"
            )

        [
            fact "hello" "6c0100010000000001000000" (hex (Wire.encode hello)).[0..23]
            fact "hello length" "128" (string (Wire.encode hello).Length)
            "message = " + hex encoded
            fact "round trip" "True" (string (Wire.decode encoded = Ok message))
            fact "signature" "ybnqiuxtdsogvaya{sv}a{ss}(oayays)ao" message.Signature
            fact
                "big-endian reply"
                "method_return #9 reply-to=7 (u) [uint32 16909060]"
                (match Wire.decode bigEndian with
                 | Ok m ->
                     let body = m.Body |> List.map string |> String.concat ", "
                     $"{m} [{body}]"
                 | Error e -> e)
            fact
                "truncated"
                "message truncated"
                (match Wire.decode encoded[..40] with
                 | Ok _ -> "ok"
                 | Error e -> e)
            fact
                "address"
                "unix:path=/a b; unix:abstract=/tmp/dbus-x"
                (match BusAddress.parse "tcp:host=h,port=1;unix:path=/a%20b;unix:abstract=/tmp/dbus-x,guid=1" with
                 | Ok endpoints -> endpoints |> List.map string |> String.concat "; "
                 | Error e -> e)
        ])

let private sessionCrypto =
    Scenario.lines "keyring-session-crypto" (fun () ->
        let pair (bytes: byte array) =
            match SessionCrypto.KeyPair.FromRandom bytes with
            | Some pair -> pair
            | None -> failwith "out of range"

        let client = pair (Array.init 128 (fun i -> byte (i + 1)))
        let service = pair (Array.init 128 (fun i -> byte ((7 * i + 3) % 256)))

        let key =
            match SessionCrypto.deriveKey client service.Public with
            | Ok key -> key
            | Error e -> failwith e

        let iv = Convert.FromHexString "000102030405060708090a0b0c0d0e0f"

        let _, ciphertext =
            SessionCrypto.encrypt key iv (Encoding.UTF8.GetBytes "correct horse battery staple")

        [
            "client public = " + hex client.Public
            // Known answers from Python and openssl (tests/Fx.Keyring.Tests/SessionCryptoTests.fs).
            fact "key" "d3f57bcf025af223fe1dcb0b005ab0b1" (hex key)
            fact
                "service side"
                "d3f57bcf025af223fe1dcb0b005ab0b1"
                (match SessionCrypto.deriveKey service client.Public with
                 | Ok k -> hex k
                 | Error e -> e)
            fact "ciphertext" "9868e9a6a715976e660247c358fad46e2bc5872ae958f6aafa5b8f8da9f5be05" (hex ciphertext)
            fact
                "decrypted"
                "correct horse battery staple"
                (match SessionCrypto.decrypt key iv ciphertext with
                 | Ok bytes -> Encoding.UTF8.GetString bytes
                 | Error e -> e)
            fact
                "degenerate peer"
                "the service's public key is not a valid group element"
                (match SessionCrypto.deriveKey client [| 1uy |] with
                 | Ok _ -> "accepted"
                 | Error e -> e)
        ])

let private headless =
    Scenario.lines "keyring-headless" (fun () ->
        let facts os variables =
            {
                Os = os
                Variables = Map.ofList variables
                RuntimeBusExists = false
                WslKernel = false
                ContainerMarker = false
            }

        let describe (f: HostFacts) =
            let settings = LocalKeyring.settings f
            let bus = settings.SessionBus |> Option.defaultValue "none"
            $"{Headless.session f} bus={bus} prompts={settings.Prompts}"

        [
            "container = "
            + describe
                { facts HostOs.Linux [] with
                    ContainerMarker = true
                }
            "ssh = "
            + describe (facts HostOs.Linux [ "SSH_TTY", "/dev/pts/1"; "DISPLAY", "localhost:10.0" ])
            "desktop = "
            + describe (
                facts HostOs.Linux [ "DISPLAY", ":0"; "DBUS_SESSION_BUS_ADDRESS", "unix:path=/run/user/1000/bus" ]
            )
            "systemd = "
            + describe
                { facts HostOs.Linux [ "XDG_RUNTIME_DIR", "/run/user/1000" ] with
                    RuntimeBusExists = true
                }
            "wsl = "
            + describe
                { facts HostOs.Linux [ "WSL_DISTRO_NAME", "Ubuntu" ] with
                    WslKernel = true
                }
            "macos-ssh = " + describe (facts HostOs.MacOS [ "SSH_CONNECTION", "a b c d" ])
            "windows-ci = " + describe (facts HostOs.Windows [ "CI", "true" ])
        ])

/// The status without messages, which may name sockets or be localized.
let private statusKind (status: KeyringStatus) =
    match status with
    | KeyringStatus.Unavailable(KeyringUnavailable.BusUnreachable _) -> "unavailable: bus-unreachable"
    | KeyringStatus.Failed(code, _) -> "failed: " + code
    | other -> string other

let private detect =
    Scenario.task "keyring-detect" (fun () ->
        task {
            let facts = LocalHostFacts.read ()

            // No D-Bus activation: the probe looks, it never starts a service.
            let settings =
                { LocalKeyring.settings facts with
                    Prompts = Prompts.Never
                    AutoStart = false
                }

            let keyring = LocalKeyring.create facts.Os settings
            let! status = keyring.Probe CancellationToken.None
            let kind = statusKind status

            let line =
                match Environment.GetEnvironmentVariable "KEYRING_EXPECT" with
                | null
                | "" -> "status = " + kind
                | expected -> fact "status" expected kind

            return [ "backend = " + keyring.Backend; line ]
        })

/// Writes, reads, replaces, lists and deletes under a fresh service, and checks every outcome.
let private roundTrip (label: string) (keyring: Keyring) : string list =
    let service =
        match KeyringService.tryCreate (Product.Name + ".aot." + Guid.NewGuid().ToString("N").Substring(0, 16)) with
        | Ok service -> service
        | Error e -> failwith e

    let key account =
        match KeyringKey.create service account with
        | Ok key -> key
        | Error e -> failwith e

    let none = CancellationToken.None

    let show (result: Result<'T, KeyringError>) (value: 'T -> string) =
        match result with
        | Ok v -> value v
        | Error e -> "error: " + string e

    let read account =
        show (wait (keyring.Read (key account) none)) (fun v ->
            match v with
            | Some s -> "\"" + s.Reveal() + "\""
            | None -> "none")

    let write account (value: string) =
        show (wait (keyring.Write (key account) (SecretText value) none)) (fun () -> "ok")

    let delete account =
        show (wait (keyring.Delete (key account) none)) string

    try
        [
            fact $"{label} probe" "ready" (string (wait (keyring.Probe none)))
            fact $"{label} read missing" "none" (read "api")
            fact $"{label} write" "ok" (write "api" "sk-first")
            fact $"{label} read" "\"sk-first\"" (read "api")
            fact $"{label} replace" "ok" (write "api" "sk-second")
            fact $"{label} read replaced" "\"sk-second\"" (read "api")
            fact $"{label} write unicode" "ok" (write "zoë 日本" "pässwörd 🔑")
            fact $"{label} read unicode" "\"pässwörd 🔑\"" (read "zoë 日本")
            fact $"{label} list" "api, zoë 日本" (show (wait (keyring.List service none)) (String.concat ", "))
            fact
                $"{label} too large"
                $"error: the value is larger than the keyring's limit of {keyring.MaxValueBytes} bytes"
                (write "api" (String('x', keyring.MaxValueBytes + 1)))
            fact $"{label} largest" "ok" (write "api" (String('x', keyring.MaxValueBytes)))
            fact $"{label} delete" "True" (delete "api")
            fact $"{label} read deleted" "none" (read "api")
            fact $"{label} delete again" "False" (delete "api")
            fact $"{label} delete unicode" "True" (delete "zoë 日本")
            fact $"{label} list empty" "" (show (wait (keyring.List service none)) (String.concat ", "))
        ]
    finally
        for account in [ "api"; "zoë 日本" ] do
            wait (keyring.Delete (key account) none) |> ignore

let private roundTrips =
    Scenario.task "keyring-roundtrip" (fun () ->
        task {
            let facts = LocalHostFacts.read ()

            let settings =
                { LocalKeyring.settings facts with
                    Prompts = Prompts.Never
                }

            match facts.Os with
            | HostOs.Linux ->
                return
                    [
                        for algorithm in [ SessionAlgorithm.DhAes; SessionAlgorithm.Plain ] do
                            yield!
                                roundTrip
                                    (string algorithm)
                                    (SecretServiceKeyring.create
                                        { SecretServiceKeyring.defaults settings.SessionBus Prompts.Never with
                                            Algorithms = [ algorithm ]
                                        })
                    ]
            | os ->
                let keyring = LocalKeyring.create os settings
                return roundTrip keyring.Backend keyring
        })
    |> Scenario.where (fun p -> p.Os = Os.Windows || requested)

let private dpapi =
    Scenario.lines "keyring-dpapi" (fun () ->
        let entropy = Encoding.UTF8.GetBytes "fx.store.file"
        let plaintext = Encoding.UTF8.GetBytes "{\"anthropic\":\"sk-ant\"}"

        let outcome (result: Result<byte array, DpapiError>) =
            match result with
            | Ok bytes -> "ok " + Encoding.UTF8.GetString bytes
            | Error(DpapiError.Rejected _) -> "rejected"
            | Error e -> "error: " + string e

        match Dpapi.protect entropy plaintext with
        | Error e -> failwith (string e)
        | Ok sealedData ->
            let tampered = Array.copy sealedData
            tampered[tampered.Length - 1] <- tampered[tampered.Length - 1] ^^^ 1uy

            [
                fact "hides the plaintext" "True" (string (sealedData.AsSpan().IndexOf(plaintext.AsSpan()) < 0))
                fact "round trip" "ok {\"anthropic\":\"sk-ant\"}" (outcome (Dpapi.unprotect entropy sealedData))
                fact "other entropy" "rejected" (outcome (Dpapi.unprotect (Encoding.UTF8.GetBytes "other") sealedData))
                fact "tampered" "rejected" (outcome (Dpapi.unprotect entropy tampered))
            ])
    |> Scenario.onlyOn [ Os.Windows ]

let scenarios: Scenario list =
    [ dbusWire; sessionCrypto; headless; detect; roundTrips; dpapi ]
