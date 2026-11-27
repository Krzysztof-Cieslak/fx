/// CodeAct turns on the bun-ts kernel (`fx.interpreter.bun-ts`) on the pinned Bun, launched as the
/// product launches it: TypeScript cells, the generated bridge, `finish` validated by the session's
/// TurnResult codec. The pinned Bun is downloaded and digest-checked (skipped offline, required in CI).
module Fx.CodeAct.Tests.LiveTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Interpreters
open Fx.Sdk
open Fx.Sessions
open Fx.Prompt
open Fx.Environments
open Fx.Machines.Host
open Fx.CodeAct
open Fx.CodeAct.Tests.Support

let private bun =
    lazy
        (task {
            match! Fx.Kernel.Tests.PinnedBun.executable () with
            | Ok exe -> return exe
            | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
                return failtest $"the pinned Bun is not available: {reason}"
            | Error reason -> return skiptest $"the pinned Bun is not available: {reason}"
        })

let private target =
    match TargetTriple.ofRid RuntimeInformation.RuntimeIdentifier with
    | Some target -> target
    | None -> failwith $"no target for {RuntimeInformation.RuntimeIdentifier}"

let private hostEnvironment () =
    Environment.GetEnvironmentVariables()
    |> Seq.cast<Collections.DictionaryEntry>
    |> Seq.choose (fun entry ->
        match entry.Key, entry.Value with
        | (:? string as name), (:? string as value) -> Some(name, value)
        | _ -> None)
    |> List.ofSeq

/// The bun-ts interpreter for a fixture rooted at `root`: each launch gets its own owned directories.
let private bunTs (exe: string) (root: string) : Interpreter<HandsEnv> =
    Interpreter.ofProvider
        { KernelOptions.defaults with
            ExitGrace = TimeSpan.FromSeconds 3.0
        }
        (fun launch ->
            {
                Provider = Fx.Ext.InterpreterBunTs.BunTs.provider
                Runtime = Fx.Ext.RuntimeBun.RuntimeBun.runtime
                Installed =
                    [
                        {
                            Id = "bun"
                            Version = Fx.Ext.RuntimeBun.RuntimeBun.bunComponent.Version
                            Dir = Path.GetDirectoryName exe |> nonNull
                            Bin = [ "bun", exe ]
                            Env = []
                        }
                    ]
                AssetsRoot = Fx.Kernel.Tests.Repo.path [ "artifacts"; "kernel-bun-ts"; "assets" ]
                Launch =
                    {
                        Target = target
                        Owned = OwnedDirs.under (Path.Combine(root, "owned", string launch))
                        Workspace = Some(Path.Combine(root, "workspace"))
                        ProjectTrusted = false
                        HostEnvironment = hostEnvironment ()
                    }
            })

let private g = Fx.Product.kernelGlobal

/// Runs a test on a fixture with the bun-ts interpreter, and disposes it.
let private live (name: string) (selections: (string * Json) list) (body: Fixture -> Task<unit>) : unit -> Task =
    fun () ->
        task {
            let! exe = bun.Force()
            use f = createWith name selections (bunTs exe)
            do! body(f).WaitAsync(TimeSpan.FromSeconds 180.0)
        }

/// The pinned Bun as the component an environment installed.
let private bunComponent (exe: string) : InstalledComponent =
    {
        Id = "bun"
        Version = Fx.Ext.RuntimeBun.RuntimeBun.bunComponent.Version
        Dir = Path.GetDirectoryName exe |> nonNull
        Bin = [ "bun", exe ]
        Env = []
    }

/// `SANDBOX_TESTS=1`: CI provides a confining sandbox driver on Linux and macOS.
let private sandboxRequired () =
    Environment.GetEnvironmentVariable "SANDBOX_TESTS" = "1"

/// What sandboxed the last environment `localEnvironment` opened.
let mutable private lastSandbox: EnvSandboxInfo option = None

