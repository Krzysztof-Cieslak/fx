/// The host's service credentials: choosing the store, starting from the contributions, resolving
/// providers' credentials (alone and through `ModelAccess`, on the Anthropic and OpenAI wires),
/// logins, and `secret:` references.
module Fx.Auth.Tests.ServiceTests

open System
open System.Threading
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Models
open Fx.Sdk
open Fx.Variants
open Fx.Auth
open Fx.Auth.Tests.Support

let private anthropic = Fx.Ext.ModelAnthropic.Provider.def
let private openai = Fx.Ext.ModelOpenAI.Provider.def

let private ready = StoreStatus.Ready
let private locked = StoreStatus.Locked "unlock it"
let private unavailable = StoreStatus.Unavailable "no Secret Service"

let private chosen (result: Result<StoreChoice, string>) =
    match result with
    | Ok choice -> choice.Kind, choice.Notice.IsSome
    | Error e -> failtest e

let private resolveHeaders (service: AuthService) (provider: ProviderDef) =
    match run (AuthService.resolve service provider) with
    | Ok(auth, kind) -> auth.Headers, kind
    | Error e -> failtest (string e)

let private login (service: AuthService) (provider: ProviderDef) (pasted: string) =
    match run (AuthService.startLogin service provider None Map.empty None) with
    | Error e -> failtest (string e)
    | Ok pending ->
        match run (AuthService.completeLogin service pending (FlowResult.Pasted(SecretText pasted))) with
        | Ok outcome -> outcome
        | Error e -> failtest (string e)

let private select (slot: Slot<'T>) (value: 'T) = Selections.select slot value

[<Tests>]
let choice =
    testList
        "store choice"
        [
            test "auto prefers the keyring when it is ready, or locked" {
                let stores status =
                    [ "keyring", status; "file", ready; "ephemeral", ready ]

                Expect.equal (chosen (StoreChoice.choose "auto" (stores ready) false)) ("keyring", false) "ready"

                Expect.equal
                    (chosen (StoreChoice.choose "auto" (stores locked) true))
                    ("keyring", false)
                    "locked, it asks"

                Expect.equal
                    (chosen (StoreChoice.choose "auto" (stores locked) false))
                    ("keyring", true)
                    "locked, nobody to ask: said once"
            }

            test "auto falls back to the file, saying why, and never to memory" {
                match StoreChoice.choose "auto" [ "keyring", unavailable; "file", ready ] false with
                | Ok choice ->
                    Expect.equal choice.Kind "file" "file"
                    Expect.stringContains choice.Notice.Value "no Secret Service" "says why"
                | Error e -> failtest e

                Expect.equal
                    (chosen (StoreChoice.choose "auto" [ "file", ready ] false))
                    ("file", true)
                    "no keyring store"

                Expect.isError
                    (StoreChoice.choose
                        "auto"
                        [
                            "keyring", unavailable
                            "file", StoreStatus.Unavailable "damaged"
                            "ephemeral", ready
                        ]
                        false)
                    "both unusable: an error, not memory"

                Expect.isError (StoreChoice.choose "auto" [ "ephemeral", ready ] false) "memory only"
            }

            test "a named store is used, or its problem is the error: never a fallback" {
                Expect.equal
                    (chosen (StoreChoice.choose "file" [ "keyring", ready; "file", ready ] false))
                    ("file", false)
                    "file"

                Expect.equal
                    (chosen (StoreChoice.choose "ephemeral" [ "ephemeral", ready ] false))
                    ("ephemeral", false)
                    "ephemeral"

                Expect.equal
                    (chosen (StoreChoice.choose "keyring" [ "keyring", locked ] false))
                    ("keyring", true)
                    "locked"

                match StoreChoice.choose "keyring" [ "keyring", unavailable; "file", ready ] false with
                | Error e ->
                    Expect.stringContains e "credentials.store is keyring" "names the setting"
                    Expect.stringContains e "no Secret Service" "and the reason"
                | Ok choice -> failtest $"expected an error, got {choice}"

                match StoreChoice.choose "vault" [ "keyring", ready ] false with
                | Error e -> Expect.stringContains e "stores: keyring" "lists the stores"
                | Ok choice -> failtest $"expected an error, got {choice}"
            }
        ]

