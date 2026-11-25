/// `fx.auth.standard` (spec §11.3–11.4, §16.4): the auth methods every API-key provider needs and the
/// built-in secret sources. A native extension built only on the public SDK. See README.md and
/// docs/modules/auth.md.
///
/// - `api_key`: a key the person pastes (`auth login`), kept in the credential store, or taken from a
///   `secret:` reference (`key`); sent as the provider's settings say (`header` and `scheme`, or
///   `query`).
/// - `env`: a key in one of the host's environment variables (`variable`), read at each request;
///   nothing is stored.
/// - Secret sources `store` (the credential store `credentials.store` selected) and `env`
///   (`<PREFIX>SECRET_<NAME>` variables).
module Fx.Ext.AuthStandard.AuthStandard

open System
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk
open Fx.Ext.AuthStandard.Settings

let private ret (value: 'T) : Eff<IAuthEnv, 'T> = Eff.ret value

let private resolution (placement: Placement) (key: string) : Result<Resolution, CredentialError> =
    match checkKey key with
    | Ok key ->
        Ok
            {
                Auth = resolved placement key
                Updated = None
            }
    | Error e -> Error(CredentialError.Invalid e)

let private settingsOf (kind: string) (settings: Json) : Result<KeySettings, CredentialError> =
    read kind settings
    |> Result.mapError (fun e -> CredentialError.Invalid("the provider's " + kind + " settings: " + e))

let private loginHint (provider: string) =
    "run `" + Product.binaryName + " auth login " + provider + "`"

/// `api_key`: a key pasted once and kept in the credential store, or a `secret:` reference.
let apiKey: AuthMethod =
    {
        Kind = ApiKey
        Label = "API key"
        CheckSettings = fun settings -> read ApiKey settings |> Result.map ignore
        Prompts = []
        Authorize =
            Some(fun request ->
                ret (
                    settingsOf ApiKey request.Settings
                    |> Result.map (fun settings ->
                        let instructions =
                            settings.Instructions
                            |> Option.defaultValue ("Paste the API key for " + request.Provider + ".")

                        {
                            Flow = AuthFlow.Paste instructions
                            State = SecretText ""
                        })
                ))
        Complete =
            Some(fun completion ->
                ret (
                    match settingsOf ApiKey completion.Settings, completion.Result with
                    | Error e, _ -> Error e
                    | Ok _, FlowResult.Pasted pasted ->
                        match checkKey (pasted.Reveal()) with
                        | Ok key -> Ok(StoredCredential.create ApiKey (SecretText key))
                        | Error e -> Error(CredentialError.Invalid e)
                    | Ok _, other -> Error(CredentialError.Invalid("an API key is pasted, not " + string other))
                ))
        Resolve =
            fun request ->
                eff {
                    match settingsOf ApiKey request.Settings with
                    | Error e -> return Error e
                    | Ok settings ->
                        match settings.Key, request.Credential with
                        | Some reference, _ ->
                            match! Secrets.resolve reference with
                            | Ok secret -> return resolution settings.Placement (secret.Reveal())
                            | Error(SecretNotFound _) ->
                                return
                                    Error(
                                        CredentialError.Missing(
                                            string reference
                                            + " is not set: store it, or export "
                                            + Product.envVar "SECRET_<NAME>"
                                        )
                                    )
                            | Error e -> return Error(CredentialError.Unavailable(string e))
                        | None, Some credential -> return resolution settings.Placement (credential.Secret.Reveal())
                        | None, None ->
                            return
                                Error(
                                    CredentialError.Missing(
                                        "no API key for " + request.Provider + ": " + loginHint request.Provider
                                    )
                                )
                }
    }

/// `env`: a key in one of the host's environment variables, read at each request.
let env: AuthMethod =
    {
        Kind = Env
        Label = "Environment variable"
        CheckSettings = fun settings -> read Env settings |> Result.map ignore
        Prompts = []
        Authorize = None
        Complete = None
        Resolve =
            fun request ->
                eff {
                    match settingsOf Env request.Settings with
                    | Error e -> return Error e
                    | Ok settings ->
                        let variable = settings.Variable |> Option.defaultValue ""

                        match! HostVariables.read variable with
                        | Some value -> return resolution settings.Placement (value.Reveal())
                        | None -> return Error(CredentialError.Missing(variable + " is not set"))
                }
    }

/// The variable the `env` source reads for `secret:<name>`: the product's prefix, `SECRET_`, and the
/// name in upper case with `.`, `-` and `/` as `_` (`secret:github-token` is `FX_SECRET_GITHUB_TOKEN`).
/// Only variables meant for the product are reachable, so configuration cannot name any variable of
/// the host's.
let variableOf (reference: SecretRef) : string =
    let name =
        reference.Name.ToUpperInvariant()
        |> String.map (fun c -> if Char.IsAsciiLetterOrDigit c then c else '_')

    Product.envVar ("SECRET_" + name)

/// The `store` source: `secret:<name>` is the secret of the credential `<name>` (lowercase) in the
/// credential store.
let storeSource: SecretSource =
    {
        Id = "store"
        Description = "Secrets in the credential store (credentials.store), by name."
        Resolve =
            fun reference ->
                eff {
                    match! CredentialStore.read (CredentialId.ofSecret reference) with
                    | Ok credential -> return Ok(credential |> Option.map _.Secret)
                    | Error e -> return Error(string e)
                }
    }

/// The `env` source: `secret:<name>` is the variable `variableOf` names.
let envSource: SecretSource =
    {
        Id = "env"
        Description =
            "Secrets in the host's environment, as "
            + Product.envVar "SECRET_<NAME>"
            + " variables."
        Resolve =
            fun reference ->
                eff {
                    let! value = HostVariables.read (variableOf reference)
                    return Ok value
                }
    }

/// The extension's id: `fx.auth.standard`.
let id = Product.extensionId "auth.standard"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "The api_key and env auth methods, and the store and env secret sources."
        Contributions =
            [
                Declare.contribution AuthMethods.contributionType ApiKey
                Declare.contribution AuthMethods.contributionType Env
                Declare.contribution SecretSources.contributionType storeSource.Id
                Declare.contribution SecretSources.contributionType envSource.Id
            ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate =
            Eff.ret (
                Activation.create
                    [
                        Contribution.create AuthMethods.contributionType ApiKey apiKey
                        Contribution.create AuthMethods.contributionType Env env
                        Contribution.create SecretSources.contributionType storeSource.Id storeSource
                        Contribution.create SecretSources.contributionType envSource.Id envSource
                    ]
                    []
            )
    }
