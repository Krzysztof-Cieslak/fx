namespace Fx.Auth

open System
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// Why an auth operation failed (an expected failure; the text is for the person).
[<RequireQualifiedAccess>]
type AuthFailure =
    | UnknownProvider of provider: string
    /// The provider has no auth method a login can store a credential for.
    | NoLogin of provider: string * reason: string
    /// A required field of the method's form is missing.
    | MissingField of name: string
    | Credential of CredentialError
    | Store of StoreError

    override this.ToString() =
        match this with
        | UnknownProvider provider -> "no provider is named " + provider
        | NoLogin(provider, reason) -> "cannot log in to " + provider + ": " + reason
        | MissingField name -> "the field " + name + " is required"
        | Credential e -> string e
        | Store e -> string e

/// A login between its start and its end: the flow the client runs, and what the host keeps until the
/// flow's result comes back. Its state never leaves the host.
[<NoEquality; NoComparison>]
type PendingLogin =
    {
        Provider: string
        /// The auth method's kind.
        Method: string
        /// Where the credential will be stored.
        Credential: CredentialId
        Settings: Json
        Form: FormValues
        /// The flow the client runs; `None` for a method without one.
        Flow: AuthFlow option
        State: SecretText
    }

    override this.ToString() =
        "login to " + this.Provider + " with " + this.Method

/// What a completed login stored.
type LoginOutcome =
    {
        Provider: string
        Method: string
        Credential: CredentialId
        /// The store's kind.
        Store: string
        /// The store choice's notice (`StoreChoice.Notice`), to show with the first credential stored.
        Notice: string option
    }

    override this.ToString() =
        this.Provider
        + ": "
        + this.Method
        + " stored as "
        + string this.Credential
        + " in "
        + this.Store

/// A stored credential as listed: never its secret.
type CredentialEntry =
    {
        Id: CredentialId
        Method: string
        Meta: Map<string, string>
    }

    override this.ToString() =
        string this.Id + " (" + this.Method + ")"

/// How a provider's credentials resolve now.
type ProviderAuth =
    {
        Provider: string
        /// The auth method that resolves, if one does.
        Method: string option
        /// Why none does.
        Problem: string option
    }

    override this.ToString() =
        match this.Method, this.Problem with
        | Some method, _ -> this.Provider + ": " + method
        | None, Some problem -> this.Provider + ": " + problem
        | None, None -> this.Provider + ": no credentials needed"

/// The credential store in use and the providers' credentials (`auth status`).
type AuthStatus =
    {
        Store: string
        Protection: string
        Reason: string
        Notice: string option
        Probe: StoreStatus
        Providers: ProviderAuth list
    }

    override this.ToString() =
        "credentials in " + this.Store + " (" + this.Reason + ")"

/// The host's service credentials (spec §11.3–11.4): the credential store the host chose, the
/// contributed auth methods and the `secret:` resolver. It resolves providers' credentials for
/// `ModelAccess` (`AuthService.modelAuth`), runs logins whose flows clients run, and lists and
/// removes stored credentials. Host-only: nothing here ever leaves the host but what goes into a
/// request to the provider (spec §18). Built by `AuthHost.start`.
[<NoEquality; NoComparison>]
type AuthService =
    {
        Store: CredentialStore
        Choice: StoreChoice
        Methods: Contributed<AuthMethod> list
        Secrets: Secrets
        Variables: HostVariables
        Http: Http
        Trace: Trace
    }

    override this.ToString() =
        "AuthService("
        + string this.Choice
        + ", "
        + string this.Methods.Length
        + " methods)"

