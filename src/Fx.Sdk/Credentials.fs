namespace Fx.Sdk

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects

/// The name of a credential in a store (spec §11.4): 1–128 characters of lowercase letters, digits
/// and `. _ - /`, starting with a letter or digit. A provider's credentials are under its id
/// (`anthropic`) unless its auth method's settings name another (`credential`). Names are lowercase
/// because the Windows Credential Manager compares them without case: every store then agrees on
/// which names are the same.
type CredentialId =
    private
    | CredentialId of string

    member this.Name =
        let (CredentialId name) = this
        name

    override this.ToString() = this.Name

module CredentialId =
    let private isNameChar (c: char) =
        Char.IsAsciiDigit c
        || (c >= 'a' && c <= 'z')
        || c = '.'
        || c = '_'
        || c = '-'
        || c = '/'

    let tryCreate (name: string) : Result<CredentialId, string> =
        if String.IsNullOrEmpty name || name.Length > 128 then
            Error "a credential id has 1 to 128 characters"
        elif not (Char.IsAsciiDigit name[0] || (name[0] >= 'a' && name[0] <= 'z')) then
            Error "a credential id starts with a lowercase letter or a digit"
        elif not (Seq.forall isNameChar name) then
            Error "a credential id has only lowercase letters, digits and . _ - /"
        else
            Ok(CredentialId name)

    /// The credential a `secret:<name>` reference names in a store: the same name in lowercase
    /// (secret names are case-insensitive, as stores compare them).
    let ofSecret (reference: SecretRef) : CredentialId =
        CredentialId(reference.Name.ToLowerInvariant())

    let codec: Codec<CredentialId> =
        Codec.string
        |> Codec.tryMap tryCreate (fun id -> id.Name)
        |> Codec.describe "A credential's name in a store: lowercase letters, digits and . _ - /."
        |> Codec.named "CredentialId"

/// A credential as a store keeps it (spec §11.3 `StoredCredential`): the auth method that made it,
/// the secret, and facts that are not secret. Its `ToString` never shows the secret, and neither
/// does its codec's preview; its JSON does, so it goes only into stores, never into events, logs or
/// traces (spec §18: secrets are referenced, never copied).
type StoredCredential =
    {
        /// The auth method that made it (`api_key`, `oauth_device`, …), or `secret` for a plain
        /// secret (`secret:<name>`).
        Method: string
        /// The secret: an API key, a token, or the method's own token set as JSON text.
        Secret: SecretText
        /// Facts that are not secret, for display and refresh: an account, an expiry. At most 16,
        /// names of at most 64 and values of at most 512 characters.
        Meta: Map<string, string>
    }

    override this.ToString() = this.Method + " credential (redacted)"

module StoredCredential =
    /// What a plain secret is stored as (`secret:<name>`, or a value someone stored in a keyring by
    /// hand): its `Method`.
    [<Literal>]
    let PlainSecret = "secret"

    /// The pattern of auth method kinds (and `secret`).
    [<Literal>]
    let MethodPattern = "^[a-z][a-z0-9_]*$"

    /// A credential made by a method, without facts.
    let create (methodKind: string) (secret: SecretText) : StoredCredential =
        {
            Method = methodKind
            Secret = secret
            Meta = Map.empty
        }

    /// A plain secret.
    let ofSecret (secret: SecretText) : StoredCredential = create PlainSecret secret

    /// A secret's text as JSON: a string whose preview never shows it.
    let secretCodec: Codec<SecretText> =
        Codec.string
        |> Codec.map SecretText (fun secret -> secret.Reveal())
        |> Codec.withPreview (fun limits _ -> Preview.atom limits "(redacted)")
        |> Codec.describe "A secret's text. Previews never show it."

    let private meta: Codec<Map<string, string>> =
        Codec.stringMap (Codec.string |> Codec.maxLength 512)
        |> Codec.refine (fun facts ->
            if facts.Count > 16 then
                Error "at most 16 facts"
            elif facts |> Map.exists (fun name _ -> name.Length = 0 || name.Length > 64) then
                Error "fact names have 1 to 64 characters"
            else
                Ok())

    /// The JSON a store keeps: `{"method", "secret", "meta"}`. It writes the secret: use it only in
    /// stores.
    let codec: Codec<StoredCredential> =
        Codec.record {
            let! methodKind =
                Codec.field
                    "method"
                    (fun (c: StoredCredential) -> c.Method)
                    (Codec.string |> Codec.pattern MethodPattern)

            and! secret = Codec.field "secret" (fun c -> c.Secret) secretCodec
            and! facts = Codec.defaultField "meta" (fun c -> c.Meta) Map.empty meta

            return
                {
                    Method = methodKind
                    Secret = secret
                    Meta = facts
                }
        }
        |> Codec.describe "A credential as a store keeps it: the auth method that made it, the secret, and facts."
        |> Codec.named "StoredCredential"

    /// The canonical JSON text a store writes (`Json.serializeCanonical`).
    let toText (credential: StoredCredential) : string =
        Json.serializeCanonical (codec.Encode credential)

    /// Reads what `toText` wrote; `None` if the text is not a stored credential's JSON.
    let tryOfText (text: string) : StoredCredential option =
        match Codec.decodeText codec text with
        | Ok credential -> Some credential
        | Error _ -> None

