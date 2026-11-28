/// Helpers shared by the auth tests: running effects, extension environments, contributions, and
/// the host's service credentials over fakes.
module Fx.Auth.Tests.Support

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Sdk
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Auth

/// Every wait is bounded, so a hang fails the test instead of the job.
let sync (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

/// An environment with a scope and variants: what `AuthHost.start` and the service's effects need.
type HostEnv =
    {
        Variants: Variants
        Scope: Scope
    }

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<HostEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

/// The auth slots at their defaults, with selections on top.
let hostEnv (selections: (string * Json) list) : HostEnv =
    {
        Variants = FakeVariants.create AuthSlots.all selections
        Scope = Scope.root
    }

let run (effect: Eff<HostEnv, 'T>) : 'T = sync (effect (hostEnv []))

let clock = FakeClock.Create()

/// An extension's environment, as the host builds it.
let extensionEnv (extension: string) : IExtensionEnv =
    {
        Extension = extension
        Clock = clock.Clock
        Capabilities = ExtensionCapabilities.none extension
        Scope = Scope.root
    }
    :> IExtensionEnv

/// A contribution in effect, as the extension host gives it.
let contributed
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
        Env = extensionEnv extension
    }

/// Opens a store definition over these keyring and private files.
let openStore (keyring: Keyring) (files: PrivateFiles) (def: CredentialStoreDef) : CredentialStore =
    let storeEnv: StoreEnv =
        {
            Extension = extensionEnv "test.store"
            Keyring = keyring
            PrivateFiles = files
        }

    sync (StoreEnv.within storeEnv def.Open (hostEnv []))

let credentialId (name: string) =
    match CredentialId.tryCreate name with
    | Ok id -> id
    | Error e -> failwith e

let secretRef (text: string) =
    match SecretRef.tryParse text with
    | Ok reference -> reference
    | Error e -> failwith e

/// The host's pieces over fakes.
type Fakes =
    {
        Keyring: InMemoryKeyring
        Files: InMemoryPrivateFiles
        Variables: (string * string) list
        Http: InMemoryHttp
        Prompts: bool
    }

let fakes () : Fakes =
    {
        Keyring = InMemoryKeyring()
        Files = InMemoryPrivateFiles()
        Variables = []
        Http = InMemoryHttp(clock.Clock, (fun _ -> None))
        Prompts = false
    }

let settings (fakes: Fakes) : AuthSettings =
    {
        Keyring = fakes.Keyring.Keyring
        PrivateFiles = fakes.Files.PrivateFiles
        Variables = InMemoryVariables.create fakes.Variables
        Http = fakes.Http.Http
        Prompts = fakes.Prompts
        Trace = Trace.none
    }

/// The built-ins' contributions: the three stores, the two methods and the two sources.
let stores: Contributed<CredentialStoreDef> list =
    [
        contributed
            Fx.Ext.StoreKeyring.StoreKeyring.id
            CredentialStores.contributionType
            Fx.Ext.StoreKeyring.StoreKeyring.Kind
            Fx.Ext.StoreKeyring.StoreKeyring.def
        contributed
            Fx.Ext.StoreFile.StoreFile.id
            CredentialStores.contributionType
            Fx.Ext.StoreFile.StoreFile.Kind
            Fx.Ext.StoreFile.StoreFile.def
        contributed
            Fx.Ext.StoreEphemeral.StoreEphemeral.id
            CredentialStores.contributionType
            Fx.Ext.StoreEphemeral.StoreEphemeral.Kind
            Fx.Ext.StoreEphemeral.StoreEphemeral.def
    ]

let methods: Contributed<AuthMethod> list =
    let standard = Fx.Ext.AuthStandard.AuthStandard.id

    [
        contributed standard AuthMethods.contributionType "api_key" Fx.Ext.AuthStandard.AuthStandard.apiKey
        contributed standard AuthMethods.contributionType "env" Fx.Ext.AuthStandard.AuthStandard.env
    ]

let sources: Contributed<SecretSource> list =
    let standard = Fx.Ext.AuthStandard.AuthStandard.id

    [
        contributed standard SecretSources.contributionType "store" Fx.Ext.AuthStandard.AuthStandard.storeSource
        contributed standard SecretSources.contributionType "env" Fx.Ext.AuthStandard.AuthStandard.envSource
    ]

/// The service over fakes with these selections, or the startup error.
let tryStart (fakes: Fakes) (selections: (string * Json) list) : Result<AuthService, string> =
    sync (AuthHost.start (settings fakes) stores methods sources (hostEnv selections))

let start (fakes: Fakes) (selections: (string * Json) list) : AuthService =
    match tryStart fakes selections with
    | Ok service -> service
    | Error e -> failtest e