[<RequireQualifiedAccess>]
module AuthService =
    type private ScopeEnv =
        {
            Scope: Scope
        }

        interface IScope with
            member env.Scope = env.Scope

    let private authEnv (service: AuthService) (method: Contributed<AuthMethod>) : AuthEnv =
        {
            Extension = method.Env
            Secrets = service.Secrets
            HostVariables = service.Variables
            Http = service.Http
        }

    /// Runs auth-method code in its environment with the caller's scope.
    let private within (service: AuthService) (method: Contributed<AuthMethod>) (effect: Eff<IAuthEnv, 'T>) =
        AuthEnv.within (authEnv service method) effect

    let private store<'R, 'T when 'R :> IScope>
        (operation: CredentialStore -> CancellationToken -> Task<'T>)
        : AuthService -> Eff<'R, 'T> =
        fun service env -> operation service.Store env.Scope.Cancellation

    /// The contributed method of a kind.
    let methodOf (kind: string) (service: AuthService) : Contributed<AuthMethod> option =
        service.Methods |> List.tryFind (fun m -> m.Value.Kind = kind)

    let private event (service: AuthService) (scope: Scope) (message: string) (attrs: Attr list) =
        service.Trace.Event scope.Span Level.Info message attrs

    /// The provider's stored credential for a method that stores one: `None` if there is none, or it
    /// was made by another method.
    let private storedFor<'R when 'R :> IScope>
        (service: AuthService)
        (method: AuthMethod)
        (provider: ProviderDef)
        (reference: AuthMethodRef)
        : Eff<'R, Result<(CredentialId * StoredCredential option) option, CredentialError>> =
        fun env ->
            if not (AuthMethod.stores method) then
                Task.FromResult(Ok None)
            else
                match AuthMethod.credentialId provider.Id reference.Settings with
                | Error e -> Task.FromResult(Error(CredentialError.Invalid("the provider's credential setting: " + e)))
                | Ok id ->
                    task {
                        match! service.Store.Read id env.Scope.Cancellation with
                        | Ok(Some credential) when credential.Method = method.Kind ->
                            return Ok(Some(id, Some credential))
                        | Ok _ -> return Ok(Some(id, None))
                        | Error e -> return Error(CredentialError.Unavailable(string e))
                    }

    /// Resolves one of the provider's methods.
    let private resolveWith<'R when 'R :> IScope>
        (service: AuthService)
        (provider: ProviderDef)
        (reference: AuthMethodRef)
        : Eff<'R, Result<Resolution, CredentialError>> =
        eff {
            match methodOf reference.Kind service with
            | None ->
                return
                    Error(
                        CredentialError.Unavailable("no enabled extension provides the auth method " + reference.Kind)
                    )
            | Some method ->
                match! storedFor service method.Value provider reference with
                | Error e -> return Error e
                | Ok stored ->
                    let request: ResolveRequest =
                        {
                            Provider = provider.Id
                            Settings = reference.Settings
                            Credential = stored |> Option.bind snd
                        }

                    match! within service method (method.Value.Resolve request) with
                    | Error e -> return Error e
                    | Ok resolution ->
                        match resolution.Updated, stored with
                        | Some updated, Some(id, _) ->
                            match! store (fun s ct -> s.Write id updated ct) service with
                            | Ok() -> ()
                            | Error e ->
                                let! scope = Eff.scope

                                event
                                    service
                                    scope
                                    "auth.renewal-not-stored"
                                    [ Attr.text "provider" provider.Id; Attr.text "error" (string e) ]
                        | _ -> ()

                        return Ok resolution
        }

    /// Resolves a provider's credentials for one request (spec §11.3): its auth methods in the order
    /// it lists them; the first that resolves wins. A method with nothing to authenticate with
    /// (`Missing`) gives way to the next; so does one that fails, which is traced (`auth.skipped`). If
    /// none resolves, the first failure other than `Missing` is the error, else every method's hint.
    /// A provider without methods needs no credentials.
    let resolve<'R when 'R :> IScope>
        (service: AuthService)
        (provider: ProviderDef)
        : Eff<'R, Result<ResolvedAuth * string, CredentialError>> =
        eff {
            let! scope = Eff.scope
            let mutable resolved = None
            let mutable failure = None
            let mutable hints = []
            let mutable remaining = provider.Auth

            while resolved.IsNone && not remaining.IsEmpty do
                let reference = remaining.Head
                remaining <- remaining.Tail

                match! resolveWith service provider reference with
                | Ok resolution -> resolved <- Some(resolution.Auth, reference.Kind)
                | Error(CredentialError.Missing hint) -> hints <- hints @ [ hint ]
                | Error e ->
                    event
                        service
                        scope
                        "auth.skipped"
                        [
                            Attr.text "provider" provider.Id
                            Attr.text "method" reference.Kind
                            Attr.text "error" (string e)
                        ]

                    if failure.IsNone then
                        failure <- Some e

            match resolved, failure with
            | Some resolved, _ -> return Ok resolved
            | None, Some e -> return Error e
            | None, None when provider.Auth.IsEmpty -> return Ok(ResolvedAuth.none, "none")
            | None, None -> return Error(CredentialError.Missing(String.concat "; or " hints))
        }

    /// The seam `ModelAccess.Auth` takes: a provider's credentials for one call, or `unavailable`
    /// saying how to provide them.
    let modelAuth (service: AuthService) : ProviderDef -> CancellationToken -> Task<Result<ResolvedAuth, ChatError>> =
        fun provider cancellation ->
            task {
                match!
                    resolve
                        service
                        provider
                        {
                            Scope = Scope.ofCancellation cancellation
                        }
                with
                | Ok(auth, _) -> return Ok auth
                | Error e -> return Error(ChatError.Unavailable(provider.Id + ": " + string e))
            }

    /// Problems with the providers' auth settings (unknown methods, settings a method refuses), for
    /// the host to report when it starts.
    let check (service: AuthService) (providers: ProviderDef list) : string list =
        [
            for provider in providers do
                for reference in provider.Auth do
                    match methodOf reference.Kind service with
                    | None ->
                        yield
                            provider.Id
                            + ": no enabled extension provides the auth method "
                            + reference.Kind
                    | Some method ->
                        match method.Value.CheckSettings reference.Settings with
                        | Ok() -> ()
                        | Error e -> yield provider.Id + ": " + reference.Kind + ": " + e

                        if AuthMethod.stores method.Value then
                            match AuthMethod.credentialId provider.Id reference.Settings with
                            | Ok _ -> ()
                            | Error e -> yield provider.Id + ": " + reference.Kind + ": " + e
        ]

    /// The provider's method a login uses: the one named, or the first that stores a credential.
    let private loginMethod
        (service: AuthService)
        (provider: ProviderDef)
        (kind: string option)
        : Result<Contributed<AuthMethod> * AuthMethodRef, AuthFailure> =
        let storing =
            provider.Auth
            |> List.choose (fun reference ->
                match methodOf reference.Kind service with
                | Some method when AuthMethod.stores method.Value -> Some(method, reference)
                | _ -> None)

        match kind with
        | Some kind ->
            match storing |> List.tryFind (fun (m, _) -> m.Value.Kind = kind) with
            | Some found -> Ok found
            | None when provider.Auth |> List.exists (fun r -> r.Kind = kind) ->
                Error(AuthFailure.NoLogin(provider.Id, kind + " stores nothing to log in with"))
            | None -> Error(AuthFailure.NoLogin(provider.Id, "it does not take " + kind))
        | None ->
            match storing with
            | found :: _ -> Ok found
            | [] ->
                let kinds = provider.Auth |> List.map _.Kind |> String.concat ", "

                Error(
                    AuthFailure.NoLogin(
                        provider.Id,
                        "none of its auth methods ("
                        + (if kinds = "" then "none" else kinds)
                        + ") stores a credential"
                    )
                )

    /// Starts a login to a provider (`auth login`): picks the method, checks the form, and asks the
    /// method for the flow a client runs. `callback`: a loopback URL the client listens on for a
    /// browser flow.
    let startLogin<'R when 'R :> IScope>
        (service: AuthService)
        (provider: ProviderDef)
        (kind: string option)
        (form: FormValues)
        (callback: string option)
        : Eff<'R, Result<PendingLogin, AuthFailure>> =
        eff {
            match loginMethod service provider kind with
            | Error e -> return Error e
            | Ok(method, reference) ->
                match
                    method.Value.Prompts
                    |> List.tryFind (fun field ->
                        field.Required
                        && (form.TryFind field.Name |> Option.forall String.IsNullOrWhiteSpace))
                with
                | Some field -> return Error(AuthFailure.MissingField field.Name)
                | None ->
                    match AuthMethod.credentialId provider.Id reference.Settings with
                    | Error e -> return Error(AuthFailure.Credential(CredentialError.Invalid e))
                    | Ok id ->
                        let pending flow state : PendingLogin =
                            {
                                Provider = provider.Id
                                Method = method.Value.Kind
                                Credential = id
                                Settings = reference.Settings
                                Form = form
                                Flow = flow
                                State = state
                            }

                        match method.Value.Authorize with
                        | None -> return Ok(pending None (SecretText ""))
                        | Some authorize ->
                            let request: LoginRequest =
                                {
                                    Provider = provider.Id
                                    Settings = reference.Settings
                                    Form = form
                                    Callback = callback
                                }

                            match! within service method (authorize request) with
                            | Ok start -> return Ok(pending (Some start.Flow) start.State)
                            | Error e -> return Error(AuthFailure.Credential e)
        }

    /// Ends a login with what its flow gave: the method makes the credential, the host stores it.
    let completeLogin<'R when 'R :> IScope>
        (service: AuthService)
        (pending: PendingLogin)
        (result: FlowResult)
        : Eff<'R, Result<LoginOutcome, AuthFailure>> =
        eff {
            match methodOf pending.Method service with
            | None ->
                return
                    Error(
                        AuthFailure.Credential(
                            CredentialError.Unavailable(
                                "no enabled extension provides the auth method " + pending.Method
                            )
                        )
                    )
            | Some method ->
                match method.Value.Complete with
                | None -> return Error(AuthFailure.NoLogin(pending.Provider, pending.Method + " stores nothing"))
                | Some complete ->
                    let completion: LoginCompletion =
                        {
                            Provider = pending.Provider
                            Settings = pending.Settings
                            Form = pending.Form
                            State = pending.State
                            Result = result
                        }

                    match! within service method (complete completion) with
                    | Error e -> return Error(AuthFailure.Credential e)
                    | Ok credential when credential.Method <> method.Value.Kind ->
                        return
                            Error(
                                AuthFailure.Credential(
                                    CredentialError.Invalid(
                                        pending.Method + " made a credential of " + credential.Method
                                    )
                                )
                            )
                    | Ok credential ->
                        match! store (fun s ct -> s.Write pending.Credential credential ct) service with
                        | Error e -> return Error(AuthFailure.Store e)
                        | Ok() ->
                            return
                                Ok
                                    {
                                        Provider = pending.Provider
                                        Method = pending.Method
                                        Credential = pending.Credential
                                        Store = service.Store.Kind
                                        Notice = service.Choice.Notice
                                    }
        }

    /// Removes a provider's stored credentials (`auth logout`): those of every method of the provider
    /// that stores one. The ids removed; none if there were none.
    let logout<'R when 'R :> IScope>
        (service: AuthService)
        (provider: ProviderDef)
        : Eff<'R, Result<CredentialId list, AuthFailure>> =
        eff {
            let ids =
                provider.Auth
                |> List.choose (fun reference ->
                    match methodOf reference.Kind service with
                    | Some method when AuthMethod.stores method.Value ->
                        AuthMethod.credentialId provider.Id reference.Settings |> Result.toOption
                    | _ -> None)
                |> List.distinct

            let mutable removed = []
            let mutable failure = None

            for id in ids do
                if failure.IsNone then
                    match! store (fun s ct -> s.Delete id ct) service with
                    | Ok true -> removed <- removed @ [ id ]
                    | Ok false -> ()
                    | Error e -> failure <- Some e

            match failure with
            | Some e -> return Error(AuthFailure.Store e)
            | None -> return Ok removed
        }

    /// The stored credentials (`auth list`): ids, methods and facts, never secrets.
    let list<'R when 'R :> IScope> (service: AuthService) : Eff<'R, Result<CredentialEntry list, AuthFailure>> =
        eff {
            match! store (fun s ct -> s.List ct) service with
            | Error e -> return Error(AuthFailure.Store e)
            | Ok ids ->
                let mutable entries = []
                let mutable failure = None

                for id in ids do
                    if failure.IsNone then
                        match! store (fun s ct -> s.Read id ct) service with
                        | Ok(Some credential) ->
                            let entry: CredentialEntry =
                                {
                                    Id = id
                                    Method = credential.Method
                                    Meta = credential.Meta
                                }

                            entries <- entries @ [ entry ]
                        | Ok None -> ()
                        | Error e -> failure <- Some e

                match failure with
                | Some e -> return Error(AuthFailure.Store e)
                | None -> return Ok entries
        }

    /// The store in use, what its probe says now, and how each provider's credentials resolve
    /// (`auth status`). Resolving reads stores and variables but shows no value.
    let status<'R when 'R :> IScope> (service: AuthService) (providers: ProviderDef list) : Eff<'R, AuthStatus> =
        eff {
            let! probe = store (fun s ct -> s.Probe ct) service
            let mutable resolved = []

            for provider in providers do
                let! outcome = resolve service provider

                let entry: ProviderAuth =
                    match outcome with
                    | Ok(_, kind) ->
                        {
                            Provider = provider.Id
                            Method = Some kind
                            Problem = None
                        }
                    | Error e ->
                        {
                            Provider = provider.Id
                            Method = None
                            Problem = Some(string e)
                        }

                resolved <- resolved @ [ entry ]

            return
                {
                    Store = service.Store.Kind
                    Protection = service.Store.Protection
                    Reason = service.Choice.Reason
                    Notice = service.Choice.Notice
                    Probe = probe
                    Providers = resolved
                }
        }
