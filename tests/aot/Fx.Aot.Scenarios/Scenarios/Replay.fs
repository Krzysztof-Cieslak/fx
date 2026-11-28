/// Record and replay (T131, docs/modules/replay.md) under Native AOT: a CodeAct turn on the session host,
/// recorded (`Recording.codeact`) with a scripted model, an in-memory workspace and the script kernel as
/// a process of the shell (`ScriptProc`); then replayed exactly from the journal (`SessionReplay.run`),
/// and once more with a changed prompt, which diverges. The output is the turn's ending, what the
/// recording holds, and the two replays' verdicts.
module Fx.Aot.Scenarios.Replay

open System
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Envd
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Models.Fakes
open Fx.Store
open Fx.Store.Fakes
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Kernel
open Fx.Variants.Fakes
open Fx.CodeAct
open Fx.Replay
open Fx.Aot.Scenarios.Kernel

type private HostEnv =
    {
        Clock: Clock
        Trace: Trace
        Llm: Llm
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITrace with
        member env.Trace = env.Trace

    interface ILlm with
        member env.Llm = env.Llm

    interface IScopeWith<HostEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "HostEnv"

type private HandsEnv =
    {
        Clock: Clock
        FsRead: FsRead
        Fs: Fs
        FsOps: FsOps
        Shell: Shell
        Ids: Ids
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IFsOps with
        member env.FsOps = env.FsOps

    interface IShell with
        member env.Shell = env.Shell

    /// Where output cut by the bindings goes (T201a): one store for every environment of this type.
    static member val Stored = Fx.Store.Fakes.InMemoryBlobs()

    interface Fx.Store.IBlobs with
        member _.Blobs = HandsEnv.Stored.Blobs

    interface IIds with
        member env.Ids = env.Ids

    interface IScopeWith<HandsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "HandsEnv"

type private DriverEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<DriverEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "DriverEnv"

let private os =
    if OperatingSystem.IsWindows() then Fx.Sdk.Os.Windows
    elif OperatingSystem.IsMacOS() then Fx.Sdk.Os.MacOs
    else Fx.Sdk.Os.Linux

let private workspace = if OperatingSystem.IsWindows() then @"C:\work" else "/work"

let private cell (n: int) (code: string) : ChatEvent list =
    ChatEvents.ofResponse
        {
            Id = None
            Model = Some "model"
            Parts =
                [
                    Part.ToolCall
                        {
                            Id = $"call-{n}"
                            Name = "cell"
                            Arguments = Json.serialize (Json.Object [ "code", Json.String code ])
                            State = None
                        }
                ]
            Stop = StopReason.ToolUse
            Usage =
                { Usage.zero with
                    Input = 100
                    Output = 10
                }
        }

let private within (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 60.0)

let private kernelOptions =
    { KernelOptions.defaults with
        ExitGrace = TimeSpan.FromSeconds 1.0
    }

let private record () : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let fsClock = FakeClock(Instant.ofUnixMicroseconds 1_700_000_000_000_000L)

        let fs =
            InMemoryFs(fsClock.Clock, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())

        fs.AddDirectory workspace

        let shell =
            ScriptProc.shell (fun command _ ->
                let script =
                    match command.Spec.Program with
                    | Program.Script script -> script
                    | Program.Exec(file, _) -> file

                Task.FromResult(Ok(ScriptedShell.output 0 $"ran {script}\n" "")))

        let queue =
            Collections.Generic.Queue<ChatEvent list>(
                [
                    cell
                        1
                        "// intent: write\ncall w = fs.write [\"notes.md\", \"# Notes\\n\"]\nprint wrote\ncall r = shell.run [\"make test\"]\nr"
                    cell 2 "// intent: read\ncall n = fs.read [\"notes.md\"]\nn"
                    cell 3 "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Wrote the notes\"}"
                ]
            )

        let model =
            ScriptedLlm(fun _ -> lock queue (fun () -> if queue.Count > 0 then Some(queue.Dequeue()) else None))

        let env: HostEnv =
            {
                Clock = SystemClock.clock
                Trace = Trace.none
                Llm = model.Llm
                Scope = Scope.root
            }

        let hands: Hands<HandsEnv> =
            {
                Env =
                    {
                        Clock = SystemClock.clock
                        Ids = SystemIds.create SystemClock.clock
                        FsRead = fs.FsRead
                        Fs = fs.Fs
                        FsOps = FsOpsOver.create fs.FsRead fs.Fs
                        Shell = shell
                        Scope = Scope.root
                    }
                Workspace = workspace
                Os = os
                CommandEnv = Inherit []
                Interpreter = ScriptProc.interpreter kernelOptions
                Guard = fun _ -> EffResult.ok []
                Snapshots = None
                BlobRoot = None
                Sandbox = None
                Variables = []
                Jobs = Jobs.none
            }

        let wraps: Wraps<HostEnv, HandsEnv> =
            {
                Host =
                    fun taps env ->
                        { env with
                            Clock = taps.Clock env.Clock
                            Llm = taps.Llm env.Llm
                        }
                Hands =
                    fun taps env ->
                        { env with
                            Clock = taps.Clock env.Clock
                            Ids = taps.Ids env.Ids
                            FsRead = taps.FsRead env.FsRead
                            Fs = taps.Fs env.Fs
                            FsOps = taps.FsOps env.FsOps
                            Shell = taps.Shell env.Shell
                        }
            }

        let variants =
            FakeVariants.create
                (CodeActSlots.read @ RecordingSlots.all)
                [
                    "model.role.main", Json.String "test/model"
                    "codeact.journal.snapshots", Json.Bool false
                ]

        let store = InMemoryEventLog<Payload>()
        let blobs = InMemoryBlobs()
        let sessions = SessionLog.typed store.Log
        let recorder = Recording.recorder store.Log blobs.Blobs SystemClock.clock

        let strategy =
            Recording.codeact
                recorder
                wraps
                {
                    Sessions = sessions
                    Contexts = ContextLog.typed store.Log
                    Steps = StepJournal.typed store.Log
                    Blobs = Some blobs.Blobs
                    Profile = fun _ -> Eff.ret variants
                    Hands = fun _ -> Eff.ret (Ok hands)
                    Catalog = fun _ -> None
                }

        let settings =
            { SessionSettings.defaults with
                Kinds = Questions.kinds
            }

        let! host =
            Eff.run env (SessionHost.create sessions (InMemorySnapshots()).Store strategy.Runner settings)

        let alice = Attribution.create "user:alice" "scenario"

        let! _ =
            Eff.run env (SessionHost.start host "s1" (IdempotencyKey "start") alice "p1" None)
            |> within

        let! _ =
            Eff.run
                env
                (SessionHost.send
                    host
                    "s1"
                    (IdempotencyKey "p1")
                    alice
                    (Command.Prompt("Write the notes", PromptMode.Steer)))
            |> within

        let mutable ending = None
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        while ending.IsNone && DateTime.UtcNow < deadline do
            ending <-
                store.Events(Session.stream "s1")
                |> List.map (EventLog.decode SessionLog.schema)
                |> List.tryPick (fun r ->
                    match r.Event with
                    | Event.TurnEnded(1, ended, _) -> Some ended
                    | _ -> None)

            if ending.IsNone then
                do! Task.Delay 10

        let ended = ending |> Option.map string |> Option.defaultValue "did not end"
        lines.Add $"recorded: turn 1 {ended}"

        do! Eff.run env (SessionHost.stop host) |> within
        do! strategy.Close().WaitAsync(TimeSpan.FromSeconds 60.0)

        let journal =
            store.Events(JournalLog.stream "s1")
            |> List.map (EventLog.decode JournalLog.schema)

        let entries =
            journal
            |> List.collect (fun r ->
                match r.Event with
                | JournalEvent.Recorded(_, entries) -> entries
                | _ -> [])

        lines.Add(
            "journal: "
            + (journal
               |> List.filter (fun r ->
                   match r.Event with
                   | JournalEvent.Recorded _ -> false
                   | _ -> true)
               |> List.map (fun r -> string r.Event)
               |> String.concat ", ")
        )

        lines.Add(
            "operations: "
            + (entries
               |> List.map (fun e -> e.Op)
               |> List.distinct
               |> List.sort
               |> String.concat " ")
        )

        lines.Add(
            "model requests: "
            + string (entries |> List.filter (fun e -> e.Op = Ops.Stream) |> List.length)
        )

        let recording: RecordedSession = { Log = store.Log; Blobs = blobs.Blobs }

        let driver: DriverEnv =
            {
                Clock = SystemClock.clock
                Scope = Scope.root
            }

        let replaySettings: ReplaySettings =
            {
                Interpreter = fun _ -> ScriptProc.interpreter kernelOptions
                Provider = fun id -> Error $"no interpreter provider {id} here"
                KernelOptions = kernelOptions
                Catalog = fun _ -> None
                StallAfter = TimeSpan.FromSeconds 10.0
            }

        let! exact =
            Eff.run driver (SessionReplay.run replaySettings recording "s1") |> within

        lines.Add
            $"exact replay: matched {exact.Matched}, {exact.Segments} segments, {exact.Turns} turns, {exact.Requests} requests, {exact.ContextEvents} context events, {exact.StepEvents} step events"

        match exact.Divergence with
        | Some divergence -> lines.Add $"unexpected divergence: {divergence}"
        | None -> ()

        let changed =
            { replaySettings with
                Interpreter =
                    fun _ ->
                        { ScriptProc.interpreter kernelOptions with
                            Prompt = fun _ -> "Cells are scripts, said differently."
                        }
            }

        let! diverged = Eff.run driver (SessionReplay.run changed recording "s1") |> within

        match diverged.Divergence with
        | Some divergence -> lines.Add $"changed prompt: diverged in {divergence.Lane}: {divergence.Reason}"
        | None -> lines.Add "changed prompt: matched (unexpected)"

        let output =
            ReplayCommand.output
                {
                    Session = "s1"
                    Json = true
                    StallSeconds = 10
                }
                exact

        match Json.parse output.Stdout with
        | Ok json ->
            match ReplayCommand.reportCodec.Decode json with
            | Ok report ->
                lines.Add $"report JSON: exit {output.ExitCode}, session {report.Session}, matched {report.Matched}"
            | Error issues -> lines.Add("report JSON does not decode: " + Issue.renderAtMost 3 issues)
        | Error e -> lines.Add $"report JSON does not parse: {e}"

        return List.ofSeq lines
    }

let scenarios: Scenario list = [ Scenario.task "replay-session" record ]
