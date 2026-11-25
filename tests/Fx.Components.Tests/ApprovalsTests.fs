/// The first install of a component is a grant (spec §6.1): `Approvals.viaGrants` asks the grants log.
module Fx.Components.Tests.ApprovalsTests

open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Grants
open Fx.Grants.Host
open Fx.Components
open Fx.Components.Tests.Support

type private Env =
    {
        Clock: Clock
        Ids: Ids
        Trace: Trace
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let private requester: Requester =
    {
        Principal = "system"
        Agent = None
        Extension = Some "fx.runtime.bun"
    }

[<Tests>]
let tests =
    testList
        "Approvals"
        [
            test "a first install waits for a person; a grant for the component approves later versions at once" {
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Ids = FakeIds(3UL, clock.Clock).Ids
                        Trace = Trace.none
                        Scope = Scope.root
                    }

                let store = InMemoryEventLog<Payload>()

                let service =
                    runIn env (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal store.Log)))

                let approve =
                    Approvals.viaGrants (GrantsService.grants service) requester GrantContext.none

                let first = specOf target (toolComponent "1.2.3" (toolArchive "1.2.3"))
                let waiting = Eff.run env (approve first)
                Expect.isFalse waiting.IsCompleted "waiting for a person"

                let request =
                    match (GrantsService.pending service).Requests with
                    | [ request ] -> request
                    | other -> failtest (string other)

                Expect.equal request.Action.Value "component.install" "the action"
                Expect.equal request.Resource "tool@1.2.3" "the resource"
                Expect.equal request.Suggestions [ "tool@*" ] "a pattern for every version"
                Expect.equal request.Offer [ ScopeKind.Once; ScopeKind.Host ] "no session or repository here"

                let answer =
                    {
                        Effect = GrantEffect.Allow
                        Scope = ScopeChoice.Host
                        Resource = Some "tool@*"
                        Expires = None
                        Reason = Some "we use tool"
                    }

                match
                    runIn env (GrantsService.answer service (Attribution.create "user:ann" "tui") request.Id answer)
                with
                | Ok(Resolution.Allowed grant) ->
                    Expect.equal (waiting.GetAwaiter().GetResult()) (Approved("grant " + grant.Value)) "approved"
                | other -> failtest (string other)

                let second = specOf target (toolComponent "2.0.0" (toolArchive "2.0.0"))

                match runIn env (approve second) with
                | Approved _ -> ()
                | other -> failtest (string other)

                Expect.isEmpty (GrantsService.pending service).Requests "asked once"
            }

            test "a denied install is rejected with the reason" {
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Ids = FakeIds(4UL, clock.Clock).Ids
                        Trace = Trace.none
                        Scope = Scope.root
                    }

                let store = InMemoryEventLog<Payload>()

                let service =
                    runIn env (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal store.Log)))

                let spec = specOf target (toolComponent "1.2.3" (toolArchive "1.2.3"))

                let waiting =
                    Eff.run env (Approvals.viaGrants (GrantsService.grants service) requester GrantContext.none spec)

                let request = (GrantsService.pending service).Requests.Head

                let deny =
                    {
                        Effect = GrantEffect.Deny
                        Scope = ScopeChoice.Once
                        Resource = None
                        Expires = None
                        Reason = Some "not now"
                    }

                runIn env (GrantsService.answer service (Attribution.create "user:ann" "tui") request.Id deny)
                |> ignore

                Expect.equal (waiting.GetAwaiter().GetResult()) (Rejected "denied by user:ann: not now") "rejected"
            }
        ]
