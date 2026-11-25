/// What every grant store must do (`GrantStore`, spec §13.2): the event log of one stream, in the SDK's
/// types. A function from a way to make an empty store to a `Test`; the host-local store passes it in
/// memory and on SQLite, and a contributed store (a repository-policy store) runs it the same way.
module Fx.Grants.Host.Tests.GrantStoreConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Sdk
open Fx.Grants

let private none = CancellationToken.None

let private wait (task: Task<'T>) : 'T =
    if not (task.Wait(TimeSpan.FromSeconds 30.0)) then
        failtest "the store did not answer in 30 s"

    task.GetAwaiter().GetResult()

let private meta (key: string) : GrantEventMeta =
    {
        At = 1_800_000_000_000_000L
        Principal = "user:alice"
        Via = "tui"
        Key = key
        Cause = None
    }

let private expired (n: int) : GrantEvent =
    GrantEvent.Expired
        {
            Grant = GrantId("g" + string n)
            At = int64 n
        }

let private revoked (n: int) : GrantEvent =
    GrantEvent.Revoked
        {
            Grant = GrantId("g" + string n)
            By = "user:bob"
            At = int64 n
            Reason = Some "because"
        }

/// The conformance suite, over a factory of empty stores.
let tests (name: string) (make: unit -> GrantStore) : Test =
    let append (store: GrantStore) expected key events =
        wait (store.Append expected (meta key) events none)

    testList
        ("GrantStore conformance: " + name)
        [
            test "an empty store has head 0 and no events" {
                let store = make ()
                Expect.equal (wait (store.Head none)) 0L "head"
                Expect.isEmpty (wait (store.Read 0L 10 none)) "events"
            }

            test "a commit appends its events in order, with its metadata" {
                let store = make ()
                Expect.equal (append store 0L "k1" [ expired 1; revoked 2 ]) (Ok 2L) "head after"

                let withCause =
                    { meta "k2" with
                        Cause = Some "grants/host#2"
                    }

                Expect.equal (wait (store.Append 2L withCause [ expired 3 ] none)) (Ok 3L) "second commit"
                let read = wait (store.Read 0L 10 none)
                Expect.equal (read |> List.map _.Seq) [ 1L; 2L; 3L ] "sequence numbers"
                Expect.equal (read |> List.map _.Event) [ expired 1; revoked 2; expired 3 ] "events"
                Expect.equal read[0].Meta (meta "k1") "metadata"
                Expect.equal read[2].Meta withCause "the cause"
                Expect.equal (wait (store.Read 1L 1 none) |> List.map _.Seq) [ 2L ] "a page"
                Expect.equal (wait (store.Head none)) 3L "head"
            }

            test "a key the store holds is a duplicate, whatever the expected head" {
                let store = make ()
                append store 0L "k1" [ expired 1; expired 2 ] |> ignore

                Expect.equal
                    (append store 2L "k1" [ expired 3 ])
                    (Error(GrantAppendError.Duplicate("k1", 0L, 2)))
                    "same head"

                Expect.equal
                    (append store 0L "k1" [ expired 3 ])
                    (Error(GrantAppendError.Duplicate("k1", 0L, 2)))
                    "stale head"

                Expect.equal (wait (store.Head none)) 2L "nothing appended"
            }

            test "a wrong head is a conflict" {
                let store = make ()
                append store 0L "k1" [ expired 1 ] |> ignore
                Expect.equal (append store 0L "k2" [ expired 2 ]) (Error(GrantAppendError.Conflict(0L, 1L))) "conflict"
            }

            test "waiting past the head completes when the next commit lands" {
                let store = make ()
                append store 0L "k1" [ expired 1 ] |> ignore
                Expect.equal (wait (store.WaitPast 0L none)) 1L "already past"
                let waiting = store.WaitPast 1L none
                append store 1L "k2" [ expired 2 ] |> ignore
                Expect.equal (wait waiting) 2L "woken"
            }

            test "every kind of event round-trips" {
                let store = make ()

                let request =
                    {
                        Id = RequestId "q1"
                        Key = "call-1"
                        Action = ActionName.create "shell.exec"
                        Resource = "git status"
                        Context =
                            {
                                Session = Some "s1"
                                Run = None
                                Repo = Some { Repo = "r1"; Path = "src" }
                            }
                        Requester =
                            {
                                Principal = "user:alice"
                                Agent = Some "build"
                                Extension = None
                            }
                        Summary = "Run git status"
                        Evidence = [ { Label = "why"; Text = "to see" } ]
                        Suggestions = [ "git *" ]
                        Offer = [ ScopeKind.Once; ScopeKind.Host ]
                        At = 1L
                        Expires = Some 99L
                    }

                let grant =
                    {
                        Id = GrantId "g1"
                        Action = request.Action
                        Resource = "git *"
                        Effect = GrantEffect.Allow
                        Scope = GrantScope.Repo { Repo = "r1"; Path = Some "src" }
                        By = "user:alice"
                        At = 2L
                        Expires = None
                        Reason = None
                        Origin = GrantOrigin.Answer request.Id
                    }

                let all =
                    [
                        GrantEvent.Requested request
                        GrantEvent.Granted grant
                        GrantEvent.Denied
                            {
                                Request = RequestId "q2"
                                By = "user:alice"
                                At = 3L
                                Reason = None
                            }
                        revoked 1
                        expired 1
                        GrantEvent.Closed
                            {
                                Request = RequestId "q3"
                                At = 4L
                                Why = CloseReason.Refused [ "policy" ]
                            }
                    ]

                append store 0L "k1" all |> ignore
                Expect.equal (wait (store.Read 0L 10 none) |> List.map _.Event) all "the same events"
            }
        ]
