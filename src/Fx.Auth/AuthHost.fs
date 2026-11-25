namespace Fx.Auth

open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Sdk
open Fx.Variants

/// What the host hands the service credentials (its composition root builds it): the OS secret
/// storage and the host's variables, HTTP and trace.
[<NoEquality; NoComparison>]
type AuthSettings =
    {
        /// This machine's keyring (`LocalKeyring.create`), with prompts only where someone can answer.
        Keyring: Keyring
        /// Private files in the per-user data directory (`LocalPrivateFiles.create`).
        PrivateFiles: PrivateFiles
        Variables: HostVariables
        Http: Http
        /// Whether the keyring may ask the person (`Headless.prompts`).
        Prompts: bool
        Trace: Trace
    }

    override this.ToString() =
        "AuthSettings(" + this.Keyring.Backend + ")"

/// A store a contribution opened, with its probe.
[<NoEquality; NoComparison>]
type OpenedStore =
    {
        Store: CredentialStore
        Status: StoreStatus
    }

    override this.ToString() =
        string this.Store + ": " + string this.Status

/// Starts the host's service credentials from the contributions in effect (spec §11.3–11.5): opens
/// and probes every credential store once, chooses one (`credentials.store`), binds the secret
/// sources (`secrets.sources`) to it, and gathers the auth methods. Every contribution here is
/// privileged: admission already refused any a project brought (spec §16.5).
[<RequireQualifiedAccess>]
module AuthHost =
    /// Opens a contributed store in its extension's environment with the host's secret storage.
    let openStore<'R when 'R :> IScope>
        (settings: AuthSettings)
        (contributed: Contributed<CredentialStoreDef>)
        : Eff<'R, CredentialStore> =
        let storeEnv: StoreEnv =
            {
                Extension = contributed.Env
                Keyring = settings.Keyring
                PrivateFiles = settings.PrivateFiles
            }

        StoreEnv.within storeEnv contributed.Value.Open

    /// Opens and probes every contributed store.
    let probe<'R when 'R :> IScope>
        (settings: AuthSettings)
        (stores: Contributed<CredentialStoreDef> list)
        : Eff<'R, OpenedStore list> =
        eff {
            let mutable opened = []

            for contributed in stores do
                let! store = openStore settings contributed
                let! status = fun (env: 'R) -> store.Probe env.Scope.Cancellation

                opened <- opened @ [ { Store = store; Status = status } ]

            return opened
        }

    /// The service, or why the host cannot keep credentials as configured (an error to show at
    /// startup, never a silent fallback).
    let start<'R when 'R :> IVariants and 'R :> IScope>
        (settings: AuthSettings)
        (stores: Contributed<CredentialStoreDef> list)
        (methods: Contributed<AuthMethod> list)
        (sources: Contributed<SecretSource> list)
        : Eff<'R, Result<AuthService, string>> =
        eff {
            let! setting = Variants.get AuthSlots.store
            let! order = Variants.get AuthSlots.sources
            let! opened = probe settings stores

            let invalid =
                methods
                |> List.choose (fun m ->
                    match AuthMethod.validate m.Value with
                    | Ok() when m.Value.Kind = m.Id -> None
                    | Ok() -> Some(m.By + ": the contribution id is not the method's kind " + m.Value.Kind)
                    | Error e -> Some(m.By + ": " + e))

            let statuses = opened |> List.map (fun o -> o.Store.Kind, o.Status)

            match invalid, StoreChoice.choose setting statuses settings.Prompts with
            | problem :: _, _ -> return Error("an auth method is invalid: " + problem)
            | [], Error e -> return Error e
            | [], Ok choice ->
                let store = (opened |> List.find (fun o -> o.Store.Kind = choice.Kind)).Store

                let unknown =
                    order
                    |> List.filter (fun id -> not (sources |> List.exists (fun s -> s.Value.Id = id)))

                match unknown with
                | id :: _ ->
                    return
                        Error(
                            "secrets.sources names "
                            + id
                            + ", but no enabled extension provides that source (sources: "
                            + (sources |> List.map _.Value.Id |> String.concat ", ")
                            + ")"
                        )
                | [] ->
                    let bound =
                        order
                        |> List.map (fun id ->
                            sources
                            |> List.find (fun s -> s.Value.Id = id)
                            |> SecretResolver.bind store settings.Variables)

                    return
                        Ok
                            {
                                Store = store
                                Choice = choice
                                Methods = methods
                                Secrets = SecretResolver.create bound
                                Variables = settings.Variables
                                Http = settings.Http
                                Trace = settings.Trace
                            }
        }
