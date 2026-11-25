/// The CodeAct strategy (T127, docs/modules/codeact.md) under Native AOT: a session on the session host
/// whose turns the strategy runs, with a scripted model, the script kernel in process, and the
/// workspace in memory. A hybrid turn reads, edits, runs a command, runs cells and finishes with
/// evidence; a cells-only turn replies in text twice and ends asking the user. The output is the turns'
/// endings, the requests' tools, the receipts, the context log, and the replay of the logs into the
/// requests sent.
module Fx.Aot.Scenarios.CodeAct

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Envd
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Rpc
open Fx.Store
open Fx.Store.Fakes
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Kernel
open Fx.Prompt
open Fx.Sdk
open Fx.Variants.Fakes
open Fx.Models.Fakes
open Fx.CodeAct

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

    interface IScopeWith<HandsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "HandsEnv"

let private os =
    if OperatingSystem.IsWindows() then Os.Windows
    elif OperatingSystem.IsMacOS() then Os.MacOs
    else Os.Linux

let private workspace = if OperatingSystem.IsWindows() then @"C:\work" else "/work"

/// The script kernel in this process: a fresh one for each launch.
let private kernel: Interpreter<HandsEnv> =
    {
        Language = "script"
        Prompt =
            fun surface ->
                "Cells are scripts. Bindings: "
                + String.concat ", " (surface.Bindings |> List.map (fun b -> b.Path))
        Start =
            fun start ->
                fun env ->
                    task {
                        let script = Kernel.ScriptKernel.Kernel ignore
                        let frames = ConnectionLimits.frames KernelOptions.defaults.Limits
                        let client, server = InProcess.pair (1 <<< 20) frames frames

                        let serverEnv: Rpc.TestService.Env =
                            {
                                Clock = SystemClock.clock
                                Scope = Scope.root
                            }

                        Eff.run serverEnv (Rpc.accept script.Service server) |> ignore

                        let terminate () =
                            server.Abort()
                            script.Kill()
                            Task.FromResult()

                        match!
                            Eff.run env (Kernel.connect KernelOptions.defaults start.Host start.Setup terminate client)
                        with
                        | Ok k -> return Ok k
                        | Error e -> return Error(string e)
                    }
    }

