namespace Fx.Host

open System
open Fx
open Fx.Grants

/// Where a grant `fx grants` makes applies: the whole host, one session, or the project in the
/// current directory (requests that name a repository: an environment's credentials, T138).
[<RequireQualifiedAccess>]
type GrantsScope =
    | Host
    | Session of session: string
    /// The project of the directory `fx` runs in.
    | Repo

    override this.ToString() =
        match this with
        | Host -> "host"
        | Session session -> "session:" + session
        | Repo -> "repo"

/// What `fx grants` asks for: a person (the OS user, signed in through `local-os-user`) lists, makes
/// or revokes grants on this host's grants log (spec §13: only people grant).
[<RequireQualifiedAccess>]
type GrantsCommand =
    /// The grants in force; `all`: every grant, with its status.
    | List of all: bool
    /// Allows (or forbids) an action on the resources a pattern matches.
    | Grant of effect: GrantEffect * action: string * pattern: string * scope: GrantsScope * reason: string option
    | Revoke of grant: string * reason: string option

    override this.ToString() =
        match this with
        | List true -> "list --all"
        | List false -> "list"
        | Grant(effect, action, pattern, scope, _) -> $"{effect} {action} {pattern} ({scope})"
        | Revoke(grant, _) -> "revoke " + grant

[<RequireQualifiedAccess>]
module GrantsCommand =
    /// The command's lines in the usage, without the binary's name.
    let usage: string list =
        [
            "grants list [--all]"
            "grants allow <action> <pattern> [--scope host|session:<id>|repo] [--reason <text>]"
            "grants deny <action> <pattern> [--scope host|session:<id>|repo] [--reason <text>]"
            "grants revoke <id> [--reason <text>]"
        ]

    /// The repository a project's requests name, and repository grants apply to: the project's
    /// directory (the host's workspace), without a final separator.
    let repo (workspace: string) : string = HostRepo.id workspace

    /// An action's name as a person typed it.
    let actionName (text: string) : Result<ActionName, string> =
        match ActionName.tryCreate text with
        | Some name -> Ok name
        | None -> Error $"not an action name: {text} (e.g. component.install, shell.exec)"

    /// The grant a person drafts on the command line, checked against the actions in effect: a known
    /// action, and a scope its definition allows (`component.install` is never for one session).
    let draft
        (actions: Actions)
        (effect: GrantEffect)
        (action: string)
        (pattern: string)
        (scope: GrantScope)
        (reason: string option)
        : Result<GrantDraft, string> =
        match actionName action with
        | Error e -> Error e
        | Ok name ->
            match Actions.tryFind name actions with
            | None -> Error $"no action is named {action}"
            | Some def when not (List.contains scope.Kind def.Scopes) ->
                let offered =
                    def.Scopes
                    |> List.filter (fun kind -> kind <> ScopeKind.Once)
                    |> List.map string
                    |> String.concat ", "

                Error $"{action} grants are not for {scope.Kind} scope (they are for: {offered})"
            | Some _ ->
                Ok
                    {
                        Action = name
                        Resource = pattern
                        Effect = effect
                        Scope = scope
                        Expires = None
                        Reason = reason
                    }

    let private plain (c: char) =
        Char.IsAsciiLetterOrDigit c
        || c = '@'
        || c = '%'
        || c = '+'
        || c = '='
        || c = ':'
        || c = ','
        || c = '.'
        || c = '/'
        || c = '_'
        || c = '-'

    /// A word of a command line as a person types it: as is when it is plain, else in single quotes
    /// (PowerShell's escaping on Windows, a POSIX shell's elsewhere).
    let quote (word: string) : string =
        if word <> "" && word |> Seq.forall plain then
            word
        elif OperatingSystem.IsWindows() then
            "'" + word.Replace("'", "''") + "'"
        else
            "'" + word.Replace("'", "'\\''") + "'"

    /// The command that allows a request's action on exactly its resource on this whole host, if the
    /// request offers that scope, else for its repository (run in the project's directory), if it
    /// offers that: what a person runs to approve it beforehand (a headless run).
    let allowing (request: GrantRequest) : string option =
        let command =
            Product.binaryName
            + " grants allow "
            + request.Action.Value
            + " "
            + quote (Pattern.exact request.Resource)

        if List.contains ScopeKind.Host request.Offer then
            Some command
        elif List.contains ScopeKind.Repo request.Offer && request.Context.Repo.IsSome then
            Some(command + " --scope repo")
        else
            None

    /// One grant on a line: id, effect, action, pattern, scope, who, why, and its status unless it
    /// is in force.
    let line (entry: GrantEntry) : string =
        let grant = entry.Grant

        let reason =
            grant.Reason |> Option.map (fun r -> " — " + r) |> Option.defaultValue ""

        let status =
            match entry.Status with
            | GrantStatus.Active -> ""
            | other -> $" [{other}]"

        $"{grant.Id.Value}  {grant.Effect} {grant.Action.Value} {quote grant.Resource}  ({grant.Scope}) by {grant.By}{reason}{status}"

    /// What `fx grants list` prints: one grant a line, oldest first.
    let render (entries: GrantEntry list) : string list =
        match entries with
        | [] -> [ "no grants" ]
        | entries -> entries |> List.sortBy (fun e -> e.Grant.At, e.Grant.Id.Value) |> List.map line
