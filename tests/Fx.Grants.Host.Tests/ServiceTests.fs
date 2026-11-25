module Fx.Grants.Host.Tests.ServiceTests

open System
open System.Threading
open Expecto
open Fx.Effects
open Fx.Machines.Host
open Fx.Store
open Fx.Sdk
open Fx.Grants
open Fx.Grants.Host
open Fx.Grants.Host.Tests.Support

let private answerAs (h: Harness) (by: Fx.Machines.Attribution) (id: RequestId) (answer: GrantAnswer) =
    h.Run(GrantsService.answer h.Service by id answer)

let private policyGate (decide: GrantQuery -> Decision) : Contributed<Gate<GrantQuery>> =
    {
        Extension = "fx.policy"
        Type = GrantPolicy.gates.Name
        Id = "test"
        Value =
            {
                Check = fun query -> Eff.ret (decide query)
            }
        Env = extensionEnv "fx.policy" (fakes ()).Env.Clock Scope.root
    }

[<Tests>]
let tests =
    testList
        "Grants service"
        [
            test "with no grant, a check asks and a request becomes a durable pending request" {
                let h = harness ()

                match h.Run(GrantsService.check h.Service (query shellExec "git status")) with
                | Ok(GrantCheck.MustAsk [ "no grant applies" ]) -> ()
                | other -> failtest (string other)

                let id = pendingOf h (ask "call-1" shellExec "git status")
                let pending = GrantsService.pending h.Service
                Expect.equal (pending.Requests |> List.map _.Id) [ id ] "pending"

                Expect.equal
                    pending.Requests.Head.Offer
                    [ ScopeKind.Once; ScopeKind.Session; ScopeKind.Repo; ScopeKind.Host ]
                    "offer"

                Expect.equal
                    (h.Run(GrantsService.request h.Service (ask "call-1" shellExec "git status")))
                    (Ok(AskOutcome.Pending id))
                    "the same key, the same request"

                Expect.equal (List.length (events h)) 1 "one event"
            }

            test "an answer resolves the waiting requester" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" shellExec "git status")
                let waiting = Eff.run h.Env (GrantsService.await h.Service id)
                Expect.isFalse waiting.IsCompleted "waiting"
                let resolution = ok (answerAs h alice id once)

                match resolution with
                | Resolution.Allowed _ -> ()
                | other -> failtest (string other)

                Expect.equal (wait waiting) (Ok resolution) "the requester sees it"
                Expect.isEmpty (GrantsService.pending h.Service).Requests "nothing pending"

                match h.Run(GrantsService.check h.Service (query shellExec "git status")) with
                | Ok(GrantCheck.MustAsk _) -> ()
                | other -> failtest ("a once answer applies once: " + string other)
            }

            test "a session answer allows the next request at once" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" shellExec "git status")
                ok (answerAs h alice id forSession) |> ignore

                match h.Run(GrantsService.request h.Service (ask "call-2" shellExec "git status")) with
                | Ok(AskOutcome.Settled(Resolution.Allowed _)) -> ()
                | other -> failtest (string other)

                Expect.equal (List.length (events h)) 2 "no new request"
            }

            test "answering one request settles the others it covers (batched prompts)" {
                let h = harness ()
                let first = pendingOf h (ask "call-1" shellExec "npm test")
                let second = pendingOf h (ask "call-2" shellExec "npm run build")
                let third = pendingOf h (ask "call-3" shellExec "rm -rf build")
                let waitingSecond = Eff.run h.Env (GrantsService.await h.Service second)
                let waitingThird = Eff.run h.Env (GrantsService.await h.Service third)

                ok (
                    answerAs
                        h
                        alice
                        first
                        { forSession with
                            Resource = Some "npm **"
                        }
                )
                |> ignore

                match wait waitingSecond with
                | Ok(Resolution.Allowed _) -> ()
                | other -> failtest (string other)

                Expect.isFalse waitingThird.IsCompleted "not covered"
                Expect.equal ((GrantsService.pending h.Service).Requests |> List.map _.Id) [ third ] "one left"

                ok (answerAs h alice third { once with Effect = GrantEffect.Deny }) |> ignore

                match wait waitingThird with
                | Ok(Resolution.Denied _) -> ()
                | other -> failtest (string other)
            }

            test "a deny grant made while a request waits refuses it" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" shellExec "git push origin main")
                let waiting = Eff.run h.Env (GrantsService.await h.Service id)

                let draft =
                    {
                        Action = shellExec
                        Resource = "git push **"
                        Effect = GrantEffect.Deny
                        Scope = GrantScope.Host
                        Expires = None
                        Reason = Some "no pushes"
                    }

                ok (h.Run(GrantsService.grant h.Service bob draft)) |> ignore

                match wait waiting with
                | Ok(Resolution.Denied [ reason ]) -> Expect.stringContains reason "no pushes" "the deny's reason"
                | other -> failtest (string other)
            }

            test "only people answer, grant and revoke" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" shellExec "ls")
                let system = Fx.Machines.Attribution.system "agent"

                Expect.equal (answerAs h system id once |> error).Code "grants.not-a-person" "answer"

                let draft =
                    {
                        Action = shellExec
                        Resource = "**"
                        Effect = GrantEffect.Allow
                        Scope = GrantScope.Host
                        Expires = None
                        Reason = None
                    }

                Expect.equal
                    (h.Run(GrantsService.grant h.Service system draft) |> error).Code
                    "grants.not-a-person"
                    "grant"

                let made = ok (h.Run(GrantsService.grant h.Service alice draft))

                Expect.equal
                    (h.Run(GrantsService.revoke h.Service system made.Id None) |> error).Code
                    "grants.not-a-person"
                    "revoke"

                ok (h.Run(GrantsService.revoke h.Service bob made.Id (Some "too broad")))
                Expect.equal (GrantsService.list h.Service false).Grants [] "none in force"
                Expect.equal (GrantsService.list h.Service true).Grants.Length 1 "one in all"
            }

            test "typed errors: invalid answers, unknown requests, unknown actions, bad resources" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" (ActionName.create "budget.raise") "run1/usd")

                Expect.equal
                    (answerAs h alice id forSession |> error).Code
                    "grants.invalid-answer"
                    "budget raises are once only"

                Expect.equal (answerAs h alice (RequestId "nope") once |> error).Code "grants.unknown-request" "unknown"

                Expect.equal
                    (h.Run(GrantsService.check h.Service (query (ActionName.create "x.y") "a"))
                     |> error)
                        .Code
                    "grants.unknown-action"
                    "unknown action"

                Expect.equal
                    (h.Run(GrantsService.request h.Service (ask "k" fsWrite "src/../secrets"))
                     |> error)
                        .Code
                    "grants.invalid-resource"
                    "not canonical"

                let first = ok (answerAs h alice id once)
                Expect.equal (answerAs h alice id once) (Ok first) "the same answer again: the first outcome"

                Expect.equal
                    (answerAs h alice id { once with Effect = GrantEffect.Deny } |> error).Code
                    "grants.not-pending"
                    "a different one"
            }

            test "withdrawing closes a request; a cancelled authorize withdraws its request" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" shellExec "make")
                let waiting = Eff.run h.Env (GrantsService.await h.Service id)
                ok (h.Run(GrantsService.withdraw h.Service id "the turn ended"))
                Expect.equal (wait waiting) (Ok(Resolution.Cancelled "withdrawn: the turn ended")) "cancelled"

                use cancel = new CancellationTokenSource()

                let env =
                    {
                        Grants = h.Grants
                        Scope =
                            { Scope.root with
                                Cancellation = cancel.Token
                            }
                    }

                let authorizing =
                    Eff.run env (Grants.authorize (ask "call-2" shellExec "make install"))

                Expect.isFalse authorizing.IsCompleted "waiting for a person"
                cancel.Cancel()
                cancelled (fun () -> wait authorizing |> ignore) "cancelled"

                match
                    (GrantsService.snapshot h.Service).State.Requests
                    |> Map.toList
                    |> List.map snd
                    |> List.find (fun e -> e.Request.Key = "call-2")
                with
                | {
                      Status = RequestStatus.Closed { Why = CloseReason.Withdrawn _ }
                  } -> ()
                | other -> failtest (string other)
            }

            test "timers: requests time out and grants expire, on the clock" {
                let h = harness ()
                let at = h.Fakes.Clock.Now.UnixMicroseconds

                let id =
                    pendingOf
                        h
                        { ask "call-1" shellExec "make" with
                            Expires = Some(at + 60_000_000L)
                        }

                let waiting = Eff.run h.Env (GrantsService.await h.Service id)
                h.Fakes.Clock.Advance(TimeSpan.FromSeconds 59.0)
                Expect.isFalse waiting.IsCompleted "not yet"
                h.Fakes.Clock.Advance(TimeSpan.FromSeconds 1.0)
                Expect.equal (wait waiting) (Ok(Resolution.Cancelled "timed out")) "timed out"

                let other = pendingOf h (ask "call-2" shellExec "make")

                ok (
                    answerAs
                        h
                        alice
                        other
                        { forSession with
                            Expires = Some(at + 120_000_000L)
                        }
                )
                |> ignore

                match h.Run(GrantsService.check h.Service (query shellExec "make")) with
                | Ok(GrantCheck.Allowed _) -> ()
                | other -> failtest (string other)

                h.Fakes.Clock.Advance(TimeSpan.FromSeconds 60.0)

                match h.Run(GrantsService.check h.Service (query shellExec "make")) with
                | Ok(GrantCheck.MustAsk _) -> ()
                | other -> failtest (string other)

                Expect.equal
                    ((GrantsService.list h.Service true).Grants |> List.map (fun e -> e.Status))
                    [ GrantStatus.Expired(at + 120_000_000L) ]
                    "the expiry is in the log"
            }

            test "policy only tightens: rules and gates outrank grants" {
                let rule =
                    {
                        Id = "host/no-push"
                        Action = shellExec
                        Resource = "git push **"
                        Effect = PolicyEffect.Deny
                        Reason = "pushes go through review"
                    }

                let gate =
                    policyGate (fun q ->
                        if q.Resource.StartsWith("curl", StringComparison.Ordinal) then
                            Decision.Ask "network tools need a person"
                        else
                            Decision.Allow)

                let h =
                    startOn (fakes ()) (InMemoryEventLog<Payload>()) (fun s ->
                        { s with
                            Policy = [ rule ]
                            Gates = [ gate ]
                        })

                let draft =
                    {
                        Action = shellExec
                        Resource = "**"
                        Effect = GrantEffect.Allow
                        Scope = GrantScope.Host
                        Expires = None
                        Reason = None
                    }

                ok (h.Run(GrantsService.grant h.Service alice draft)) |> ignore

                match h.Run(GrantsService.check h.Service (query shellExec "git push origin")) with
                | Ok(GrantCheck.Denied [ reason ]) -> Expect.stringContains reason "host/no-push" "the rule"
                | other -> failtest (string other)

                match h.Run(GrantsService.check h.Service (query shellExec "curl example.com")) with
                | Ok(GrantCheck.MustAsk [ reason ]) -> Expect.stringContains reason "network tools" "the gate"
                | other -> failtest (string other)

                match h.Run(GrantsService.check h.Service (query shellExec "ls")) with
                | Ok(GrantCheck.Allowed _) -> ()
                | other -> failtest (string other)

                match h.Run(GrantsService.request h.Service (ask "k" shellExec "git push origin")) with
                | Ok(AskOutcome.Settled(Resolution.Denied _)) -> ()
                | other -> failtest ("policy denies without asking: " + string other)
            }

            test "a gate that fails denies" {
                let failing =
                    { policyGate (fun _ -> Decision.Allow) with
                        Value =
                            {
                                Check = fun _ -> fun _ -> raise (InvalidOperationException "boom")
                            }
                    }

                let h =
                    startOn (fakes ()) (InMemoryEventLog<Payload>()) (fun s -> { s with Gates = [ failing ] })

                match h.Run(GrantsService.check h.Service (query shellExec "ls")) with
                | Ok(GrantCheck.Denied _) -> ()
                | other -> failtest (string other)
            }

            test "ending a session expires its grants and closes its requests" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" shellExec "make")
                ok (answerAs h alice id forSession) |> ignore
                let other = pendingOf h (ask "call-2" fsWrite "src/a.fs")
                let waiting = Eff.run h.Env (GrantsService.await h.Service other)
                ok (h.Run(GrantsService.endSession h.Service "s1"))
                Expect.equal (wait waiting) (Ok(Resolution.Cancelled "session ended")) "closed"

                match h.Run(GrantsService.check h.Service (query shellExec "make")) with
                | Ok(GrantCheck.MustAsk _) -> ()
                | other -> failtest (string other)
            }

            test "the grants capability as a gate: other gates only tighten it" {
                let h = harness ()
                let id = pendingOf h (ask "call-1" shellExec "ls")

                ok (
                    answerAs
                        h
                        alice
                        id
                        { forSession with
                            Scope = ScopeChoice.Host
                        }
                )
                |> ignore

                let gate =
                    Grants.gate h.Grants (fun (command: string) ->
                        if command.StartsWith("#", StringComparison.Ordinal) then
                            None
                        else
                            Some(query shellExec command))

                let decide command =
                    h.Run(fun (env: Env) -> gate.Check command (extensionEnv "fx.test" env.Clock env.Scope))

                Expect.equal (decide "ls") Decision.Allow "granted"
                Expect.equal (decide "# a comment") Decision.Allow "not about grants"

                match decide "rm -rf /" with
                | Decision.Ask _ -> ()
                | other -> failtest (string other)

                match decide "a  b" with
                | Decision.Deny _ -> ()
                | other -> failtest ("an invalid resource denies: " + string other)
            }
        ]