let private call (n: int) (name: string) (arguments: (string * Json) list) : ChatEvent list =
    ChatEvents.ofResponse
        {
            Id = None
            Model = Some "model"
            Parts =
                [
                    Part.ToolCall
                        {
                            Id = $"call-{n}"
                            Name = name
                            Arguments = Json.serialize (Json.Object arguments)
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

let private text (words: string) : ChatEvent list =
    ChatEvents.ofResponse
        {
            Id = None
            Model = Some "model"
            Parts = [ Part.text words ]
            Stop = StopReason.EndTurn
            Usage =
                { Usage.zero with
                    Input = 100
                    Output = 10
                }
        }

let private cell (n: int) (code: string) =
    call n "cell" [ "code", Json.String code ]

let private hash (request: ChatRequest) =
    let json = Json.serializeCanonical (ChatRequest.codec.Encode request)
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes json)).Substring(0, 16)

let private within (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 60.0)

/// Runs one session's turn with these replies and selections; gives the output lines.
let private session
    (name: string)
    (selections: (string * Json) list)
    (replies: ChatEvent list list)
    : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let fsClock = FakeClock(Instant.ofUnixMicroseconds 1_700_000_000_000_000L)

        let fs =
            InMemoryFs(fsClock.Clock, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())

        fs.AddDirectory workspace

        fs.AddText(
            (if OperatingSystem.IsWindows() then
                 @"C:\work\notes.md"
             else
                 "/work/notes.md"),
            "# Notes\nfirst\n"
        )

        let shell =
            ScriptedShell(fun command ->
                match command.Spec.Program with
                | Program.Script script -> Some(Ok(ScriptedShell.output 0 $"ran {script}\n" ""))
                | Exec(file, _) -> Some(Error(NotStarted(file, "not scripted"))))

        let queue = Collections.Generic.Queue<ChatEvent list>(replies)

        let script =
            ScriptedLlm(fun _ -> lock queue (fun () -> if queue.Count > 0 then Some(queue.Dequeue()) else None))

        let env: HostEnv =
            {
                Clock = SystemClock.clock
                Trace = Trace.none
                Llm = script.Llm
                Scope = Scope.root
            }

        let hands: HandsEnv =
            {
                Clock = SystemClock.clock
                FsRead = fs.FsRead
                Fs = fs.Fs
                FsOps = FsOpsOver.create fs.FsRead fs.Fs
                Shell = shell.Shell
                Scope = Scope.root
            }

        let variants =
            FakeVariants.create CodeActSlots.read (("model.role.main", Json.String "test/model") :: selections)

        let store = InMemoryEventLog<Payload>()
        let sessions = SessionLog.typed store.Log
        let contexts = ContextLog.typed store.Log

        let codeact =
            CodeAct.create
                {
                    Sessions = sessions
                    Contexts = contexts
                    Profile = fun _ -> Eff.ret variants
                    Hands =
                        fun _ ->
                            Eff.ret (
                                Ok
                                    {
                                        Env = hands
                                        Workspace = workspace
                                        Os = os
                                        CommandEnv = Inherit []
                                        Interpreter = kernel
                                        Guard = fun _ -> EffResult.ok ()
                                    }
                            )
                    Catalog = fun _ -> None
                }

        let settings =
            { SessionSettings.defaults with
                Kinds = Questions.kinds
            }

        let! host =
            Eff.run env (SessionHost.create sessions (InMemorySnapshots()).Store codeact.Runner settings)

        let alice = Attribution.create "user:alice" "scenario"

        let! _ =
            Eff.run env (SessionHost.start host "s1" (IdempotencyKey "start") alice "p1" None)
            |> within

        let! _ =
            Eff.run
                env
                (SessionHost.send host "s1" (IdempotencyKey "p1") alice (Command.Prompt("Do it", PromptMode.Steer)))
            |> within

        let events () =
            store.Events(Session.stream "s1")
            |> List.map (EventLog.decode SessionLog.schema)

        let mutable ending = None
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        while ending.IsNone && DateTime.UtcNow < deadline do
            ending <-
                events ()
                |> List.tryPick (fun r ->
                    match r.Event with
                    | Event.TurnEnded(1, ended, _) -> Some ended
                    | _ -> None)

            if ending.IsNone then
                do! Task.Delay 10

        let ended = ending |> Option.map string |> Option.defaultValue "did not end"

        lines.Add $"{name}: turn 1 {ended}"
        let requests = script.Requests
        lines.Add $"requests: {requests.Length}"

        lines.Add(
            "tools: "
            + (requests
               |> List.map (fun r -> r.Tools |> List.map (fun t -> t.Name) |> String.concat ",")
               |> List.distinct
               |> String.concat " | ")
        )

        let context =
            store.Events(ContextLog.stream "s1")
            |> List.map (EventLog.decode ContextLog.schema)

        for recorded in context do
            match recorded.Event with
            | ContextEvent.Returned receipt ->
                let shown = receipt.Pieces |> List.map Piece.render |> String.concat "\n"
                lines.Add(kv $"receipt {receipt.Step} {receipt.Name} error={receipt.IsError}" shown)
            | other -> lines.Add $"context {recorded.Seq}: {other}"

        // The logs replay into the requests sent.
        let! history = Eff.run env (ContextLog.load sessions contexts "s1") |> within

        let cfg =
            PromptSettings.config PromptSettings.defaults [] Exclusions.none ModelCaps.minimal.Caching

        let replies =
            history.Items
            |> List.indexed
            |> List.choose (fun (i, item) ->
                match item with
                | LogItem.Context(_, ContextEvent.Replied reply) -> Some(i, reply)
                | _ -> None)

        for (index, reply), sent in List.zip replies requests do
            let rendered =
                Render.render cfg (Transcript.ofItems (List.take index history.Items)) Live.none

            let replayed =
                Render.request
                    cfg
                    { sent with
                        System = []
                        Messages = []
                        Cache = { sent.Cache with Key = None }
                    }
                    { rendered with
                        Prompt =
                            { rendered.Prompt with
                                Suffix = reply.Shown
                            }
                    }

            lines.Add $"request {reply.Step}: {hash sent} replays: {hash replayed = hash sent}"

        let! state = Eff.run env (SessionHost.state host "s1") |> within

        for request in state.State.Requests do
            lines.Add(kv $"open {request.Request.Id} ({request.Request.Kind})" (Json.serialize request.Request.Payload))

        do! codeact.Close().WaitAsync(TimeSpan.FromSeconds 60.0)
        do! Eff.run env (SessionHost.stop host) |> within
        return List.ofSeq lines
    }

let private hybrid () : Task<string list> =
    session
        "hybrid"
        [ "agent.actionSurface", Json.String "hybrid" ]
        [
            call 1 "read" [ "path", Json.String "notes.md" ]
            call
                2
                "edit"
                [
                    "path", Json.String "notes.md"
                    "old", Json.String "first"
                    "new", Json.String "first\nsecond"
                ]
            call 3 "bash" [ "command", Json.String "make test" ]
            cell 4 "// intent: read it back\ncall n = fs.read [\"notes.md\"]\nn"
            cell
                5
                "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Added a line\",\"evidence\":[{\"kind\":\"command\",\"summary\":\"make test passed\"}]}"
        ]

let private textOnly () : Task<string list> =
    session "text-only" [] [ text "Which notes?"; text "Which notes should I change?" ]

let scenarios: Scenario list =
    [
        Scenario.task "codeact-hybrid" hybrid
        Scenario.task "codeact-text-only" textOnly
    ]
