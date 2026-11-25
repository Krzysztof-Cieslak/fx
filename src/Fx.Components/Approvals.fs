namespace Fx.Components

open Fx.Effects
open Fx.Sdk
open Fx.Grants

/// An environment with only a grants capability, for asking on behalf of an installer.
type internal GrantsOnly =
    {
        Grants: Grants
        Scope: Scope
    }

    interface IGrants with
        member env.Grants = env.Grants

    interface IScopeWith<GrantsOnly> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "GrantsOnly"

/// The first install of a component is a grant (spec §6.1, §13): `InstallerSettings.Approve` from the
/// grants log. The request is `component.install` on `<id>@<version>`, keyed by the install, so asking
/// again (a retried install, another installer) is the same request, and a host-wide or repository
/// grant for the component approves later installs at once.
[<RequireQualifiedAccess>]
module Approvals =
    let private componentInstall = ActionName.create "component.install"

    /// What an install asks the grants log.
    let ask (requester: Requester) (context: GrantContext) (spec: InstallSpec) : GrantAsk =
        {
            Key = "component.install/" + spec.Key.Name
            Action = componentInstall
            Resource = spec.Id + "@" + string spec.Version
            Context = context
            Requester = requester
            Summary = "Install " + spec.Id + " " + string spec.Version + " for " + string spec.Target
            Evidence =
                [
                    {
                        Label = "method"
                        Text = string spec.Method
                    }
                    {
                        Label = "size"
                        Text = string spec.Size
                    }
                ]
            Suggestions = [ spec.Id + "@*" ]
            Scopes = []
            Expires = None
        }

    /// Approves installs through a grants capability: asks, and waits for a person if the rules say
    /// ask. Allowed is `Approved` (by the grant); denied, cancelled or failed is `Rejected`. The answer
    /// to an install's request is final for that install: after "deny once", installing the same
    /// version again is refused until a grant allows it (made in the grants view).
    let viaGrants<'R when 'R :> IScopeWith<'R>>
        (grants: Grants)
        (requester: Requester)
        (context: GrantContext)
        : InstallSpec -> Eff<'R, ApprovalAnswer> =
        fun spec ->
            fun env ->
                task {
                    let grantsEnv: GrantsOnly = { Grants = grants; Scope = env.Scope }

                    let! resolution = Grants.authorize (ask requester context spec) grantsEnv

                    return
                        match resolution with
                        | Ok(Resolution.Allowed grant) -> Approved("grant " + grant.Value)
                        | Ok(Resolution.Denied reasons) -> Rejected(String.concat "; " reasons)
                        | Ok(Resolution.Cancelled reason) -> Rejected reason
                        | Error e -> Rejected(string e)
                }
