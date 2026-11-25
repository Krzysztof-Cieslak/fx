namespace Fx.Sdk

open System
open Fx.Contracts
open Fx.Effects
open Fx.Identity

/// What a person typed into a provider's login form, by field name.
type FormValues = Map<string, string>

/// An interactive login a client runs for a provider (spec §11.3 `AuthFlow`): open a page, show a
/// code to enter elsewhere, or ask the person to paste something. The host forwards it to a client
/// that can run it. Service credentials (T121's `AuthMethod`) use the same flows.
[<RequireQualifiedAccess>]
type AuthFlow =
    /// Open `url` in a browser; the provider redirects to `callback` (a loopback URL the client
    /// listens on) when the person is done.
    | Browser of url: string * callback: string
    /// Show `userCode` and `verifyUri`; the provider is polled every `interval` until the person has
    /// entered the code.
    | DeviceCode of userCode: string * verifyUri: string * interval: TimeSpan
    /// Show `instructions` and ask the person to paste what they say.
    | Paste of instructions: string

    override this.ToString() =
        match this with
        | Browser(url, _) -> "browser " + url
        | DeviceCode(_, uri, _) -> "device code at " + uri
        | Paste _ -> "paste"

/// Says who is connecting (spec §11.2 `IdentityProvider`): a privileged contribution
/// (`IdentityProviders.contributionType`, spec §11.5) of native extensions, e.g.
/// `fx.identity.local-os-user`. The host's authenticator (Fx.Identity.Host) asks the provider a
/// client names in `initialize`, then issues the session token itself: a provider never mints
/// tokens. Every function runs in the provider's extension environment.
type IdentityProvider =
    {
        /// The provider's id (`PrincipalId.isProvider`), e.g. `local-os-user`. Its principals' ids are
        /// namespaced by it, and its contribution's id is the same.
        Id: string
        /// How clients name the provider, e.g. in a list of ways to sign in.
        Display: string
        /// Starts an interactive login (device pairing, OAuth: T505, T506); `None` for a provider
        /// that needs none.
        Login: (FormValues -> Eff<IExtensionEnv, Result<AuthFlow, AuthError>>) option
        /// Who the client is, from what it presented and what the host established about its
        /// connection. A principal of this provider (`Principal.Provider = Id`) that validates
        /// (`Principal.validate`), or why not: never an exception for a bad credential.
        Verify: Credential -> Eff<IExtensionEnv, Result<Principal, AuthError>>
        /// Renews a credential a client presents (`Credential.Presented`) before it expires, e.g. an
        /// OAuth refresh; `None` if the provider's credentials do not expire.
        Refresh: (Json -> Eff<IExtensionEnv, Result<Json, AuthError>>) option
        /// What a principal this provider vouched for may do on this host; `[]` for none, and for
        /// principals of other providers.
        Roles: Principal -> Eff<IExtensionEnv, Role list>
    }

    override this.ToString() = "IdentityProvider(" + this.Id + ")"

module IdentityProvider =
    /// Problems with a provider's declaration, if any.
    let validate (provider: IdentityProvider) : Result<unit, string> =
        if not (PrincipalId.isProvider provider.Id) then
            Error(
                "the provider id "
                + provider.Id
                + " is not lowercase segments joined by '.' or '-'"
            )
        elif String.IsNullOrWhiteSpace provider.Display then
            Error "the display name cannot be empty"
        else
            Ok()

/// The contribution type of identity providers (spec §16.2 "Identity and secrets").
[<RequireQualifiedAccess>]
module IdentityProviders =
    /// Identity providers, by id (the contribution id is the provider's). Code, so in process only
    /// (native extensions); privileged (spec §11.5, §16.5): only extensions from the binary, host
    /// config or policy may say who is connecting.
    let contributionType: ContributionType<IdentityProvider> =
        ContributionType.inProcess
            "identity.provider"
            "Says who a connecting client is (spec §11.2 IdentityProvider)."
            ContributionKind.Code
        |> ContributionType.privileged

    /// The identity contribution types, for the host's catalog (`Catalog.create`).
    let contributionTypes: ContributionTypeInfo list =
        [ ContributionType.info contributionType ]
