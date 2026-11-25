/// `fx.auth.standard`: the `api_key` and `env` methods' settings, logins and resolution, and the
/// `store` and `env` secret sources, on their own.
module Fx.Auth.Tests.MethodTests

open System
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Models
open Fx.Sdk
open Fx.Ext.AuthStandard
open Fx.Auth.Tests.Support

let private settings (members: (string * string) list) =
    Json.Object(members |> List.map (fun (name, value) -> name, Json.String value))

let private authEnv (variables: (string * string) list) (secrets: (string * string) list) : AuthEnv =
    {
        Extension = extensionEnv AuthStandard.id
        Secrets = InMemorySecrets.create secrets
        HostVariables = InMemoryVariables.create variables
        Http = (InMemoryHttp(clock.Clock, (fun _ -> None))).Http
    }

let private resolveWith (env: AuthEnv) (method: AuthMethod) (request: ResolveRequest) =
    sync (AuthEnv.within env (method.Resolve request) (hostEnv []))

let private headers (result: Result<Resolution, CredentialError>) =
    match result with
    | Ok resolution -> resolution.Auth.Headers, resolution.Auth.Query
    | Error e -> failtest (string e)

let private request (provider: string) (settings: Json) (credential: StoredCredential option) : ResolveRequest =
    {
        Provider = provider
        Settings = settings
        Credential = credential
    }

let private anthropicRefs = Fx.Ext.ModelAnthropic.Provider.def.Auth
let private openaiRefs = Fx.Ext.ModelOpenAI.Provider.def.Auth

let private settingsOf (refs: AuthMethodRef list) (kind: string) =
    (refs |> List.find (fun r -> r.Kind = kind)).Settings

