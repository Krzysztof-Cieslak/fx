/// The host end to end with the real kernel (spec §19.5 `fx run -p`): TypeScript cells on
/// `fx.interpreter.bun-ts`, on the pinned Bun that the host's own installer downloads after a person
/// approves it (the grants log), in an environment of `fx.env.local` with envd in this process; then a
/// restart that continues the session, and one that finds a turn the host stopped during. Needs the
/// network for Bun (cached in artifacts/; skipped offline, required in CI).
module Fx.Host.Tests.LiveTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Grants
open Fx.Grants.Host
open Fx.Sessions
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

/// A person at the terminal: answers every grant request with `answer`, and keeps what was asked.
type private Person(answer: GrantAnswer) =
    let asked = ResizeArray<GrantRequest>()
    let lines = ResizeArray<string>()

    member _.Asked = lock asked (fun () -> List.ofSeq asked)
    member _.Lines = lock lines (fun () -> List.ofSeq lines)

    member _.Io(interrupt: CancellationToken) : RunIo =
        {
            Show = fun line -> lock lines (fun () -> lines.Add line)
            Answer =
                fun request ->
                    lock asked (fun () -> asked.Add request)
                    Task.FromResult(Some answer)
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
        ]
