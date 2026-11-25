namespace Fx.Sdk

open System
open Fx.Contracts
open Fx.Effects
open Fx.Models

/// Something an auth method asks before its flow (spec §11.3 `Prompts`), e.g. a region or an
/// account name. Never a secret: secrets are pasted (`AuthFlow.Paste`), so they travel only as
/// `SecretText`.
type FormField =
    {
        /// The key of its value in `FormValues`.
        Name: string
        Label: string
        Required: bool
    }

    override this.ToString() = "field " + this.Name

/// Why a provider's credentials could not be had (an expected failure, never an exception).
[<RequireQualifiedAccess>]
type CredentialError =
    /// Nothing to authenticate with (no stored credential, an unset variable); the hint says how to
    /// provide one. The host tries the provider's next auth method.
    | Missing of hint: string
    /// What was given, stored or configured is wrong (an empty key, settings without a header).
    | Invalid of reason: string
    /// The credential expired and cannot be renewed: log in again (the hint says how).
    | Expired of hint: string
    /// It could not be had now (a locked store, an unreachable token endpoint).
    | Unavailable of reason: string

    override this.ToString() =
        match this with
        | Missing hint -> "no credentials: " + hint
        | Invalid reason -> "invalid credentials: " + reason
        | Expired hint -> "the credentials expired: " + hint
        | Unavailable reason -> "credentials unavailable: " + reason

/// A login's start, from the client that runs its flow (spec §11.3 `Authorize`).
type LoginRequest =
    {
        /// The provider's id.
        Provider: string
        /// The provider's settings for the method (`AuthMethodRef.Settings`).
        Settings: Json
        /// What the person entered for the method's `Prompts`.
        Form: FormValues
        /// A loopback URL the client listens on for a `Browser` flow's redirect
        /// (`http://127.0.0.1:<port>/…`); `None` if it cannot run one.
        Callback: string option
    }

    override this.ToString() = "login to " + this.Provider

/// The flow a client runs for a login, and the method's own state for completing it (a PKCE
/// verifier, a device code). The state stays on the host: never sent to the client, never logged.
[<NoEquality; NoComparison>]
type AuthStart =
    {
        Flow: AuthFlow
        State: SecretText
    }

    override this.ToString() = "start " + string this.Flow

/// What came back from a login's flow (spec §11.3 `FlowResult`).
[<RequireQualifiedAccess>]
type FlowResult =
    /// What the person pasted (`AuthFlow.Paste`).
    | Pasted of SecretText
    /// The query of the redirect to the flow's callback (`AuthFlow.Browser`): `code` and `state`, or
    /// `error`.
    | Redirected of query: (string * string) list
    /// A device-code flow: nothing comes from the client; the method polls its provider until the
    /// person has entered the code, as long as the call's scope allows.
    | Device
    /// The method has no flow (`Authorize` is `None`): the form is all it needs.
    | NoFlow

    // The pasted text and the redirect's values (an authorization code) are secrets.
    override this.ToString() =
        match this with
        | Pasted _ -> "pasted (redacted)"
        | Redirected query -> "redirected (" + (query |> List.map fst |> String.concat ", ") + ")"
        | Device -> "device code"
        | NoFlow -> "no flow"

/// A login's end (spec §11.3 `Complete`): what the flow gave, with what the start had.
[<NoEquality; NoComparison>]
type LoginCompletion =
    {
        Provider: string
        Settings: Json
        Form: FormValues
        /// `AuthStart.State`; empty for a method without a flow.
        State: SecretText
        Result: FlowResult
    }

    override this.ToString() =
        "complete login to " + this.Provider + " (" + string this.Result + ")"

/// What resolving a provider's credentials for one call starts from.
[<NoEquality; NoComparison>]
type ResolveRequest =
    {
        Provider: string
        Settings: Json
        /// The provider's stored credential, if the method stores one and it made it.
        Credential: StoredCredential option
    }

    override this.ToString() = "resolve " + this.Provider

/// What an auth method resolved for a call: what to add to the request, and a renewed credential for
/// the host to store (an OAuth refresh; the host is the single refresher, spec §11.3).
[<NoEquality; NoComparison>]
type Resolution =
    {
        Auth: ResolvedAuth
        Updated: StoredCredential option
    }

    override this.ToString() = string this.Auth

/// The environment auth methods run in (spec §11.3 `IAuthEnv`): their extension's environment, the
/// host's `secret:` references, its environment variables and its HTTP (for token endpoints). Auth
/// methods are privileged contributions (spec §16.5). They never get the credential store: the host
/// reads and writes credentials for them.
type IAuthEnv =
    inherit IExtensionEnv
    inherit ISecrets
    inherit IHostVariables
    inherit IHttp

