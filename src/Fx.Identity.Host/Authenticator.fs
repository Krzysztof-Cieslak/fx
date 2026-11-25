namespace Fx.Identity.Host

open System
open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Sdk
open Fx.Identity

/// What the host knows about a connection when its client says `initialize`: established by the
/// transport, never by the client.
type ConnectionFacts =
    {
        Connection: Connection
        /// The id of a session token the transport already verified: the bearer token the client
        /// presented to the local listener (`LocalAccess.Bearer.SessionToken`).
        Session: string option
    }

    override this.ToString() =
        match this.Session with
        | Some id -> string this.Connection + ", session " + id
        | None -> string this.Connection

module ConnectionFacts =
    /// A connection with nothing but what the host established.
    let ofConnection (connection: Connection) : ConnectionFacts =
        {
            Connection = connection
            Session = None
        }

    /// Embedded mode: the client is in the host's process.
    let inProcess: ConnectionFacts = ofConnection Connection.inProcess

/// The outcome of a sign-in: the client's session and, if it was just issued, its token.
type SignedIn =
    {
        Session: ClientSession
        /// The new token, to give the client once; `None` when the client resumed a session.
        Token: string option
    }

    // Never shows the token.
    override this.ToString() =
        (if this.Token.IsSome then "issued " else "resumed ") + string this.Session

/// Says who a client is (spec §3: principal auth via identity-provider extensions, then a
/// host-issued session token): asks the provider the client names, or resumes the session of a token
/// it presents, and issues tokens. The providers come from the extension host
/// (`ExtensionHost.contributions IdentityProviders.contributionType`), so only extensions from the
/// binary, host config or policy can vouch for anyone (spec §11.5).
type Authenticator =
    {
        Providers: Contributed<IdentityProvider> list
        Host: HostIdentity
        Tokens: SessionTokens
    }

    override this.ToString() =
        let ids = this.Providers |> List.map (fun p -> p.Value.Id) |> String.concat ", "
        $"Authenticator([{ids}], {this.Host})"

module Authenticator =
    /// The longest client name kept in a client id.
    [<Literal>]
    let MaxClientName = 32

    let create (providers: Contributed<IdentityProvider> list) (host: HostIdentity) (tokens: SessionTokens) =
        {
            Providers = providers
            Host = host
            Tokens = tokens
        }

    /// The name part of a client id: the client's name from `initialize` (`PeerInfo.Name`) with
    /// anything but ASCII letters, digits, `.`, `_` and `-` dropped, at most `MaxClientName`
    /// characters, or `client`.
    let clientName (name: string) : string =
        let kept =
            name
            |> String.filter (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-')

        let kept =
            if kept.Length > MaxClientName then
                kept.Substring(0, MaxClientName)
            else
                kept

        if kept.Length = 0 then "client" else kept

    let private tokenFailure (result: Result<ClientSession, TokenError>) : Result<SignedIn, IdentityFailure> =
        match result with
        | Ok session -> Ok { Session = session; Token = None }
        | Error _ -> Error IdentityFailure.TokenRejected

    let private viaProvider<'R when 'R :> IClock and 'R :> IIds and 'R :> IVariants and 'R :> IScopeWith<'R>>
        (auth: Authenticator)
        (facts: ConnectionFacts)
        (client: string)
        (providerId: string)
        (presented: Json)
        : Eff<'R, Result<SignedIn, IdentityFailure>> =
        eff {
            match auth.Providers |> List.tryFind (fun c -> c.Value.Id = providerId) with
            | None -> return Error(IdentityFailure.UnknownProvider providerId)
            | Some contributed ->
                let provider = contributed.Value

                let credential =
                    {
                        Presented = presented
                        Connection = facts.Connection
                        Host = auth.Host
                    }

                let! verified = ExtensionEnv.within contributed.Env (provider.Verify credential)

                match verified with
                | Error error -> return Error(IdentityFailure.Refused(providerId, error))
                | Ok principal ->
                    match Principal.validate principal with
                    | Error reason -> return Error(IdentityFailure.InvalidPrincipal(providerId, reason))
                    | Ok() when principal.Provider <> providerId ->
                        return
                            Error(
                                IdentityFailure.InvalidPrincipal(
                                    providerId,
                                    "a principal of another provider (" + principal.Provider + ")"
                                )
                            )
                    | Ok() ->
                        let! roles = ExtensionEnv.within contributed.Env (provider.Roles principal)

                        match roles |> List.distinct |> List.sortBy Role.rank with
                        | [] -> return Error(IdentityFailure.NoRole principal.Id.Value)
                        | roles ->
                            let! clientId = Ids.newId
                            let via = clientName client + "/" + string clientId
                            let! token, session = SessionTokens.issue auth.Tokens principal roles via

                            return
                                Ok
                                    {
                                        Session = session
                                        Token = Some token
                                    }
        }

    /// Signs a client in, from what it said in `initialize` (`None`: it said nothing) and what the
    /// transport established; `client` is its name from `initialize`. A provider sign-in issues a new
    /// token and client id; a token (in `initialize`, or the listener's bearer) resumes its session.
    let signIn<'R when 'R :> IClock and 'R :> IIds and 'R :> IVariants and 'R :> IScopeWith<'R>>
        (auth: Authenticator)
        (facts: ConnectionFacts)
        (client: string)
        (request: SignIn option)
        : Eff<'R, Result<SignedIn, IdentityFailure>> =
        match request, facts.Session with
        | Some(SignIn.Token token), _ -> SessionTokens.verify auth.Tokens token |> Eff.map tokenFailure
        | Some(SignIn.Provider(provider, presented)), _ -> viaProvider auth facts client provider presented
        | None, Some id -> SessionTokens.find auth.Tokens id |> Eff.map tokenFailure
        | None, None -> Eff.ret (Error IdentityFailure.Missing)

    /// The session of a signed-in client now: refused once its token is revoked (expiry only stops
    /// new connections).
    let current (auth: Authenticator) (session: ClientSession) : Result<ClientSession, IdentityFailure> =
        if SessionTokens.isRevoked auth.Tokens session.Id then
            Error IdentityFailure.TokenRejected
        else
            Ok session
