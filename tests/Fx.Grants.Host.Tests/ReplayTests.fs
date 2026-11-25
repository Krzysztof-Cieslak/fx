/// Replay (spec §1 principle 4, AGENTS.md: machine changes need replay tests): a grants log rebuilt from
/// its events is the state the running service had, the same inputs give the same log, and what version
/// 1 wrote is read the same way through the store's schema.
module Fx.Grants.Host.Tests.ReplayTests

open System
open System.IO
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Grants
open Fx.Grants.Host
open Fx.Grants.Host.Tests.Support

let private stream = GrantMachine.stream "host"

/// The events of the host-local stream, decoded.
let private decoded (log: InMemoryEventLog<Payload>) : GrantEvent list =
    log.Events stream
    |> List.map (fun recorded ->
        match EventSchema.decode GrantMachine.schema recorded.Event with
        | Ok event -> event
        | Error reason -> failtest reason)

/// A session of the grants view: requests, answers of every scope, a direct grant, a revocation, a
/// timeout and an expiry on the clock, a withdrawal.
let private scenario (h: Harness) =
    let at = h.Fakes.Clock.Now.UnixMicroseconds
    let first = pendingOf h (ask "call-1" shellExec "git status")

    ok (
        h.Run(
            GrantsService.answer
                h.Service
                alice
                first
                { forSession with
                    Resource = Some "git *"
                }
        )
    )
    |> ignore

    let second = pendingOf h (ask "call-2" fsWrite "src/a.fs")

    ok (
        h.Run(
            GrantsService.answer
                h.Service
                bob
                second
                { forSession with
                    Scope = ScopeChoice.Repo(Some "src")
                    Expires = Some(at + 30_000_000L)
                }
        )
    )
    |> ignore

    let third =
        pendingOf
            h
            { ask "call-3" shellExec "make" with
                Expires = Some(at + 10_000_000L)
            }

    let fourth = pendingOf h (ask "call-4" shellExec "rm -rf build")

    ok (
        h.Run(
            GrantsService.answer
                h.Service
                alice
                fourth
                { once with
                    Effect = GrantEffect.Deny
                    Scope = ScopeChoice.Host
                }
        )
    )
    |> ignore

    let made =
        ok (
            h.Run(
                GrantsService.grant
                    h.Service
                    alice
                    {
                        Action = ActionName.create "net.connect"
                        Resource = "*.github.com:443"
                        Effect = GrantEffect.Allow
                        Scope = GrantScope.Host
                        Expires = None
                        Reason = Some "GitHub"
                    }
            )
        )

    ok (h.Run(GrantsService.revoke h.Service bob made.Id (Some "not needed")))
    let fifth = pendingOf h (ask "call-5" shellExec "npm test")
    ok (h.Run(GrantsService.withdraw h.Service fifth "interrupted"))
    ignore third
    pendingOf h (ask "call-6" fsWrite "docs/b.md") |> ignore

let private lines (log: InMemoryEventLog<Payload>) : string list =
    log.Events stream
    |> List.map (fun recorded -> Json.serializeCanonical recorded.Event.Data + " " + string recorded.Meta)

let private root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith ("no Fx.slnx above " + AppContext.BaseDirectory)
        | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
        | dir -> up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

[<Tests>]
let tests =
    testList
        "Grants replay"
        [
            test "a restarted service rebuilds the state from the log, and keeps its timers" {
                let fakes = fakes ()
                let log = InMemoryEventLog<Payload>()
                let h = startOn fakes log id
                scenario h
                h.Fakes.Clock.Advance(TimeSpan.FromSeconds 10.0)
                let before = GrantsService.snapshot h.Service
                h.Run(GrantsService.stop h.Service)

                Expect.equal before.State (GrantLog.replay (decoded log)) "the state is the fold of the log"

                let restarted = startOn fakes log id
                Expect.equal (GrantsService.snapshot restarted.Service) before "the same state after a restart"

                // The repo grant's expiry was recovered: it fires on the restarted service's clock.
                fakes.Clock.Advance(TimeSpan.FromSeconds 20.0)

                Expect.exists
                    (decoded log)
                    (fun event ->
                        match event with
                        | GrantEvent.Expired _ -> true
                        | _ -> false)
                    "expired after the restart"

                Expect.equal
                    (GrantsService.snapshot restarted.Service).State
                    (GrantLog.replay (decoded log))
                    "still the fold of the log"

                Expect.equal
                    ((GrantsService.pending restarted.Service).Requests |> List.map _.Key)
                    [ "call-6" ]
                    "the durable pending request survived"
            }

            test "the same inputs give the same log" {
                let run () =
                    let h = harness ()
                    scenario h
                    h.Fakes.Clock.Advance(TimeSpan.FromSeconds 40.0)
                    lines h.Log

                let first = run ()
                Expect.isGreaterThan first.Length 10 "a real log"
                Expect.equal (run ()) first "identical"
            }

            test "version 1's events, as kept by the pure tests, read through the store's schema" {
                let kept =
                    File.ReadAllText(Path.Combine(root, "tests", "Fx.Grants.Tests", "golden", "events-v1.jsonl"))

                let payloads =
                    kept.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Json.parse line with
                        | Ok json -> { Version = 1; Data = json }
                        | Error e -> failtest (string e))
                    |> List.ofArray

                let log = InMemoryEventLog<Payload>()
                let fakes = fakes ()

                let meta: EventMeta =
                    {
                        At = fakes.Clock.Now
                        By = Attribution.system "import"
                        Key = IdempotencyKey "import"
                        Cause = None
                    }

                match wait (log.Log.Append stream 0L meta payloads none) with
                | Ok head -> Expect.equal head (int64 payloads.Length) "appended"
                | Error e -> failtest (string e)

                let h = startOn fakes log id
                let events = decoded log
                Expect.equal (GrantsService.snapshot h.Service).State (GrantLog.replay events) "the service folds them"

                Expect.equal
                    (events
                     |> List.map (fun e -> Json.serializeCanonical (Codecs.event.Encode e) + "\n")
                     |> String.concat "")
                    kept
                    "written back the same"
            }
        ]
