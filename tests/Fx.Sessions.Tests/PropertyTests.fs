/// Properties of the session machine over random scripts: the state is the fold of the log, the log
/// and the state survive their JSON, and the session's guarantees hold after every command.
module Fx.Sessions.Tests.PropertyTests

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Sessions
open Fx.Sessions.Tests.Support

/// What a script does; numbers pick among what the state offers, so most commands mean something.
type Step =
    | Steering of client: int
    | Queueing of client: int
    | Interrupting of client: int
    | Taking of step: int
    | Ending of result: int
    | Failing
    | Abandoning
    | Opening of kind: bool * asInput: bool * turnLifetime: bool * origin: int * expires: int
    | Answering of request: int * client: int * valid: bool
    | Cancelling of request: int
    | Expiring of request: int
    | Notifying of job: int
    | Selecting of profile: int * stale: bool
    | Forking of at: int
    | ChildReady of fork: int
    | Closing
    | Reopening
    | Archiving
    | Withdrawing of input: int
    | Waiting of seconds: int

    override this.ToString() =
        match this with
        | Steering client -> $"steer {client}"
        | Queueing client -> $"queue {client}"
        | Interrupting client -> $"interrupt {client}"
        | Taking step -> $"take {step}"
        | Ending result -> $"end {result}"
        | Failing -> "fail"
        | Abandoning -> "abandon"
        | Opening(kind, asInput, turnLifetime, origin, expires) ->
            $"open {kind} {asInput} {turnLifetime} {origin} {expires}"
        | Answering(request, client, valid) -> $"answer {request} {client} {valid}"
        | Cancelling request -> $"cancel {request}"
        | Expiring request -> $"expire {request}"
        | Notifying job -> $"job {job}"
        | Selecting(profile, stale) -> $"select {profile} {stale}"
        | Forking at -> $"fork {at}"
        | ChildReady fork -> $"child {fork}"
        | Closing -> "close"
        | Reopening -> "reopen"
        | Archiving -> "archive"
        | Withdrawing input -> $"withdraw {input}"
        | Waiting seconds -> $"wait {seconds}"

let private clients = [| alice; bob; Attribution.create "user:carol" "ide" |]