/// The environment the host runs an auth method in.
type AuthEnv =
    {
        Extension: IExtensionEnv
        Secrets: Secrets
        HostVariables: HostVariables
        Http: Http
    }

    interface IAuthEnv

    interface IExtensionEnv with
        member env.Extension = env.Extension.Extension

    interface IClock with
        member env.Clock = env.Extension.Clock

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Extension.Scope

        member env.WithScope scope =
            { env with
                Extension = env.Extension.WithScope scope
            }
            :> IExtensionEnv

    interface ISecrets with
        member env.Secrets = env.Secrets

    interface IHostVariables with
        member env.HostVariables = env.HostVariables

    interface IHttp with
        member env.Http = env.Http

    override env.ToString() =
        "AuthEnv(" + env.Extension.Extension + ")"

[<RequireQualifiedAccess>]
module AuthEnv =
    /// Runs auth-method code in its environment with the caller's scope.
    let within (authEnv: AuthEnv) (effect: Eff<IAuthEnv, 'T>) : Eff<#IScope, 'T> =
        fun caller ->
            effect
                { authEnv with
                    Extension = authEnv.Extension.WithScope caller.Scope
                }

/// How to authenticate to services of one kind (spec §11.3 `AuthMethod`): `api_key`, `env`,
/// `oauth_device`, … A provider lists the methods it takes (`ProviderDef.Auth`, kinds and settings);
/// extensions contribute the methods under `AuthMethods.contributionType` (privileged), with the
/// kind as the contribution id. Every function runs in the method's `IAuthEnv` with the caller's
/// scope, and fails with a `CredentialError`, never an exception.
///
/// A method that can `Complete` a login keeps a credential: the host stores what `Complete` returns
/// (under `AuthMethod.credentialId`) and hands it to `Resolve`. A method without `Complete` (`env`)
/// resolves from what the host has (its variables, `secret:` references).
[<NoEquality; NoComparison>]
type AuthMethod =
    {
        /// What providers name in `AuthMethodRef.Kind`, e.g. `api_key` (`StoredCredential.MethodPattern`).
        Kind: string
        /// How clients name it, e.g. "API key".
        Label: string
        /// Checks a provider's settings for the method (`AuthMethodRef.Settings`): an error says what
        /// is wrong, for whoever wrote the provider.
        CheckSettings: Json -> Result<unit, string>
        /// What to ask before the flow (spec §11.3 `Prompts`); never secrets.
        Prompts: FormField list
        /// Starts a login's flow; `None` for a method that needs none (the form is all).
        Authorize: (LoginRequest -> Eff<IAuthEnv, Result<AuthStart, CredentialError>>) option
        /// Ends a login with the credential to store; `None` for a method that stores nothing.
        Complete: (LoginCompletion -> Eff<IAuthEnv, Result<StoredCredential, CredentialError>>) option
        /// What to add to one request. `Missing` when there is nothing to authenticate with, so the
        /// host tries the provider's next method.
        Resolve: ResolveRequest -> Eff<IAuthEnv, Result<Resolution, CredentialError>>
    }

    override this.ToString() = "AuthMethod(" + this.Kind + ")"

module AuthMethod =
    /// The settings member every method may have: the credential's id in the store, if not the
    /// provider's id.
    [<Literal>]
    let CredentialSetting = "credential"

    /// Problems with a method's declaration, if any.
    let validate (method: AuthMethod) : Result<unit, string> =
        if
            not (
                Text.RegularExpressions.Regex.IsMatch(
                    method.Kind,
                    StoredCredential.MethodPattern,
                    Text.RegularExpressions.RegexOptions.CultureInvariant
                )
            )
            || method.Kind = StoredCredential.PlainSecret
        then
            Error(
                "the method kind "
                + method.Kind
                + " is not lowercase letters, digits and _ (nor `secret`)"
            )
        elif String.IsNullOrWhiteSpace method.Label then
            Error "the label cannot be empty"
        elif method.Prompts |> List.exists (fun f -> String.IsNullOrWhiteSpace f.Name) then
            Error "every form field has a name"
        else
            Ok()

    /// Whether the method keeps a credential in the store (it can complete a login).
    let stores (method: AuthMethod) : bool = method.Complete.IsSome

    /// The id a provider's credential is stored under: the settings' `credential`, else the
    /// provider's id.
    let credentialId (provider: string) (settings: Json) : Result<CredentialId, string> =
        match settings with
        | Json.Object members ->
            match members |> List.tryFind (fun (name, _) -> name = CredentialSetting) with
            | Some(_, Json.String name) -> CredentialId.tryCreate name
            | Some _ -> Error "settings.credential must be a string"
            | None -> CredentialId.tryCreate provider
        | _ -> CredentialId.tryCreate provider

/// Codecs of the login contracts that cross the client protocol (`auth/*`, T129).
[<RequireQualifiedAccess>]
module AuthCodecs =
    let formField: Codec<FormField> =
        Codec.record {
            let! name =
                Codec.field "name" (fun (f: FormField) -> f.Name) (Codec.string |> Codec.nonEmpty)

            and! label = Codec.field "label" (fun f -> f.Label) Codec.string
            and! required = Codec.defaultField "required" (fun f -> f.Required) true Codec.bool

            return
                {
                    Name = name
                    Label = label
                    Required = required
                }
        }
        |> Codec.describe "A non-secret field an auth method asks for before its flow."
        |> Codec.named "FormField"

    /// `{"kind": "browser" | "deviceCode" | "paste", …}`; a device code's interval in milliseconds.
    let authFlow: Codec<AuthFlow> =
        Codec.union "kind" {
            let! browser =
                Codec.case2
                    "browser"
                    (fun url callback -> AuthFlow.Browser(url, callback))
                    (Field.required "url" Codec.string)
                    (Field.required "callback" Codec.string)

            and! device =
                Codec.case3
                    "deviceCode"
                    (fun code uri (interval: int64) ->
                        AuthFlow.DeviceCode(code, uri, TimeSpan.FromMilliseconds(float interval)))
                    (Field.required "userCode" Codec.string)
                    (Field.required "verifyUri" Codec.string)
                    (Field.required "intervalMs" (Codec.int64 |> Codec.between 0L 3_600_000L))

            and! paste =
                Codec.case1 "paste" AuthFlow.Paste (Field.required "instructions" Codec.string)

            return
                fun flow ->
                    match flow with
                    | AuthFlow.Browser(url, callback) -> browser url callback
                    | AuthFlow.DeviceCode(code, uri, interval) -> device code uri (int64 interval.TotalMilliseconds)
                    | AuthFlow.Paste instructions -> paste instructions
        }
        |> Codec.describe "An interactive login a client runs: open a page, show a code, or paste."
        |> Codec.named "AuthFlow"

    /// `{"kind": "pasted" | "redirected" | "device" | "noFlow", …}`. A pasted secret is written (the
    /// client sends it to the host); previews never show it.
    let flowResult: Codec<FlowResult> =
        Codec.union "kind" {
            let! pasted =
                Codec.case1 "pasted" FlowResult.Pasted (Field.required "secret" StoredCredential.secretCodec)

            and! redirected =
                Codec.case1
                    "redirected"
                    FlowResult.Redirected
                    (Field.required
                        "query"
                        (Codec.keyValues Codec.string
                         |> Codec.withPreview (fun limits query ->
                             Preview.atom limits (query |> List.map fst |> String.concat ", "))))

            and! device = Codec.case0 "device" FlowResult.Device
            and! noFlow = Codec.case0 "noFlow" FlowResult.NoFlow

            return
                fun result ->
                    match result with
                    | FlowResult.Pasted secret -> pasted secret
                    | FlowResult.Redirected query -> redirected query
                    | FlowResult.Device -> device
                    | FlowResult.NoFlow -> noFlow
        }
        |> Codec.describe "What came back from a login's flow."
        |> Codec.named "FlowResult"

/// The contribution type of auth methods, and the catalog entries of service credentials.
[<RequireQualifiedAccess>]
module AuthMethods =
    /// Auth methods, by kind (the contribution id is the kind). Code, so in process only (native
    /// extensions); privileged (spec §11.5, §16.5).
    let contributionType: ContributionType<AuthMethod> =
        ContributionType.inProcess
            "auth-method"
            "Authenticates to external services: logins, stored credentials, per-request auth (spec §11.3)."
            ContributionKind.Code
        |> ContributionType.privileged

    /// The service-credential contribution types (auth methods, credential stores, secret sources),
    /// for the host's catalog (`Catalog.create`).
    let contributionTypes: ContributionTypeInfo list =
        [
            ContributionType.info contributionType
            ContributionType.info CredentialStores.contributionType
            ContributionType.info SecretSources.contributionType
        ]
