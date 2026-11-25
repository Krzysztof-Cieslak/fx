/// Sessions run by a host, as clients and turn runners use them: one scenario per guarantee.
module Fx.Sessions.Host.Tests.HostTests

open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Variants
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Sessions.Host.Fakes
open Fx.Sessions.Host.Tests.Support

let private done' (summary: string) =
    TurnStep.Finish(TurnResult.Done(summary, [], None))

[<Tests>]
let tests =
    testList
        "Session host"
        [
            test
                "a conversation: a steer reaches the running turn at its next step, a queued prompt starts the next turn" {
                let f =
                    create
                        [
                            1,
                            [
                                TurnStep.Think(seconds 2.0)
                                TurnStep.Boundary
                                TurnStep.Think(seconds 1.0)
                                done' "one"
                            ]
                            2, [ done' "two" ]
                        ]

                Expect.equal (start f "s1") (accepted 0L 1) "started"
                Expect.equal (steer f "s1" "p1" alice "hello") (accepted 1L 2) "turn 1 starts"
                Expect.equal (steer f "s1" "p2" bob "also this") (accepted 3L 1) "steered"
                Expect.equal (queue f "s1" "p3" alice "then this") (accepted 4L 1) "queued"
                f.Clock.Advance(seconds 2.0)
                f.Clock.Advance(seconds 1.0)

                Expect.equal
                    (lines f "s1")
                    [
                        "#1 started s1 under sha256:base by user:alice"
                        "#2 steered #2 user:alice: \"hello\""
                        "#3 turn 1 started with #2"
                        "#4 steered #4 user:bob: \"also this\""
                        "#5 queued #5 user:alice: \"then this\""
                        "#6 turn 1 step 1 took #4"
                        "#7 turn 1 ended: done (0 evidence): one"
                        "#8 turn 2 started with #5"
                        "#9 turn 2 ended: done (0 evidence): two"
                    ]
                    "the log"

                Expect.equal
                    f.Runner.Transcript
                    [
                        "turn 1 started with [#2 user:alice: \"hello\"]"
                        "turn 1 step 1 took [#4 user:bob: \"also this\"]"
                        "turn 1 finished: done (0 evidence): one"
                        "turn 2 started with [#5 user:alice: \"then this\"]"
                        "turn 2 finished: done (0 evidence): two"
                    ]
                    "what the turns saw"

                Expect.equal
                    (events f "s1" |> List.map (fun recorded -> string recorded.Meta.By))
                    [
                        "user:alice via tui"
                        "user:alice via tui"
                        "user:alice via tui"
                        "user:bob via web"
                        "user:alice via tui"
                        "system via turn"
                        "system via interpreter"
                        "system via interpreter"
                        "system via interpreter"
                    ]
                    "every event is attributed to the sender of its command"

                Expect.equal
                    (events f "s1" |> List.map (fun recorded -> recorded.Meta.Key.Value))
                    [
                        "s1/start"
                        "p1"
                        "p1"
                        "p2"
                        "p3"
                        "session/s1#3/step/1"
                        "session/s1#3/0.0"
                        "session/s1#3/0.0"
                        "session/s1#8/0.0"
                    ]
                    "keys of the host's commands derive from the events that caused them"

                runIn f.Env (SessionHost.stop f.Host)
            }

            test "inputs from concurrent clients are serialized and attributed, each client's in its order" {
                let f = create []
                start f "s1" |> ignore

                let client (id: int) =
                    Task.Run(fun () ->
                        let by = Attribution.create $"user:{id}" "test"

                        for n in 1..25 do
                            let outcome =
                                (Eff.run
                                    f.Env
                                    (SessionHost.send
                                        f.Host
                                        "s1"
                                        (IdempotencyKey $"c{id}-{n}")
                                        by
                                        (Command.Prompt($"{id}.{n}", PromptMode.Queue))))
                                    .GetAwaiter()
                                    .GetResult()

                            Expect.isTrue (isAccepted outcome) (string outcome))

                Task.WaitAll [| for id in 1..4 -> client id |]
                let final = state f "s1"
                Expect.equal final.Turns 100 "every prompt ran a turn of its own"
                Expect.isNone final.Running "and the session is idle"

                let queued =
                    events f "s1"
                    |> List.choose (fun recorded ->
                        match recorded.Event with
                        | Event.PromptQueued input -> Some(recorded, input)
                        | _ -> None)

                Expect.equal queued.Length 100 "each prompt once"

                for recorded, input in queued do
                    Expect.equal input.By recorded.Meta.By "the input carries its sender"

                for id in 1..4 do
                    Expect.equal
                        (queued
                         |> List.filter (fun (_, input) -> input.By.Principal = $"user:{id}")
                         |> List.map (fun (_, input) -> string input.Content))
                        [ for n in 1..25 -> $"\"{id}.{n}\"" ]
                        "in the order the client sent them"

                Expect.equal
                    (Machine.replay (Session.machine RequestKinds.builtIn) (events f "s1" |> List.map _.Event))
                    final
                    "the state is the fold of the log"

                runIn f.Env (SessionHost.stop f.Host)
            }

            test "a command sent again gets its first outcome: in memory, after a restart, and from before a snapshot" {
                let f =
                    createWith
                        { SessionSettings.defaults with
                            SnapshotEvery = 3L
                        }
                        []

                start f "s1" |> ignore
                let first = steer f "s1" "p1" alice "one"
                Expect.equal first (accepted 1L 2) "accepted"
                Expect.equal (steer f "s1" "p1" alice "one") first "the first outcome"
                Expect.equal (lines f "s1").Length 4 "turn 1 ran once"
                Expect.equal f.Snapshots.Count 1 "a snapshot after 3 events"

                let f = restart f
                // The new actor started from the snapshot: it never read the commits of these keys.
                Expect.equal (steer f "s1" "p1" alice "one") first "after the restart"

                Expect.equal (start f "s1") (accepted 0L 1) "a start sent again is not refused: it took effect"

                Expect.equal (lines f "s1").Length 4 "nothing appended"
                Expect.equal (steer f "s1" "p2" alice "two") (accepted 4L 2) "a new command is handled"
                runIn f.Env (SessionHost.stop f.Host)
            }

            test "an interrupt stops the running turn at once, holds the session, and keeps the request timers" {
                let f = create [ 1, [ TurnStep.Think(seconds 60.0); done' "late" ] ]
                start f "s1" |> ignore

                send
                    f
                    "s1"
                    "g1"
                    system
                    (Command.OpenRequest(
                        confirm "g1" RequestOrigin.Session Reply.ToOpener Lifetime.Session
                        |> expiring (seconds 30.0) ExpiryPolicy.Cancel
                    ))
                |> ignore

                steer f "s1" "p1" alice "work" |> ignore
                queue f "s1" "p2" alice "later" |> ignore
                Expect.equal f.Clock.PendingDelays 2 "the timer and the turn"

                let interrupted =
                    runIn f.Env (SessionHost.interrupt f.Host "s1" (IdempotencyKey "i1") bob)

                Expect.equal interrupted (accepted 5L 2) "interrupted"
                Expect.equal f.Clock.PendingDelays 1 "the turn was cancelled; the timer was armed again"

                Expect.equal
                    (linesAfter f "s1" 5L)
                    [ "#6 interrupted turn 1 by user:bob"; "#7 turn 1 ended: interrupted" ]
                    "requested and applied"

                Expect.equal (f.Runner.Transcript |> List.last) "turn 1 cancelled" "the runner saw it"
                Expect.isTrue (state f "s1").Held "held"

                Expect.equal
                    (runIn f.Env (SessionHost.interrupt f.Host "s1" (IdempotencyKey "i1") bob))
                    interrupted
                    "sent again, it is the same interrupt"

                f.Clock.Advance(seconds 60.0)

                Expect.equal
                    (linesAfter f "s1" 7L)
                    [ "#8 resolved g1 expired by system" ]
                    "the timer fired; nothing else started"

                steer f "s1" "p3" alice "go" |> ignore

                Expect.equal
                    (linesAfter f "s1" 8L)
                    [
                        "#9 steered #9 user:alice: \"go\""
                        "#10 turn 2 started with #9"
                        "#11 turn 2 ended: done (0 evidence): done"
                        "#12 turn 3 started with #5"
                        "#13 turn 3 ended: done (0 evidence): done"
                    ]
                    "a prompt starts the session again, and the queue follows"

                runIn f.Env (SessionHost.stop f.Host)
            }

            test "a request is offered to capable clients and answered once; the turn waiting for it goes on" {
                let f =
                    create
                        [
                            1,
                            [
                                TurnStep.Ask(confirm "g1" RequestOrigin.Session Reply.ToOpener Lifetime.Turn)
                                done' "asked"
                            ]
                        ]

                start f "s1" |> ignore
                steer f "s1" "p1" alice "do it" |> ignore

                Expect.equal (lines f "s1" |> List.last) "#4 opened g1 (confirm) \"confirm g1\"" "the turn asked"

                let offered = runIn f.Env (SessionHost.offers f.Host "s1" [ "confirm"; "choice" ])
                Expect.equal (offered |> List.map _.Request.Id) [ "g1" ] "offered to a client that can answer it"

                Expect.equal (offered |> List.map _.Request.Origin) [ RequestOrigin.Turn 1 ] "the turn is its origin"

                Expect.isEmpty (runIn f.Env (SessionHost.offers f.Host "s1" [ "choice" ])) "not to others"
                let answered = send f "s1" "a1" alice (Command.Answer("g1", yes))
                Expect.equal answered (accepted 4L 1) "answered"

                Expect.equal
                    (rejectedWith (send f "s1" "a2" bob (Command.Answer("g1", Json.Bool false))))
                    Rejections.NotOpen
                    "once"

                Expect.equal
                    (send f "s1" "a1" alice (Command.Answer("g1", yes)))
                    answered
                    "sent again: the first outcome"

                Expect.equal
                    f.Runner.Transcript
                    [
                        "turn 1 started with [#2 user:alice: \"do it\"]"
                        "turn 1 asked g1: answered true"
                        "turn 1 finished: done (0 evidence): asked"
                    ]
                    "the turn got the answer"

                Expect.equal
                    (linesAfter f "s1" 4L)
                    [
                        "#5 resolved g1 answered true by user:alice"
                        "#6 turn 1 ended: done (0 evidence): asked"
                    ]
                    "the log"

                runIn f.Env (SessionHost.stop f.Host)
            }

            test "a decision a turn posts expires with its default answer, which starts the next turn" {
                let f =
                    create
                        [
                            1,
                            [
                                TurnStep.Post(
                                    choice "d1" RequestOrigin.Session Reply.AsInput Lifetime.Session
                                    |> expiring (seconds 30.0) (ExpiryPolicy.Answer(pick "a"))
                                )
                                TurnStep.Finish(TurnResult.NeedInput [ "d1" ])
                            ]
                            2, [ TurnStep.Boundary; done' "continued" ]
                        ]

                start f "s1" |> ignore
                steer f "s1" "p1" alice "decide" |> ignore
                queue f "s1" "p2" bob "later" |> ignore
                Expect.equal (state f "s1").AwaitingRequests [ "d1" ] "waiting for the decision"
                f.Clock.Advance(seconds 30.0)

                Expect.equal
                    (linesAfter f "s1" 3L)
                    [
                        "#4 opened d1 (choice) \"choose d1\""
                        "#5 turn 1 ended: need input d1"
                        "#6 queued #6 user:bob: \"later\""
                        "#7 resolved d1 expired, defaulted to \"a\" by system"
                        "#8 turn 2 started with #7"
                        "#9 turn 2 ended: done (0 evidence): continued"
                        "#10 turn 3 started with #6"
                        "#11 turn 3 ended: done (0 evidence): done"
                    ]
                    "the answer, then the queue"

                Expect.contains
                    f.Runner.Transcript
                    "turn 2 started with [#7 system: d1 expired, defaulted to \"a\"]"
                    "turn 2 got the default answer"

                runIn f.Env (SessionHost.stop f.Host)
            }

            test "after a restart mid-turn the turn is abandoned, timers run again, and nothing auto-resumes" {
                let f = create [ 1, [ TurnStep.Think(seconds 60.0); done' "never" ] ]
                start f "s1" |> ignore

                send
                    f
                    "s1"
                    "g1"
                    system
                    (Command.OpenRequest(
                        confirm "g1" RequestOrigin.Session Reply.ToOpener Lifetime.Session
                        |> expiring (seconds 30.0) ExpiryPolicy.Cancel
                    ))
                |> ignore

                steer f "s1" "p1" alice "work" |> ignore
                queue f "s1" "p2" alice "later" |> ignore
                let f = restart f
                Expect.equal (f.Runner.Transcript |> List.last) "turn 1 cancelled" "stopping cancelled the turn"
                Expect.equal f.Clock.PendingDelays 0 "nothing waits"
                let recovered = state f "s1"
                Expect.isTrue recovered.Held "held"
                Expect.isNone recovered.Running "no turn"
                Expect.equal f.Clock.PendingDelays 1 "the timer runs again"
                f.Clock.Advance(seconds 30.0)

                Expect.equal
                    (linesAfter f "s1" 5L)
                    [
                        "#6 turn 1 ended: abandoned: the host stopped during the turn"
                        "#7 resolved g1 expired by system"
                    ]
                    "abandoned, and the request expired on time"

                Expect.equal (state f "s1").Queue.Length 1 "the queued prompt waits for someone"
                runIn f.Env (SessionHost.stop f.Host)
            }

            test "a fork starts a child session whose history is its parent's up to an event" {
                let f = create []
                start f "s1" |> ignore
                steer f "s1" "p1" alice "one" |> ignore
                Expect.equal (send f "s1" "f1" alice (Command.Fork("c1", None))) (accepted 4L 1) "forked"

                Expect.equal
                    (linesAfter f "s1" 4L)
                    [ "#5 forked c1 at #4 by user:alice"; "#6 fork c1 ready" ]
                    "the parent knows its child"

                let child = events f "c1"

                Expect.equal
                    (child |> List.map (fun recorded -> string recorded.Event))
                    [ "started c1 under sha256:base forked from session/s1#4 by system" ]
                    "the child's first event refers to the parent"

                Expect.equal
                    (child |> List.map (fun recorded -> recorded.Meta.Cause, recorded.Meta.Key.Value))
                    [ Some(EventRef.create (Session.stream "s1") 5L), "session/s1#5/fork" ]
                    "caused by the fork, keyed by it"

                Expect.equal (state f "c1").Parent (Some(EventRef.create (Session.stream "s1") 4L)) "parent"
                Expect.equal (steer f "c1" "c1/p1" bob "two") (accepted 1L 2) "the child takes input"

                Expect.equal
                    (rejectedWith (send f "s1" "f2" alice (Command.Fork("c1", None))))
                    Rejections.ForkExists
                    "once"

                runIn f.Env (SessionHost.stop f.Host)
            }

            test "subscribe replays, then follows live; a subscriber far behind starts from a snapshot" {
                let f =
                    createWith
                        { SessionSettings.defaults with
                            SnapshotEvery = 4L
                            MaxGap = 3L
                        }
                        []

                start f "s1" |> ignore
                steer f "s1" "p1" alice "one" |> ignore
                steer f "s1" "p2" bob "two" |> ignore
                queue f "s1" "p3" alice "three" |> ignore
                Expect.equal (state f "s1").Seq 10L "ten events"

                // A client that has the state at `after` (from an earlier session) and follows from there.
                let follow (after: int64) =
                    let received = ResizeArray<Feed<SessionState, Event>>()

                    let folded =
                        ref (
                            events f "s1"
                            |> List.truncate (int after)
                            |> List.fold (fun state recorded -> Session.evolve state recorded.Event) Session.initial
                        )

                    let task =
                        Eff.run
                            f.Env
                            (SessionHost.subscribe f.Host "s1" after (fun item ->
                                eff {
                                    // What the host server would send, and what the client reads.
                                    let sent = Codec.encodeText SessionLog.feed item

                                    match Codec.decodeText SessionLog.feed sent with
                                    | Error issues -> return failtest (Issue.render issues)
                                    | Ok read ->
                                        received.Add read

                                        match read with
                                        | Feed.Reset snapshot -> folded.Value <- snapshot.State
                                        | Feed.Next recorded ->
                                            folded.Value <- Session.evolve folded.Value recorded.Event

                                        return
                                            match read with
                                            | Feed.Next { Event = Event.Closed _ } -> false
                                            | _ -> true
                                }))

                    received, folded, task

                let farReceived, farState, far = follow 0L
                let nearReceived, nearState, near = follow 8L
                Expect.isFalse far.IsCompleted "following"
                send f "s1" "c" bob Command.Close |> ignore
                Expect.equal (completed far) 11L "to the end"
                Expect.equal (completed near) 11L "to the end"

                Expect.equal
                    (farReceived |> Seq.map string |> List.ofSeq)
                    [ "reset to 10"; "next session/s1#11" ]
                    "the snapshot, then the rest"

                Expect.equal
                    (nearReceived |> Seq.map string |> List.ofSeq)
                    [ "next session/s1#9"; "next session/s1#10"; "next session/s1#11" ]
                    "the events after its position"

                Expect.equal farState.Value (state f "s1") "the far client's view is the session's state"
                Expect.equal nearState.Value (state f "s1") "so is the near client's"
                runIn f.Env (SessionHost.stop f.Host)
            }

            test "overrides resolve a new profile, stored before the session binds it" {
                let f = create []
                let registry = SlotRegistry.ofSlots Slots.all
                let binder = ProfileBinder.ofVariants registry [] [] (Profile.ofResolution "test")

                let bound =
                    match runIn f.Env (binder.Bind []) with
                    | Ok bound -> bound
                    | Error issues -> failtest (String.concat "\n" issues)

                runIn f.Env (SessionHost.start f.Host "s1" (IdempotencyKey "start") alice bound.Id None)
                |> ignore

                let set key changes =
                    runIn f.Env (SessionHost.setOverrides f.Host binder "s1" (IdempotencyKey key) alice changes)

                Expect.equal (set "o1" [ "edit.guard", Some(Json.String "require") ]) (Ok(accepted 1L 1)) "selected"
                let selected = state f "s1"
                Expect.equal selected.Overrides [ "edit.guard", Json.String "require" ] "the override"
                Expect.notEqual selected.Profile bound.Id "a new profile"

                Expect.equal
                    (lines f "s1" |> List.last)
                    ("#2 profile " + selected.Profile + " (edit.guard) by user:alice")
                    "the slots that changed"

                let stored =
                    match BlobHash.tryParse selected.Profile with
                    | None -> failtest "a profile id is a blob hash"
                    | Some hash ->
                        match completed (f.Blobs.Blobs.Get hash System.Threading.CancellationToken.None) with
                        | Error error -> failtest (string error)
                        | Ok bytes -> Profile.ofBytes (ProfileId.tryParse selected.Profile).Value bytes

                Expect.equal
                    (stored
                     |> Result.map (fun profile -> profile.Selections[SlotId.create "edit.guard"]))
                    (Ok(Json.String "require"))
                    "anyone with the blob store can load it"

                match set "o2" [ "edit.gaurd", Some(Json.String "require") ] with
                | Error issues -> Expect.isNonEmpty issues "why"
                | Ok outcome -> failtest (string outcome)

                Expect.equal (state f "s1").Seq 2L "nothing appended"
                Expect.equal (set "o3" [ "edit.guard", None ]) (Ok(accepted 2L 1)) "removed"
                Expect.equal (state f "s1").Profile bound.Id "back to the base profile"

                // Another client changes the profile while this one resolves: it resolves again.
                let calls = ref 0

                let racing: ProfileBinder<Env> =
                    {
                        Bind =
                            fun overrides ->
                                eff {
                                    calls.Value <- calls.Value + 1

                                    if calls.Value = 2 then
                                        let! current = SessionHost.state f.Host "s1"
                                        let theirs = [ "shell.windows", Json.String "powershell" ]
                                        let! other = binder.Bind theirs

                                        let! _ =
                                            SessionHost.send
                                                f.Host
                                                "s1"
                                                (IdempotencyKey "race")
                                                bob
                                                (Command.SelectProfile
                                                    {
                                                        Profile = (Result.toOption other).Value.Id
                                                        Overrides = theirs
                                                        Changed = [ "shell.windows" ]
                                                        BasedOn = current.State.Profile
                                                    })

                                        ()

                                    return! binder.Bind overrides
                                }
                    }

                let raced =
                    runIn
                        f.Env
                        (SessionHost.setOverrides
                            f.Host
                            racing
                            "s1"
                            (IdempotencyKey "o4")
                            alice
                            [ "edit.guard", Some(Json.String "off") ])

                Expect.equal raced (Ok(accepted 4L 1)) "accepted on the second attempt"

                Expect.equal
                    (state f "s1").Overrides
                    [ "edit.guard", Json.String "off"; "shell.windows", Json.String "powershell" ]
                    "both clients' overrides"

                Expect.equal
                    (events f "s1"
                     |> List.map (fun recorded -> recorded.Meta.Key.Value)
                     |> List.skip 3)
                    [ "race"; "o4/2" ]
                    "the retry has a key of its own"

                runIn f.Env (SessionHost.stop f.Host)
            }

            test "a turn runner's failure, expected or a defect, ends the turn and holds the session" {
                let f = create [ 1, [ TurnStep.Throw ]; 2, [ TurnStep.Fail "model down" ] ]
                start f "s1" |> ignore
                steer f "s1" "p1" alice "one" |> ignore

                Expect.equal
                    (lines f "s1" |> List.last)
                    "#4 turn 1 ended: failed: the turn runner failed: ScriptedDefectException"
                    "a defect is reported by its type"

                Expect.isTrue (state f "s1").Held "held"
                queue f "s1" "p2" alice "again" |> ignore

                Expect.equal
                    (linesAfter f "s1" 4L)
                    [
                        "#5 queued #5 user:alice: \"again\""
                        "#6 turn 2 started with #5"
                        "#7 turn 2 ended: failed: model down"
                    ]
                    "a prompt starts it again; an expected failure is reported by its reason"

                Expect.isTrue (state f "s1").Held "held again"

                runIn f.Env (SessionHost.stop f.Host)
            }
        ]