[<Tests>]
let service =
    testList
        "auth service"
        [
            test "the host starts on the keyring when it is ready, and on the file when there is none" {
                let ready = start (fakes ()) []
                Expect.equal ready.Store.Kind "keyring" "keyring"
                Expect.equal ready.Store.Protection "memory" "the in-memory keyring"

                let headless = fakes ()
                headless.Keyring.Status <- KeyringStatus.Unavailable KeyringUnavailable.NoSessionBus
                let service = start headless []
                Expect.equal service.Store.Kind "file" "file"
                Expect.stringContains service.Choice.Notice.Value "no D-Bus session bus" "says why"
            }

            test "a store setting that cannot be honoured stops the host" {
                let headless = fakes ()
                headless.Keyring.Status <- KeyringStatus.Unavailable KeyringUnavailable.NoSessionBus

                match tryStart headless [ select AuthSlots.store "keyring" ] with
                | Error e -> Expect.stringContains e "no D-Bus session bus" "reason"
                | Ok service -> failtest $"expected an error, got {service}"

                match tryStart (fakes ()) [ select AuthSlots.sources [ "store"; "vault" ] ] with
                | Error e -> Expect.stringContains e "secrets.sources names vault" "unknown source"
                | Ok service -> failtest $"expected an error, got {service}"
            }

            test "without credentials, the error says how to provide them" {
                let service = start (fakes ()) []

                match run (AuthService.resolve service anthropic) with
                | Error(CredentialError.Missing hint) ->
                    Expect.stringContains hint (Product.binaryName + " auth login anthropic") "login"
                    Expect.stringContains hint "ANTHROPIC_API_KEY" "or the variable"
                | other -> failtest $"expected Missing, got {other}"

                match sync (AuthService.modelAuth service openai CancellationToken.None) with
                | Error(ChatError.Unavailable message) ->
                    Expect.stringContains message "OPENAI_API_KEY" "for ModelAccess"
                | other -> failtest $"expected unavailable, got {other}"
            }

            test "a stored key wins over the environment, as the providers list them" {
                let host =
                    { fakes () with
                        Variables = [ "ANTHROPIC_API_KEY", "sk-env"; "OPENAI_API_KEY", "sk-oai-env" ]
                    }

                let service = start host []
                Expect.equal (resolveHeaders service anthropic) ([ "x-api-key", "sk-env" ], "env") "the variable"
                Expect.equal (resolveHeaders service openai) ([ "authorization", "Bearer sk-oai-env" ], "env") "Bearer"

                let outcome = login service anthropic " sk-ant-stored\n"
                Expect.equal outcome.Credential.Name "anthropic" "under the provider's id"
                Expect.equal outcome.Store "keyring" "in the keyring"

                Expect.equal
                    (resolveHeaders service anthropic)
                    ([ "x-api-key", "sk-ant-stored" ], "api_key")
                    "the stored key"
            }

            test "a store that cannot be read gives way to the environment, and is traced" {
                let host =
                    { fakes () with
                        Variables = [ "ANTHROPIC_API_KEY", "sk-env" ]
                    }

                let trace = TraceCollector(clock.Clock)

                let service =
                    { start host [] with
                        Trace = trace.Trace
                    }

                host.Keyring.Status <- KeyringStatus.Locked
                Expect.equal (resolveHeaders service anthropic) ([ "x-api-key", "sk-env" ], "env") "the variable"

                Expect.isTrue
                    (trace.Records
                     |> List.exists (function
                         | EventRecorded(_, _, "auth.skipped", _, _) -> true
                         | _ -> false))
                    "traced"

                let bare = { fakes () with Variables = [] }
                let service = start bare []
                bare.Keyring.Status <- KeyringStatus.Locked

                match run (AuthService.resolve service anthropic) with
                | Error(CredentialError.Unavailable reason) ->
                    Expect.stringContains reason "locked" "the store's problem"
                | other -> failtest $"expected Unavailable, got {other}"
            }

            test "ModelAccess sends each provider's key in its own header" {
                let host =
                    { fakes () with
                        Variables = [ "ANTHROPIC_API_KEY", "sk-ant-env"; "OPENAI_API_KEY", "sk-oai-env" ]
                    }

                let service = start host []
                let http = InMemoryHttp(clock.Clock, (fun _ -> Some(Ok(HttpReply.text 401 "{}"))))

                let access =
                    { ModelAccess.create
                          [ Fx.Ext.ModelAnthropic.Anthropic.wire; Fx.Ext.ModelOpenAI.OpenAI.wire ]
                          [ anthropic; openai ]
                          http.Http
                          clock.Clock with
                        Auth = AuthService.modelAuth service
                    }

                let llm = ModelAccess.llm access

                for provider, model in [ "anthropic", "claude-opus-5-5"; "openai", "gpt-6.2-astra" ] do
                    let reference =
                        match ModelRef.tryParse (provider + "/" + model) with
                        | Ok reference -> reference
                        | Error e -> failwith e

                    let stream =
                        sync (
                            llm.Stream (ChatRequest.create reference [ Message.userText "Hi" ]) CancellationToken.None
                        )

                    sync (ChatStream.collect stream CancellationToken.None) |> ignore

                let sent =
                    http.Requests
                    |> List.map (fun r ->
                        r.Headers
                        |> List.filter (fun (name, _) -> name = "x-api-key" || name = "authorization"))

                Expect.equal sent [ [ "x-api-key", "sk-ant-env" ]; [ "authorization", "Bearer sk-oai-env" ] ] "headers"
            }

            test "a login needs a method that stores, and a pasted key" {
                let service = start (fakes ()) []

                let envOnly =
                    { anthropic with
                        Id = "envonly"
                        Auth = [ anthropic.Auth[1] ]
                    }

                match run (AuthService.startLogin service envOnly None Map.empty None) with
                | Error(AuthFailure.NoLogin(_, reason)) -> Expect.stringContains reason "env" "names the methods"
                | other -> failtest $"expected NoLogin, got {other}"

                match run (AuthService.startLogin service anthropic (Some "env") Map.empty None) with
                | Error(AuthFailure.NoLogin(_, reason)) ->
                    Expect.stringContains reason "stores nothing" "env stores nothing"
                | other -> failtest $"expected NoLogin, got {other}"

                match run (AuthService.startLogin service anthropic None Map.empty None) with
                | Ok pending ->
                    Expect.equal pending.Method "api_key" "api_key"
                    Expect.equal pending.Flow (Some(AuthFlow.Paste "Paste the API key for anthropic.")) "paste"

                    match run (AuthService.completeLogin service pending (FlowResult.Pasted(SecretText ""))) with
                    | Error(AuthFailure.Credential(CredentialError.Invalid _)) -> ()
                    | other -> failtest $"expected Invalid, got {other}"
                | Error e -> failtest (string e)
            }

            test "logout removes what a login stored, and list never shows a secret" {
                let service = start (fakes ()) []
                login service anthropic "sk-ant-secret-1" |> ignore
                login service openai "sk-oai-secret-2" |> ignore

                match run (AuthService.list service) with
                | Ok entries ->
                    Expect.equal
                        (entries |> List.map (fun e -> e.Id.Name, e.Method))
                        [ "anthropic", "api_key"; "openai", "api_key" ]
                        "listed"

                    Expect.isFalse ((string entries).Contains "secret-") "no secrets"
                | Error e -> failtest (string e)

                Expect.equal
                    (run (AuthService.logout service anthropic) |> Result.map (List.map string))
                    (Ok [ "anthropic" ])
                    "removed"

                Expect.equal
                    (run (AuthService.logout service anthropic) |> Result.map (List.map string))
                    (Ok [])
                    "nothing left"

                Expect.equal
                    (run (AuthService.list service) |> Result.map (List.map (fun e -> e.Id.Name)))
                    (Ok [ "openai" ])
                    "openai stays"
            }

            test "providers' settings are checked against the methods" {
                let service = start (fakes ()) []
                Expect.isEmpty (AuthService.check service [ anthropic; openai ]) "the built-in providers"

                let broken =
                    { anthropic with
                        Id = "broken"
                        Auth =
                            [
                                ({
                                    Kind = "api_key"
                                    Settings = Json.Object [ "hedaer", Json.String "x" ]
                                }
                                : AuthMethodRef)
                                ({
                                    Kind = "oauth_pkce"
                                    Settings = Json.Object []
                                }
                                : AuthMethodRef)
                            ]
                    }

                match AuthService.check service [ broken ] with
                | [ settings; unknown ] ->
                    Expect.stringContains settings "broken: api_key" "settings"
                    Expect.stringContains unknown "oauth_pkce" "unknown method"
                | other -> failtest $"expected two problems, got {other}"
            }
        ]

