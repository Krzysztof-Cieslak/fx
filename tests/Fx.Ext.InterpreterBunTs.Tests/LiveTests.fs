/// The bun-ts interpreter end to end, in embedded mode: the pinned Bun (digest-checked; skipped
/// offline, required in CI), the kernel extracted from the provider's assets and launched by
/// `Interpreter.startBridge` through fx.runtime.bun's hardened launch, cells calling the core bindings
/// over the local filesystem and shell in a scratch workspace, `finish`, and `cell/check` against
/// the generated declarations.
module Fx.Ext.InterpreterBunTs.Tests.LiveTests

open System
open System.Collections.Concurrent
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd.Live
open Fx.Ext.InterpreterBunTs
open Fx.Interpreters
open Fx.Interpreters.Tests.Support
open Fx.Kernel
open Fx.Kernel.Tests
open Fx.Sdk

let private counter = ref 0

/// What a live test works with.
type Live =
    {
        Kernel: Kernel
        Env: Env
        Workspace: string
        Finished: ConcurrentQueue<Turn>
        mutable Next: int
    }

    member this.NextId() =
        this.Next <- this.Next + 1
        $"c{this.Next}"

    member this.Run(code: string, ?check: bool) : Task<CellResult> =
        task {
            let cell =
                {
                    CellId = this.NextId()
                    Code = code
                    Intent = Cell.intent code
                    TimeoutMs = Some 30000
                    Check = defaultArg check false
                }

            match! Eff.run this.Env (Kernel.run this.Kernel cell) with
            | Ok result -> return result
            | Error e -> return failtest $"{e}; stderr: {Kernel.stderr this.Kernel}"
        }

    member this.Check(code: string) : Task<Diagnostic list> =
        task {
            match! Eff.run this.Env (Kernel.check this.Kernel { CellId = this.NextId(); Code = code }) with
            | Ok checkedCell -> return checkedCell.Diagnostics
            | Error e -> return failtest $"{e}"
        }

    member this.Json(name: string) : Task<Json> =
        task {
            match! Eff.run this.Env (Kernel.encode this.Kernel (ValueRef.Variable name) None) with
            | Ok(Value.Inline json) -> return json
            | other -> return failtest $"{name}: {other}"
        }

    override this.ToString() = $"Live({this.Workspace})"

