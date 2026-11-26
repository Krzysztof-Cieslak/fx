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

/// The first attempt of an install, asked for by an environment.
let private attempt (holder: string) (n: int) (spec: InstallSpec) : ApprovalRequest =
    {
        Spec = spec
        Holder = holder
        Attempt = n
    }

let private first = attempt "environment:e1" 1

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

                let one = specOf target (toolComponent "1.2.3" (toolArchive "1.2.3"))
                let waiting = Eff.run env (approve (first one))
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

                match runIn env (approve (attempt "environment:e2" 1 second)) with
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
                    Eff.run
                        env
                        (Approvals.viaGrants (GrantsService.grants service) requester GrantContext.none (first spec))

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

            test "an answer is final for its attempt only: the same attempt asks once, a later one asks again" {
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Ids = FakeIds(5UL, clock.Clock).Ids
                        Trace = Trace.none
                        Scope = Scope.root
                    }

                let store = InMemoryEventLog<Payload>()

                let service =
                    runIn env (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal store.Log)))

                let approve =
                    Approvals.viaGrants (GrantsService.grants service) requester GrantContext.none

                let spec = specOf target (toolComponent "1.2.3" (toolArchive "1.2.3"))
                let person = Attribution.create "user:ann" "tui"

                let deny =
                    {
                        Effect = GrantEffect.Deny
                        Scope = ScopeChoice.Once
                        Resource = None
                        Expires = None
                        Reason = Some "not now"
                    }

                // Nobody answers the first attempt: the host withdraws its request (a headless run).
                let waiting = Eff.run env (approve (first spec))
                let asked = (GrantsService.pending service).Requests.Head

                runIn env (GrantsService.withdraw service asked.Id "nobody could answer")
                |> ignore

                Expect.equal
                    (waiting.GetAwaiter().GetResult())
                    (Rejected "withdrawn: nobody could answer")
                    "the attempt is rejected"

                // The same attempt asking again (the pipeline retrying) is the same request: no prompt.
                Expect.equal
                    (runIn env (approve (first spec)))
                    (Rejected "withdrawn: nobody could answer")
                    "the same attempt keeps its outcome"

                Expect.isEmpty (GrantsService.pending service).Requests "not asked again"

                // A later attempt, or another holder's (a later run's environment), is a new request.
                for later in [ attempt "environment:e1" 2 spec; attempt "environment:e2" 1 spec ] do
                    let waiting = Eff.run env (approve later)
                    Expect.isFalse waiting.IsCompleted "a person is asked again"

                    let request =
                        match (GrantsService.pending service).Requests with
                        | [ request ] -> request
                        | other -> failtest (string other)

                    Expect.equal request.Resource "tool@1.2.3" "the same component"

                    runIn env (GrantsService.answer service person request.Id deny) |> ignore

                    Expect.equal
                        (waiting.GetAwaiter().GetResult())
                        (Rejected "denied by user:ann: not now")
                        "denied once: that attempt only"

                Expect.notEqual
                    (Approvals.key (first spec))
                    (Approvals.key (attempt "environment:e1" 2 spec))
                    "keys tell attempts apart"
            }

            test "the prompt shows sizes as people read them" {
                Expect.equal (Approvals.size 512L) "512 B" "bytes"
                Expect.equal (Approvals.size 54438042L) "51.9 MiB" "the Bun download"
                Expect.equal (Approvals.size 115378776L) "110.0 MiB" "Bun installed"
                Expect.equal (Approvals.size (3L * 1024L * 1024L * 1024L)) "3.0 GiB" "GiB"

                Expect.equal
                    (Approvals.sizes
                        {
                            Download = Some 54438042L
                            Installed = None
                        })
                    "download 51.9 MiB, installed size unknown"
                    "a hint"

                let spec = specOf target (toolComponent "1.2.3" (toolArchive "1.2.3"))

                let size =
                    (Approvals.ask requester GrantContext.none (first spec)).Evidence
                    |> List.find (fun e -> e.Label = "size")

                Expect.isFalse (size.Text.StartsWith "size") "the label is not repeated"
            }
        ]
