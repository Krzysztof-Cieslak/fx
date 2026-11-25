/// Service credentials (T121, docs/modules/auth.md): the store choice, credentials and login flows
/// through their codecs, `fx.auth.standard`'s methods and sources and the host's auth service over
/// fakes, the `auth` commands (all pure, everywhere); the `file` store over this machine's private
/// files (owner-only modes, DPAPI on Windows); and the `keyring` store over this machine's keyring
/// where `keyring-roundtrip` runs (Windows, and CI's keyring job with `KEYRING_TESTS=1`).
///
/// Facts are checked against the values they must have, so code that misbehaves the same way on the
/// JIT and natively still fails.
module Fx.Aot.Scenarios.Auth

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Keyring
open Fx.Models
open Fx.Sdk
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Auth
open Fx.Ext.AuthStandard
open Fx.Ext.StoreEphemeral
open Fx.Ext.StoreFile
open Fx.Ext.StoreKeyring

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

let private none = CancellationToken.None

/// `label = actual`, failing the scenario if it is not `expected`.
let private fact (label: string) (expected: string) (actual: string) : string =
    if actual <> expected then
        failwith $"{label}: expected {expected}, got {actual}"

    label + " = " + Show.escape actual

let private credentialId (name: string) =
    match CredentialId.tryCreate name with
    | Ok id -> id
    | Error e -> failwith e

let private storeChoice =
    Scenario.lines "auth-store-choice" (fun () ->
        let show (result: Result<StoreChoice, string>) =
            match result with
            | Ok choice ->
                choice.Kind
                + " ("
                + choice.Reason
                + ")"
                + (match choice.Notice with
                   | Some notice -> " notice: " + notice
                   | None -> "")
            | Error e -> "error: " + e

        let ready = StoreStatus.Ready
        let locked = StoreStatus.Locked "unlock it"
        let gone = StoreStatus.Unavailable "no session bus"

        [
            for label, setting, statuses, prompts in
                [
                    "auto, keyring ready", "auto", [ "keyring", ready; "file", ready ], false
                    "auto, keyring locked, prompts", "auto", [ "keyring", locked; "file", ready ], true
                    "auto, keyring locked, no prompts", "auto", [ "keyring", locked; "file", ready ], false
                    "auto, no keyring", "auto", [ "keyring", gone; "file", ready; "ephemeral", ready ], false
                    "auto, nothing", "auto", [ "keyring", gone; "ephemeral", ready ], false
                    "keyring, unavailable", "keyring", [ "keyring", gone; "file", ready ], false
                    "ephemeral", "ephemeral", [ "keyring", ready; "ephemeral", ready ], false
                    "unknown", "vault", [ "keyring", ready ], false
                ] do
                label + ": " + show (StoreChoice.choose setting statuses prompts)
        ])

let private credentials =
    Scenario.lines "auth-credentials" (fun () ->
        let credential =
            { StoredCredential.create "api_key" (SecretText "sk-ant-🔑-secret") with
                Meta = Map.ofList [ "account", "zoë" ]
            }

        let text = StoredCredential.toText credential

        let back =
            match StoredCredential.tryOfText text with
            | Some c -> c.Method + " " + c.Secret.Reveal() + " " + string c.Meta.Count
            | None -> "none"

        let preview = (Codec.preview StoredCredential.codec Limits.defaults credential).Text

        let flows =
            [
                AuthFlow.Browser("https://example.com/authorize?x=1", "http://127.0.0.1:8123/callback")
                AuthFlow.DeviceCode("ABCD-1234", "https://github.com/login/device", TimeSpan.FromSeconds 5.0)
                AuthFlow.Paste "Paste the key."
            ]

        let flowText (flow: AuthFlow) =
            let json = Json.serializeCanonical (AuthCodecs.authFlow.Encode flow)

            match Codec.decodeText AuthCodecs.authFlow json with
            | Ok decoded when decoded = flow -> json
            | other -> failwith $"{flow} did not round-trip: {other}"

        let pasted = FlowResult.Pasted(SecretText "sk-pasted")

        [
            fact "json" """{"meta":{"account":"zoë"},"method":"api_key","secret":"sk-ant-🔑-secret"}""" text
            fact "read back" "api_key sk-ant-🔑-secret 1" back
            fact "to string" "api_key credential (redacted)" (string credential)
            fact "preview hides the secret" "False" (string (preview.Contains "sk-ant"))
            fact
                "not a credential"
                "none"
                (StoredCredential.tryOfText "ghp_plain"
                 |> Option.map string
                 |> Option.defaultValue "none")
            fact "flow result" "pasted (redacted)" (string pasted)

            fact
                "pasted preview hides the secret"
                "False"
                (string ((Codec.preview AuthCodecs.flowResult Limits.defaults pasted).Text.Contains "sk-pasted"))

            fact
                "secret id"
                "github/token"
                (CredentialId.ofSecret (
                    match SecretRef.tryParse "secret:GitHub/Token" with
                    | Ok r -> r
                    | Error e -> failwith e
                ))
                    .Name
            for flow in flows do
                flowText flow
        ])

