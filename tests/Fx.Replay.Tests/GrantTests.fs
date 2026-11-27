/// Grants in recorded sessions (T127b's guard, docs/modules/replay.md): the guard asks the grants log
/// before a cell runs a command; one request is allowed and one denied by a person. The guard's
/// decisions are journaled (`Hands.Guard`), so replay answers them without a grants log or a person,
/// and the session replays exactly; so does a session whose commands ran on auto approvals (T134), with
/// how each ran in the journal and on the receipts.
module Fx.Replay.Tests.GrantTests

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Store
open Fx.Machines.Host
open Fx.Sessions
open Fx.Grants
open Fx.Grants.Host
open Fx.CodeAct
open Fx.Replay
open Fx.Replay.Tests.Support
open Fx.Replay.Tests.Harness

/// What the grants service runs in.
type GrantsEnv =
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

    interface IScopeWith<GrantsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "GrantsEnv"

let private answer (effect: GrantEffect) (reason: string option) : GrantAnswer =
    {
        Effect = effect
        Scope = ScopeChoice.Once
        Resource = None
        Expires = None
        Reason = reason
    }

/// Waits for the next pending grant request, and gives it.
let private nextRequest (service: GrantsService<GrantsEnv>) : Task<GrantRequest> =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 60.0
        let mutable found = None

        while found.IsNone do
            found <- (GrantsService.pending service).Requests |> List.tryHead

            if found.IsNone then
                if DateTime.UtcNow > deadline then
                    failtest "no grant request"

                do! Task.Delay 10

        return found.Value
    }

let private grantsEnv () : GrantsEnv =
    {
        Clock = SystemClock.clock
        Ids = SystemIds.create SystemClock.clock
        Trace = Trace.none
        Scope = Scope.root
    }

let private autoApprovals =
    testTask "a session whose commands ran on auto approvals replays exactly, with how each ran" {
        let env = grantsEnv ()
        let grantsStore = InMemoryEventLog<Payload>()

        let! service =
            Eff.run env (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal grantsStore.Log)))

        let grants = GrantsService.grants service

        // The host's mode is auto; nothing confines the hands (the default).
        let guard (variants: Fx.Variants.Variants) sessions (turn: TurnStart) hands =
            eff {
                let! settings = GrantSettings.ofVariants variants

                return
                    GrantGuard.attach
                        (GrantSetup.create grants sessions turn { settings with Mode = GrantMode.Auto })
                        hands
            }

        let f = createGuarded [] ran (Inherit []) None guard

        try
            f.Model.AddAll
                [
                    Say.cell "// intent: build\ncall r = shell.run [\"make test\"]\nr"
                    Say.cell "// intent: fetch\ncall r = shell.run [\"curl -O https://example.com/a\"]\nr"
                    Say.finish "Built and fetched"
                ]

            do! start f "s1"
            do! prompt f "s1" "p1" "Build, then fetch"

            // Only the download asks.
            let! (asked: GrantRequest) = nextRequest service
            Expect.equal asked.Resource "curl -O https://example.com/a" "the download"

            match! Eff.run env (GrantsService.answer service alice asked.Id (answer GrantEffect.Allow None)) with
            | Ok _ -> ()
            | Error e -> failtest $"{e}"

            let! ending = ended f "s1" 1
            SessionTests.completed ending
            do! close f

            Expect.equal (GrantsService.snapshot service).State.Requests.Count 1 "one request in the grants log"

            let guarded =
                f.Store.Events(JournalLog.stream "s1")
                |> List.map (EventLog.decode JournalLog.schema)
                |> List.collect (fun r ->
                    match r.Event with
                    | JournalEvent.Recorded(_, entries) -> entries
                    | _ -> [])
                |> List.filter (fun e -> e.Op = Markers.Guard)
                |> List.map (fun e -> string e.Outcome)

            Expect.isTrue
                (guarded |> List.exists (fun o -> o.Contains "\"auto\""))
                $"auto approvals journaled: {guarded}"

            Expect.isTrue
                (guarded |> List.exists (fun o -> o.Contains "\"person\""))
                $"a person's answer journaled: {guarded}"

            let receipts =
                f.Store.Events(ContextLog.stream "s1")
                |> List.map (EventLog.decode ContextLog.schema)
                |> List.choose (fun r ->
                    match r.Event with
                    | Fx.Prompt.ContextEvent.Returned receipt -> Some receipt.Approvals
                    | _ -> None)
                |> List.concat
                |> List.map (fun a -> a.Resource + " " + string a.By)

            Expect.contains receipts "make test auto" "the build's receipt"

            Expect.isTrue
                (receipts
                 |> List.exists (fun r -> r.StartsWith "curl -O https://example.com/a person"))
                $"the download's receipt: {receipts}"

            let! report = replay f.Recording "s1"
            dump f "s1" report
            expectMatched report
        finally
            (f :> IDisposable).Dispose()

            (Eff.run env (GrantsService.stop service)).Wait(TimeSpan.FromSeconds 30.0)
            |> ignore
    }

let private peopleTests =
    testTask "a session whose commands were allowed and denied by people replays exactly" {
        let env: GrantsEnv =
            {
                Clock = SystemClock.clock
                Ids = SystemIds.create SystemClock.clock
                Trace = Trace.none
                Scope = Scope.root
            }

        let grantsStore = InMemoryEventLog<Payload>()

        let! service =
            Eff.run env (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal grantsStore.Log)))

        let grants = GrantsService.grants service

        let guard (variants: Fx.Variants.Variants) sessions (turn: TurnStart) hands =
            eff {
                let! settings = GrantSettings.ofVariants variants
                return GrantGuard.attach (GrantSetup.create grants sessions turn settings) hands
            }

        let f = createGuarded [] ran (Inherit []) None guard

        try
            f.Model.AddAll
                [
                    Say.cell "// intent: test\ncall r = shell.run [\"make test\"]\nr"
                    Say.cell "// intent: clean\ncall r = shell.run [\"rm -rf build\"]\nr"
                    Say.finish "Tested; cleaning was refused"
                ]

            do! start f "s1"
            do! prompt f "s1" "p1" "Test, then clean"

            let! (allowed: GrantRequest) = nextRequest service

            match! Eff.run env (GrantsService.answer service alice allowed.Id (answer GrantEffect.Allow None)) with
            | Ok _ -> ()
            | Error e -> failtest $"{e}"

            let! (denied: GrantRequest) = nextRequest service

            match!
                Eff.run env (GrantsService.answer service alice denied.Id (answer GrantEffect.Deny (Some "not now")))
            with
            | Ok _ -> ()
            | Error e -> failtest $"{e}"

            let! ending = ended f "s1" 1
            SessionTests.completed ending
            do! close f

            let journal =
                f.Store.Events(JournalLog.stream "s1")
                |> List.map (EventLog.decode JournalLog.schema)
                |> List.collect (fun r ->
                    match r.Event with
                    | JournalEvent.Recorded(_, entries) -> entries
                    | _ -> [])
                |> List.filter (fun e -> e.Op = Markers.Guard)

            Expect.isTrue
                (journal |> List.exists (fun e -> (string e.Outcome).Contains "denied"))
                "the denial is journaled"

            let! report = replay f.Recording "s1"
            dump f "s1" report
            expectMatched report
        finally
            (f :> IDisposable).Dispose()

            (Eff.run env (GrantsService.stop service)).Wait(TimeSpan.FromSeconds 30.0)
            |> ignore
    }

[<Tests>]
let tests = testList "grants" [ peopleTests; autoApprovals ]
