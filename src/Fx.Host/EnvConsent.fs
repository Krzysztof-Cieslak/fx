namespace Fx.Host

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Sdk
open Fx.Grants
open Fx.Environments

/// The repository a host's project requests name, and repository grants apply to.
[<RequireQualifiedAccess>]
module HostRepo =
    /// The project's directory (the host's workspace), without a final separator.
    let id (workspace: string) : string =
        IO.Path.TrimEndingDirectorySeparator workspace

/// An environment with only a grants capability, for asking on behalf of an environment's start.
type private ConsentEnv =
    {
        Grants: Grants
        Scope: Scope
    }

    interface IGrants with
        member env.Grants = env.Grants

    interface IScopeWith<ConsentEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "ConsentEnv"

/// What the host's resolver of environment variables needs.
[<NoEquality; NoComparison>]
type EnvVariableSettings =
    {
        /// The person's `env.variables`.
        Person: Map<string, string>
        /// The person's `env.deny`.
        Deny: string list
        /// The host's own variables, read once.
        HostVariables: (string * string) list
        /// The host's secret sources.
        Secrets: Secrets
        Grants: Grants
        /// The project's repository (`HostRepo.id`): what a consent for a project names.
        Repo: string
        Config: HostConfig
    }

    override this.ToString() = $"EnvVariableSettings({this.Repo})"