/// What a store's probe found.
[<RequireQualifiedAccess>]
type StoreStatus =
    /// Reads and writes work without asking the user.
    | Ready
    /// The store exists but is locked: the hint says how to unlock it, or that the first use asks.
    | Locked of hint: string
    /// The store cannot be used on this machine: the reason says why, and what to do instead.
    | Unavailable of reason: string

    override this.ToString() =
        match this with
        | Ready -> "ready"
        | Locked hint -> "locked: " + hint
        | Unavailable reason -> "unavailable: " + reason

/// A store operation that failed (an expected failure).
[<RequireQualifiedAccess>]
type StoreError =
    | Unavailable of reason: string
    /// The store is locked and nobody can be asked to unlock it now; the hint says how.
    | Locked of hint: string
    /// The credential is larger than the store keeps (in bytes of its JSON).
    | TooLarge of limit: int
    /// What the store holds cannot be read as credentials (a damaged file).
    | Corrupt of reason: string
    /// The OS or the store reported an error; the reason is for display.
    | Failed of reason: string

    override this.ToString() =
        match this with
        | Unavailable reason -> "the credential store is unavailable: " + reason
        | Locked hint -> "the credential store is locked: " + hint
        | TooLarge limit -> "the credential is larger than the store's limit of " + string limit + " bytes"
        | Corrupt reason -> "the credential store is damaged: " + reason
        | Failed reason -> "the credential store failed: " + reason

/// A credential store (spec §11.4): keeps `StoredCredential`s under `CredentialId`s on the host.
/// The capability the host's auth service and the `store` secret source use; contributed stores are
/// opened into one (`CredentialStoreDef.Open`). Data in and out, the cancellation token last; values
/// never leave the host (spec §18). A store passes `CredentialStoreConformance`
/// (tests/Fx.Auth.Tests/Conformance).
type CredentialStore =
    {
        /// The store's kind: `keyring`, `file`, `ephemeral`, or a plugin's.
        Kind: string
        /// How it protects what it keeps, for display: `keychain`, `credman`, `secret-service`,
        /// `dpapi`, `owner-only`, `memory`, …
        Protection: string
        /// Whether it can be used now, without side effects.
        Probe: CancellationToken -> Task<StoreStatus>
        /// The credential under an id; `None` if there is none.
        Read: CredentialId -> CancellationToken -> Task<Result<StoredCredential option, StoreError>>
        /// Stores a credential under an id, replacing any there, whole or not at all.
        Write: CredentialId -> StoredCredential -> CancellationToken -> Task<Result<unit, StoreError>>
        /// Deletes the credential under an id; `false` if there was none.
        Delete: CredentialId -> CancellationToken -> Task<Result<bool, StoreError>>
        /// The ids with a credential, in ordinal order.
        List: CancellationToken -> Task<Result<CredentialId list, StoreError>>
    }

    override this.ToString() =
        "CredentialStore(" + this.Kind + ", " + this.Protection + ")"

/// An environment with the host's credential store.
type ICredentialStore =
    abstract CredentialStore: CredentialStore

[<RequireQualifiedAccess>]
module CredentialStore =
    /// The pattern of store kinds.
    [<Literal>]
    let KindPattern = "^[a-z][a-z0-9-]*$"

    let probe<'R when 'R :> ICredentialStore and 'R :> IScope> : Eff<'R, StoreStatus> =
        fun env -> env.CredentialStore.Probe env.Scope.Cancellation

    let read<'R when 'R :> ICredentialStore and 'R :> IScope>
        (id: CredentialId)
        : Eff<'R, Result<StoredCredential option, StoreError>> =
        fun env -> env.CredentialStore.Read id env.Scope.Cancellation

    let write<'R when 'R :> ICredentialStore and 'R :> IScope>
        (id: CredentialId)
        (credential: StoredCredential)
        : Eff<'R, Result<unit, StoreError>> =
        fun env -> env.CredentialStore.Write id credential env.Scope.Cancellation

    let delete<'R when 'R :> ICredentialStore and 'R :> IScope> (id: CredentialId) : Eff<'R, Result<bool, StoreError>> =
        fun env -> env.CredentialStore.Delete id env.Scope.Cancellation

    let list<'R when 'R :> ICredentialStore and 'R :> IScope> : Eff<'R, Result<CredentialId list, StoreError>> =
        fun env -> env.CredentialStore.List env.Scope.Cancellation

