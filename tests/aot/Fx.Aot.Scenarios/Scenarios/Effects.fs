/// Fx.Effects: the resumable eff/effResult state machines (static and dynamic), structured
/// concurrency on the fake clock, the fakes, record/replay, and the real interpreters.
module Fx.Aot.Scenarios.Effects

#nowarn "3511" // The dynamic-path block below is deliberately not statically compilable.

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Effects.Replay

type Env =
    {
        Clock: Clock
        Ids: Ids
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        Secrets: Secrets
        Trace: Trace
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IShell with
        member env.Shell = env.Shell

    interface ISecrets with
        member env.Secrets = env.Secrets

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

[<Struct>]
type ClockOnly =
    val Clock: Clock
    new(clock) = { Clock = clock }

    interface IClock with
        member env.Clock = env.Clock

type Fakes =
    {
        Env: Env
        Clock: FakeClock
        Fs: InMemoryFs
        Shell: ScriptedShell
        Trace: TraceCollector
    }

let private root = if OperatingSystem.IsWindows() then @"C:\" else "/"

let private path (segments: string list) =
    Path.Combine(Array.ofList (root :: segments))

let private fakes () =
    let clock = FakeClock.Create()
    let fs = InMemoryFs(clock.Clock, false)

    let shell =
        ScriptedShell(fun command ->
            match command.Spec.Program with
            | Exec("git", [ "status" ]) -> Some(ScriptedShell.ok "clean\n")
            | Exec("cat", []) -> Some(Ok(ScriptedShell.output 0 (Encoding.UTF8.GetString command.Stdin) ""))
            | _ -> None)

    let trace = TraceCollector clock.Clock

    {
        Env =
            {
                Clock = clock.Clock
                Ids = FakeIds(1UL, clock.Clock).Ids
                FsRead = fs.FsRead
                Fs = fs.Fs
                Shell = shell.Shell
                Secrets = InMemorySecrets.create [ "api/key", "k-123" ]
                Trace = trace.Trace
                Scope = Scope.root
            }
        Clock = clock
        Fs = fs
        Shell = shell
        Trace = trace
    }

let private run (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()

let private later (value: 'T) : Eff<'R, 'T> =
    Eff.ofTask (fun () ->
        task {
            do! Task.Yield()
            return value
        })

let private failing (message: string) : Eff<'R, 'T> =
    Eff.ofTask (fun () ->
        task {
            do! Task.Yield()
            return invalidOp message
        })

let private seconds (n: float) = TimeSpan.FromSeconds n

let private showResult (result: Result<'T, 'E>) : string =
    match result with
    | Ok value -> $"Ok {value}"
    | Error e -> $"Error {e}"

let private showOption (option: 'T option) : string =
    match option with
    | Some value -> $"Some {value}"
    | None -> "None"

/// The outcome of a task, without messages from the runtime.
let private outcome (task: Task<'T>) : string =
    try
        $"ok {task.GetAwaiter().GetResult()}"
    with e ->
        $"threw {e.GetType().Name}"

type private Resource(log: ResizeArray<string>) =
    interface IDisposable with
        member _.Dispose() = log.Add "disposed"

let builders =
    Scenario.lines "eff-builders" (fun () ->
        let env = (fakes ()).Env
        let log = ResizeArray<string>()

        let program (n: int) =
            eff {
                use _ = new Resource(log)
                let mutable total = 0

                for i in 1..n do
                    let! v = later i
                    total <- total + v

                while total < 100 do
                    let! one = Eff.ret 1
                    total <- total + one

                let! caught =
                    eff {
                        try
                            let! (_: int) = failing "inner"
                            return "no"
                        with :? InvalidOperationException as e ->
                            return e.Message
                    }

                try
                    match! later (Some total) with
                    | Some t -> return $"{t} {caught}"
                    | None -> return "none"
                finally
                    log.Add "finally"
            }

        let dynamicProgram =
            eff {
                let rec countdown n =
                    if n = 0 then "dynamic" else countdown (n - 1)

                let! a = later 1

                try
                    let! (_: int) = failing "x"
                    return "no"
                with _ ->
                    return $"{countdown 3} {a}"
            }

        let generic (value: 'T) : Eff<ClockOnly, string> =
            eff {
                let! now = Clock.now
                let! v = later value
                return $"{typeof<'T>.Name} {v} {now}"
            }

        let clockOnly = ClockOnly(FakeClock.Create().Clock)

        [
            run env (program 3)
            run env (program 20)
            String.concat "," log
            run env dynamicProgram
            run clockOnly (generic 1)
            run clockOnly (generic "s")
            run clockOnly (generic (Some 2.5))
            outcome (Eff.run env (eff { return! failing "outer" }))
            outcome (Eff.run () (eff { return! ((fun () -> invalidOp "synchronous"): Eff<unit, int>) }))
        ])

let results =
    Scenario.lines "eff-result" (fun () ->
        let env = (fakes ()).Env
        let log = ResizeArray<string>()

        let program (failAt: int) : Eff<Env, Result<int, string>> =
            effResult {
                let! a = later 1
                let! b = Ok 2
                let! c = Eff.map Ok (later 3)
                let mutable i = 0

                while i < 5 do
                    let! v =
                        if i = failAt then
                            EffResult.error $"stopped at {i}"
                        else
                            EffResult.ok i

                    log.Add $"{v}"
                    i <- i + 1

                return a + b + c + i
            }

        [
            showResult (run env (program 99))
            showResult (run env (program 2))
            String.concat "," log
            showResult (
                run env (EffResult.traverse (fun x -> if x < 3 then EffResult.ok x else EffResult.error x) [ 1..5 ])
            )
        ])

let concurrency =
    Scenario.lines "eff-concurrency" (fun () ->
        let f = fakes ()
        let log = ResizeArray<string>()

        let sleeper name (d: float) (value: 'T) : Eff<Env, 'T> =
            Trace.span
                name
                []
                (eff {
                    try
                        do! Clock.sleep (seconds d)
                        lock log (fun () -> log.Add $"{name} done")
                        return value
                    with :? OperationCanceledException as e ->
                        lock log (fun () -> log.Add $"{name} cancelled")
                        return raise e
                })

        let all =
            Eff.run f.Env (Eff.all [ sleeper "a" 3.0 1; sleeper "b" 1.0 2; sleeper "c" 2.0 3 ])

        let race =
            Eff.run f.Env (Eff.race [ sleeper "slow" 9.0 "slow"; sleeper "fast" 1.0 "fast" ])

        let timedOut = Eff.run f.Env (Eff.timeout (seconds 2.0) (sleeper "late" 5.0 0))
        let inTime = Eff.run f.Env (Eff.timeout (seconds 2.0) (sleeper "early" 1.0 7))

        let failed =
            Eff.run
                f.Env
                (Eff.all
                    [
                        sleeper "x" 5.0 0
                        // Fails inline when the delay fires: a thread-pool hop (e.g. Task.Yield) would race
                        // with the next AdvanceToNextDelay.
                        Eff.bind (fun () -> invalidOp "boom") (Clock.sleep (seconds 1.0))
                    ])

        while f.Clock.AdvanceToNextDelay() do
            ()

        let results =
            [
                outcome all
                outcome race
                outcome (timedOut.ContinueWith(fun (t: Task<int option>) -> showOption t.Result))
                outcome (inTime.ContinueWith(fun (t: Task<int option>) -> showOption t.Result))
                outcome failed
            ]

        results
        @ (log |> Seq.sort |> List.ofSeq)
        @ (f.Trace.Records |> List.map string |> List.sort)
        @ [ $"pending delays {f.Clock.PendingDelays}" ])

let fakesAndReplay =
    Scenario.lines "eff-fakes" (fun () ->
        let f = fakes ()
        let env = f.Env

        let fsProgram =
            effResult {
                do! Fs.createDirectory (path [ "work"; "src" ])
                do! Fs.writeText (path [ "work"; "src"; "b.fs" ]) "let b = 2"
                do! Fs.writeText (path [ "work"; "a.txt" ]) "zażółć"
                do! Fs.move (path [ "work"; "a.txt" ]) (path [ "work"; "src"; "a.txt" ])
                let! entries = Fs.list (path [ "work"; "src" ])
                let! text = Fs.readText (path [ "work"; "src"; "a.txt" ])
                return entries |> List.map (fun e -> $"{e.Name} {e.Kind} {e.Size}"), text
            }

        let shellProgram =
            effResult {
                let! status = Shell.exec "git" [ "status" ]
                let! cat = Shell.run (Command.exec "cat" [] |> Command.withStdinText "piped")
                return Output.stdoutText status + Output.stdoutText cat
            }

        let secret =
            match SecretRef.tryParse "secret:api/key" with
            | Ok reference -> run env (Secrets.resolve reference)
            | Error e -> failwith e

        let journal = Journal()
        let recordClock = FakeClock.Create()

        let recordEnv =
            { env with
                Clock = Record.clock journal recordClock.Clock
                Ids = Record.ids journal (FakeIds(9UL, recordClock.Clock)).Ids
            }

        let replayed =
            eff {
                let! now = Clock.now
                let! id = Ids.newId
                let! bytes = Ids.randomBytes 4
                return $"{now} {id} {Convert.ToHexString bytes}"
            }

        let recorded = run recordEnv replayed
        let cursor = ReplayCursor journal.Entries

        let replayEnv =
            { env with
                Clock = Replay.clock cursor
                Ids = Replay.ids cursor
            }

        let missing = run env (Fs.readText (path [ "missing" ]))

        [
            yield! List.init 3 (fun _ -> string (env.Ids.NewId()))
            Convert.ToHexString(env.Ids.RandomBytes 8)
            $"{Id.tryParse (string (env.Ids.NewId())) |> Option.isSome}"
            showResult (run env fsProgram)
            showResult missing
            showResult (run env shellProgram)
            showResult secret
            recorded
            run replayEnv replayed
            $"replay finished {cursor.Finished}"
            yield! journal.Entries |> List.map string
        ]
        |> List.map Show.escape)

let private windows = OperatingSystem.IsWindows()

let live =
    Scenario.task "eff-live" (fun () ->
        task {
            let dir =
                Path.Combine(Path.GetTempPath(), "aot-effects-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory dir |> ignore

            try
                let env =
                    {
                        Clock = SystemClock.clock
                        Ids = SystemIds.create SystemClock.clock
                        FsRead = LocalFs.read
                        Fs = LocalFs.write
                        Shell = LocalShell.create (LocalShell.defaultShell ()) dir
                        Secrets = InMemorySecrets.create []
                        Trace = Trace.none
                        Scope = Scope.root
                    }

                let fsProgram =
                    effResult {
                        do! Fs.createDirectory (Path.Combine(dir, "sub"))
                        do! Fs.writeText (Path.Combine(dir, "sub", "f.txt")) "content"
                        let! entries = Fs.list (Path.Combine(dir, "sub"))
                        let! text = Fs.readText (Path.Combine(dir, "sub", "f.txt"))
                        return entries |> List.map (fun e -> $"{e.Name} {e.Kind} {e.Size}"), text
                    }

                let script =
                    if windows then
                        "Write-Output hello; [Console]::Error.WriteLine('err'); exit 3"
                    else
                        "echo hello; echo err 1>&2; exit 3"

                let! fs = Eff.run env fsProgram
                let! shell = Eff.run env (Shell.run (Command.script script))
                let! missing = Eff.run env (Shell.exec "no-such-program-for-aot-effects" [])

                let! timedOut =
                    Eff.run env (Eff.timeout (TimeSpan.FromMilliseconds 50.0) (Clock.sleep (TimeSpan.FromHours 1.0)))

                let monotonic =
                    List.init 1000 (fun _ -> SystemClock.clock.Monotonic())
                    |> List.pairwise
                    |> List.forall (fun (a, b) -> a <= b)

                let ids = List.init 1000 (fun _ -> env.Ids.NewId())

                return
                    [
                        showResult fs
                        match shell with
                        | Ok output ->
                            let clean (text: string) =
                                text.Replace("\r\n", "\n") |> Show.escape

                            $"exit {output.ExitCode} out {clean (Output.stdoutText output)} err {clean (Output.stderrText output)}"
                        | Error e -> $"{e}"
                        match missing with
                        | Error(NotStarted(program, _)) -> $"not started {program}"
                        | other -> $"unexpected {other}"
                        $"timed out {timedOut.IsNone}"
                        $"monotonic {monotonic}"
                        $"ids increase {ids |> List.pairwise |> List.forall (fun (a, b) -> a < b)}"
                    ]
            finally
                try
                    Directory.Delete(dir, true)
                with _ ->
                    ()
        })

let scenarios = [ builders; results; concurrency; fakesAndReplay; live ]