let private nth (items: 'T list) (n: int) : 'T option =
    if items.IsEmpty then
        None
    else
        Some(List.item ((n % items.Length + items.Length) % items.Length) items)

let private client (n: int) = clients[(n % 3 + 3) % 3]

let private running (state: SessionState) =
    state.Running |> Option.map _.Turn |> Option.defaultValue (state.Turns + 1)

/// The command a step means in a state, if any.
let private command (drive: Drive) (step: Step) : (Attribution * Command) option =
    let state = drive.State
    let requests = state.Requests |> List.map _.Request

    match step with
    | Steering n -> Some(client n, Command.Prompt($"steer {n}", PromptMode.Steer))
    | Queueing n -> Some(client n, Command.Prompt($"queue {n}", PromptMode.Queue))
    | Interrupting n -> Some(client n, Command.Interrupt)
    | Taking step -> Some(system, Command.TakeInputs(running state, max 1 step))
    | Ending result ->
        let ending =
            match (result % 3 + 3) % 3 with
            | 0 ->
                TurnResult.Done(
                    "done",
                    [
                        {
                            Kind = "check"
                            Summary = "ok"
                            Detail = Json.Null
                        }
                    ],
                    Some "test"
                )
            | 1 ->
                match requests |> List.map _.Id with
                | [] -> TurnResult.NeedInput [ "missing" ]
                | ids -> TurnResult.NeedInput ids
            | _ -> TurnResult.Waiting([ $"j{result % 2}" ], "building")

        Some(system, Command.EndTurn(running state, ending))
    | Failing -> Some(system, Command.FailTurn(running state, "model down"))
    | Abandoning -> Some(system, Command.AbandonTurn(running state, "restart"))
    | Opening(isChoice, asInput, turnLifetime, origin, expires) ->
        let id = $"r{state.Seq}"

        let origin =
            match (origin % 3 + 3) % 3 with
            | 0 -> RequestOrigin.Turn(running state)
            | 1 -> RequestOrigin.Session
            | _ -> RequestOrigin.Stream(EventRef.create (StreamId "run/1") 3L)

        let reply = if asInput then Reply.AsInput else Reply.ToOpener
        let lifetime = if turnLifetime then Lifetime.Turn else Lifetime.Session

        let request =
            if isChoice then
                choice id origin reply lifetime
            else
                confirm id origin reply lifetime

        let expiry =
            match (expires % 4 + 4) % 4 with
            | 0 -> None
            | 1 ->
                Some
                    {
                        At = drive.Now + 10L * second
                        Then = ExpiryPolicy.Cancel
                    }
            | 2 ->
                Some
                    {
                        At = drive.Now + 30L * second
                        Then = ExpiryPolicy.Answer(if isChoice then pick "a" else yes)
                    }
            | _ ->
                Some
                    {
                        At = drive.Now
                        Then = ExpiryPolicy.Cancel
                    }

        Some(system, Command.OpenRequest { request with Expiry = expiry })
    | Answering(n, who, valid) ->
        nth requests n
        |> Option.map (fun request ->
            let response =
                match request.Kind, valid with
                | "choice", true -> pick "b"
                | "choice", false -> pick "z"
                | _, true -> yes
                | _, false -> Json.String "yes"

            client who, Command.Answer(request.Id, response))
    | Cancelling n ->
        nth requests n
        |> Option.map (fun request -> bob, Command.CancelRequest(request.Id, "never mind"))
    | Expiring n ->
        nth requests n
        |> Option.map (fun request -> system, Command.ExpireRequest request.Id)
    | Notifying job -> Some(system, Command.NotifyJob($"j{(job % 2 + 2) % 2}", "finished"))
    | Selecting(profile, stale) ->
        Some(
            alice,
            Command.SelectProfile
                {
                    Profile = $"sha256:p{(profile % 3 + 3) % 3}"
                    Overrides = [ "edit.guard", Json.String $"v{profile}" ]
                    Changed = [ "edit.guard" ]
                    BasedOn = if stale then "sha256:stale" else state.Profile
                }
        )
    | Forking at ->
        Some(
            bob,
            Command.Fork(
                $"c{state.Seq}",
                if at % 2 = 0 then
                    None
                else
                    Some(int64 ((at % 50 + 50) % 50 + 1))
            )
        )
    | ChildReady n ->
        nth state.Forks n
        |> Option.map (fun fork ->
            system,
            (if n % 3 = 0 then
                 Command.ChildFailed(fork.Child, "exists")
             else
                 Command.ChildStarted fork.Child))
    | Closing -> Some(alice, Command.Close)
    | Reopening -> Some(alice, Command.Reopen)
    | Archiving -> Some(alice, Command.Archive)
    | Withdrawing n ->
        nth (state.Queue @ state.Inbox) n
        |> Option.map (fun input -> bob, Command.Withdraw input.Id)
    | Waiting _ -> None

let private kinds = RequestKinds.builtIn

let private roundTrip (codec: Codec<'T>) (value: 'T) : 'T =
    match Codec.decodeText codec (Codec.encodeText codec value) with
    | Ok decoded -> decoded
    | Error issues -> failtest (Issue.render issues)

/// Runs a script, checking after every command what must hold whatever happens.
let private run (steps: Step list) : Drive =
    steps
    |> List.fold
        (fun (drive: Drive) step ->
            match step with
            | Waiting seconds -> drive |> wait (int64 ((seconds % 20 + 20) % 20))
            | _ ->
                match command drive step with
                | None -> drive
                | Some(by, command) ->
                    let sent =
                        {
                            Command = command
                            By = by
                            At = drive.Now + second
                        }

                    // Handle depends on nothing the snapshot loses.
                    let restored = roundTrip Codecs.state drive.State

                    Expect.equal restored drive.State "the state survives its JSON"

                    Expect.equal
                        (Session.handle kinds restored sent)
                        (Session.handle kinds drive.State sent)
                        "a restored state decides alike"

                    Expect.equal (roundTrip Codecs.sessionCommand sent) sent "the command survives its JSON"
                    let next = drive |> send by command

                    match next.Last with
                    | Ok _ ->
                        Expect.equal next.State.Seq (int64 next.Events.Length) "the state counts the events"
                        Expect.isNone (Session.nextInputs next.State) "never idle with work it could start"
                    | Error _ -> Expect.equal next.State drive.State "a rejection changes nothing"

                    next)
        started

let private config =
    { FsCheckConfig.defaultConfig with
        maxTest = 300
        endSize = 60
    }

[<Tests>]
let tests =
    testList
        "Session properties"
        [
            testPropertyWithConfig
                config
                "the state is the fold of the log, through its JSON"
                (fun (steps: Step list) ->
                    let drive = run steps
                    let events = drive.Events
                    let stored = events |> List.map (fun event -> Codec.encodeText Codecs.event event)

                    let read =
                        stored
                        |> List.map (fun text ->
                            match Codec.decodeText Codecs.event text with
                            | Ok event -> event
                            | Error issues -> failtest (Issue.render issues))

                    Expect.equal read events "every event survives its JSON"
                    Expect.equal (Machine.replay machine read) drive.State "replaying the stored log gives the state"

                    Expect.equal
                        (events
                         |> List.map (fun event -> Json.serializeCanonical (Codecs.event.Encode event)))
                        (read
                         |> List.map (fun event -> Json.serializeCanonical (Codecs.event.Encode event)))
                        "the same canonical JSON")

            testPropertyWithConfig
                config
                "inputs are numbered by their events and delivered at most once; turns follow each other"
                (fun (steps: Step list) ->
                    let drive = run steps

                    let numbered =
                        drive.Events |> List.mapi (fun index event -> int64 index + 1L, event)

                    for seq, event in numbered do
                        match Session.inputOf event with
                        | Some input -> Expect.equal input.Id seq "an input's id is its event's sequence number"
                        | None -> ()

                    let received =
                        numbered
                        |> List.choose (fun (seq, event) -> Session.inputOf event |> Option.map (fun _ -> seq))
                        |> Set.ofList

                    let delivered =
                        drive.Events
                        |> List.collect (fun event ->
                            match event with
                            | Event.TurnStarted(_, inputs, _, _)
                            | Event.InputsDelivered(_, _, inputs) -> inputs
                            | _ -> [])

                    Expect.equal (List.distinct delivered) delivered "each input is delivered once"
                    Expect.isTrue (delivered |> List.forall received.Contains) "only inputs that were received"

                    let turns =
                        drive.Events
                        |> List.choose (fun event ->
                            match event with
                            | Event.TurnStarted(turn, _, _, _) -> Some(turn, true)
                            | Event.TurnEnded(turn, _, _) -> Some(turn, false)
                            | _ -> None)

                    let expected = turns |> List.mapi (fun index _ -> index / 2 + 1, index % 2 = 0)

                    Expect.equal turns expected "turns start and end one at a time, numbered from 1")

            testPropertyWithConfig config "a request is settled at most once after it opens" (fun (steps: Step list) ->
                let drive = run steps

                let unsettled =
                    drive.Events
                    |> List.fold
                        (fun (open': Set<string>) event ->
                            match event with
                            | Event.RequestOpened pending ->
                                Expect.isFalse (open'.Contains pending.Request.Id) "not open twice"
                                open'.Add pending.Request.Id
                            | Event.RequestResolved resolved ->
                                Expect.isTrue (open'.Contains resolved.Request) "settled while open"
                                open'.Remove resolved.Request
                            | _ -> open')
                        Set.empty

                Expect.equal (drive.State.Requests |> List.map _.Request.Id |> Set.ofList) unsettled "the open ones")
        ]
