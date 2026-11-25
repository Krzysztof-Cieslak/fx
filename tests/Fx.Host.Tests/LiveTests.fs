/// The host end to end with the real kernel (spec §19.5 `fx run -p`): TypeScript cells on
/// `fx.interpreter.bun-ts`, on the pinned Bun that the host's own installer downloads after a person
/// approves it (the grants log), in an environment of `fx.env.local` with envd in this process; then a
/// restart that continues the session, one that finds a turn the host stopped during, and one that
/// finds a cell in doubt and asks a person about it. Needs the network for Bun (cached in artifacts/;
/// skipped offline, required in CI).
module Fx.Host.Tests.LiveTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Grants
open Fx.Grants.Host
open Fx.Machines.Host
open Fx.Sessions
open Fx.CodeAct
open Fx.Host
open Fx.Host.Tests.Support

let private network =
    lazy
        (task {
            match! Fx.Kernel.Tests.PinnedBun.executable () with
            | Ok _ -> return ()
            | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
                return failtest $"Bun is not available: {reason}"
            | Error reason -> return skiptest $"Bun is not available: {reason}"
        })

/// A person at the terminal: answers every grant request with `answer`, decides calls in doubt with
/// `Decision` (`None`: nobody can), and keeps what was asked.
type private Person(answer: GrantAnswer) =
    let asked = ResizeArray<GrantRequest>()
    let lines = ResizeArray<string>()
    let decided = ResizeArray<OutcomeUnknown>()

    member val Decision: string option = None with get, set
    member _.Asked = lock asked (fun () -> List.ofSeq asked)
    member _.Lines = lock lines (fun () -> List.ofSeq lines)
    member _.Decided = lock decided (fun () -> List.ofSeq decided)

    member this.Io(interrupt: CancellationToken) : RunIo =
        {
            Show = fun line -> lock lines (fun () -> lines.Add line)
            Answer =
                fun request ->
                    lock asked (fun () -> asked.Add request)
                    Task.FromResult(Some answer)
            Decide =
                fun card ->
                    lock decided (fun () -> decided.Add card)

                    Task.FromResult(
                        this.Decision
                        |> Option.map (fun option ->
                            {
                                Option = option
                                Note = Some "decided in a test"
                            })
                    )
            Interrupt = interrupt
        }

let private always: GrantAnswer =
    {
        Effect = GrantEffect.Allow
        Scope = ScopeChoice.Host
        Resource = None
        Expires = None
        Reason = None
    }

let private finish (summary: string) (evidence: string) =
    $"// intent: finish\nawait {g}.finish({{ kind: \"done\", summary: {Json.quote summary}, evidence: [{{ kind: \"command\", summary: {evidence} }}] }})"

let private run (host: Host) (person: Person) (request: RunRequest) : RunOutcome =
    match waitFor 300.0 (Headless.run host request (person.Io CancellationToken.None) CancellationToken.None) with
    | Ok outcome -> outcome
    | Error e ->
        let shown = String.concat "\n" person.Lines
        failtest $"the run failed: {e}\n{shown}"

let private done' (outcome: RunOutcome) (person: Person) : string =
    match outcome.Ending with
    | TurnEnd.Completed(TurnResult.Done(summary, _, _)) -> summary
    | other ->
        let shown = String.concat "\n" person.Lines
        failtest $"{other}\n{shown}"

