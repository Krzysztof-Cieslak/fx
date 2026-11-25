module Fx.Grants.Tests.GrantLogTests

open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Grants
open Fx.Grants.Tests.Support

let private context =
    {
        Session = Some "s1"
        Run = Some "run1"
        Repo = Some { Repo = "r1"; Path = "src/a" }
    }

let private gitStatus = ask "call-1" shellExec "git status" context

/// A state with one pending request `q1` for `git status`.
let private requested () =
    let request = requestOf gitStatus "q1" t0

    let events, state =
        decideOk GrantLog.initial (command alice t0 (GrantOp.Request request))

    Expect.equal events [ GrantEvent.Requested request ] "requested"
    request, state

let private answer effect scope : GrantAnswer =
    {
        Effect = effect
        Scope = scope
        Resource = None
        Expires = None
        Reason = Some "fine"
    }

let private answerOp (answer: GrantAnswer) =
    GrantOp.Answer(RequestId "q1", answer, GrantId "g1")

let private later = t0 + 5L * second

[<Tests>]
let tests =
    testList
        "Grant log"
        [
            test "a request offers the scopes its action and context allow, once first" {
                let request = requestOf gitStatus "q1" t0

                Expect.equal
                    request.Offer
                    [ ScopeKind.Once; ScopeKind.Session; ScopeKind.Repo; ScopeKind.Host ]
                    "everything"

                let bare = requestOf (ask "k" shellExec "ls" GrantContext.none) "q2" t0
                Expect.equal bare.Offer [ ScopeKind.Once; ScopeKind.Host ] "no session, no repository"

                let budget =
                    requestOf (ask "k" (ActionName.create "budget.raise") "run1/usd" context) "q3" t0

                Expect.equal budget.Offer [ ScopeKind.Once ] "budget raises are once only"

                let narrowed =
                    requestOf
                        { gitStatus with
                            Scopes = [ ScopeKind.Session ]
                        }
                        "q4"
                        t0

                Expect.equal narrowed.Offer [ ScopeKind.Once; ScopeKind.Session ] "the requester narrows"
            }

            test "requests are validated" {
                let invalid (a: GrantAsk) =
                    match GrantLog.request actions a (RequestId "q") t0 with
                    | Ok _ -> failtest "accepted"
                    | Error e -> e.Code

                Expect.equal
                    (invalid
                        { gitStatus with
                            Action = ActionName.create "x.y"
                        })
                    "grants.unknown-action"
                    ""

                Expect.equal
                    (invalid
                        { gitStatus with
                            Resource = "git  status"
                        })
                    "grants.invalid-resource"
                    ""

                Expect.equal (invalid { gitStatus with Summary = "" }) "grants.invalid-request" "summary"

                Expect.equal
                    (invalid
                        { gitStatus with
                            Suggestions = [ "npm *" ]
                        })
                    "grants.invalid-request"
                    "a suggestion that does not match"

                Expect.equal
                    (invalid
                        { gitStatus with
                            Expires = Some(t0 - 1L)
                        })
                    "grants.invalid-request"
                    "expired already"

                Expect.equal
                    (invalid
                        { gitStatus with
                            Context =
                                { context with
                                    Repo = Some { Repo = "r1"; Path = "../x" }
                                }
                        })
                    "grants.invalid-request"
                    "a context path outside the repository"
            }

            test "asking again with the same key is the same request" {
                let _, state = requested ()
                let again = requestOf gitStatus "q2" later
                let events, state = decideOk state (command alice later (GrantOp.Request again))
                Expect.isEmpty events "nothing new"
                Expect.equal state.Keys[gitStatus.Key] (RequestId "q1") "the first request"
            }

            test "answering once allows the request and spends the grant" {
                let request, state = requested ()

                let events, state =
                    decideOk state (command alice later (answerOp (answer GrantEffect.Allow ScopeChoice.Once)))

                match events with
                | [ GrantEvent.Granted g ] ->
                    Expect.equal g.Scope (GrantScope.Once request.Id) "once"
                    Expect.equal g.Resource "git status" "the resource exactly"
                    Expect.equal g.By alice "by the person"
                    Expect.equal g.Origin (GrantOrigin.Answer request.Id) "an answer"
                | other -> failtest (string other)

                Expect.equal (GrantLog.resolution state request.Id) (Some(Resolution.Allowed(GrantId "g1"))) "allowed"
                Expect.equal state.Grants[GrantId "g1"].Status GrantStatus.Spent "spent"
                Expect.equal (severity (Rules.decide actions [] later state request.Query)) 1 "the next time asks again"
            }

            test "answering for the session, repository or host records a grant that applies later" {
                for scope, inScope, outOfScope in
                    [
                        ScopeChoice.Session, context, { context with Session = Some "s2" }
                        ScopeChoice.Repo(Some "src"),
                        { context with
                            Session = None
                            Repo = Some { Repo = "r1"; Path = "src/b" }
                        },
                        { context with
                            Session = None
                            Repo = Some { Repo = "r1"; Path = "docs" }
                        }
                        ScopeChoice.Host, GrantContext.none, GrantContext.none
                    ] do
                    let request, state = requested ()

                    let _, state =
                        decideOk state (command alice later (answerOp (answer GrantEffect.Allow scope)))

                    let q c = { request.Query with Context = c }

                    Expect.equal
                        (Rules.decide actions [] later state (q inScope) |> Rules.toCheck)
                        (GrantCheck.Allowed(GrantId "g1"))
                        (string scope + " in scope")

                    if scope <> ScopeChoice.Host then
                        Expect.equal
                            (severity (Rules.decide actions [] later state (q outOfScope)))
                            1
                            (string scope + " out of scope")
            }

            test "a broader pattern in an answer must match the request" {
                let _, state = requested ()

                let broad =
                    { answer GrantEffect.Allow ScopeChoice.Host with
                        Resource = Some "git *"
                    }

                let events, _ = decideOk state (command alice later (answerOp broad))

                match events with
                | [ GrantEvent.Granted g ] -> Expect.equal g.Resource "git *" "the pattern"
                | other -> failtest (string other)

                let wrong = { broad with Resource = Some "npm *" }

                Expect.equal (decideError state (command alice later (answerOp wrong))).Code "grants.invalid-answer" ""
            }

            test "answers are checked against the request" {
                let _, state = requested ()

                let refuse a =
                    (decideError state (command alice later (answerOp a))).Code

                let budget =
                    requestOf (ask "b" (ActionName.create "budget.raise") "run1/usd" context) "q1" t0

                let _, budgetState =
                    decideOk GrantLog.initial (command alice t0 (GrantOp.Request budget))

                Expect.equal
                    (decideError
                        budgetState
                        (command alice later (answerOp (answer GrantEffect.Allow ScopeChoice.Host))))
                        .Code
                    "grants.invalid-answer"
                    "a scope the request does not offer"

                Expect.equal
                    (refuse (answer GrantEffect.Allow (ScopeChoice.Repo(Some "docs"))))
                    "grants.invalid-answer"
                    "a path that does not cover the request"

                Expect.equal
                    (refuse
                        { answer GrantEffect.Allow ScopeChoice.Host with
                            Expires = Some t0
                        })
                    "grants.invalid-answer"
                    "expired already"

                Expect.equal
                    (decideError state (command "system" later (answerOp (answer GrantEffect.Allow ScopeChoice.Once))))
                        .Code
                    "grants.not-a-person"
                    "the host itself"

                Expect.equal
                    (decideError
                        state
                        (command
                            alice
                            later
                            (GrantOp.Answer(RequestId "nope", answer GrantEffect.Allow ScopeChoice.Once, GrantId "g1"))))
                        .Code
                    "grants.unknown-request"
                    "no such request"
            }

            test "a request is answered once" {
                let _, state = requested ()

                let _, state =
                    decideOk state (command alice later (answerOp (answer GrantEffect.Allow ScopeChoice.Once)))

                let again =
                    GrantOp.Answer(RequestId "q1", answer GrantEffect.Deny ScopeChoice.Once, GrantId "g2")

                Expect.equal (decideError state (command alice later again)).Code "grants.not-pending" ""
            }

            test "deny once rejects the request; deny for a scope also forbids later" {
                let request, state = requested ()

                let events, denied =
                    decideOk state (command alice later (answerOp (answer GrantEffect.Deny ScopeChoice.Once)))

                match events with
                | [ GrantEvent.Denied d ] -> Expect.equal d.By alice "by the person"
                | other -> failtest (string other)

                Expect.equal
                    (GrantLog.resolution denied request.Id)
                    (Some(Resolution.Denied [ "denied by user:alice: fine" ]))
                    "denied"

                Expect.equal (severity (Rules.decide actions [] later denied request.Query)) 1 "asks again later"

                let _, remembered =
                    decideOk state (command alice later (answerOp (answer GrantEffect.Deny ScopeChoice.Session)))

                Expect.equal
                    (GrantLog.resolution remembered request.Id)
                    (Some(Resolution.Denied [ "denied by user:alice: fine" ]))
                    "denied"

                Expect.equal (severity (Rules.decide actions [] later remembered request.Query)) 2 "denied later"
            }

            test "direct grants are made by a person, not for once" {
                let grant =
                    {
                        Id = GrantId "g9"
                        Action = netConnect
                        Resource = "*.github.com:443"
                        Effect = GrantEffect.Allow
                        Scope = GrantScope.Host
                        By = alice
                        At = t0
                        Expires = None
                        Reason = None
                        Origin = GrantOrigin.Direct "tui"
                    }

                let events, _ = decideOk GrantLog.initial (command alice t0 (GrantOp.Grant grant))
                Expect.equal events [ GrantEvent.Granted grant ] "granted"

                let refuse g by =
                    (decideError GrantLog.initial (command by t0 (GrantOp.Grant g))).Code

                Expect.equal (refuse grant "system") "grants.not-a-person" "the host"
                Expect.equal (refuse { grant with By = "user:bob" } alice) "grants.invalid-grant" "someone else's"

                Expect.equal
                    (refuse
                        { grant with
                            Scope = GrantScope.Once(RequestId "q")
                        }
                        alice)
                    "grants.invalid-grant"
                    "once"

                Expect.equal (refuse { grant with Resource = "a**" } alice) "grants.invalid-pattern" "a bad pattern"

                Expect.equal
                    (refuse
                        { grant with
                            Action = ActionName.create "x.y"
                        }
                        alice)
                    "grants.unknown-action"
                    "unknown action"
            }

            test "revoking stops a grant; only people revoke" {
                let request, state = requested ()

                let _, state =
                    decideOk state (command alice later (answerOp (answer GrantEffect.Allow ScopeChoice.Host)))

                Expect.equal
                    (decideError state (command "system" later (GrantOp.Revoke(GrantId "g1", None)))).Code
                    "grants.not-a-person"
                    "the host"

                let _, state =
                    decideOk state (command "user:bob" later (GrantOp.Revoke(GrantId "g1", Some "too broad")))

                Expect.equal (severity (Rules.decide actions [] later state request.Query)) 1 "asks again"

                Expect.equal
                    (decideError state (command alice later (GrantOp.Revoke(GrantId "g1", None)))).Code
                    "grants.not-active"
                    "revoked already"
            }

            test "expiry: a timer ends a grant when its time has come" {
                let _, state = requested ()

                let expiring =
                    { answer GrantEffect.Allow ScopeChoice.Host with
                        Expires = Some(later + 60L * second)
                    }

                let events, state = decideOk state (command alice later (answerOp expiring))

                Expect.equal
                    (GrantLog.react state events.Head)
                    [ GrantIntent.ExpireAt(GrantId "g1", later + 60L * second) ]
                    "a timer"

                Expect.equal
                    (GrantLog.recover state)
                    [ GrantIntent.ExpireAt(GrantId "g1", later + 60L * second) ]
                    "recovered"

                Expect.equal
                    (decideError state (command "system" later (GrantOp.Expire(GrantId "g1")))).Code
                    "grants.not-due"
                    "too early"

                let _, state =
                    decideOk state (command "system" (later + 60L * second) (GrantOp.Expire(GrantId "g1")))

                Expect.equal state.Grants[GrantId "g1"].Status (GrantStatus.Expired(later + 60L * second)) "expired"
                Expect.isEmpty (GrantLog.recover state) "no timer"
            }

            test "requests time out, are withdrawn, covered or refused" {
                let timed = requestOf { gitStatus with Expires = Some later } "q1" t0

                let events, state =
                    decideOk GrantLog.initial (command alice t0 (GrantOp.Request timed))

                Expect.equal
                    (GrantLog.react state events.Head)
                    [ GrantIntent.TimeOutAt(RequestId "q1", later) ]
                    "a timer"

                Expect.equal
                    (decideError state (command "system" t0 (GrantOp.Close(RequestId "q1", CloseReason.TimedOut)))).Code
                    "grants.not-due"
                    "too early"

                let _, closed =
                    decideOk state (command "system" later (GrantOp.Close(RequestId "q1", CloseReason.TimedOut)))

                Expect.equal
                    (GrantLog.resolution closed (RequestId "q1"))
                    (Some(Resolution.Cancelled "timed out"))
                    "cancelled"

                Expect.isEmpty (GrantLog.pending closed) "not pending"

                let _, withdrawn =
                    decideOk
                        state
                        (command alice t0 (GrantOp.Close(RequestId "q1", CloseReason.Withdrawn "turn interrupted")))

                Expect.equal
                    (GrantLog.resolution withdrawn (RequestId "q1"))
                    (Some(Resolution.Cancelled "withdrawn: turn interrupted"))
                    "withdrawn"

                Expect.equal
                    (decideError
                        state
                        (command alice t0 (GrantOp.Close(RequestId "q1", CloseReason.Covered(GrantId "g7")))))
                        .Code
                    "grants.unknown-grant"
                    "covered by a grant that does not exist"

                let _, refused =
                    decideOk state (command alice t0 (GrantOp.Close(RequestId "q1", CloseReason.Refused [ "policy" ])))

                Expect.equal
                    (GrantLog.resolution refused (RequestId "q1"))
                    (Some(Resolution.Denied [ "policy" ]))
                    "refused"
            }

            test "ending a session expires its grants and closes its requests" {
                let request, state = requested ()

                let _, state =
                    decideOk state (command alice later (answerOp (answer GrantEffect.Allow ScopeChoice.Session)))

                let other = requestOf (ask "call-2" fsWrite "a.fs" context) "q2" later
                let _, state = decideOk state (command alice later (GrantOp.Request other))

                let events, state =
                    decideOk state (command "system" later (GrantOp.EndSession "s1"))

                Expect.equal
                    events
                    [
                        GrantEvent.Expired { Grant = GrantId "g1"; At = later }
                        GrantEvent.Closed
                            {
                                Request = RequestId "q2"
                                At = later
                                Why = CloseReason.SessionEnded
                            }
                    ]
                    "events"

                Expect.equal (severity (Rules.decide actions [] later state request.Query)) 1 "asks again"

                Expect.isEmpty
                    (snd (decideOk state (command "system" later (GrantOp.EndSession "s1")))
                     |> GrantLog.pending)
                    "none"
            }

            test "evolve is total: events about unknown grants and requests change only the sequence number" {
                let state =
                    GrantLog.replay
                        [
                            GrantEvent.Revoked
                                {
                                    Grant = GrantId "nope"
                                    By = alice
                                    At = t0
                                    Reason = None
                                }
                            GrantEvent.Expired { Grant = GrantId "nope"; At = t0 }
                            GrantEvent.Denied
                                {
                                    Request = RequestId "nope"
                                    By = alice
                                    At = t0
                                    Reason = None
                                }
                            GrantEvent.Closed
                                {
                                    Request = RequestId "nope"
                                    At = t0
                                    Why = CloseReason.TimedOut
                                }
                        ]

                Expect.equal state { GrantLog.initial with Seq = 4L } "unchanged"
            }

            testPropertyWithConfig config "replay: the state after any commands is the fold of their events"
            <| Prop.forAll (Arb.fromGen Scripts.script) (fun commands ->
                let state, events, _ = run GrantLog.initial commands
                state = GrantLog.replay events && state.Seq = int64 events.Length)

            testPropertyWithConfig config "replay: a log replayed in two parts gives the same state"
            <| Prop.forAll (Arb.fromGen (Gen.zip Scripts.script (Gen.choose (0, 100)))) (fun (commands, cut) ->
                let _, events, _ = run GrantLog.initial commands
                let cut = min cut events.Length
                let first = GrantLog.replay (List.take cut events)
                List.fold GrantLog.evolve first (List.skip cut events) = GrantLog.replay events)

            testPropertyWithConfig config "only people make grants; every grant answers a pending request or is direct"
            <| Prop.forAll (Arb.fromGen Scripts.script) (fun commands ->
                let _, events, _ = run GrantLog.initial commands

                events
                |> List.forall (fun event ->
                    match event with
                    | GrantEvent.Granted g -> GrantLog.isPerson g.By
                    | GrantEvent.Denied d -> GrantLog.isPerson d.By
                    | GrantEvent.Revoked r -> GrantLog.isPerson r.By
                    | _ -> true))

            testPropertyWithConfig config "a request is resolved at most once"
            <| Prop.forAll (Arb.fromGen Scripts.script) (fun commands ->
                let _, events, _ = run GrantLog.initial commands

                let resolutions =
                    events
                    |> List.choose (fun event ->
                        match event with
                        | GrantEvent.Granted { Origin = GrantOrigin.Answer r } -> Some r
                        | GrantEvent.Denied d -> Some d.Request
                        | GrantEvent.Closed c -> Some c.Request
                        | _ -> None)

                List.distinct resolutions = resolutions)
        ]
