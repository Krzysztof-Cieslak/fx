/// The real keyrings. They run on Windows always (every logged-on user has a Credential Manager),
/// and on macOS and Linux with `KEYRING_TESTS=1`, where CI prepares a temporary keychain or a
/// gnome-keyring under `dbus-run-session` (docs/spikes/keychains.md, "CI"). With
/// `KEYRING_TEST_PASSWORD` (and `KEYRING_TEST_KEYCHAIN` on macOS) they also lock the keyring and
/// unlock it again.
module Fx.Keyring.Tests.LiveTests

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Keyring
open Fx.Keyring.DBus
open Fx.Keyring.SecretService
open Fx.Keyring.Tests.Conformance

let private variable (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

let private requested = variable "KEYRING_TESTS" = Some "1"
let private password = variable "KEYRING_TEST_PASSWORD"
let private facts = LocalHostFacts.read ()

let private settings =
    { LocalKeyring.settings facts with
        Prompts = Prompts.Never
    }

let private keyring = LocalKeyring.create facts.Os settings

let private sync (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

let private none = CancellationToken.None

let private key (service: KeyringService) (account: string) =
    match KeyringKey.create service account with
    | Ok key -> key
    | Error e -> failwith e

/// Runs a program to completion: (exit code, standard output).
let private runProgram (file: string) (args: string list) (input: string option) =
    let start = ProcessStartInfo(file, args)
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.UseShellExecute <- false

    use p =
        match Process.Start start with
        | null -> failwith $"cannot start {file}"
        | p -> p

    match input with
    | Some text -> p.StandardInput.Write text
    | None -> ()

    p.StandardInput.Close()
    let output = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    p.ExitCode, output

let private onPath (program: string) =
    (variable "PATH" |> Option.defaultValue "").Split(IO.Path.PathSeparator)
    |> Array.exists (fun dir -> IO.File.Exists(IO.Path.Combine(dir, program)))

let private expectLockedThenUnlock (lock: unit -> unit) (unlock: unit -> unit) =
    let service = KeyringConformance.uniqueService ()
    let k = key service "locked"
    lock ()

    try
        Expect.equal (sync (keyring.Probe none)) KeyringStatus.Locked "probe"
        Expect.equal (sync (keyring.Read k none)) (Error KeyringError.Locked) "read"
        Expect.equal (sync (keyring.Write k (SecretText "x") none)) (Error KeyringError.Locked) "write"
    finally
        unlock ()

    Expect.equal (sync (keyring.Probe none)) KeyringStatus.Ready "probe after unlocking"

module private SecretServiceHelpers =
    let bus () =
        match settings.SessionBus with
        | Some bus -> bus
        | None -> failtest "no session bus"

    let call (connection: BusConnection) path iface methodName body =
        match sync (connection.Call (Message.call "org.freedesktop.secrets" path iface methodName body) none) with
        | Ok reply -> reply
        | Error e -> failtest $"{methodName}: {e}"

    let withDefaultCollection (body: BusConnection -> string -> unit) =
        match sync (BusConnection.Connect (bus ()) none) with
        | Error e -> failtest e
        | Ok connection ->
            use connection = connection

            match
                call
                    connection
                    "/org/freedesktop/secrets"
                    "org.freedesktop.Secret.Service"
                    "ReadAlias"
                    [ DBusValue.String "default" ]
            with
            | [ DBusValue.ObjectPath collection ] -> body connection collection
            | other -> failtest $"ReadAlias: {other}"

    let lock () =
        withDefaultCollection (fun connection collection ->
            call
                connection
                "/org/freedesktop/secrets"
                "org.freedesktop.Secret.Service"
                "Lock"
                [ DBusValue.paths [ collection ] ]
            |> ignore)

    /// gnome-keyring's own way to unlock with the password, without a prompt (tests only).
    let unlock (password: string) () =
        withDefaultCollection (fun connection collection ->
            let session =
                match
                    call
                        connection
                        "/org/freedesktop/secrets"
                        "org.freedesktop.Secret.Service"
                        "OpenSession"
                        [ DBusValue.String "plain"; DBusValue.Variant(DBusValue.String "") ]
                with
                | [ _; DBusValue.ObjectPath session ] -> session
                | other -> failtest $"OpenSession: {other}"

            call
                connection
                "/org/freedesktop/secrets"
                "org.gnome.keyring.InternalUnsupportedGuiltRiddenInterface"
                "UnlockWithMasterPassword"
                [
                    DBusValue.ObjectPath collection
                    DBusValue.Struct
                        [
                            DBusValue.ObjectPath session
                            DBusValue.Bytes [||]
                            DBusValue.Bytes(Encoding.UTF8.GetBytes password)
                            DBusValue.String "text/plain"
                        ]
                ]
            |> ignore)

let private linux () =
    let bus = settings.SessionBus

    [
        for algorithm in [ SessionAlgorithm.DhAes; SessionAlgorithm.Plain ] do
            KeyringConformance.tests
                $"Secret Service ({algorithm})"
                (SecretServiceKeyring.create
                    { SecretServiceKeyring.defaults bus Prompts.Never with
                        Algorithms = [ algorithm ]
                    })

        if onPath "secret-tool" then
            test "items are libsecret's: secret-tool reads ours and we read its" {
                let service = KeyringConformance.uniqueService ()
                let ours = key service "ours"
                let theirs = key service "theirs"

                try
                    sync (keyring.Write ours (SecretText "written by us") none) |> Expect.isOk
                    <| "write"

                    let code, output =
                        runProgram "secret-tool" [ "lookup"; "service"; service.Name; "account"; "ours" ] None

                    Expect.equal (code, output.TrimEnd('\n')) (0, "written by us") "secret-tool lookup"

                    let code, _ =
                        runProgram
                            "secret-tool"
                            [ "store"; "--label=test"; "service"; service.Name; "account"; "theirs" ]
                            (Some "written by secret-tool")

                    Expect.equal code 0 "secret-tool store"

                    Expect.equal
                        (sync (keyring.Read theirs none) |> Result.map (Option.map (fun s -> s.Reveal())))
                        (Ok(Some "written by secret-tool"))
                        "read"
                finally
                    sync (keyring.Delete ours none) |> ignore
                    sync (keyring.Delete theirs none) |> ignore
            }

        match password with
        | Some password ->
            test "a locked collection fails without prompting, and the probe says so" {
                expectLockedThenUnlock SecretServiceHelpers.lock (SecretServiceHelpers.unlock password)
            }

            if not (Headless.session facts).Display then
                // The unlock prompt goes through the Prompt object and its Completed signal; without a
                // display gnome-keyring's prompter cannot show it, and the prompt completes dismissed.
                test "with prompts allowed, the unlock prompt runs and, with no display, is dismissed" {
                    let prompting =
                        SecretServiceKeyring.create (SecretServiceKeyring.defaults bus Prompts.Allow)

                    let k = key (KeyringConformance.uniqueService ()) "prompted"
                    SecretServiceHelpers.lock ()

                    try
                        Expect.equal (sync (prompting.Read k none)) (Error KeyringError.Dismissed) "read"
                    finally
                        SecretServiceHelpers.unlock password ()

                    Expect.equal (sync (prompting.Read k none)) (Ok None) "read after unlocking"
                }
        | None -> ()
    ]

let private macOS () =
    [
        KeyringConformance.tests "Keychain" keyring

        match password, variable "KEYRING_TEST_KEYCHAIN" with
        | Some password, Some keychain ->
            test "a locked keychain fails without prompting, and the probe says so" {
                let security args =
                    let code, _ = runProgram "security" args None
                    Expect.equal code 0 (String.concat " " ("security" :: args))

                expectLockedThenUnlock (fun () -> security [ "lock-keychain"; keychain ]) (fun () ->
                    security [ "unlock-keychain"; "-p"; password; keychain ])
            }
        | _ -> ()
    ]

let private windows () =
    [
        KeyringConformance.tests "Credential Manager" keyring

        test "DPAPI protects for this user and entropy, and detects tampering" {
            let entropy = Encoding.UTF8.GetBytes "fx.store.file"
            let plaintext = Encoding.UTF8.GetBytes "{\"anthropic\":\"sk-ant\"}"

            match Dpapi.protect entropy plaintext with
            | Error e -> failtest (string e)
            | Ok sealedData ->
                Expect.isFalse (sealedData.AsSpan().IndexOf(plaintext.AsSpan()) >= 0) "encrypted"
                Expect.equal (Dpapi.unprotect entropy sealedData) (Ok plaintext) "round trip"

                match Dpapi.unprotect (Encoding.UTF8.GetBytes "other") sealedData with
                | Error(DpapiError.Rejected _) -> ()
                | other -> failtest $"other entropy: {other}"

                let tampered = Array.copy sealedData
                tampered[tampered.Length - 1] <- tampered[tampered.Length - 1] ^^^ 1uy

                match Dpapi.unprotect entropy tampered with
                | Error(DpapiError.Rejected _) -> ()
                | other -> failtest $"tampered: {other}"
        }
    ]

[<Tests>]
let tests =
    if OperatingSystem.IsWindows() || requested then
        testSequenced (
            testList
                "Live keyring"
                (match facts.Os with
                 | HostOs.Linux -> linux ()
                 | HostOs.MacOS -> macOS ()
                 | HostOs.Windows -> windows ()
                 | HostOs.Other _ -> [])
        )
    else
        testList "Live keyring (set KEYRING_TESTS=1)" []

[<Tests>]
let portable =
    testList
        "Portable"
        [
            test "DPAPI is Windows-only" {
                if not (OperatingSystem.IsWindows()) then
                    Expect.equal (Dpapi.protect [||] [| 1uy |]) (Error DpapiError.NotSupported) "protect"
            }
        ]
