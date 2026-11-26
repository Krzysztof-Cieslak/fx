namespace Fx.Components

open System.Globalization
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
/// grants log. The request is `component.install` on `<id>@<version>`, keyed by what asks: the
/// install, the holder that started the attempt and the attempt. Asking again for the same attempt (the
/// pipeline retrying, recovery) is the same request; a later attempt or another holder (the
/// environment of a later run) is a new one, so an answer for one attempt ("once", or a request nobody
/// could answer) never stands for the next. A host-wide or repository grant for the component approves
/// later installs at once.
[<RequireQualifiedAccess>]
module Approvals =
    let private componentInstall = ActionName.create "component.install"

    /// A byte count as people read it: `51.9 MiB`.
    let size (bytes: int64) : string =
        let units = [ "KiB"; "MiB"; "GiB"; "TiB" ]

        if bytes < 1024L then
            $"{bytes} B"
        else
            let mutable value = float bytes / 1024.0
            let mutable unit = 0

            while value >= 1024.0 && unit < units.Length - 1 do
                value <- value / 1024.0
                unit <- unit + 1

            value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit]

    /// What an install's size hint says: `download 51.9 MiB, installed 110.0 MiB`.
    let sizes (hint: SizeHint) : string =
        let show (what: string) (bytes: int64 option) =
            match bytes with
            | Some bytes -> what + " " + size bytes
            | None -> what + " size unknown"

        show "download" hint.Download + ", " + show "installed" hint.Installed

    /// The request key of an install attempt.
    let key (request: ApprovalRequest) : string =
        "component.install/"
        + request.Spec.Key.Name
        + "/"
        + request.Holder
        + "/"
        + string request.Attempt

    /// What an install asks the grants log.
    let ask (requester: Requester) (context: GrantContext) (request: ApprovalRequest) : GrantAsk =
        let spec = request.Spec

        {
            Key = key request
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
                        Text = sizes spec.Size
                    }
                ]
            Suggestions = [ spec.Id + "@*" ]
            Scopes = []
            Expires = None
        }

    /// Approves installs through a grants capability: asks, and waits for a person if the rules say
    /// ask. Allowed is `Approved` (by the grant); denied, cancelled or failed is `Rejected`. The answer
    /// is final for that attempt only: a later attempt asks again unless a grant settles it.
    let viaGrants<'R when 'R :> IScopeWith<'R>>
        (grants: Grants)
        (requester: Requester)
        (context: GrantContext)
        : ApprovalRequest -> Eff<'R, ApprovalAnswer> =
        fun request ->
            fun env ->
                task {
                    let grantsEnv: GrantsOnly = { Grants = grants; Scope = env.Scope }

                    let! resolution = Grants.authorize (ask requester context request) grantsEnv

                    return
                        match resolution with
                        | Ok(Resolution.Allowed grant) -> Approved("grant " + grant.Value)
                        | Ok(Resolution.Denied reasons) -> Rejected(String.concat "; " reasons)
                        | Ok(Resolution.Cancelled reason) -> Rejected reason
                        | Error e -> Rejected(string e)
                }
