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

                // The person allows everything the grants log asks about (T127b): the write outside the
                // workspace and the command the shell expands. So the sandbox alone keeps the file out.
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

                    let asked =
                        person.Asked
                        |> List.filter (fun r -> r.Action.Value <> "component.install")
                        |> List.map (fun r -> r.Action.Value + " " + r.Resource)
                        |> List.sort

                    Expect.equal
                        asked
                        [
                            "fs.write " + Fx.Models.WorkspacePath.normalize escaped
                            "shell.exec echo $TMPDIR|$XDG_CACHE_HOME"
                        ]
                        "the grants log asked, and the person allowed"

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

            testTask
                "fx run -p: a turn's command asks the grants log, a person's answer lets it run; nobody's answer is no answer, and a later run asks again" {
                do! network.Force()
                let p = place "grant"
                let model = Model()

                model.AddAll
                    [
                        $"// intent: make a directory\nconst made = await {g}.shell.run(\"mkdir made\")\nmade.exitCode"
                        finish "Made it" "\"mkdir made\""
                    ]

                let person = Person always
                let host = start (options p model [ "model.role.main", Json.String modelId ])

                let first =
                    try
                        run
                            host
                            person
                            {
                                Prompt = "Make a directory"
                                Session = None
                                Title = None
                            }
                    finally
                        stop host

                Expect.equal (done' first person) "Made it" "done"
                Expect.isTrue (IO.Directory.Exists(p.File "made")) "the command ran"

                match person.Asked |> List.filter (fun r -> r.Action.Value = "shell.exec") with
                | [ request ] ->
                    Expect.equal request.Resource "mkdir made" "the command"
                    Expect.equal request.Context.Session (Some first.Session) "the run's session"

                    Expect.isTrue
                        (request.Evidence |> List.exists (fun e -> e.Label = "asked by"))
                        "which turn and call asked"
                | other -> failtest $"asked {other}"

                // Nobody at a terminal: the next command's request is withdrawn (nobody denied it), the cell
                // fails, and the model reads why.
                let nobody () =
                    let lines = ResizeArray<string>()

                    let io: RunIo =
                        {
                            Show = fun line -> lock lines (fun () -> lines.Add line)
                            Answer = fun _ -> Task.FromResult None
                            Decide = fun _ -> Task.FromResult None
                            Interrupt = CancellationToken.None
                        }

                    lines, io

                let runAs (io: RunIo) (prompt: string) =
                    let host = start (options p model [])

                    try
                        match
                            waitFor
                                300.0
                                (Headless.run
                                    host
                                    {
                                        Prompt = prompt
                                        Session = Some first.Session
                                        Title = None
                                    }
                                    io
                                    CancellationToken.None)
                        with
                        | Ok outcome -> outcome
                        | Error e -> failtest $"the run failed: {e}"
                    finally
                        stop host

                let other = $"// intent: make another\nawait {g}.shell.run(\"mkdir other\")"

                let unanswered (lines: ResizeArray<string>) =
                    lines
                    |> Seq.exists (fun l ->
                        l.Contains "nobody can answer here"
                        && l.Contains $"grants allow shell.exec 'mkdir other'")

                model.AddAll [ other; finish "Could not" "\"not answered\"" ]
                let lines, io = nobody ()
                let second = runAs io "Make another"

                match second.Ending with
                | TurnEnd.Completed(TurnResult.Done("Could not", _, _)) -> ()
                | other -> failtest $"{other}"

                Expect.isFalse (IO.Directory.Exists(p.File "other")) "nothing ran"
                Expect.isTrue (unanswered lines) "the run says nobody could answer, and how to allow it"

                Expect.stringContains
                    (Fx.Effects.HttpRequest.bodyText (List.last model.Requests))
                    "its grant request was not answered"
                    "the model reads that nobody answered"

                // Then from a terminal, the same command in a later run: a person is asked again.
                model.AddAll [ other; finish "Made another" "\"mkdir other\"" ]

                let once =
                    Person
                        {
                            Effect = GrantEffect.Allow
                            Scope = ScopeChoice.Once
                            Resource = None
                            Expires = None
                            Reason = None
                        }

                let third = runAs (once.Io CancellationToken.None) "Make another, please"
                Expect.equal (done' third once) "Made another" "done"
                Expect.isTrue (IO.Directory.Exists(p.File "other")) "the command ran"

                Expect.equal
                    (once.Asked |> List.map (fun r -> r.Action.Value + " " + r.Resource))
                    [ "shell.exec mkdir other" ]
                    "asked again"

                // A terminal first, headless next: the allowance was for that call only.
                model.AddAll [ other; finish "Could not again" "\"not answered\"" ]
                let lines, io = nobody ()
                let fourth = runAs io "Once more"

                match fourth.Ending with
                | TurnEnd.Completed(TurnResult.Done("Could not again", _, _)) -> ()
                | other -> failtest $"{other}"

                Expect.isTrue (unanswered lines) "asked, and nobody could answer"
            }

            testTask "fx run -p is recorded and fx replay replays it exactly; with session.record off nothing is" {
                do! network.Force()
                let p = place "replay"
                p.Write("greeting.txt", "hello\n")
                let model = Model()

                model.AddAll
                    [
                        $"// intent: change the greeting\nconst file = await {g}.fs.read(\"greeting.txt\")\nawait {g}.fs.write(\"greeting.txt\", file.text.replace(\"hello\", \"hello, world\"))\nconst run = await {g}.shell.run(\"cat greeting.txt\")\nrun.stdout"
                        finish "The greeting says hello, world" "`cat greeting.txt: ${run.stdout.trim()}`"
                        $"// intent: look\nconst run = await {g}.shell.run(\"cat greeting.txt\")\nrun.stdout"
                        finish "Looked" "`cat greeting.txt: ${run.stdout.trim()}`"
                    ]

                let person = Person always
                let selections = [ "model.role.main", Json.String modelId ]

                // Two host processes: the second continues the session with a kernel of its own.
                let runIn (request: RunRequest) =
                    let host = start (options p model selections)

                    try
                        run host person request
                    finally
                        stop host

                let first =
                    runIn
                        {
                            Prompt = "Make the greeting say hello, world"
                            Session = None
                            Title = None
                        }

                Expect.equal (done' first person) "The greeting says hello, world" "done"

                let second =
                    runIn
                        {
                            Prompt = "Look again"
                            Session = Some first.Session
                            Title = None
                        }

                Expect.equal (done' second person) "Looked" "done again"

                // `fx replay <session> --json`: the data directory's stores, without the model, kernels,
                // files or clock.
                let replayed =
                    waitFor
                        300.0
                        (Commands.replay
                            (options p (Model()) [])
                            {
                                Session = first.Session
                                Json = true
                                StallSeconds = 60
                            })

                match replayed with
                | Error errors -> failtest (String.concat "\n" errors)
                | Ok output ->
                    Expect.equal output.ExitCode 0 $"replay matched:\n{output.Stdout}{output.Stderr}"

                    match
                        Json.parse output.Stdout
                        |> Result.mapError string
                        |> Result.bind (fun json ->
                            Fx.Replay.ReplayCommand.reportCodec.Decode json
                            |> Result.mapError (Issue.renderAtMost 3))
                    with
                    | Error e -> failtest e
                    | Ok report ->
                        Expect.equal report.Segments 2 "two host processes"
                        Expect.equal report.Turns 2 "two turns"
                        Expect.equal report.Requests 4 "every model request again"

                // Not recorded: the same run journals nothing.
                let off = place "unrecorded"
                let unrecorded = Model()

                unrecorded.AddAll
                    [
                        $"// intent: look\nconst run = await {g}.shell.run(\"echo hi\")\nrun.stdout"
                        finish "Looked" "`echo hi: ${run.stdout.trim()}`"
                    ]

                let host =
                    start (options off unrecorded (selections @ [ "session.record", Json.String "off" ]))

                let outcome =
                    try
                        run
                            host
                            person
                            {
                                Prompt = "Look"
                                Session = None
                                Title = None
                            }
                    finally
                        stop host

                Expect.equal (done' outcome person) "Looked" "done"
                let offOptions = options off unrecorded []

                match offOptions.OpenStores offOptions.Paths StoreDurability.Normal with
                | Error e -> failtest e
                | Ok stores ->
                    try
                        let journal =
                            wait (
                                stores.Log.Read
                                    (Fx.Replay.JournalLog.stream outcome.Session)
                                    0L
                                    10
                                    CancellationToken.None
                            )

                        let session =
                            wait (stores.Log.Read (Session.stream outcome.Session) 0L 10 CancellationToken.None)

                        Expect.isNonEmpty session "the session is there"
                        Expect.isEmpty journal "nothing is journaled"
                    finally
                        stores.Close()

                match
                    waitFor
                        120.0
                        (Commands.replay
                            offOptions
                            {
                                Session = outcome.Session
                                Json = false
                                StallSeconds = 10
                            })
                with
                | Ok output -> Expect.equal output.ExitCode 1 $"not replayable:\n{output.Stdout}"
                | Error errors -> failtest (String.concat "\n" errors)
            }

            testTask
                "fx run -p: a long turn compacts beside a cell and when a cell asks; summaries share the conversation's prefix, and it replays" {
                do! network.Force()
                let p = place "compaction"
                p.Write("greeting.txt", "hello\n")
                let model = Model()
                model.Add $"// intent: read the greeting\nconst file = await {g}.fs.read(\"greeting.txt\")\nfile.text"

                // The provider counted 900000 of the model's 1000000 tokens: past the threshold (80%).
                model.AddSized(
                    900000,
                    $"// intent: change it\nawait {g}.fs.write(\"greeting.txt\", \"hello, world\\n\")"
                )

                model.AddText "Summary: read greeting.txt, then changed it to say hello, world."
                model.Add $"// intent: compact\nawait {g}.history.compact(\"the greeting\")"
                model.AddText "Summary: greeting.txt says hello, world."
                model.Add(finish "The greeting says hello, world" "\"compacted twice\"")
                let person = Person always
                let host = start (options p model [ "model.role.main", Json.String modelId ])

                let outcome =
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

                Expect.equal (done' outcome person) "The greeting says hello, world" "done"
                Expect.equal (p.Read "greeting.txt") "hello, world\n" "the file changed"

                let compacted =
                    person.Lines
                    |> List.filter (fun line -> line.StartsWith("◇ compacted", StringComparison.Ordinal))

                Expect.equal compacted.Length 2 "an activity line for each compaction"

                let bodies =
                    model.Requests
                    |> List.map (fun request ->
                        match Json.parse (Fx.Effects.HttpRequest.bodyText request) with
                        | Ok json -> json
                        | Error e -> failtest (string e))

                Expect.equal bodies.Length 6 "four calls and two summaries"

                let field (name: string) (json: Json) =
                    Json.tryField name json |> Option.defaultValue Json.Null

                let messages (json: Json) =
                    match field "messages" json with
                    | Json.Array messages -> messages
                    | other -> failtest $"messages: {other}"

                // Each summary request is the call before it, as the Messages API received it, and the
                // instruction: the same system, tools and history, so the provider's cache hits.
                for summary, call in [ bodies[2], bodies[1]; bodies[4], bodies[3] ] do
                    Expect.equal (field "system" summary) (field "system" call) "the same system"
                    Expect.equal (field "tools" summary) (field "tools" call) "the same tools"
                    let sent = messages summary
                    let before = messages call
                    Expect.equal sent.Length before.Length "the same messages"

                    Expect.equal
                        (List.truncate (sent.Length - 1) sent)
                        (List.truncate (before.Length - 1) before)
                        "as sent"

                    match field "content" (List.last sent), field "content" (List.last before) with
                    | Json.Array blocks, Json.Array earlier ->
                        Expect.equal (List.truncate earlier.Length blocks) earlier "then the instruction"
                        Expect.equal blocks.Length (earlier.Length + 1) "one block more"
                    | other -> failtest $"{other}"

                let last = Fx.Effects.HttpRequest.bodyText (List.last model.Requests)
                Expect.stringContains last "greeting.txt says hello, world." "the latest summary"
                Expect.isFalse (last.Contains "Make the greeting say hello, world") "the prompt is summarized"

                // `fx replay`: the summaries are model calls like the others.
                match
                    waitFor
                        300.0
                        (Commands.replay
                            (options p (Model()) [])
                            {
                                Session = outcome.Session
                                Json = false
                                StallSeconds = 60
                            })
                with
                | Ok output -> Expect.equal output.ExitCode 0 $"replay matched:\n{output.Stdout}{output.Stderr}"
                | Error errors -> failtest (String.concat "\n" errors)
            }
        ]