/// Hands in an environment of `fx.env.local` with envd embedded in this process, created on the
/// fixture's workspace and opened through the host's `IEnvs`, as the composition root does, on a
/// host that opted in to the sandbox (`sandbox.driver = auto`; off by default, T137); the session's
/// agent has these sandbox overrides.
let private localEnvironmentWith
    (overrides: SandboxOverrides)
    (exe: string)
    (root: string)
    (workspace: string)
    (cleanup: ResizeArray<unit -> Task>)
    =
    let wait (task: Task<'T>) =
        task.WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

    let dataDir = Path.Combine(root, "data")
    Directory.CreateDirectory dataDir |> ignore
    let log = InMemoryEventLog<EnvEvent>()

    let host =
        Fx.Environments.Tests.Local.startWith
            [ "sandbox.driver", Json.String Fx.Environments.SandboxSlots.Auto ]
            (Fx.Environments.Tests.Local.settings dataDir)
            log.Log

    cleanup.Add(fun () -> EnvsHost.stop host)
    let envs = EnvsHost.envs host

    let spec =
        { (EnvSpec.create "dev" Fx.Ext.EnvLocal.EnvLocal.Kind
           |> EnvSpec.withWorkspace workspace) with
            Transport = Some TransportKind.InProcess
        }

    let record =
        match wait (envs.Create spec Threading.CancellationToken.None) with
        | Ok record -> record
        | Error e -> failtest $"the environment was not created: {e}"

    let handle =
        match wait (envs.Open record.Id Threading.CancellationToken.None) with
        | Ok handle -> handle
        | Error e -> failtest $"the environment did not open: {e}"

    let plan: KernelPlan =
        {
            Provider = Fx.Ext.InterpreterBunTs.BunTs.provider
            Runtime = Fx.Ext.RuntimeBun.RuntimeBun.runtime
            Options =
                { KernelOptions.defaults with
                    ExitGrace = TimeSpan.FromSeconds 3.0
                }
            ProjectTrusted = false
            Launcher = HostEnvironment.scrub (hostEnvironment ())
            Guard = fun _ -> EffResult.ok []
            Sandbox = overrides
        }

    lastSandbox <- Some handle.Sandbox.Info

    EnvHands.ofHandle
        Fx.Effects.Live.SystemClock.clock
        Scope.root
        plan
        "s1"
        { handle with
            Installed = [ bunComponent exe ]
        }


/// Runs a test on a fixture whose hands are an environment of the local provider, and disposes it.
let private inEnvironmentWith
    (overrides: SandboxOverrides)
    (name: string)
    (body: Fixture -> Task<unit>)
    : unit -> Task =
    fun () ->
        task {
            let! exe = bun.Force()

            use f =
                createOn
                    name
                    []
                    (fun root workspace cleanup -> localEnvironmentWith overrides exe root workspace cleanup)
                    None
                    (fun _ -> None)

            do! body(f).WaitAsync(TimeSpan.FromSeconds 180.0)
        }

let private inEnvironment = inEnvironmentWith SandboxOverrides.none

let private seen (f: Fixture) =
    receipts f "s1" |> List.map text |> String.concat "\n---\n"

/// The coding task: make greeting.txt say "hello, world", check it, and finish with evidence.
let private finishCell =
    $"// intent: finish with the command's output as evidence\nawait {g}.finish({{ kind: \"done\", summary: \"The greeting says hello, world\", evidence: [{{ kind: \"command\", summary: `cat greeting.txt: ${{run.stdout.trim()}}` }}], verify: \"cat greeting.txt\" }})"

let private cellsOnly (f: Fixture) : Task<unit> =
    task {
        f.Write("greeting.txt", "hello\n")

        f.Model.AddAll
            [
                Say.cell $"// intent: read the greeting\nconst file = await {g}.fs.read(\"greeting.txt\")\nfile.text"
                Say.cell
                    $"// intent: change the greeting and check it\nawait {g}.fs.write(file.path, file.text.replace(\"hello\", \"hello, world\"))\nconst run = await {g}.shell.run(\"cat greeting.txt\")\nrun.stdout"
                Say.cell finishCell
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Make the greeting say hello, world"
        let! ending = (ended f "s1" 1).WaitAsync(TimeSpan.FromSeconds 180.0)

        match completed ending with
        | TurnResult.Done(summary, [ evidence ], Some "cat greeting.txt") ->
            Expect.equal summary "The greeting says hello, world" "summary"
            Expect.stringContains evidence.Summary "hello, world" "evidence from the kernel's values"
        | other -> failtest $"{other}; receipts: {seen f}"

        Expect.equal (f.Read "greeting.txt") "hello, world\n" "the file changed"
        let rs = receipts f "s1"
        Expect.isFalse (rs |> List.exists (fun r -> r.IsError)) $"no errors: {seen f}"
        Expect.stringContains (text rs[0]) "hello" "the file's text"

        match rs[0].Pieces with
        | [ Piece.Quote quote ] ->
            Expect.equal (quote.Sources |> List.map (fun s -> s.Path)) [ "greeting.txt" ] "provenance"
        | other -> failtest $"{other}"

        Expect.stringContains (requestText f.Model.Requests.Head) g "the bridge's declarations"
    }

let private hybrid (f: Fixture) : Task<unit> =
    task {
        f.Write("greeting.txt", "hello\n")
        let finishing = finishCell.Substring(finishCell.IndexOf '\n' + 1)

        f.Model.AddAll
            [
                Say.tool "read" [ "path", Json.String "greeting.txt" ]
                Say.tool
                    "edit"
                    [
                        "path", Json.String "greeting.txt"
                        "old", Json.String "hello"
                        "new", Json.String "hello, world"
                    ]
                Say.tool "bash" [ "command", Json.String "cat greeting.txt" ]
                Say.cell
                    $"// intent: check and finish\nconst run = await {g}.shell.run(\"cat greeting.txt\")\n{finishing}"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Make the greeting say hello, world"
        let! ending = (ended f "s1" 1).WaitAsync(TimeSpan.FromSeconds 180.0)

        match completed ending with
        | TurnResult.Done(_, [ evidence ], _) -> Expect.stringContains evidence.Summary "hello, world" "evidence"
        | other -> failtest $"{other}; receipts: {seen f}"

        Expect.equal (f.Read "greeting.txt") "hello, world\n" "the file changed"
        let rs = receipts f "s1"
        Expect.isFalse (rs |> List.exists (fun r -> r.IsError)) $"no errors: {seen f}"
    }

/// A reviewer's turn in a read-only sandbox (spec §7.1): its bindings refuse to write (the type
/// level), and so does the sandbox its commands run in, wherever a driver confines them.
let private reviewer (f: Fixture) : Task<unit> =
    task {
        f.Write("greeting.txt", "hello\n")

        f.Model.AddAll
            [
                Say.cell
                    $"// intent: try to change the workspace, then read it\nconst refused = await {g}.fs.write(\"greeting.txt\", \"changed\").then(() => \"written\", (e) => String(e.message))\nconst run = await {g}.shell.run(\"echo x > made.txt; cat greeting.txt\")\nconst file = await {g}.fs.read(\"greeting.txt\")\nfile.text.trim() + \" | \" + run.stdout.trim() + \" | \" + refused"
                Say.cell
                    $"// intent: finish\nawait {g}.finish({{ kind: \"done\", summary: \"Reviewed the greeting\", evidence: [{{ kind: \"command\", summary: \"read greeting.txt\" }}] }})"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Review the greeting"
        let! ending = (ended f "s1" 1).WaitAsync(TimeSpan.FromSeconds 180.0)

        match completed ending with
        | TurnResult.Done _ -> ()
        | other -> failtest $"{other}; receipts: {seen f}"

        Expect.equal (f.Read "greeting.txt") "hello\n" "the binding did not write"
        let first = receipts f "s1" |> List.head |> text
        Expect.stringContains first "sandbox" $"the write was refused by the sandbox: {seen f}"
        Expect.stringContains first "hello" "reading works"

        match lastSandbox with
        | Some sandbox when sandbox.Confines ->
            Expect.isFalse (File.Exists(f.File "made.txt")) "the command could not write"
        | Some _ when sandboxRequired () && not (OperatingSystem.IsWindows()) ->
            failtest "a driver must confine the environment's processes here"
        | _ -> ()
    }

let private rejected (f: Fixture) : Task<unit> =
    task {
        f.Model.AddAll
            [
                Say.cell $"// intent: finish\nawait {g}.finish({{ kind: \"done\", summary: \" \" }})"
                Say.cell $"// intent: finish\nawait {g}.finish({{ kind: \"done\", summary: \"done it\" }})"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Finish"
        let! ending = (ended f "s1" 1).WaitAsync(TimeSpan.FromSeconds 180.0)
        Expect.equal (completed ending) (TurnResult.Done("done it", [], None)) "accepted"
        let rs = receipts f "s1"
        Expect.isTrue rs[0].IsError "rejected"
        Expect.stringContains (text rs[0]) "the summary must say what was done" "the reasons"
    }

/// The host stops while a TypeScript cell waits: the decision shows what the cell printed; after the
/// answer, the next cell reads the value an earlier cell returned back from the journal.
let private crashed (f: Fixture) : Task<unit> =
    task {
        f.Model.AddAll
            [
                Say.cell "// intent: count the rows\n({ rows: 3 })"
                Say.cell "// intent: wait for the deploy\nconsole.log(\"deploying\")\nawait new Promise(() => {})"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Deploy"

        do!
            until "the waiting cell's output is journaled" (fun () ->
                journal f "s1"
                |> List.exists (fun r ->
                    match r.Event with
                    | StepEvent.Printed printed -> printed.Text.Contains "deploying"
                    | _ -> false))

        do! restart f
        let! _ = state f "s1"
        let! request = opened f "s1" "unknown-t1-s2-1"

        match Recovery.payload.Decode request.Request.Payload with
        | Ok card -> Expect.stringContains card.Evidence.Output "deploying" "what the cell printed"
        | Error issues -> failtest (Fx.Contracts.Issue.render issues)

        f.Model.AddAll
            [
                Say.cell
                    $"// intent: read the rows back\nconst r = await {g}.recall(\"t1-s1-1\") as {{ rows: number }}\n[(Out[\"t1-s1-1\"] as {{ rows: number }}).rows, r.rows]"
                Say.cell $"// intent: finish\nawait {g}.finish({{ kind: \"done\", summary: \"deployed\" }})"
            ]

        let! _ =
            send
                f
                "s1"
                "a1"
                (Fx.Sessions.Command.Answer(
                    "unknown-t1-s2-1",
                    RequestKind.answer Recovery.kind { Option = "completed"; Note = None }
                ))

        let! ending = (ended f "s1" 1).WaitAsync(TimeSpan.FromSeconds 180.0)
        Expect.equal (completed ending) (TurnResult.Done("deployed", [], None)) $"finished: {seen f}"
        let read = receipts f "s1" |> List.find (fun r -> r.Step = 3)
        Expect.stringContains (text read) "the host restarted" "a new kernel"
        Expect.stringContains (text read) "[3, 3]" $"Out and recall give the value back: {seen f}"
    }

[<Tests>]
let tests =
    testList
        "CodeAct on bun-ts"
        [
            testTask "cells-only: TypeScript cells read, edit, run a command and finish with evidence" {
                do! live "live-cells" [] cellsOnly ()
            }

            testTask "hybrid: read, edit and bash, then a TypeScript cell finishes" {
                do! live "live-hybrid" [ "agent.actionSurface", Json.String "hybrid" ] hybrid ()
            }

            testTask "cells-only, in an environment of the local provider opened through IEnvs" {
                do! inEnvironment "live-env" cellsOnly ()

                match lastSandbox with
                | Some sandbox when sandboxRequired () && not (OperatingSystem.IsWindows()) ->
                    Expect.isTrue sandbox.Confines $"the kernel ran in the sandbox: {sandbox}"
                | _ -> ()
            }

            testTask "a reviewer's read-only sandbox, in an environment: bindings and commands cannot write" {
                do! inEnvironmentWith SandboxOverrides.readOnly "live-reviewer" reviewer ()
            }

            testTask "a rejected finish throws in the TypeScript cell, and the next one is accepted" {
                do! live "live-finish" [] rejected ()
            }

            testTask "a host stopped during a TypeScript cell: a decision with its output, then values back" {
                do! live "live-crash" [] crashed ()
            }
        ]
