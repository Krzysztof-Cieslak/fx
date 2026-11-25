/// Grants in recorded sessions (T127b's guard, docs/modules/replay.md): the guard asks the grants log
/// before a cell runs a command; one request is allowed and one denied by a person. The guard's
/// decisions are journaled (`Hands.Guard`), so replay answers them without a grants log or a person,
/// and the session replays exactly.
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

[<Tests>]
let tests =
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