/// The environment a credential store is opened in (spec §11.4): its extension's environment and the
/// host's secret storage, the OS keyring and private files. Stores are privileged contributions
/// (spec §16.5), so only extensions from the binary, host config or policy get it.
type IStoreEnv =
    inherit IExtensionEnv
    inherit IKeyring
    inherit IPrivateFiles

/// A kind of credential store an extension contributes (`CredentialStores.contributionType`, with
/// the kind as the contribution id): `Open` makes the store in the host's store environment, once,
/// when the host starts; the store keeps what it needs of that environment.
[<NoEquality; NoComparison>]
type CredentialStoreDef =
    {
        /// What `credentials.store` selects, e.g. `keyring`.
        Kind: string
        Description: string
        Open: Eff<IStoreEnv, CredentialStore>
    }

    override this.ToString() = "credential store " + this.Kind

/// The environment the host opens a store in.
type StoreEnv =
    {
        /// The store's extension's environment (its id, clock and scope).
        Extension: IExtensionEnv
        Keyring: Keyring
        PrivateFiles: PrivateFiles
    }

    interface IStoreEnv

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

    interface IKeyring with
        member env.Keyring = env.Keyring

    interface IPrivateFiles with
        member env.PrivateFiles = env.PrivateFiles

    override env.ToString() =
        "StoreEnv(" + env.Extension.Extension + ")"

[<RequireQualifiedAccess>]
module StoreEnv =
    /// Runs store code in the store's environment with the caller's scope.
    let within (storeEnv: StoreEnv) (effect: Eff<IStoreEnv, 'T>) : Eff<#IScope, 'T> =
        fun caller ->
            effect
                { storeEnv with
                    Extension = storeEnv.Extension.WithScope caller.Scope
                }

/// The environment secret sources run in: their extension's environment, the host's credential
/// store (the one `credentials.store` selected) and the host's environment variables.
type ISecretSourceEnv =
    inherit IExtensionEnv
    inherit ICredentialStore
    inherit IHostVariables

/// Resolves `secret:<name>` references from one place (spec §11.4 "sources: store, env, command,
/// plugins"): `Some` value, `None` if it does not have the secret (the next source is asked), or why
/// it could not tell. The host asks its sources in the order `secrets.sources` gives.
[<NoEquality; NoComparison>]
type SecretSource =
    {
        /// What `secrets.sources` lists, e.g. `store`, `env`.
        Id: string
        Description: string
        Resolve: SecretRef -> Eff<ISecretSourceEnv, Result<SecretText option, string>>
    }

    override this.ToString() = "secret source " + this.Id

/// The environment the host runs a secret source in.
type SecretSourceEnv =
    {
        Extension: IExtensionEnv
        CredentialStore: CredentialStore
        HostVariables: HostVariables
    }

    interface ISecretSourceEnv

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

    interface ICredentialStore with
        member env.CredentialStore = env.CredentialStore

    interface IHostVariables with
        member env.HostVariables = env.HostVariables

    override env.ToString() =
        "SecretSourceEnv(" + env.Extension.Extension + ")"

[<RequireQualifiedAccess>]
module SecretSourceEnv =
    /// Runs a source in its environment with the caller's scope.
    let within (sourceEnv: SecretSourceEnv) (effect: Eff<ISecretSourceEnv, 'T>) : Eff<#IScope, 'T> =
        fun caller ->
            effect
                { sourceEnv with
                    Extension = sourceEnv.Extension.WithScope caller.Scope
                }

/// The contribution type of credential stores (spec §16.2 "Identity and secrets").
[<RequireQualifiedAccess>]
module CredentialStores =
    /// Credential stores, by kind (the contribution id is the kind). Code, so in process only (native
    /// extensions); privileged (spec §11.5, §16.5): only extensions from the binary, host config or
    /// policy may keep credentials.
    let contributionType: ContributionType<CredentialStoreDef> =
        ContributionType.inProcess
            "credential-store"
            "Keeps service credentials on the host (spec §11.4 CredentialStore)."
            ContributionKind.Code
        |> ContributionType.privileged

/// The contribution type of secret sources (spec §16.2 "Identity and secrets").
[<RequireQualifiedAccess>]
module SecretSources =
    /// Secret sources, by id (the contribution id). Code, so in process only; privileged.
    let contributionType: ContributionType<SecretSource> =
        ContributionType.inProcess
            "secret-source"
            "Resolves secret: references (spec §11.4 secret sources)."
            ContributionKind.Code
        |> ContributionType.privileged