/// The variables an environment's commands get (T138; docs/modules/environments.md, "Variables"): the
/// person's `env.variables` and the spec's `variables`, values from the host's variables and
/// secrets, a project's credentials only with a person's consent (`env.credential`, `secret.read`,
/// offered for this start or for the project, never the host).
[<RequireQualifiedAccess>]
module EnvVariableConsent =
    let private envCredential = ActionName.create "env.credential"
    let private secretRead = ActionName.create "secret.read"

    /// Whose file a spec came from: the project's when the project's config defines a spec of its
    /// name, or the host's defines it with other variables (a spec the project defined once and still
    /// recorded); else the host's.
    let origin (config: HostConfig) (spec: EnvSpec) : VariableOrigin =
        let fromProject =
            config.Project
            |> Option.exists (fun project -> project.Environments |> List.exists (fun s -> s.Name = spec.Name))

        let hostSpec =
            config.Host.Environments |> List.tryFind (fun s -> s.Name = spec.Name)

        match hostSpec with
        | _ when fromProject -> VariableOrigin.ProjectSpec
        | Some host when host.Variables = spec.Variables -> VariableOrigin.HostSpec
        | _ when spec.Variables.IsEmpty -> VariableOrigin.HostSpec
        | _ -> VariableOrigin.ProjectSpec

    /// The request key of a consent: the environment, the attempt and the variable, so an answer
    /// never stands for a later start (as installs are keyed).
    let key (action: ActionName) (ask: EnvVariablesAsk) (request: VariableRequest) : string =
        action.Value + "/" + ask.Id + "/" + string ask.Attempt + "/" + request.Name

    /// What a project's credential asks the grants log: `env.credential` on the host variable it reads,
    /// or `secret.read` on the secret, for the project's repository; `None` for a literal.
    let grantAsk (repo: string) (ask: EnvVariablesAsk) (request: VariableRequest) : GrantAsk option =
        let make (action: ActionName) (resource: string) (summary: string) =
            {
                Key = key action ask request
                Action = action
                Resource = resource
                Context =
                    {
                        Session = None
                        Run = None
                        Repo = Some { Repo = repo; Path = "" }
                    }
                Requester =
                    {
                        Principal = "system"
                        Agent = None
                        Extension = None
                    }
                Summary = summary
                Evidence =
                    [
                        {
                            Label = "asked by"
                            Text = $"the project's environment spec {Json.quote ask.Spec.Name} ({repo})"
                        }
                        {
                            Label = "variable"
                            Text = $"{request.Name}, redacted from what the model sees"
                        }
                    ]
                Suggestions = []
                // A yes for one project never hands a credential to another: no host scope.
                Scopes = [ ScopeKind.Once; ScopeKind.Repo ]
                Expires = None
            }

        match request.Source with
        | VariableSource.Host name ->
            Some(make envCredential name $"Give the environment's commands {request.Name} (your variable {name})")
        | VariableSource.Secret name ->
            Some(make secretRead name $"Give the environment's commands {request.Name} (the secret {name})")
        | VariableSource.Literal _ -> None

    let private consent
        (settings: EnvVariableSettings)
        (ask: EnvVariablesAsk)
        (request: VariableRequest)
        (cancellation: CancellationToken)
        : Task<Result<string option, VariableOutcome>> =
        task {
            match grantAsk settings.Repo ask request with
            | None -> return Ok None
            | Some grantAsk ->
                let env: ConsentEnv =
                    {
                        Grants = settings.Grants
                        Scope = Scope.ofCancellation cancellation
                    }

                match! Grants.authorize grantAsk env with
                | Ok(Resolution.Allowed grant) -> return Ok(Some grant.Value)
                | Ok(Resolution.Denied reasons) -> return Error(VariableOutcome.Denied(String.concat "; " reasons))
                | Ok(Resolution.Cancelled reason) -> return Error(VariableOutcome.Denied reason)
                | Error e -> return Error(VariableOutcome.Denied $"the grants log could not be asked: {e}")
        }

    let private hostValue (settings: EnvVariableSettings) (name: string) : string option =
        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        settings.HostVariables
        |> List.tryFindBack (fun (n, _) -> String.Equals(n, name, comparison))
        |> Option.map snd

    let private value
        (settings: EnvVariableSettings)
        (request: VariableRequest)
        (cancellation: CancellationToken)
        : Task<Result<string, VariableOutcome>> =
        task {
            match request.Source with
            | VariableSource.Literal text -> return Ok text
            | VariableSource.Host name ->
                match hostValue settings name with
                | Some text when text <> "" -> return Ok text
                | _ -> return Error(VariableOutcome.Unavailable $"the host has no variable {name}")
            | VariableSource.Secret name ->
                match SecretRef.tryCreate name with
                | Error e -> return Error(VariableOutcome.Unavailable e)
                | Ok reference ->
                    match! settings.Secrets.Resolve reference cancellation with
                    | Ok secret -> return Ok(secret.Reveal())
                    | Error e -> return Error(VariableOutcome.Unavailable(string e))
        }

    /// One request: its value (a host variable is looked up before asking, a secret only after a
    /// consent), the consent it needs, and the length a credential must have.
    let private resolveOne
        (settings: EnvVariableSettings)
        (ask: EnvVariablesAsk)
        (request: VariableRequest)
        (cancellation: CancellationToken)
        : Task<EnvVariable * (string * string) option> =
        task {
            let accepted (granted: string option) (text: string) =
                match EnvVariables.accept request text with
                | Error reason -> EnvVariables.record request (VariableOutcome.Unavailable reason), None
                | Ok text ->
                    let outcome =
                        match granted with
                        | Some grant -> VariableOutcome.Granted grant
                        | None -> VariableOutcome.Passed

                    EnvVariables.record request outcome, Some(request.Name, text)

            let consentFirst =
                match request.Source with
                | VariableSource.Secret _ -> true
                | _ -> false

            if not request.NeedsConsent then
                match! value settings request cancellation with
                | Error outcome -> return EnvVariables.record request outcome, None
                | Ok text -> return accepted None text
            elif consentFirst then
                match! consent settings ask request cancellation with
                | Error outcome -> return EnvVariables.record request outcome, None
                | Ok granted ->
                    match! value settings request cancellation with
                    | Error outcome -> return EnvVariables.record request outcome, None
                    | Ok text -> return accepted granted text
            else
                match! value settings request cancellation with
                | Error outcome -> return EnvVariables.record request outcome, None
                | Ok text ->
                    match! consent settings ask request cancellation with
                    | Error outcome -> return EnvVariables.record request outcome, None
                    | Ok granted -> return accepted granted text
        }

    /// The resolver the environments host calls each time envd starts.
    let resolver (settings: EnvVariableSettings) : EnvVariablesAsk -> CancellationToken -> Task<ResolvedVariables> =
        fun ask cancellation ->
            task {
                let requests, denied =
                    EnvVariables.requests
                        settings.Person
                        ask.Spec.Variables
                        (origin settings.Config ask.Spec)
                        settings.Deny

                let deniedRecords =
                    denied
                    |> List.map (fun r -> EnvVariables.record r (VariableOutcome.Denied EnvVariables.DeniedByHost))

                if not ask.Machine.SameHost then
                    let unavailable =
                        requests
                        |> List.map (fun r ->
                            EnvVariables.record
                                r
                                (VariableOutcome.Unavailable
                                    "only environments on the host's machine get variables yet (T501)"))

                    return
                        { EnvVariables.none with
                            Record = unavailable @ deniedRecords
                        }
                else
                    let results = ResizeArray()

                    for request in requests do
                        let! resolved = resolveOne settings ask request cancellation
                        results.Add resolved

                    let passed = results |> Seq.choose snd |> List.ofSeq
                    let record = (results |> Seq.map fst |> List.ofSeq) @ deniedRecords

                    let credential (name: string) =
                        record |> List.exists (fun v -> v.Name = name && v.Credential)

                    return
                        {
                            Values = passed
                            Secrets = passed |> List.filter (fst >> credential)
                            Record = record
                        }
            }
