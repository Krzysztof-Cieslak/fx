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
        /// asking) or `yolo` (everything runs without asking; only deny grants and policy refuse).
        Mode: string
        /// The host's `codeact.grants.mode` for the session.
        Configured: string
        /// The sandbox driver that confines the session's commands; none when no driver does (the
        /// default: they run with the user's rights) or the environment is not ready.
        Sandbox: string option
        /// Why yolo approvals are on (when they are), or why auto approvals are on or off, for people.
        Reason: string
    }

    override this.ToString() = $"{this.Session}: {this.Mode}"

/// What a person switches a session to (`session/setApprovals`).
[<RequireQualifiedAccess>]
type ApprovalSwitch =
    /// A person is asked about what no grant covers (or, in the host's `deny` mode, it is refused).
    | Ask
    /// Auto approvals: commands written plainly in the workspace run without asking.
    | Auto
    /// Yolo approvals: everything runs without asking.
    | Yolo

    override this.ToString() =
        match this with
        | Ask -> "ask"
        | Auto -> "auto"
        | Yolo -> "yolo"

/// A session's approval mode for clients (`session/approvals`, `session/setApprovals`) and `fx run`
/// (docs/modules/codeact.md, "Approval modes"): what the grants guard does with its commands, and a
/// person's switch, which is a grant of `approvals.auto` or `approvals.yolo` for the session (only
/// people grant: the method's least role is owner).
[<RequireQualifiedAccess>]
module HostApprovals =
    /// Auto or yolo approvals: their grant, and how it is checked and decided.
    type private Kind =
        {
            Name: string
            Action: ActionName
            Query: string -> GrantContext -> GrantQuery
            Decide: GrantMode -> string -> Result<GrantCheck, GrantError> -> AutoApprovals
        }

    let private auto: Kind =
        {
            Name = "auto approvals"
            Action = AutoApprovals.action
            Query = AutoApprovals.query
            Decide = AutoApprovals.decide
        }

    let private yolo: Kind =
        {
            Name = "yolo approvals"
            Action = YoloApprovals.action
            Query = YoloApprovals.query
            Decide = YoloApprovals.decide
        }

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

    let private stateOf
        (host: Host)
        (session: string)
        (kind: Kind)
        : Eff<HostEnv, GrantSettings * string option * AutoApprovals> =
        eff {
            let! settings = settingsOf host session
            let! sandbox = sandboxOf host session
            let driver = sandbox |> Option.defaultValue AutoApprovals.Unsandboxed
            let! check = GrantsService.check host.Grants (kind.Query driver (context session))
            return settings, sandbox, kind.Decide settings.Mode driver check
        }

    /// What the guard does with the session's commands now.
    let view (host: Host) (session: string) : Eff<HostEnv, ApprovalsView> =
        eff {
            let! settings, sandbox, autoState = stateOf host session auto
            let! _, _, yoloState = stateOf host session yolo

            let mode =
                match yoloState, autoState with
                | AutoApprovals.On _, _ -> "yolo"
                | _, AutoApprovals.On _ -> "auto"
                | _ when settings.Mode = GrantMode.Deny -> "deny"
                | _ -> "ask"

            let reason =
                match yoloState, autoState with
                | AutoApprovals.On because, _
                | _, AutoApprovals.On because
                | _, AutoApprovals.Off because -> because

            return
                {
                    Session = session
                    Mode = mode
                    Configured = string settings.Mode
                    Sandbox = sandbox
                    Reason = reason
                }
        }

    /// What `fx run` says once, before the turn, when auto or yolo approvals hold: what runs unasked,
    /// and with what rights.
    let notice (view: ApprovalsView) : string option =
        match view.Mode, view.Sandbox with
        | "auto", None ->
            Some
                "approvals: auto · unsandboxed: commands written plainly in the workspace run without asking, with your rights (they can read your files and use the network); downloads, installs and writes outside the workspace still ask"
        | "auto", Some driver ->
            Some
                $"approvals: auto · {driver}: commands written plainly in the workspace run without asking, confined by the sandbox; downloads, installs and writes outside the workspace still ask"
        | "yolo", None ->
            Some
                "approvals: yolo · unsandboxed: everything runs without asking, with your rights: any command, downloads, installs, pushes, writes and reads anywhere you can, the network; only deny grants and policy still refuse"
        | "yolo", Some driver ->
            Some
                $"approvals: yolo · {driver}: everything runs without asking, confined by the sandbox: any command, downloads, installs, pushes, the network; only deny grants and policy still refuse"
        | _ -> None

    /// This session's own grants of the kind's action in force, with this effect.
    let private sessionGrants (host: Host) (session: string) (kind: Kind) (effect: GrantEffect) : Grant list =
        (GrantsService.list host.Grants false).Grants
        |> List.map _.Grant
        |> List.filter (fun grant ->
            grant.Action = kind.Action
            && grant.Effect = effect
            && grant.Scope = GrantScope.Session session)

    let private draft (kind: Kind) (session: string) (effect: GrantEffect) (reason: string) : GrantDraft =
        {
            Action = kind.Action
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

    let private switchOn
        (host: Host)
        (by: Attribution)
        (session: string)
        (kind: Kind)
        : Eff<HostEnv, Result<unit, string list>> =
        effResult {
            let why = $"{kind.Name} switched on through {by.Via}"
            do! revokeAll host by (sessionGrants host session kind GrantEffect.Deny) why
            let! _, _, state = stateOf host session kind

            let! outcome =
                match state with
                | AutoApprovals.On _ -> EffResult.ok ()
                | AutoApprovals.Off _ ->
                    effResult {
                        do! grant host by (draft kind session GrantEffect.Allow why)
                        let! _, _, after = stateOf host session kind

                        match after with
                        | AutoApprovals.On _ -> return ()
                        | AutoApprovals.Off forbidden ->
                            // Policy denies or asks: the grant would stay without effect.
                            do!
                                revokeAll
                                    host
                                    by
                                    (sessionGrants host session kind GrantEffect.Allow)
                                    $"{kind.Name} are not allowed here"

                            return! EffResult.error [ forbidden ]
                    }

            return outcome
        }

    let private switchOff
        (host: Host)
        (by: Attribution)
        (session: string)
        (kind: Kind)
        : Eff<HostEnv, Result<unit, string list>> =
        effResult {
            let why = $"{kind.Name} switched off through {by.Via}"
            do! revokeAll host by (sessionGrants host session kind GrantEffect.Allow) why
            let! _, _, state = stateOf host session kind

            match state with
            | AutoApprovals.On _ -> do! grant host by (draft kind session GrantEffect.Deny why)
            | AutoApprovals.Off _ -> ()
        }

    /// A person switches the session, sandbox or not:
    /// - `Yolo`: yolo approvals on (a session grant of `approvals.yolo`, after revoking the session's
    ///   deny grants of it);
    /// - `Auto`: yolo approvals off, auto approvals on (a session grant of `approvals.auto`);
    /// - `Ask`: both off.
    /// Off revokes the session's allow grants, and makes a session deny grant where they would still
    /// hold (the host's mode, a repository or host grant). Refused, with why, where they cannot hold:
    /// policy forbids them (everywhere, or only unsandboxed).
    let set
        (host: Host)
        (by: Attribution)
        (session: string)
        (switch: ApprovalSwitch)
        : Eff<HostEnv, Result<ApprovalsView, string list>> =
        effResult {
            match switch with
            | ApprovalSwitch.Yolo -> do! switchOn host by session yolo
            | ApprovalSwitch.Auto ->
                do! switchOff host by session yolo
                do! switchOn host by session auto
            | ApprovalSwitch.Ask ->
                do! switchOff host by session yolo
                do! switchOff host by session auto

            let! view = view host session
            return view
        }