[<Tests>]
let tests =
    testList
        AuthStandard.id
        [
            test "the providers' settings are valid for both methods" {
                for refs in [ anthropicRefs; openaiRefs ] do
                    for reference in refs do
                        let method =
                            if reference.Kind = "api_key" then
                                AuthStandard.apiKey
                            else
                                AuthStandard.env

                        Expect.equal (method.CheckSettings reference.Settings) (Ok()) (string reference)
            }

            test "settings say where the key goes, and refuse what would send it wrong" {
                let check kind members =
                    Settings.read kind (settings members) |> Result.map _.Placement

                Expect.equal
                    (check "api_key" [ "header", "x-api-key" ])
                    (Ok(Settings.Placement.Header("x-api-key", None)))
                    "header"

                Expect.equal
                    (check "api_key" [ "header", "Authorization"; "scheme", "Bearer" ])
                    (Ok(Settings.Placement.Header("Authorization", Some "Bearer")))
                    "scheme"

                Expect.equal (check "api_key" [ "query", "key" ]) (Ok(Settings.Placement.Query "key")) "query"

                for kind, members, problem in
                    [
                        "api_key", [], "header or query"
                        "api_key", [ "header", "a"; "query", "b" ], "not both"
                        "api_key", [ "header", "x api key" ], "not a header name"
                        "api_key", [ "header", "a"; "scheme", "Bearer token" ], "not an auth scheme"
                        "api_key", [ "query", "k"; "scheme", "Bearer" ], "goes with a header"
                        "api_key", [ "hedaer", "x-api-key" ], "hedaer"
                        "api_key", [ "header", "a"; "key", "sk-inline" ], "never holds keys"
                        "api_key", [ "header", "a"; "variable", "X" ], "variable is a setting of env"
                        "api_key", [ "header", "a"; "credential", "Upper" ], "lowercase"
                        "env", [ "header", "a" ], "variable is required"
                        "env", [ "header", "a"; "variable", "1X" ], "not an environment variable"
                        "env", [ "header", "a"; "variable", "X"; "key", "secret:x" ], "key is a setting of api_key"
                        "env",
                        [ "header", "a"; "variable", "X"; "credential", "x" ],
                        "credential is a setting of api_key"
                    ] do
                    match Settings.read kind (settings members) with
                    | Ok _ -> failtest $"{kind} {members}: expected an error"
                    | Error e -> Expect.stringContains e problem $"{kind} {members}"
            }

            test "api_key sends a stored key as each provider says" {
                let env = authEnv [] []
                let stored = Some(StoredCredential.create "api_key" (SecretText "sk-test"))

                Expect.equal
                    (resolveWith
                        env
                        AuthStandard.apiKey
                        (request "anthropic" (settingsOf anthropicRefs "api_key") stored)
                     |> headers)
                    ([ "x-api-key", "sk-test" ], [])
                    "anthropic"

                Expect.equal
                    (resolveWith env AuthStandard.apiKey (request "openai" (settingsOf openaiRefs "api_key") stored)
                     |> headers)
                    ([ "authorization", "Bearer sk-test" ], [])
                    "openai"

                Expect.equal
                    (resolveWith env AuthStandard.apiKey (request "g" (settings [ "query", "key" ]) stored)
                     |> headers)
                    ([], [ "key", "sk-test" ])
                    "query"
            }

            test "api_key without a stored key says how to log in" {
                match
                    resolveWith
                        (authEnv [] [])
                        AuthStandard.apiKey
                        (request "anthropic" (settingsOf anthropicRefs "api_key") None)
                with
                | Error(CredentialError.Missing hint) ->
                    Expect.stringContains hint (Product.binaryName + " auth login anthropic") "hint"
                | other -> failtest $"expected Missing, got {other}"
            }

            test "api_key takes its key from a secret: reference when the settings name one" {
                let env = authEnv [] [ "work-anthropic", "sk-work" ]
                let withKey = settings [ "header", "x-api-key"; "key", "secret:work-anthropic" ]
                let stored = Some(StoredCredential.create "api_key" (SecretText "sk-stored"))

                Expect.equal
                    (resolveWith env AuthStandard.apiKey (request "anthropic" withKey stored)
                     |> headers)
                    ([ "x-api-key", "sk-work" ], [])
                    "the reference wins"

                match
                    resolveWith
                        env
                        AuthStandard.apiKey
                        (request "anthropic" (settings [ "header", "x-api-key"; "key", "secret:absent" ]) None)
                with
                | Error(CredentialError.Missing hint) ->
                    Expect.stringContains hint "secret:absent" "names the reference"
                | other -> failtest $"expected Missing, got {other}"
            }

            test "env reads the provider's variable at each request" {
                let env = authEnv [ "ANTHROPIC_API_KEY", "sk-env"; "OPENAI_API_KEY", "sk-oai" ] []

                Expect.equal
                    (resolveWith env AuthStandard.env (request "anthropic" (settingsOf anthropicRefs "env") None)
                     |> headers)
                    ([ "x-api-key", "sk-env" ], [])
                    "anthropic"

                Expect.equal
                    (resolveWith env AuthStandard.env (request "openai" (settingsOf openaiRefs "env") None)
                     |> headers)
                    ([ "authorization", "Bearer sk-oai" ], [])
                    "openai"

                match
                    resolveWith (authEnv [] []) AuthStandard.env (request "openai" (settingsOf openaiRefs "env") None)
                with
                | Error(CredentialError.Missing hint) -> Expect.equal hint "OPENAI_API_KEY is not set" "hint"
                | other -> failtest $"expected Missing, got {other}"
            }

            test "keys that could split a header are refused" {
                for bad in [ "sk a"; "sk\r\nx-evil: 1"; "sk\u0000"; "ключ"; "   " ] do
                    let env = authEnv [ "K", bad ] []

                    match
                        resolveWith
                            env
                            AuthStandard.env
                            (request "p" (settings [ "header", "x-api-key"; "variable", "K" ]) None)
                    with
                    | Error(CredentialError.Invalid _) -> ()
                    | other -> failtest $"{bad}: expected Invalid, got {other}"

                let env = authEnv [ "K", "  sk-padded\n" ] []

                Expect.equal
                    (resolveWith
                        env
                        AuthStandard.env
                        (request "p" (settings [ "header", "x-api-key"; "variable", "K" ]) None)
                     |> headers)
                    ([ "x-api-key", "sk-padded" ], [])
                    "surrounding whitespace is dropped"
            }

            test "an api_key login pastes the key and stores it trimmed" {
                let env = authEnv [] []
                let apiSettings = settingsOf anthropicRefs "api_key"

                let start =
                    sync (
                        AuthEnv.within
                            env
                            (AuthStandard.apiKey.Authorize.Value
                                {
                                    Provider = "anthropic"
                                    Settings = apiSettings
                                    Form = Map.empty
                                    Callback = None
                                })
                            (hostEnv [])
                    )

                match start with
                | Ok start -> Expect.equal start.Flow (AuthFlow.Paste "Paste the API key for anthropic.") "paste"
                | Error e -> failtest (string e)

                let complete result =
                    sync (
                        AuthEnv.within
                            env
                            (AuthStandard.apiKey.Complete.Value
                                {
                                    Provider = "anthropic"
                                    Settings = apiSettings
                                    Form = Map.empty
                                    State = SecretText ""
                                    Result = result
                                })
                            (hostEnv [])
                    )

                match complete (FlowResult.Pasted(SecretText "  sk-ant-pasted\n")) with
                | Ok credential ->
                    Expect.equal credential.Method "api_key" "method"
                    Expect.equal (credential.Secret.Reveal()) "sk-ant-pasted" "trimmed"
                | Error e -> failtest (string e)

                for bad in
                    [
                        FlowResult.Pasted(SecretText " ")
                        FlowResult.Redirected [ "code", "c" ]
                        FlowResult.NoFlow
                    ] do
                    match complete bad with
                    | Error(CredentialError.Invalid _) -> ()
                    | other -> failtest $"{bad}: expected Invalid, got {other}"
            }

            test "the env source reads the product's SECRET_ variables" {
                Expect.equal
                    (AuthStandard.variableOf (secretRef "secret:github-token"))
                    (Product.envVar "SECRET_GITHUB_TOKEN")
                    "dashes"

                Expect.equal
                    (AuthStandard.variableOf (secretRef "secret:Work/a.b"))
                    (Product.envVar "SECRET_WORK_A_B")
                    "slashes and dots"
            }

            test "both methods declare themselves correctly" {
                for method in [ AuthStandard.apiKey; AuthStandard.env ] do
                    Expect.equal (AuthMethod.validate method) (Ok()) method.Kind

                Expect.isTrue (AuthMethod.stores AuthStandard.apiKey) "api_key stores"
                Expect.isFalse (AuthMethod.stores AuthStandard.env) "env does not"
            }
        ]
