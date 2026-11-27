namespace Fx.Host

open Fx.Effects
open Fx.Machines
open Fx.Environments
open Fx.Grants
open Fx.Grants.Host
open Fx.Sessions.Host
open Fx.CodeAct

/// How the grants guard decides a session's commands now (`session/approvals`; docs/modules/codeact.md,
/// "Approval modes").
type ApprovalsView =
    {
        Session: string
        /// What happens to a command no grant covers: `ask` (a person is asked), `auto` (auto approvals
        /// run commands written plainly in the workspace; the rest asks), `deny` (refused without
        /// asking) or `off` (nothing is checked).
        Mode: string
        /// The host's `codeact.grants.mode` for the session.
        Configured: string
        /// The sandbox driver that confines the session's commands; none when no driver does (the
        /// default: they run with the user's rights) or the environment is not ready.
        Sandbox: string option
        /// Why auto approvals are on or off, for people.
        Reason: string
    }

    override this.ToString() = $"{this.Session}: {this.Mode}"

/// A session's approval mode for clients (`session/approvals`, `session/setApprovals`) and `fx run`
/// (docs/modules/codeact.md, "Approval modes"): what the grants guard does with its commands, and a
/// person's switch of auto approvals, which is a grant of `approvals.auto` for the session (only
/// people grant: the method's least role is owner).
[<RequireQualifiedAccess>]
module HostApprovals =
    /// The driver that confines the session's environment's processes, if one does.
    let private sandboxOf (host: Host) (session: string) : Eff<HostEnv, string option> =
        match HostSessions.tryFind host session with
        | None -> Eff.ret None
        | Some entry ->
            Envs.get entry.Environment
            |> Eff.map (fun record ->
                record
                |> Option.bind (fun r -> r.Sandbox)
                |> Option.filter (fun sandbox -> sandbox.Confines)
                |> Option.map (fun sandbox -> sandbox.Driver))

    let private context (session: string) : GrantContext =
        {
            Session = Some session
            Run = None
            Repo = None
        }

    /// The guard's settings for the session's next turn: its profile's.
    let private settingsOf (host: Host) (session: string) : Eff<HostEnv, GrantSettings> =
        eff {
            let! snapshot = SessionHost.state host.Sessions session
            let! variants = host.ProfileOf snapshot.State.Profile
            return! GrantSettings.ofVariants variants
        }

    let private stateOf (host: Host) (session: string) : Eff<HostEnv, GrantSettings * string option * AutoApprovals> =
        eff {
            let! settings = settingsOf host session
            let! sandbox = sandboxOf host session
            let driver = sandbox |> Option.defaultValue AutoApprovals.Unsandboxed

            let! check =
                GrantsService.check host.Grants (AutoApprovals.query driver (context session))

            return settings, sandbox, AutoApprovals.decide settings.Mode driver check
        }

    /// What the guard does with the session's commands now.
    let view (host: Host) (session: string) : Eff<HostEnv, ApprovalsView> =
        eff {
            let! settings, sandbox, auto = stateOf host session

            let mode =
                match settings.Mode, auto with
                | GrantMode.Off, _ -> "off"
                | _, AutoApprovals.On _ -> "auto"
                | GrantMode.Deny, _ -> "deny"
                | _ -> "ask"

            let reason =
                match auto with
                | AutoApprovals.On because
                | AutoApprovals.Off because -> because

            return
                {
                    Session = session
                    Mode = mode
                    Configured = string settings.Mode
                    Sandbox = sandbox
                    Reason = reason
                }
        }

    /// What `fx run` says once, before the turn, when auto approvals hold: that commands run
    /// unasked, and with what rights.
    let notice (view: ApprovalsView) : string option =
        match view.Mode, view.Sandbox with
        | "auto", None ->
            Some
                "approvals: auto · unsandboxed: commands written plainly in the workspace run without asking, with your rights (they can read your files and use the network); downloads, installs and writes outside the workspace still ask"
        | "auto", Some driver ->
            Some
                $"approvals: auto · {driver}: commands written plainly in the workspace run without asking, confined by the sandbox; downloads, installs and writes outside the workspace still ask"
        | _ -> None

    /// This session's own grants of `approvals.auto` in force, with this effect.
    let private sessionGrants (host: Host) (session: string) (effect: GrantEffect) : Grant list =
        (GrantsService.list host.Grants false).Grants
        |> List.map _.Grant
        |> List.filter (fun grant ->
            grant.Action = AutoApprovals.action
            && grant.Effect = effect
            && grant.Scope = GrantScope.Session session)

    let private draft (session: string) (effect: GrantEffect) (reason: string) : GrantDraft =
        {
            Action = AutoApprovals.action
            Resource = "*"
            Effect = effect
            Scope = GrantScope.Session session
            Expires = None
            Reason = Some reason
        }

    let private grant (host: Host) (by: Attribution) (draft: GrantDraft) : Eff<HostEnv, Result<unit, string list>> =
        GrantsService.grant host.Grants by draft
        |> Eff.map (Result.map ignore >> Result.mapError (fun e -> [ e.Message ]))

    let private revokeAll
        (host: Host)
        (by: Attribution)
        (grants: Grant list)
        (reason: string)
        : Eff<HostEnv, Result<unit, string list>> =
        effResult {
            for grant in grants do
                do!
                    GrantsService.revoke host.Grants by grant.Id (Some reason)
                    |> EffResult.mapError (fun e -> [ e.Message ])
        }

    let private switchOn (host: Host) (by: Attribution) (session: string) : Eff<HostEnv, Result<unit, string list>> =
        effResult {
            let why = $"auto approvals switched on through {by.Via}"
            do! revokeAll host by (sessionGrants host session GrantEffect.Deny) why
            let! settings, _, state = stateOf host session

            let! outcome =
                match state, settings.Mode with
                | AutoApprovals.On _, _ -> EffResult.ok ()
                | AutoApprovals.Off because, GrantMode.Off -> EffResult.error [ because ]
                | AutoApprovals.Off _, _ ->
                    effResult {
                        do! grant host by (draft session GrantEffect.Allow why)
                        let! _, _, after = stateOf host session

                        match after with
                        | AutoApprovals.On _ -> return ()
                        | AutoApprovals.Off forbidden ->
                            // Policy denies or asks: the grant would stay without effect.
                            do!
                                revokeAll
                                    host
                                    by
                                    (sessionGrants host session GrantEffect.Allow)
                                    "auto approvals are not allowed here"

                            return! EffResult.error [ forbidden ]
                    }

            return outcome
        }

    let private switchOff (host: Host) (by: Attribution) (session: string) : Eff<HostEnv, Result<unit, string list>> =
        effResult {
            let why = $"auto approvals switched off through {by.Via}"
            do! revokeAll host by (sessionGrants host session GrantEffect.Allow) why
            let! _, _, state = stateOf host session

            match state with
            | AutoApprovals.On _ -> do! grant host by (draft session GrantEffect.Deny why)
            | AutoApprovals.Off _ -> ()
        }

    /// A person turns auto approvals on (a session grant of `approvals.auto`, after revoking the
    /// session's deny grants) or off (revoking the session's allow grants, and a session deny grant
    /// where they would still hold: the host's mode, a repository or host grant), sandbox or not.
    /// Refused, with why, where they cannot hold: nothing is checked (`off`), or policy forbids them
    /// (everywhere, or only unsandboxed).
    let set
        (host: Host)
        (by: Attribution)
        (session: string)
        (auto: bool)
        : Eff<HostEnv, Result<ApprovalsView, string list>> =
        effResult {
            do!
                if auto then
                    switchOn host by session
                else
                    switchOff host by session

            let! view = view host session
            return view
        }
