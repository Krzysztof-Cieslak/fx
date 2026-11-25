/// The session machine's rules, one scenario each, without a host: what each command appends, what
/// it is refused with, and what the session asks its host to do.
module Fx.Sessions.Tests.MachineTests

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Sessions
open Fx.Sessions.Tests.Support

let private turn (n: int) = RequestOrigin.Turn n

let private endTurn (n: int) =
    send system (Command.EndTurn(n, TurnResult.Done("ok", [], None)))

[<Tests>]
let tests =
    testList
        "Session machine"
        [
            test "a session starts once, binding its profile in its first event" {
                Expect.equal (fresh |> steer alice "hi" |> rejected) Rejections.New "nothing before the start"
                Expect.equal (appended started) [ "started s1 under sha256:base by user:alice" ] "started"
                Expect.equal started.State.Status Status.Open "open"
                Expect.equal started.State.Profile "sha256:base" "profile"

                Expect.equal
                    (started
                     |> send
                         bob
                         (Command.Start
                             {
                                 Id = "s1"
                                 Profile = "sha256:other"
                                 Overrides = []
                                 Parent = None
                                 Title = None
                             })
                     |> rejected)
                    Rejections.Started
                    "only once"

                Expect.equal
                    (fresh
                     |> send
                         alice
                         (Command.Start
                             {
                                 Id = "s1"
                                 Profile = " "
                                 Overrides = []
                                 Parent = None
                                 Title = None
                             })
                     |> rejected)
                    Rejections.Invalid
                    "a profile is required"
            }

            test
                "a prompt with no turn running starts one; while it runs, steers wait for a step, queued prompts for the end" {
                let one = started |> steer alice "one"
                Expect.equal (appended one) [ "steered #2 user:alice: \"one\""; "turn 1 started with #2" ] "turn 1"

                Expect.equal
                    one.Intents
                    [
                        Intent.RunTurn
                            {
                                Session = "s1"
                                Turn = 1
                                Profile = "sha256:base"
                                Inputs = one.State.Running.Value.Inputs
                                Seq = 3L
                            }
                    ]
                    "the host runs turn 1 with its input"

                let running = one |> steer bob "two" |> queue alice "three" |> steer bob "four"

                Expect.equal (ids running.State.Inbox) [ 4L; 6L ] "steers wait in the inbox"
                Expect.equal (ids running.State.Queue) [ 5L ] "the queued prompt waits for the end"

                let stepped = running |> send system (Command.TakeInputs(1, 1))

                Expect.equal
                    (appended stepped)
                    [ "turn 1 step 1 took #4 #6" ]
                    "delivered at the step boundary, in order"

                Expect.equal (ids stepped.State.Running.Value.Inputs) [ 2L; 4L; 6L ] "the turn's inputs"
                Expect.isEmpty stepped.State.Inbox "taken"
                Expect.equal (stepped |> send system (Command.TakeInputs(1, 2)) |> appended) [] "nothing waits"

                let ended = stepped |> endTurn 1

                Expect.equal
                    (appended ended)
                    [ "turn 1 ended: done (0 evidence): ok"; "turn 2 started with #5" ]
                    "the queued prompt starts the next turn"
            }

            test "steers a turn did not take go to the next turn, before queued prompts, one queued prompt per turn" {
                let drive =
                    started
                    |> steer alice "one"
                    |> queue alice "q1"
                    |> queue bob "q2"
                    |> steer bob "late"
                    |> endTurn 1

                Expect.equal (appended drive |> List.last) "turn 2 started with #6" "the steer first"
                let drive = drive |> endTurn 2
                Expect.equal (appended drive |> List.last) "turn 3 started with #4" "then the oldest queued prompt"
                let drive = drive |> endTurn 3
                Expect.equal (appended drive |> List.last) "turn 4 started with #5" "then the next"
                let drive = drive |> endTurn 4
                Expect.equal (appended drive) [ "turn 4 ended: done (0 evidence): ok" ] "then idle"
                Expect.isNone drive.State.Running "idle"
            }

            test "an interrupt ends the running turn and holds the session until someone prompts again" {
                let drive =
                    started
                    |> steer alice "one"
                    |> queue alice "later"
                    |> send system (Command.OpenRequest(confirm "g1" (turn 1) Reply.ToOpener Lifetime.Turn))
                    |> send bob Command.Interrupt

                Expect.equal
                    (appended drive)
                    [
                        "interrupted turn 1 by user:bob"
                        "resolved g1 cancelled (turn ended) by user:bob"
                        "turn 1 ended: interrupted"
                    ]
                    "the turn ends at once, and its requests with it"

                Expect.isTrue drive.State.Held "held"
                Expect.equal (ids drive.State.Queue) [ 4L ] "the queued prompt stays"

                let idle = drive |> send bob Command.Interrupt
                Expect.equal (appended idle) [ "interrupted (idle) by user:bob" ] "an idle interrupt is recorded too"

                let resumed = idle |> steer alice "go"

                Expect.equal
                    (appended resumed)
                    [ "steered #10 user:alice: \"go\""; "turn 2 started with #10" ]
                    "a prompt starts a turn"

                Expect.equal
                    (resumed |> endTurn 2 |> appended |> List.last)
                    "turn 3 started with #4"
                    "and the queue goes on"
            }

            test "a request is answered exactly once, by an answer its kind accepts" {
                let drive =
                    started
                    |> steer alice "one"
                    |> send system (Command.OpenRequest(choice "c1" (turn 1) Reply.ToOpener Lifetime.Turn))

                let opened = drive.State.Requests |> List.exactlyOne
                Expect.equal opened.Capabilities [ "choice" ] "answerable by clients that offer the kind"
                Expect.equal opened.Seq 4L "where it was opened"
                Expect.equal (Session.offers [ "confirm" ] drive.State) [] "not offered to others"
                Expect.equal (Session.offers [ "confirm"; "choice" ] drive.State) [ opened ] "offered"

                Expect.equal
                    (drive |> send alice (Command.Answer("c1", pick "c")) |> rejected)
                    Rejections.InvalidAnswer
                    "not an option"

                Expect.equal
                    (drive |> send alice (Command.Answer("c1", yes)) |> rejected)
                    Rejections.InvalidAnswer
                    "not a string"

                let answered = drive |> send alice (Command.Answer("c1", pick "b"))
                Expect.equal (appended answered) [ "resolved c1 answered \"b\" by user:alice" ] "answered"
                Expect.isEmpty answered.State.Inbox "its opener waits for it in the log: no input"

                Expect.equal
                    (answered |> send bob (Command.Answer("c1", pick "a")) |> rejected)
                    Rejections.NotOpen
                    "a second answer is refused"
            }

            test "opening a request checks its kind, payload, origin, lifetime, expiry and id" {
                let drive = started |> steer alice "one"

                let opening request =
                    drive |> send system (Command.OpenRequest request) |> rejected

                Expect.equal
                    (opening
                        { (confirm "x" (turn 1) Reply.ToOpener Lifetime.Turn) with
                            Kind = "nope"
                        })
                    Rejections.UnknownKind
                    "kind"

                Expect.equal
                    (opening
                        { (confirm "x" (turn 1) Reply.ToOpener Lifetime.Turn) with
                            Payload = Json.Object []
                        })
                    Rejections.InvalidPayload
                    "payload"

                Expect.equal
                    (opening (confirm "x" RequestOrigin.Session Reply.ToOpener Lifetime.Turn))
                    Rejections.Invalid
                    "only a turn's requests live as long as the turn"

                Expect.equal (opening (confirm "x" (turn 7) Reply.ToOpener Lifetime.Turn)) Rejections.StaleTurn "turn"

                Expect.equal
                    (opening
                        { (confirm "x" RequestOrigin.Session Reply.AsInput Lifetime.Session) with
                            Expiry =
                                Some
                                    {
                                        At = drive.Now
                                        Then = ExpiryPolicy.Cancel
                                    }
                        })
                    Rejections.Invalid
                    "an expiry in the past"

                Expect.equal
                    (opening
                        { (choice "x" RequestOrigin.Session Reply.AsInput Lifetime.Session) with
                            Expiry =
                                Some
                                    {
                                        At = drive.Now + 60L * second
                                        Then = ExpiryPolicy.Answer(pick "z")
                                    }
                        })
                    Rejections.InvalidAnswer
                    "an expiry's answer must be an answer"

                let opened =
                    drive
                    |> send
                        system
                        (Command.OpenRequest(confirm "x" RequestOrigin.Session Reply.AsInput Lifetime.Session))

                Expect.equal
                    (opened
                     |> send
                         system
                         (Command.OpenRequest(confirm "x" RequestOrigin.Session Reply.AsInput Lifetime.Session))
                     |> rejected)
                    Rejections.RequestExists
                    "ids are unique among open requests"
            }

            test "a request that replies as input reaches a running turn at its next step" {
                let drive =
                    started
                    |> steer alice "one"
                    |> send system (Command.OpenRequest(choice "d1" (turn 1) Reply.AsInput Lifetime.Session))
                    |> send alice (Command.Answer("d1", pick "a"))

                Expect.equal (appended drive) [ "resolved d1 answered \"a\" by user:alice" ] "answered"
                Expect.equal (ids drive.State.Inbox) [ 5L ] "an input, numbered by its event"

                Expect.equal
                    (drive |> send system (Command.TakeInputs(1, 1)) |> appended)
                    [ "turn 1 step 1 took #5" ]
                    "delivered"
            }

            test "a turn that needs input waits for its requests, then continues with their answers" {
                let drive =
                    started
                    |> steer alice "one"
                    |> send system (Command.OpenRequest(choice "d1" (turn 1) Reply.AsInput Lifetime.Session))
                    |> send system (Command.OpenRequest(confirm "d2" (turn 1) Reply.AsInput Lifetime.Session))
                    |> send system (Command.EndTurn(1, TurnResult.NeedInput [ "d1"; "d2"; "gone" ]))

                Expect.equal drive.State.AwaitingRequests [ "d1"; "d2" ] "waits for the open ones"
                let drive = drive |> queue bob "later"
                Expect.isNone drive.State.Running "a queued prompt waits too"
                let drive = drive |> send alice (Command.Answer("d1", pick "b"))
                Expect.isNone drive.State.Running "one answer is not enough"
                let drive = drive |> send bob (Command.Answer("d2", yes))

                Expect.equal
                    (appended drive)
                    [ "resolved d2 answered true by user:bob"; "turn 2 started with #8 #9" ]
                    "the answers start the next turn"

                Expect.equal
                    (drive |> endTurn 2 |> appended |> List.last)
                    "turn 3 started with #7"
                    "then the queued prompt"

                let steered =
                    started
                    |> steer alice "one"
                    |> send system (Command.OpenRequest(choice "d1" (turn 1) Reply.AsInput Lifetime.Session))
                    |> send system (Command.EndTurn(1, TurnResult.NeedInput [ "d1" ]))
                    |> steer bob "never mind"

                Expect.equal (appended steered |> List.last) "turn 2 started with #6" "a steer takes over at once"

                Expect.isEmpty steered.State.AwaitingRequests "no longer waiting"
                Expect.equal (steered.State.Requests |> List.map _.Request.Id) [ "d1" ] "the request stays open"
            }

            test "requests expire on time with their policy's answer, and not before" {
                let drive =
                    started
                    |> send
                        system
                        (Command.OpenRequest
                            { (confirm "g1" RequestOrigin.Session Reply.AsInput Lifetime.Session) with
                                Expiry =
                                    Some
                                        {
                                            At = started.Now + 31L * second
                                            Then = ExpiryPolicy.Answer(Json.Bool false)
                                        }
                            })

                Expect.equal drive.Intents [ Intent.Expire("g1", started.Now + 31L * second) ] "a timer"

                Expect.equal
                    (drive |> send system (Command.ExpireRequest "g1") |> rejected)
                    Rejections.NotExpired
                    "early"

                let late = drive |> wait 30L
                Expect.equal (late |> send alice (Command.Answer("g1", yes)) |> rejected) Rejections.Expired "too late"
                let expired = late |> send system (Command.ExpireRequest "g1")

                Expect.equal
                    (appended expired)
                    [
                        "resolved g1 expired, defaulted to false by system"
                        "turn 1 started with #3"
                    ]
                    "the default answer is an input"

                Expect.equal (expired |> send system (Command.ExpireRequest "g1") |> rejected) Rejections.NotOpen "once"
            }

            test "a turn waiting on jobs continues when they finish" {
                let drive =
                    started
                    |> steer alice "build it"
                    |> send system (Command.EndTurn(1, TurnResult.Waiting([ "j1" ], "building")))

                Expect.equal drive.State.AwaitingJobs [ "j1" ] "waiting"
                let drive = drive |> send system (Command.NotifyJob("j1", "built"))

                Expect.equal
                    (appended drive)
                    [ "job #5 system: job j1 finished: built"; "turn 2 started with #5" ]
                    "the job's notice starts the next turn"
            }

            test "a failed or abandoned turn holds the session" {
                for ending in [ Command.FailTurn(1, "model down"); Command.AbandonTurn(1, "restart") ] do
                    let drive = started |> steer alice "one" |> queue alice "two" |> send system ending
                    Expect.isTrue drive.State.Held "held"
                    Expect.isNone drive.State.Running "nothing started"

                Expect.equal
                    (started
                     |> send system (Command.EndTurn(1, TurnResult.Done("x", [], None)))
                     |> rejected)
                    Rejections.StaleTurn
                    "no such turn"
            }

            test "a prompt is withdrawn while it waits, and only then" {
                let drive = started |> steer alice "one" |> queue bob "two"
                let withdrawn = drive |> send bob (Command.Withdraw 4L)
                Expect.equal (appended withdrawn) [ "withdrawn #4 by user:bob" ] "withdrawn"
                Expect.isEmpty withdrawn.State.Queue "gone"
                Expect.equal (withdrawn |> send bob (Command.Withdraw 4L) |> rejected) Rejections.NotPending "once"

                Expect.equal
                    (drive |> send bob (Command.Withdraw 2L) |> rejected)
                    Rejections.NotPending
                    "not a delivered one"
            }

            test "prompts are checked" {
                Expect.equal (started |> steer alice "  " |> rejected) Rejections.Invalid "empty"

                Expect.equal
                    (started
                     |> steer alice (String.replicate (Session.MaxPromptChars + 1) "x")
                     |> rejected)
                    Rejections.Invalid
                    "too long"

                Expect.equal
                    (started |> steer alice (String.replicate Session.MaxPromptChars "x") |> appended).Length
                    2
                    "at the limit"
            }

            test "closing cancels open requests and drops waiting prompts; a closed session takes no input" {
                let running =
                    started
                    |> steer alice "one"
                    |> send system (Command.OpenRequest(choice "d1" (turn 1) Reply.AsInput Lifetime.Session))

                Expect.equal (running |> send alice Command.Close |> rejected) Rejections.Busy "interrupt first"

                let closed =
                    running
                    |> send system (Command.EndTurn(1, TurnResult.Done("ok", [], None)))
                    |> queue bob "x"

                Expect.isSome closed.State.Running "the queued prompt started turn 2"
                let closed = closed |> endTurn 2 |> send alice Command.Close

                Expect.equal
                    (appended closed)
                    [
                        "resolved d1 cancelled (session closed) by user:alice"
                        "closed by user:alice"
                    ]
                    "closed"

                Expect.isEmpty closed.State.Inbox "the cancellation is not an input"
                Expect.equal (closed |> steer bob "hello" |> rejected) Rejections.Closed "no input"
                Expect.equal (closed |> send bob Command.Close |> appended) [] "closing again changes nothing"
                let reopened = closed |> send bob Command.Reopen
                Expect.equal (appended reopened) [ "reopened by user:bob" ] "reopened"
                Expect.equal (reopened |> steer bob "hello" |> appended).Length 2 "takes input again"

                let archived = reopened |> send alice Command.Archive
                Expect.equal (appended archived) [ "closed by user:alice"; "archived by user:alice" ] "archived"
                Expect.equal archived.State.Status Status.Archived "status"
            }

            test "a fork records the child, and the host starts it from this session's stream" {
                let drive = started |> steer alice "one" |> endTurn 1
                Expect.equal (drive |> send alice (Command.Fork("s1", None)) |> rejected) Rejections.Invalid "own id"
                Expect.equal (drive |> send alice (Command.Fork("c1", Some 99L)) |> rejected) Rejections.Invalid "past"
                let forked = drive |> send alice (Command.Fork("c1", Some 2L))
                Expect.equal (appended forked) [ "forked c1 at #2 by user:alice" ] "forked"

                Expect.equal
                    (forked.Intents |> List.last)
                    (Intent.StartFork
                        {
                            Parent = EventRef.create (Session.stream "s1") 2L
                            Start =
                                {
                                    Id = "c1"
                                    Profile = "sha256:base"
                                    Overrides = []
                                    Parent = Some(EventRef.create (Session.stream "s1") 2L)
                                    Title = Some "test"
                                }
                            Seq = 5L
                        })
                    "start the child"

                Expect.equal (forked |> send alice (Command.Fork("c1", None)) |> rejected) Rejections.ForkExists "once"
                let ready = forked |> send system (Command.ChildStarted "c1")
                Expect.equal (appended ready) [ "fork c1 ready" ] "ready"
                Expect.equal (ready |> send system (Command.ChildStarted "c1") |> appended) [] "idempotent"
                Expect.equal (ready.State.Forks |> List.map _.Status) [ ForkStatus.Ready ] "status"
            }

            test "a profile selection applies from the next turn, and only on the profile it was resolved from" {
                let selection =
                    {
                        Profile = "sha256:new"
                        Overrides = [ "edit.guard", Json.String "require" ]
                        Changed = [ "edit.guard" ]
                        BasedOn = "sha256:base"
                    }

                let running = started |> steer alice "one"

                Expect.equal
                    (running
                     |> send
                         bob
                         (Command.SelectProfile
                             { selection with
                                 BasedOn = "sha256:old"
                             })
                     |> rejected)
                    Rejections.StaleProfile
                    "resolved from another profile"

                let selected = running |> send bob (Command.SelectProfile selection)
                Expect.equal (appended selected) [ "profile sha256:new (edit.guard) by user:bob" ] "selected"
                Expect.equal selected.State.Running.Value.Profile "sha256:base" "the running turn keeps its profile"

                Expect.equal
                    (selected
                     |> send
                         bob
                         (Command.SelectProfile
                             { selection with
                                 BasedOn = "sha256:new"
                             })
                     |> appended)
                    []
                    "the same selection again changes nothing"

                let next = selected |> queue alice "two" |> endTurn 1
                Expect.equal next.State.Running.Value.Profile "sha256:new" "the next turn runs under it"
            }

            test
                "JSON in commands is kept canonical, as the store keeps it: the live state is the fold of the stored log" {
                let loose =
                    Json.Object
                        [
                            "b", (Json.tryNumber "1.50").Value
                            "a", Json.Array [ (Json.tryNumber "2e0").Value ]
                        ]

                let drive =
                    started
                    |> send
                        bob
                        (Command.SelectProfile
                            {
                                Profile = "sha256:new"
                                Overrides = [ "z.slot", loose; "a.slot", Json.Null ]
                                Changed = [ "z.slot" ]
                                BasedOn = "sha256:base"
                            })

                Expect.equal
                    drive.State.Overrides
                    [ "a.slot", Json.Null; "z.slot", Json.canonicalize loose ]
                    "sorted, with canonical values"

                let stored =
                    drive.Events
                    |> List.map (fun event ->
                        match Codec.decodeText Codecs.event (Json.serializeCanonical (Codecs.event.Encode event)) with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.render issues))

                Expect.equal (Machine.replay machine stored) drive.State "the same state"

                Expect.equal
                    (started |> send alice (Command.Withdraw 0L) |> rejected)
                    Rejections.Invalid
                    "what the codec refuses is refused"
            }

            test "an interrupt asks again for the background work it cancelled; recovery abandons the running turn" {
                let expiry =
                    {
                        At = started.Now + 3600L * second
                        Then = ExpiryPolicy.Cancel
                    }

                let drive =
                    started
                    |> send
                        system
                        (Command.OpenRequest
                            { (confirm "g1" RequestOrigin.Session Reply.ToOpener Lifetime.Session) with
                                Expiry = Some expiry
                            })
                    |> send alice (Command.Fork("c1", None))
                    |> steer alice "one"

                let interrupted = drive |> send bob Command.Interrupt
                let after = interrupted.Intents |> List.skip drive.Intents.Length
                Expect.equal after (Session.background interrupted.State) "timers and forks again"
                Expect.equal after.Length 2 "a timer and a fork"

                let running = drive.State.Running.Value

                Expect.equal
                    (Session.recover drive.State)
                    (Intent.RecoverTurn
                        {
                            Session = drive.State.Id
                            Turn = 1
                            Profile = running.Profile
                            Inputs = running.Inputs
                            Seq = running.Seq
                        }
                     :: Session.background drive.State)
                    "recover the turn, restart the rest"
            }
        ]