let private bun =
    lazy
        (task {
            match! PinnedBun.executable () with
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

/// Starts a kernel with the core bridge in a fresh workspace, runs the test, and shuts it down.
let private live (body: Live -> Task<unit>) : unit -> Task =
    fun () ->
        task {
            let! exe = bun.Force()
            let n = Threading.Interlocked.Increment counter

            let root =
                Repo.path [ "artifacts"; "kernel-bun-ts"; "live"; $"{Environment.ProcessId}-{n}" ]

            let workspace = Path.Combine(root, "workspace")
            Directory.CreateDirectory workspace |> ignore
            let clock = SystemClock.clock

            let env: Env =
                {
                    Clock = clock
                    FsRead = LocalFs.read
                    Fs = LocalFs.write
                    FsOps = LocalFsOps.create clock (SystemIds.create clock)
                    Shell = LocalShell.create (LocalShell.defaultShell ()) workspace
                    Scope = Scope.root
                }

            let finished = ConcurrentQueue<Turn>()
            let settings = CoreSettings.defaults workspace target.Os

            let bridge =
                Bridge.create (
                    Finishing.create turnCodec [ summaryEnsure ] (fun turn ->
                        finished.Enqueue turn
                        Eff.unit)
                )
                |> Bridge.add CoreBindings.groups (CoreBindings.all settings)

            let session: InterpreterSession =
                {
                    Provider = BunTs.provider
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
                    AssetsRoot = Repo.path [ "artifacts"; "kernel-bun-ts"; "assets" ]
                    Launch =
                        {
                            Target = target
                            Owned = OwnedDirs.under (Path.Combine(root, "owned"))
                            Workspace = Some workspace
                            ProjectTrusted = false
                            HostEnvironment = hostEnvironment ()
                        }
                }

            let start =
                Interpreter.startBridge
                    { KernelOptions.defaults with
                        ExitGrace = TimeSpan.FromSeconds 3.0
                    }
                    bridge
                    (fun _ -> Eff.unit)
                    (fun _ -> Eff.unit)
                    KernelProtocol.defaultLimits
                    session

            match! (Eff.run env start).WaitAsync(TimeSpan.FromSeconds 60.0) with
            | Error e -> failtest $"the kernel did not start: {e}"
            | Ok kernel ->
                try
                    do!
                        (body
                            {
                                Kernel = kernel
                                Env = env
                                Workspace = workspace
                                Finished = finished
                                Next = 0
                            })
                            .WaitAsync(TimeSpan.FromSeconds 120.0)
                finally
                    (Eff.run env (Kernel.close kernel)).Wait(TimeSpan.FromSeconds 30.0) |> ignore

                    try
                        Directory.Delete(root, true)
                    with _ ->
                        ()
        }

let private completed (result: CellResult) =
    match result.Outcome with
    | CellOutcome.Completed value -> value
    | other -> failtest $"cell {result.CellId}: {other}; output: {result.Output.Text}"

let private runtimeError (result: CellResult) =
    match result.Outcome with
    | CellOutcome.RuntimeError failure -> failure
    | other -> failtest $"cell {result.CellId} did not fail: {other}"

let private g = Product.kernelGlobal

[<Tests>]
let tests =
    testList
        "bun-ts live"
        [
            testTask "the kernel announces the interpreter's caps and handles cell/check" {
                do!
                    live
                        (fun l ->
                            task {
                                let info = Kernel.info l.Kernel
                                Expect.equal info.Language BunTs.provider.Language "language"
                                Expect.equal info.Caps BunTs.provider.Caps "caps"
                                Expect.isTrue (Kernel.supports l.Kernel "cell/check") "cell/check"
                            })
                        ()
            }

            testTask "cells write, read, list, stat and patch files, and run commands, in the workspace" {
                do!
                    live
                        (fun l ->
                            task {
                                let! r =
                                    l.Run(
                                        $"// intent: write and read a file\nconst w = await {g}.fs.write(\"src/a.txt\", \"one\\ntwo\\n\")\nconst r = await {g}.fs.read(\"src/a.txt\")\nr.text"
                                    )

                                completed r |> ignore

                                Expect.equal
                                    (File.ReadAllText(Path.Combine(l.Workspace, "src", "a.txt")))
                                    "one\ntwo\n"
                                    "written"

                                let! read = l.Json "r"

                                match read with
                                | Json.Object members ->
                                    Expect.equal
                                        (members |> List.find (fun (n, _) -> n = "path") |> snd)
                                        (Json.String "src/a.txt")
                                        "path"
                                | other -> failtest $"{other}"

                                let! _ = l.Run($"const names = (await {g}.fs.list(\"src\")).map(e => e.path)")
                                let! names = l.Json "names"
                                Expect.equal (canonical names) """["src/a.txt"]""" "listed"

                                let! _ = l.Run($"const kind = (await {g}.fs.stat(\"src\")).kind")
                                let! kind = l.Json "kind"
                                Expect.equal kind (Json.String "directory") "stat"

                                let! _ =
                                    l.Run(
                                        $"const patched = await {g}.fs.patch([{{ op: \"edit\", path: \"src/a.txt\", digest: r.digest, edits: [{{ line: 2, count: 1, lines: [\"TWO\"] }}] }}])"
                                    )

                                Expect.equal
                                    (File.ReadAllText(Path.Combine(l.Workspace, "src", "a.txt")))
                                    "one\nTWO\n"
                                    "patched"

                                let! _ =
                                    l.Run(
                                        $"const run = await {g}.shell.run(\"echo hello\")\nconst out = run.stdout.trim()"
                                    )

                                let! out = l.Json "out"
                                Expect.equal out (Json.String "hello") "the command's output"
                            })
                        ()
            }

            testTask "failures throw in the cell with their code" {
                do!
                    live
                        (fun l ->
                            task {
                                // A capability's failure is `failed`, with its own code in the message and data.
                                let! missing = l.Run($"await {g}.fs.read(\"missing.txt\")")
                                let failure = runtimeError missing
                                Expect.equal failure.Code (Some "failed") "a capability's failure"
                                Expect.stringContains failure.Message "notFound" "its code"

                                let! wrong = l.Run($"await ({g}.shell.run as any)(42)")
                                Expect.equal (runtimeError wrong).Code (Some "invalidArguments") "wrong arguments"
                            })
                        ()
            }

            testTask "finish is validated by the result's codec and checks" {
                do!
                    live
                        (fun l ->
                            task {
                                let! rejected = l.Run($"await {g}.finish({{ kind: \"done\", summary: \" \" }})")
                                let failure = runtimeError rejected
                                Expect.equal failure.Code (Some "finishRejected") "rejected"
                                Expect.stringContains failure.Message "the summary must say what was done" "the check"
                                Expect.isFalse rejected.Finished "not finished"

                                let! accepted =
                                    l.Run(
                                        $"await {g}.finish({{ kind: \"done\", summary: \"done it\", verify: undefined }})"
                                    )

                                completed accepted |> ignore
                                Expect.isTrue accepted.Finished "finished"
                                Expect.equal (List.ofSeq l.Finished) [ Turn.Done("done it", None) ] "handed on"
                            })
                        ()
            }

            testTask "cells are checked against the bridge's declarations and earlier cells" {
                do!
                    live
                        (fun l ->
                            task {
                                let! wrong = l.Check($"// intent: run\nawait {g}.shell.run(42)")

                                Expect.equal
                                    (wrong |> List.map (fun d -> d.Line, d.Code))
                                    [ 2, "TS2345" ]
                                    "a number for a string"

                                let! _ = l.Run("const items: { name: string }[] = [{ name: 'a' }]")

                                let! ok =
                                    l.Check(
                                        "const first: string = items[0]!.name\nconst p = await import('node:path')"
                                    )

                                Expect.isEmpty ok "earlier cells' names; modules as any"

                                let! refused = l.Run($"await {g}.fs.write(\"never.txt\", 1)", check = true)

                                match refused.Outcome with
                                | CellOutcome.TypeError diagnostics -> Expect.isNonEmpty diagnostics "diagnostics"
                                | other -> failtest $"{other}"

                                Expect.isFalse
                                    (File.Exists(Path.Combine(l.Workspace, "never.txt")))
                                    "the cell did not run"

                                let! fine = l.Run($"const n: number = (await {g}.fs.list()).length", check = true)
                                completed fine |> ignore
                            })
                        ()
            }

            testTask "values the host binds are declared to the checker; names a cell could not use are refused" {
                do!
                    live
                        (fun l ->
                            task {
                                match!
                                    Eff.run
                                        l.Env
                                        (Kernel.decode l.Kernel "notes" (Value.Inline(Json.String "a `b`")) None)
                                with
                                | Ok _ -> ()
                                | Error e -> failtest $"{e}"

                                let! fine = l.Check "// intent: use the text\nconst n: number = notes.length"
                                Expect.isEmpty fine "a string"
                                let! wrong = l.Check "const n: number = notes"
                                Expect.equal (wrong |> List.map (fun d -> d.Code)) [ "TS2322" ] "not a number"
                                let! ran = l.Run("notes.split(\"`\").length", check = true)
                                completed ran |> ignore

                                // Reserved words, the context's own globals and the bridge's global.
                                for name in [ "class"; "await"; "eval"; "console"; "Out"; "JSON"; "undefined"; g ] do
                                    match!
                                        Eff.run
                                            l.Env
                                            (Kernel.decode l.Kernel name (Value.Inline(Json.String "x")) None)
                                    with
                                    | Error(KernelFailure.Refused(KernelError.InvalidName refused)) ->
                                        Expect.equal refused name "the name"
                                    | other -> failtest $"{name}: {other}"

                                let! still = l.Run "console.log(JSON.stringify(Object.keys(Out)))"
                                completed still |> ignore
                                Expect.stringContains still.Output.Text "[" "console and Out still work"
                            })
                        ()
            }

            testTask "the prompt's examples type-check against the core bridge" {
                do!
                    live
                        (fun l ->
                            task {
                                // The examples use texts the cell tool binds (T140), bound here the same way.
                                for name in [ "notes"; "message" ] do
                                    match!
                                        Eff.run
                                            l.Env
                                            (Kernel.decode l.Kernel name (Value.Inline(Json.String "text")) None)
                                    with
                                    | Ok _ -> ()
                                    | Error e -> failtest $"{name}: {e}"

                                // Examples of the session's own bindings (`jobs`, T143) type-check in a
                                // session's kernel instead (tests/Fx.CodeAct.Tests/LiveTests.fs).
                                let core =
                                    BunTs.promptPack.Examples
                                    |> List.filter (fun example ->
                                        not (example.Code.Contains(Product.kernelGlobal + ".jobs.")))

                                for example in core do
                                    let! diagnostics = l.Check example.Code
                                    Expect.isEmpty (diagnostics |> List.map string) example.Title
                            })
                        ()
            }
        ]