[<Tests>]
let tests =
    testSequenced
    <| testList
        "Host on bun-ts"
        [
            testTask
                "fx run -p: Bun is installed once a person approves, TypeScript cells act, a restart continues the session" {
                do! network.Force()
                let p = place "live"
                p.Write("greeting.txt", "hello\n")
                let model = Model()

                model.AddAll
                    [
                        $"// intent: change the greeting\nconst file = await {g}.fs.read(\"greeting.txt\")\nawait {g}.fs.write(\"greeting.txt\", file.text.replace(\"hello\", \"hello, world\"))\nconst run = await {g}.shell.run(\"cat greeting.txt\")\nrun.stdout"
                        finish "The greeting says hello, world" "`cat greeting.txt: ${run.stdout.trim()}`"
                    ]

                let person = Person always
                let host = start (options p model [ "model.role.main", Json.String modelId ])

                let first =
                    try
                        run
                            host
                            person
                            {
                                Prompt = "Make the greeting say hello, world"
                                Session = None
                                Title = None
                            }
                    finally
                        stop host

                Expect.equal (done' first person) "The greeting says hello, world" "done"
                Expect.equal (p.Read "greeting.txt") "hello, world\n" "the file changed"
                Expect.equal (Headless.exitCode first) 0 "exit code"

                match person.Asked with
                | [ request ] ->
                    Expect.equal request.Action.Value "component.install" "asked to install"
                    Expect.stringStarts request.Resource "bun@" "Bun"
                | other -> failtest $"asked {other}"

                Expect.contains person.Lines "◇ cell  change the greeting" "activity"

                // The key reached the provider; the model's requests carried the cells' receipts.
                let requests = model.Requests
                Expect.equal requests.Length 2 "two model calls"

                Expect.isTrue
                    (requests
                     |> List.forall (fun r -> r.Headers |> List.contains ("x-api-key", "test-key")))
                    "the API key from the host's variables"

                Expect.stringContains (Fx.Effects.HttpRequest.bodyText requests[1]) "hello, world" "the receipt"

                // A new host process: the session goes on in its kernel's environment, Bun is not asked
                // for again (a host-wide grant, and installed).
                model.Add(finish "Read it again" "\"seen\"")
                let person = Person always
                let host = start (options p model [])

                try
                    let second =
                        run
                            host
                            person
                            {
                                Prompt = "Finish again"
                                Session = Some first.Session
                                Title = None
                            }

                    Expect.equal second.Turn 2 "the second turn"
                    Expect.equal (done' second person) "Read it again" "done"
                    Expect.isEmpty person.Asked "nothing asked"

                    // The conversation continued: turn 1's prompt is in turn 2's request.
                    Expect.stringContains
                        (Fx.Effects.HttpRequest.bodyText (List.last model.Requests))
                        "Make the greeting say hello, world"
                        "the history"
                finally
                    stop host
            }

            testTask
                "a turn running when the host stops is abandoned at the next start, and the session takes prompts again" {
                do! network.Force()
                let p = place "abandon"
                let model = Model()
                let held = model.Hold 1
                let person = Person always
                let host = start (options p model [ "model.role.main", Json.String modelId ])
                let mutable session = ""

                try
                    let running =
                        Headless.run
                            host
                            {
                                Prompt = "Take your time"
                                Session = None
                                Title = None
                            }
                            (person.Io CancellationToken.None)
                            CancellationToken.None

                    do! (model.Asked 1).WaitAsync(TimeSpan.FromSeconds 300.0)

                    session <-
                        match HostSessions.entries host with
                        | [ entry ] -> entry.Session
                        | other -> failtest $"{other}"

                    // The host stops mid-turn: the run ends without an ending.
                    do! (Host.stop host).WaitAsync(TimeSpan.FromSeconds 60.0)

                    match! running.WaitAsync(TimeSpan.FromSeconds 60.0) with
                    | Error _ -> ()
                    | Ok outcome ->
                        Expect.notEqual outcome.Ending (TurnEnd.Completed(TurnResult.Done("", [], None))) "no result"
                finally
                    held.TrySetResult() |> ignore
                    stop host

                let host = start (options p model [])

                try
                    // Recovery runs as the session's actor starts: wait for it to settle.
                    let deadline = DateTime.UtcNow.AddSeconds 60.0
                    let mutable state = wait (HostSessions.state host session CancellationToken.None)

                    while state.Last.IsNone && DateTime.UtcNow < deadline do
                        do! Task.Delay 20
                        state <- wait (HostSessions.state host session CancellationToken.None)

                    match state.Last with
                    | Some last ->
                        match last.End with
                        | TurnEnd.Abandoned _ -> ()
                        | other -> failtest $"turn 1 ended {other}"
                    | None -> failtest "no turn ended"

                    Expect.isNone state.Running "nothing runs"
                    model.Add(finish "Back" "\"after the restart\"")

                    let again =
                        run
                            host
                            person
                            {
                                Prompt = "Go on"
                                Session = Some session
                                Title = None
                            }

                    Expect.equal again.Turn 2 "a new turn"
                    Expect.equal (done' again person) "Back" "done"
                finally
                    stop host
            }

            testTask
                "a cell running when the host stops is a person's decision at the next start, then the turn goes on" {
                do! network.Force()
                let p = place "doubt"
                let model = Model()

                model.Add(
                    "// intent: wait\nconsole.log(\"waiting\")\nawait new Promise((resolve) => setTimeout(resolve, 600000))"
                )

                let person = Person always
                let host = start (options p model [ "model.role.main", Json.String modelId ])
                let mutable session = ""

                try
                    let running =
                        Headless.run
                            host
                            {
                                Prompt = "Wait a while"
                                Session = None
                                Title = None
                            }
                            (person.Io CancellationToken.None)
                            CancellationToken.None

                    do! (model.Asked 1).WaitAsync(TimeSpan.FromSeconds 300.0)

                    session <-
                        match HostSessions.entries host with
                        | [ entry ] -> entry.Session
                        | other -> failtest $"{other}"

                    // The step journal says the cell started: it is in doubt once the host stops.
                    let steps = StepJournal.typed host.Stores.Log

                    let! _ =
                        Eff.run
                            (Host.within host (new CancellationTokenSource(TimeSpan.FromSeconds 300.0)).Token)
                            (EventLog.subscribe steps (StepJournal.stream session) 0L (fun recorded ->
                                match recorded.Event with
                                | StepEvent.CallStarted _ -> Eff.ret false
                                | _ -> Eff.ret true))

                    do! (Host.stop host).WaitAsync(TimeSpan.FromSeconds 60.0)
                    let! _ = running.WaitAsync(TimeSpan.FromSeconds 60.0)
                    ()
                finally
                    stop host

                let host = start (options p model [])

                try
                    // Nobody at a terminal: the run says what waits, and the decision stays open.
                    match
                        waitFor
                            120.0
                            (Headless.run
                                host
                                {
                                    Prompt = "Go on"
                                    Session = Some session
                                    Title = None
                                }
                                (person.Io CancellationToken.None)
                                CancellationToken.None)
                    with
                    | Ok outcome -> failtest $"the run ended {outcome}"
                    | Error e -> Expect.stringContains e "waits for a decision" "says so"

                    match person.Decided with
                    | [ card ] ->
                        Expect.equal card.Call.Tool "cell" "the cell"
                        Expect.equal card.Call.Intent (Some "wait") "its intent"

                        Expect.equal
                            (card.Options |> List.map _.Id)
                            [ "completed"; "failed"; "rerun"; "inspect" ]
                            "the options"
                    | other -> failtest $"decided {other}"

                    // A person decides: the call failed; the turn goes on to its end.
                    person.Decision <- Some "failed"
                    model.Add(finish "Back" "\"after the decision\"")

                    let decided =
                        run
                            host
                            person
                            {
                                Prompt = "Go on, please"
                                Session = Some session
                                Title = None
                            }

                    Expect.equal decided.Turn 1 "the recovered turn"
                    Expect.equal (done' decided person) "Back" "done"
                    Expect.equal person.Decided.Length 2 "asked once more"

                    Expect.stringContains
                        (Fx.Effects.HttpRequest.bodyText model.Requests[1])
                        "decided in a test"
                        "the decision's note reached the model"
                finally
                    stop host
            }

            testTask "fx run -p: a turn's commands and kernel run in the environment's sandbox" {
                do! network.Force()

                if OperatingSystem.IsWindows() then
                    skiptest "no sandbox driver confines processes on Windows yet (T511)"

                let p = place "sandboxed-run"
                let outside = Scratch.directory "outside"
                let escaped = System.IO.Path.Combine(outside, "escaped.txt")
                let model = Model()

                let command =
                    Json.quote (
                        "echo inside > made.txt; echo x > '"
                        + escaped
                        + "' 2>/dev/null; echo \"$TMPDIR|$XDG_CACHE_HOME\" > variables.txt"
                    )

                model.AddAll
                    [
                        $"// intent: write inside and outside the workspace\nconst run = await {g}.shell.run({command})\nrun.stdout"
                        finish "Wrote what the sandbox allows" "\"made.txt\""
                    ]

                let person = Person always
                let host = start (options p model [ "model.role.main", Json.String modelId ])

                try
                    let outcome =
                        run
                            host
                            person
                            {
                                Prompt = "Write a file"
                                Session = None
                                Title = None
                            }

                    Expect.equal (done' outcome person) "Wrote what the sandbox allows" "done"

                    let sandbox =
                        match HostSessions.entries host with
                        | [ entry ] ->
                            match wait (host.Env.Envs.Get entry.Environment CancellationToken.None) with
                            | Some record -> record.Sandbox
                            | None -> None
                        | _ -> None

                    Expect.isTrue (System.IO.File.Exists(p.File "made.txt")) "the workspace is writable"

                    match sandbox with
                    | Some sandbox when sandbox.Confines ->
                        Expect.isFalse (System.IO.File.Exists escaped) "nothing lands outside the workspace"

                        let variables = (p.Read "variables.txt").Trim().Split('|')
                        Expect.stringEnds variables[0] "/tmp" "TMPDIR is the environment's"
                        Expect.stringEnds variables[1] "/cache" "the tools' caches are the environment's"
                    | Some sandbox when Environment.GetEnvironmentVariable "SANDBOX_TESTS" = "1" ->
                        failtest $"a driver must confine here: {sandbox}"
                    | _ -> ()
                finally
                    stop host
            }
        ]