/// The host's service over fakes: the built-ins' contributions, an in-memory keyring and files.
type private Env =
    {
        Variants: Variants
        Scope: Scope
    }

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

let private contributed
    (extension: string)
    (contributionType: ContributionType<'T>)
    (id: string)
    (value: 'T)
    : Contributed<'T> =
    {
        Extension = extension
        Type = contributionType.Name
        Id = id
        Value = value
        Env =
            {
                Extension = extension
                Clock = (FakeClock.Create()).Clock
                Scope = Scope.root
            }
            :> IExtensionEnv
    }

let private service (keyring: InMemoryKeyring) (variables: (string * string) list) : Env * AuthService =
    let env =
        {
            Variants = FakeVariants.create AuthSlots.all []
            Scope = Scope.root
        }

    let settings: AuthSettings =
        {
            Keyring = keyring.Keyring
            PrivateFiles = InMemoryPrivateFiles().PrivateFiles
            Variables = InMemoryVariables.create variables
            Http = (InMemoryHttp((FakeClock.Create()).Clock, (fun _ -> None))).Http
            Prompts = false
            Trace = Trace.none
        }

    let stores =
        [
            contributed StoreKeyring.id CredentialStores.contributionType StoreKeyring.Kind StoreKeyring.def
            contributed StoreFile.id CredentialStores.contributionType StoreFile.Kind StoreFile.def
            contributed StoreEphemeral.id CredentialStores.contributionType StoreEphemeral.Kind StoreEphemeral.def
        ]

    let methods =
        [
            contributed AuthStandard.id AuthMethods.contributionType "api_key" AuthStandard.apiKey
            contributed AuthStandard.id AuthMethods.contributionType "env" AuthStandard.env
        ]

    let sources =
        [
            contributed AuthStandard.id SecretSources.contributionType "store" AuthStandard.storeSource
            contributed AuthStandard.id SecretSources.contributionType "env" AuthStandard.envSource
        ]

    match wait (AuthHost.start settings stores methods sources env) with
    | Ok service -> env, service
    | Error e -> failwith e

let private authService =
    Scenario.task "auth-service" (fun () ->
        task {
            let providers =
                [ Fx.Ext.ModelAnthropic.Provider.def; Fx.Ext.ModelOpenAI.Provider.def ]

            let env, service =
                service
                    (InMemoryKeyring())
                    [
                        "OPENAI_API_KEY", "sk-oai-env"
                        Product.envVar "SECRET_GITHUB_TOKEN", "ghp-env"
                    ]

            let headers (provider: ProviderDef) =
                match wait (AuthService.modelAuth service provider none) with
                | Ok auth -> auth.Headers |> List.map (fun (n, v) -> n + ": " + v) |> String.concat "; "
                | Error e -> "error: " + string e

            let pasting: FlowRunner =
                fun _ _ -> Task.FromResult(Ok(FlowResult.Pasted(SecretText "  sk-ant-pasted\n")))

            let command (args: string list) =
                match AuthCommand.parse args with
                | Ok command ->
                    let code, lines = wait (AuthCli.run service providers pasting command env)
                    string code :: lines
                | Error e -> [ "parse error: " + e ]

            let anthropicBefore = headers providers[0]
            let openaiHeaders = headers providers[1]
            let login = command [ "login"; "anthropic" ]
            let anthropicAfter = headers providers[0]
            let status = command [ "status" ]
            let list = command [ "list" ]

            let secret (text: string) =
                match SecretRef.tryParse text with
                | Ok reference ->
                    match wait (service.Secrets.Resolve reference none) with
                    | Ok value -> value.Reveal()
                    | Error e -> "error: " + string e
                | Error e -> "invalid: " + e

            let fromStore = secret "secret:anthropic"
            let fromEnv = secret "secret:github-token"
            let missing = secret "secret:nope"
            let logout = command [ "logout"; "anthropic" ]

            return
                [
                    fact "store" "keyring" service.Store.Kind
                    fact
                        "anthropic without a key"
                        ("error: unavailable: anthropic: no credentials: no API key for anthropic: run `"
                         + Product.binaryName
                         + " auth login anthropic`; or ANTHROPIC_API_KEY is not set")
                        anthropicBefore
                    fact "openai from its variable" "authorization: Bearer sk-oai-env" openaiHeaders
                    fact
                        "login"
                        "0 | Stored the api_key credential for anthropic in the keyring store (memory) as anthropic."
                        (String.concat " | " login)
                    fact "anthropic after the login" "x-api-key: sk-ant-pasted" anthropicAfter
                    fact
                        "status"
                        "0 | Credential store: keyring (memory), auto: the OS keyring is ready | Store status: ready |   anthropic  api_key |   openai     env"
                        (String.concat " | " status)
                    fact
                        "list"
                        "0 | Credentials in the keyring store (memory): |   anthropic  api_key"
                        (String.concat " | " list)
                    fact "secret from the store" "sk-ant-pasted" fromStore
                    fact "secret from the environment" "ghp-env" fromEnv
                    fact "missing secret" "error: secret:nope not found" missing
                    fact "logout" "0 | Removed anthropic from the keyring store (memory)." (String.concat " | " logout)
                    fact
                        "bad command"
                        "parse error: unknown auth command whoami: use login, logout, list or status"
                        (String.concat " | " (command [ "whoami" ]))
                ]
        })

let private fileStore =
    Scenario.task "auth-file-store" (fun () ->
        task {
            let root =
                Path.Combine(Path.GetTempPath(), Product.Name + "-aot-" + Guid.NewGuid().ToString("N"), "credentials")

            try
                let files = LocalPrivateFiles.create root Product.Name
                let store = StoreFile.create files
                let secret = "sk-file-🔑"
                let id = credentialId "anthropic"

                let show (result: Result<'T, StoreError>) (value: 'T -> string) =
                    match result with
                    | Ok v -> value v
                    | Error e -> "error: " + string e

                let write =
                    show
                        (wait (store.Write id (StoredCredential.create "api_key" (SecretText secret)) none))
                        (fun () -> "ok")

                let read =
                    show (wait (store.Read id none)) (fun c ->
                        match c with
                        | Some c -> c.Method + " " + c.Secret.Reveal()
                        | None -> "none")

                let path = Path.Combine(root, StoreFile.FileName)
                let onDisk = File.ReadAllBytes path
                let plaintextOnDisk = Encoding.UTF8.GetString(onDisk).Contains "sk-file-"

                let expectedProtection =
                    if OperatingSystem.IsWindows() then
                        "dpapi"
                    else
                        "owner-only"

                let list =
                    show (wait (store.List none)) (fun ids -> ids |> List.map string |> String.concat ", ")

                let delete = show (wait (store.Delete id none)) string
                let after = show (wait (store.List none)) (fun ids -> string ids.Length)

                return
                    [
                        fact "probe" "ready" (string (wait (store.Probe none)))
                        fact "protection" expectedProtection store.Protection
                        fact "write" "ok" write
                        fact "read" ("api_key " + secret) read
                        fact "owner-only" "True" (string (LocalPrivateFiles.isOwnerOnly path))
                        fact "plaintext on disk" (string (not (OperatingSystem.IsWindows()))) (string plaintextOnDisk)
                        fact "no staging file" "False" (string (File.Exists(path + ".new")))
                        fact "list" "anthropic" list
                        fact "delete" "True" delete
                        fact "empty" "0" after
                    ]
            finally
                match Path.GetDirectoryName root with
                | null -> ()
                | top when Directory.Exists top -> Directory.Delete(top, true)
                | _ -> ()
        })

let private keyringStore =
    Scenario.task "keyring-credential-store" (fun () ->
        task {
            let facts = LocalHostFacts.read ()

            let keyring =
                LocalKeyring.create
                    facts.Os
                    { LocalKeyring.settings facts with
                        Prompts = Prompts.Never
                    }

            let store = StoreKeyring.create keyring
            let prefix = "aot-" + Guid.NewGuid().ToString("N").Substring(0, 12)
            let id = credentialId (prefix + "/anthropic")

            let show (result: Result<'T, StoreError>) (value: 'T -> string) =
                match result with
                | Ok v -> value v
                | Error e -> "error: " + string e

            let read () =
                show (wait (store.Read id none)) (fun c ->
                    match c with
                    | Some c -> c.Method + " " + c.Secret.Reveal()
                    | None -> "none")

            try
                let probe = string (wait (store.Probe none))
                let missing = read ()

                let write =
                    show
                        (wait (store.Write id (StoredCredential.create "api_key" (SecretText "sk-ring-🔑")) none))
                        (fun () -> "ok")

                let back = read ()

                let listed =
                    show (wait (store.List none)) (fun ids -> string (ids |> List.contains id))

                let delete = show (wait (store.Delete id none)) string
                let gone = read ()

                return
                    [
                        fact "probe" "ready" probe
                        fact "missing" "none" missing
                        fact "write" "ok" write
                        fact "read" "api_key sk-ring-🔑" back
                        fact "listed" "True" listed
                        fact "delete" "True" delete
                        fact "gone" "none" gone
                    ]
            finally
                wait (store.Delete id none) |> ignore
        })
    |> Scenario.where (fun p ->
        p.Os = Fx.Aot.Scenarios.Os.Windows
        || Environment.GetEnvironmentVariable "KEYRING_TESTS" = "1")

let private hostVariables =
    Scenario.lines "auth-host-variables" (fun () ->
        let name = Product.envVar "AOT_SCENARIO_KEY"
        Environment.SetEnvironmentVariable(name, "sk-from-env")

        let read (n: string) =
            match wait (Fx.Effects.Live.SystemVariables.variables.Read n none) with
            | Some value -> value.Reveal()
            | None -> "none"

        [
            fact "set" "sk-from-env" (read name)
            fact "unset" "none" (read (Product.envVar "AOT_SCENARIO_UNSET"))
            fact "not a name" "none" (read "A=B")
        ])

let scenarios: Scenario list =
    [
        storeChoice
        credentials
        authService
        fileStore
        keyringStore
        hostVariables
    ]