[<Tests>]
let secrets =
    testList
        "secret references"
        [
            let resolve (service: AuthService) (text: string) =
                sync (service.Secrets.Resolve (secretRef text) CancellationToken.None)
                |> Result.map _.Reveal()
                |> Result.mapError string

            test "the store answers first, then the environment" {
                let host =
                    { fakes () with
                        Variables =
                            [
                                Product.envVar "SECRET_GITHUB_TOKEN", "ghp-env"
                                Product.envVar "SECRET_ONLY_ENV", "e"
                            ]
                    }

                let service = start host []

                sync (
                    service.Store.Write
                        (credentialId "github-token")
                        (StoredCredential.ofSecret (SecretText "ghp-store"))
                        CancellationToken.None
                )
                |> ignore

                Expect.equal (resolve service "secret:github-token") (Ok "ghp-store") "store"
                Expect.equal (resolve service "secret:GitHub-Token") (Ok "ghp-store") "case-insensitive"
                Expect.equal (resolve service "secret:only-env") (Ok "e") "env"
                Expect.equal (resolve service "secret:absent") (Error "secret:absent not found") "not found"

                let envFirst = start host [ select AuthSlots.sources [ "env" ] ]
                Expect.equal (resolve envFirst "secret:github-token") (Ok "ghp-env") "only env"
            }

            test "a store that cannot tell does not hide the environment" {
                let host =
                    { fakes () with
                        Variables = [ Product.envVar "SECRET_X", "from-env" ]
                    }

                let service = start host []
                host.Keyring.Status <- KeyringStatus.Locked
                Expect.equal (resolve service "secret:x") (Ok "from-env") "env"

                match resolve service "secret:y" with
                | Error e -> Expect.stringContains e "unavailable: store:" "the store's reason"
                | Ok value -> failtest $"expected an error, got {value}"
            }

            test "api_key settings can take the key from a secret: reference" {
                let host =
                    { fakes () with
                        Variables = [ Product.envVar "SECRET_WORK_ANTHROPIC", "sk-work" ]
                    }

                let service = start host []

                let work =
                    { anthropic with
                        Id = "anthropic-work"
                        Auth =
                            [
                                ({
                                    Kind = "api_key"
                                    Settings =
                                        Json.Object
                                            [
                                                "header", Json.String "x-api-key"
                                                "key", Json.String "secret:work-anthropic"
                                            ]
                                }
                                : AuthMethodRef)
                            ]
                    }

                Expect.equal (resolveHeaders service work) ([ "x-api-key", "sk-work" ], "api_key") "from the env source"
            }
        ]
