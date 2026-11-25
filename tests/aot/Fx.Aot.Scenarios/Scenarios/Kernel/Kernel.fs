/// The kernel protocol (T116, docs/modules/kernel-protocol.md) under Native AOT: its codecs, the
/// kernel framing, the generated TypeScript declarations, and the host's `Kernel` client driving
/// the script kernel in process and as a child process (`kernel-peer`) through the shell capability.
module Fx.Aot.Scenarios.Kernel.Kernel

open System
open System.Text
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Rpc
open Fx.Store
open Fx.Kernel
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Show

type Env =
    {
        Clock: Clock
        Shell: Shell
        Ids: Ids
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IShell with
        member env.Shell = env.Shell

    interface IIds with
        member env.Ids = env.Ids

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = "Env"

let private env () =
    {
        Clock = SystemClock.clock
        Shell = LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory
        Ids = FakeIds(42UL, SystemClock.clock).Ids
        Scope = Scope.root
    }

let private parse (text: string) =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failwith (string e)

let private either (result: Result<'T, 'E>) =
    match result with
    | Ok value -> "ok " + string value
    | Error e -> "error " + string e

let private within (work: Task<'T>) =
    work.WaitAsync(TimeSpan.FromSeconds 60.0)

/// Every message type, encoded, decoded and encoded again.
let private codecs () : string list =
    let roundTrip (name: string) (codec: Codec<'T>) (value: 'T) =
        let json = codec.Encode value

        let back =
            match codec.Decode json with
            | Ok decoded -> Json.serialize (codec.Encode decoded)
            | Error issues -> "invalid: " + Issue.renderAtMost 3 issues

        [
            kv name (Json.serialize json)
            kv (name + " again") back
            kv (name + " preview") (codec.Preview Limits.defaults value).Text
        ]

    let handle =
        match HandleId.tryParse "h-00ff" with
        | Some id -> id
        | None -> failwith "handle id"

    let values =
        [
            Value.Inline(parse """{"a": [1, 2.50, "\u00e9"], "b": null}""")
            Value.Blob(
                {
                    Hash = BlobHash.ofBytes [| 7uy |]
                    Size = 1L
                },
                Preview.full "seven"
            )
            Value.Handle(handle, TypeRef "RunView", Preview.full "run 1")
        ]

    let failure =
        {
            Name = "Error"
            Message = "boom"
            Code = Some "failed"
            Location = Some { CellId = "c1"; Line = 2; Column = 3 }
            Stack = "Error: boom"
        }

    let result outcome =
        {
            CellId = "c1"
            Outcome = outcome
            Output =
                {
                    Text = "x\n"
                    Truncated = false
                    TotalChars = 2L
                }
            Declared = [ "x" ]
            Finished = true
            Notified = 2
            DurationMs = 3L
        }

    List.concat
        [
            for value in values do
                roundTrip "value" ValueCodecs.value value
            roundTrip "result" KernelProtocol.Codecs.cellResult (result (CellOutcome.RuntimeError failure))
            roundTrip "result" KernelProtocol.Codecs.cellResult (result (CellOutcome.TimedOut 5))
            roundTrip
                "result"
                KernelProtocol.Codecs.cellResult
                (result (
                    CellOutcome.SyntaxError
                        {
                            Message = "m"
                            Line = Some 1
                            Column = None
                            LineText = None
                        }
                ))
            for error in
                [
                    KernelError.Busy "c1"
                    KernelError.NotFound(ValueRef.Output "c2")
                    KernelError.TooLarge(9L, 1)
                ] do
                roundTrip "kernel error" KernelProtocol.Codecs.kernelError error
            for error in
                [
                    HostCallError.UnknownHandle handle
                    HostCallError.Failed("code", "message", Some(Json.int 1))
                ] do
                roundTrip "host error" KernelProtocol.Codecs.hostCallError error
            roundTrip
                "setup"
                KernelProtocol.Codecs.kernelSetup
                {
                    Versions = KernelProtocol.versions
                    Global = Product.kernelGlobal
                    Bindings = [ "shell.run" ]
                    ModuleBase = None
                    Limits = KernelProtocol.defaultLimits
                }
            roundTrip
                "output"
                KernelProtocol.Codecs.cellOutput
                {
                    CellId = Some "c1"
                    Kind = OutputKind.Error
                    Text = "late"
                    Late = true
                }
        ]

/// Lines with frames after stray bytes, zero bytes, invalid UTF-8 and CRLF.
let private framing () : string list =
    let frame = "{\"jsonrpc\":\"2.0\",\"method\":\"m\"}"

    let input =
        Array.concat
            [
                Encoding.UTF8.GetBytes("stray" + frame + "\r\n\n")
                [| 0uy; 0xffuy; 10uy |]
                Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",bad\n" + frame)
            ]

    match KernelFraming.split 1024 input with
    | Ok lines ->
        lines
        |> List.map (fun line ->
            escape (string line)
            + ": "
            + escape (
                match line with
                | KernelLine.Frame t
                | KernelLine.Stray t -> t
            ))
    | Error e -> [ "error " + string e ]

let private declarations () : string list =
    let text = KernelDeclarations.render ()
    let lines = text.Split '\n' |> List.ofArray
    $"{lines.Length} lines" :: lines

/// What the host side saw, in order.
type private Log() =
    let lines = Collections.Concurrent.ConcurrentQueue<string>()
    member _.Add(line: string) = lines.Enqueue line
    member _.Lines = List.ofSeq lines

    /// Completes when the cell `frozen` printed: its kernel is blocked, and only a kill stops it.
    member val Frozen = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    override _.ToString() = "Log"

let private host (log: Log) (handles: HandleTable<string>) : KernelHost<Env> =
    {
        HostCall =
            fun call ->
                fun env ->
                    task {
                        let cell = Option.defaultValue "-" call.CellId
                        log.Add $"host/call {call.Binding} from {cell}"

                        match call.Binding, call.Args with
                        | "test.add", [ Value.Inline(Json.Number a); Value.Inline(Json.Number b) ] ->
                            let sum =
                                (JsonNumber.tryToInt64 a |> Option.defaultValue 0L)
                                + (JsonNumber.tryToInt64 b |> Option.defaultValue 0L)

                            return Ok(Value.Inline(Json.int64 sum))
                        | "test.handle", [] ->
                            return!
                                (HandleTable.issue handles (TypeRef "Thing") (Preview.full "a thing") "thing"
                                 |> Eff.map Ok)
                                    env
                        | "test.inspect", [ Value.Handle(id, _, _) ] ->
                            let resolved = HandleTable.resolve handles id |> Result.isOk
                            return Ok(Value.Inline(Json.Bool resolved))
                        | "test.signal", [] -> return Ok(Value.Inline Json.Null)
                        | binding, _ -> return Error(HostCallError.UnknownBinding binding)
                    }
        Finish =
            fun finish ->
                let validated =
                    KernelProtocol.Finish.validate
                        (Codec.record {
                            let! summary = Codec.field "summary" id (Codec.string |> Codec.nonEmpty)
                            return summary
                        })
                        []
                        finish

                log.Add $"finish: {either validated}"
                Eff.ret (validated |> Result.map ignore)
        Output =
            fun output ->
                let cell = Option.defaultValue "-" output.CellId
                log.Add $"output {cell}: {escape output.Text}"

                if cell = "frozen" then
                    log.Frozen.TrySetResult() |> ignore

                Eff.ret ()
        Activity = fun activity -> Eff.ret (log.Add $"activity: {activity}")
    }

let private setup =
    {
        Versions = KernelProtocol.versions
        Global = Product.kernelGlobal
        Bindings = [ "test.add"; "test.handle"; "test.inspect"; "test.signal" ]
        ModuleBase = None
        Limits =
            { KernelProtocol.defaultLimits with
                OutputChars = 40
            }
    }

/// The conversation both scenarios have with the script kernel.
let private transcript (env: Env) (log: Log) (kernel: Kernel) (stray: bool) : Task<string list> =
    task {
        let n = ref 0

        let run (code: string) =
            task {
                n.Value <- n.Value + 1

                let! result =
                    Eff.run
                        env
                        (Kernel.run
                            kernel
                            {
                                CellId = $"c{n.Value}"
                                Code = code
                                Intent = None
                                TimeoutMs = Some 30000
                                Check = false
                            })
                    |> within

                match result with
                | Ok r ->
                    log.Add $"{r} declared [{String.Join(',', r.Declared)}] finished {r.Finished} notified {r.Notified}"
                    log.Add $"  output: {escape r.Output.Text}"
                | Error e -> log.Add $"c{n.Value}: {e}"
            }

        let info = Kernel.info kernel
        log.Add $"kernel: {info}"
        log.Add $"methods: {String.Join(',', Kernel.methods kernel)}"
        do! run "let x = {\"a\": [1, 2, 3]}\nprint hello\nx"
        do! run "print one\nprint two\nactivity run tests\nvalue 42"
        do! run (String.concat "\n" (List.replicate 10 "print 0123456789"))
        do! run "call s = test.add [2, 3]\ns"
        do! run "call h = test.handle []\ncall seen = test.inspect [\"$h\"]\nseen"
        do! run "throw broken"
        do! run "this is not a statement"
        do! run "finish {\"summary\": \"done\"}"
        do! run "finish {\"summary\": \"\"}"

        if stray then
            do! run "stray bytes without a newline\nvalue 1"
            log.Add $"stray: {escape (Kernel.stray kernel)}"

        let! encoded = Eff.run env (Kernel.encode kernel (ValueRef.Variable "x") None)
        log.Add $"encode x: {either encoded}"
        let! missing = Eff.run env (Kernel.encode kernel (ValueRef.Variable "nope") None)
        log.Add $"encode nope: {either missing}"
        let! big = Eff.run env (Kernel.encode kernel (ValueRef.Variable "x") (Some 5))
        log.Add $"encode x within 5 bytes: {either big}"
        let! preview = Eff.run env (Kernel.preview kernel (ValueRef.Output "c2") None)
        log.Add $"preview Out[c2]: {either preview}"

        let! decoded =
            Eff.run env (Kernel.decode kernel "y" (Value.Inline(parse "[true]")) None)

        log.Add $"decode y: {either decoded}"

        let! recalled =
            Eff.run env (Kernel.recall kernel "old" (Value.Inline(Json.String "restored")))

        log.Add $"recall old: {either recalled}"
        do! run "out r = old\nr"
        let! state = Eff.run env (Kernel.snapshot kernel)
        log.Add $"snapshot: {either state}"

        match state with
        | Ok state ->
            do! run "let x = 0"
            let! restored = Eff.run env (Kernel.restore kernel state)
            log.Add $"restore: {either restored}"
            do! run "x"
        | Error _ -> ()

        let running =
            Eff.run
                env
                (Kernel.run
                    kernel
                    {
                        CellId = "frozen"
                        Code = "call _s = test.signal []\nspin frozen now"
                        Intent = None
                        TimeoutMs = None
                        Check = false
                    })

        // `spin <text>` prints once the kernel is blocked: from then on, an interrupt must kill.
        do! log.Frozen.Task.WaitAsync(TimeSpan.FromSeconds 60.0)
        let! interruption = Eff.run env (Kernel.interrupt kernel) |> within
        log.Add $"interrupt: {interruption}"
        let! frozen = running |> within

        log.Add(
            match frozen with
            | Error(KernelFailure.Exited _) -> "frozen: exited"
            | other -> $"frozen: {either other}"
        )

        let! _ = Kernel.exited kernel |> within
        return log.Lines
    }

let private killOptions =
    { KernelOptions.defaults with
        ExitGrace = TimeSpan.FromSeconds 1.0
    }

let private inProcess () : Task<string list> =
    task {
        let env = env ()
        let log = Log()
        let script = ScriptKernel.Kernel ignore
        let frames = ConnectionLimits.frames KernelOptions.defaults.Limits
        let client, server = InProcess.pair 65536 frames frames

        let serverEnv: Fx.Aot.Scenarios.Rpc.TestService.Env =
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
            Eff.run env (Kernel.connect killOptions (host log (HandleTable())) setup terminate client)
            |> within
        with
        | Ok kernel -> return! transcript env log kernel false
        | Error e -> return [ "did not start: " + string e ]
    }

let private child () : Task<string list> =
    task {
        let env = env ()
        let log = Log()
        let info = Fx.Aot.Scenarios.Rpc.Peer.startInfo ()

        let args =
            info.ArgumentList
            |> List.ofSeq
            |> List.map (fun a -> if a = "rpc-peer" then "kernel-peer" else a)

        let basics =
            [
                "PATH"
                "DOTNET_ROOT"
                "DOTNET_ROOT_X64"
                "DOTNET_ROOT_ARM64"
                "SystemRoot"
                "windir"
            ]
            |> List.choose (fun name ->
                match Environment.GetEnvironmentVariable name with
                | null -> None
                | value -> Some(name, value))

        let launch =
            {
                Process =
                    {
                        Program = Exec(info.FileName, args)
                        Cwd = Some AppContext.BaseDirectory
                        Env = Exactly basics
                    }
                EnvDeny = []
                IgnoreEnvCase = OperatingSystem.IsWindows()
            }

        let! refused =
            Eff.run
                env
                (Kernel.start
                    killOptions
                    (host log (HandleTable()))
                    setup
                    { launch with
                        Process =
                            { launch.Process with
                                Env = Exactly(("NODE_OPTIONS", "--require=x") :: basics)
                            }
                    })

        log.Add(
            match refused with
            | Error e -> "unsafe launch: " + escape (string e)
            | Ok _ -> "unsafe launch started"
        )

        match!
            Eff.run env (Kernel.start killOptions (host log (HandleTable())) setup launch)
            |> within
        with
        | Ok kernel -> return! transcript env log kernel true
        | Error e -> return [ "did not start: " + string e ]
    }

let scenarios: Scenario list =
    [
        Scenario.lines "kernel-codecs" codecs
        Scenario.lines "kernel-framing" framing
        Scenario.lines "kernel-declarations" declarations
        Scenario.task "kernel-in-process" inProcess
        Scenario.task "kernel-child" child
    ]
