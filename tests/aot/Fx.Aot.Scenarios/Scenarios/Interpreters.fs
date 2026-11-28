/// Interpreters (T117, docs/modules/interpreters.md): the host bridge's binding table rendered into
/// the bun-ts declarations and prompt, calls dispatched to the core bindings over fakes (results,
/// typed failures, invalid arguments), `finish` validated, the provider's checks and programs, and
/// its assets extracted (and repaired) with hash checks, then a launch prepared. Nothing runs a
/// process; paths are the in-memory filesystem's, shown relative to the workspace.
module Fx.Aot.Scenarios.Interpreters

open System
open System.Text
open System.Threading.Tasks
open Fx
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Envd
open Fx.Ext.InterpreterBunTs
open Fx.Interpreters
open Fx.Kernel
open Fx.Sdk

type private Env =
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

    /// Where output cut by the bindings goes (T201a): one store for every environment of this type.
    static member val Stored = Fx.Store.Fakes.InMemoryBlobs()

    interface Fx.Store.IBlobs with
        member _.Blobs = Env.Stored.Blobs

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let private os =
    if OperatingSystem.IsWindows() then Os.Windows
    elif OperatingSystem.IsMacOS() then Os.MacOs
    else Os.Linux

let private rooted (segments: string list) =
    if OperatingSystem.IsWindows() then
        @"C:\" + String.Join(@"\", segments)
    else
        "/" + String.Join("/", segments)

let private workspace = rooted [ "work" ]

let private fakes () : Env * InMemoryFs =
    let clock = FakeClock(Instant.ofUnixMicroseconds 1_700_000_000_000_000L)

    let fs =
        InMemoryFs(clock.Clock, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())

    fs.AddDirectory workspace

    let shell =
        ScriptedShell(fun command ->
            match command.Spec.Program with
            | Program.Script "missing-tool" -> Some(Error(NotStarted("missing-tool", "not found")))
            | Program.Script script -> Some(Ok(ScriptedShell.output 0 $"ran {script}\n" ""))
            | Exec(file, _) -> Some(Error(NotStarted(file, "not scripted"))))

    {
        Clock = clock.Clock
        FsRead = fs.FsRead
        Fs = fs.Fs
        FsOps = FsOpsOver.create fs.FsRead fs.Fs
        // `shell.run` spawns its commands (T142): a program that is not there does not start.
        Shell =
            { shell.Shell with
                Spawn =
                    fun spec cancellation ->
                        match spec.Program with
                        | Program.Script "missing-tool" ->
                            Task.FromResult(Error(NotStarted("missing-tool", "not found")))
                        | _ -> shell.Shell.Spawn spec cancellation
            }
        Scope = Scope.root
    },
    fs

[<RequireQualifiedAccess>]
type private Turn =
    | Done of summary: string
    | Waiting of note: string

    override this.ToString() =
        match this with
        | Done summary -> "done: " + summary
        | Waiting note -> "waiting: " + note

let private turnCodec: Codec<Turn> =
    Codec.union "kind" {
        let! isDone = Codec.case1 "done" Turn.Done (Field.required "summary" Codec.string)

        and! waiting =
            Codec.case1 "waiting" Turn.Waiting (Field.required "note" Codec.string)

        return
            fun turn ->
                match turn with
                | Turn.Done summary -> isDone summary
                | Turn.Waiting note -> waiting note
    }
    |> Codec.named "TurnResult"

let private ensure (turn: Turn) : Issue list =
    match turn with
    | Turn.Done "" -> [ Issue.create "say what was done" ]
    | _ -> []

let private bridge (accepted: ResizeArray<string>) : Bridge<Env> =
    Bridge.create (
        Finishing.create turnCodec [ ensure ] (fun turn ->
            accepted.Add(string turn)
            Eff.unit)
    )
    |> Bridge.add CoreBindings.groups (CoreBindings.all (CoreSettings.defaults workspace os))

let private parse (text: string) =
    match Json.parse text with
    | Ok json -> Value.Inline json
    | Error e -> invalidOp (string e)

let private show (result: Result<Value, HostCallError>) : string =
    match result with
    | Ok(Value.Inline json) -> "ok " + Json.serializeCanonical json
    | Ok other -> $"ok {other}"
    | Error(HostCallError.Failed(code, _, data)) ->
        let data = data |> Option.map Json.serializeCanonical |> Option.defaultValue "-"

        $"failed {code} {data}"
    | Error(HostCallError.InvalidArguments issues) ->
        let rendered = issues |> List.map string |> String.concat "; "
        $"invalid arguments: {escape rendered}"
    | Error e -> $"error {e}"

/// The workspace as JSON writes it inside a string.
let private workspaceInJson = (Json.serialize (Json.String workspace)).Trim('"')

/// Typed failures carry absolute paths; the workspace differs between operating systems.
let private workspacePrefix (text: string) = text.Replace(workspaceInJson, "<work>")

let private dispatch (env: Env) (b: Bridge<Env>) (binding: string) (args: Value list) : Task<string> =
    task {
        let! result =
            Eff.run
                env
                (HostBinding.dispatch
                    b.Bindings
                    {
                        CellId = Some "c1"
                        Binding = binding
                        Args = args
                    })

        return $"{binding}: {show result |> workspacePrefix}"
    }

let bridgeScenario =
    Scenario.task "interpreters-bridge" (fun () ->
        task {
            let env, _ = fakes ()
            let accepted = ResizeArray()
            let b = bridge accepted
            let lines = ResizeArray<string>()
            let surface = Bridge.surface b
            lines.Add $"validate: {(Bridge.validate b).Length} issues"

            lines.Add
                $"setup bindings: {String.Join(',', (Bridge.setup b KernelProtocol.defaultLimits None).Bindings)}"

            lines.Add "declarations:"

            for line in (BunTs.declarations surface).Split '\n' do
                lines.Add("  " + line)

            let str (text: string) = Value.Inline(Json.String text)

            let calls =
                [
                    "fs.write", [ str "src/app.ts"; str "one\ntwo\n" ]
                    "fs.read", [ str "src/app.ts" ]
                    "fs.list", []
                    "fs.list", [ str "src" ]
                    "fs.stat", [ str "src/app.ts" ]
                    "fs.read", [ str "missing.ts" ]
                    "fs.patch",
                    [
                        parse
                            """[{"op": "edit", "path": "src/app.ts", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000", "edits": [{"line": 1, "count": 1, "lines": ["x"]}]}]"""
                    ]
                    "fs.patch",
                    [
                        parse
                            """[{"op": "edit", "path": "src/app.ts", "edits": [{"line": 2, "count": 1, "lines": ["TWO"]}]}, {"op": "create", "path": "b.ts", "text": "b\n"}]"""
                    ]
                    "fs.read", [ str "src/app.ts" ]
                    "fs.read", [ str "src/app.ts"; parse """{"lines": [2, 9]}""" ]
                    "fs.read", [ str "src/app.ts"; parse """{"lines": [2, 1]}""" ]
                    "fs.edit", [ str "src/app.ts"; parse """{"old": "TWO", "new": "two\nthree"}""" ]
                    "fs.edit",
                    [
                        str "src/app.ts"
                        parse """[{"old": "one", "new": "x"}, {"old": "o", "new": "0", "all": true}]"""
                    ]
                    "fs.edit", [ str "src/app.ts"; parse """{"old": "missing", "new": "x"}""" ]
                    "fs.editRange",
                    [
                        parse
                            """{"path": "src/app.ts", "start": 2, "end": 2, "text": "tw0\n", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000", "lines": 3}"""
                        str "2\r\n2b"
                    ]
                    "fs.editRange",
                    [
                        parse
                            """{"path": "src/app.ts", "start": 2, "end": 2, "text": "tw0\n", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000", "lines": 3}"""
                        str "x"
                    ]
                    "fs.read", [ str "src/app.ts" ]
                    "shell.run", [ str "make test"; parse """{"cwd": "src", "stdin": "in"}""" ]
                    "shell.run", [ str "missing-tool" ]
                    "shell.run", [ parse "42" ]
                    "fs.write", [ str "x"; parse "1"; parse "2" ]
                    "fs.nothing", []
                ]

            for binding, args in calls do
                let! line = dispatch env b binding args
                lines.Add line

            for result in
                [
                    """{"kind": "done", "summary": "migrated"}"""
                    """{"kind": "done", "summary": ""}"""
                    """{"kind": "dnoe"}"""
                    """{"kind": "waiting", "note": "CI"}"""
                ] do
                let! outcome =
                    Eff.run
                        env
                        (b.Finish.Validate
                            {
                                CellId = Some "c2"
                                Result = parse result
                            })

                match outcome with
                | Ok() -> lines.Add "finish accepted"
                | Error rejected ->
                    let issues = rejected.Issues |> List.map string |> String.concat "; "
                    lines.Add $"finish rejected: {escape issues}"

            lines.Add $"""accepted: {String.Join(" | ", accepted)}"""
            let intent = Cell.intent "// intent: rename it\nlet x = 1"
            lines.Add $"""intent: {intent |> Option.defaultValue "<none>"}"""
            return List.ofSeq lines
        })

let provider =
    Scenario.lines "interpreters-provider" (fun () ->
        let p = BunTs.provider

        [
            $"extension {BunTs.manifest.Id} {BunTs.manifest.Version} hash {Manifest.hash BunTs.manifest}"
            $"check: {(BunTs.check ()).Length} issues"
            $"interpreter {p}"
            $"""requires {String.Join(", ", p.Requires)}"""
            $"launch {p.Launch}"
            $"caps {Json.serializeCanonical (InterpreterCaps.codec.Encode p.Caps)}"
            $"assets {p.Assets.Length} files"

            for os, assets, bindings in [ Os.Linux, "/k", "/o/bindings"; Os.Windows, @"C:\k", @"C:\o\bindings" ] do
                match InterpreterProvider.program p os assets bindings with
                | Ok program -> $"""program on {os}: {String.Join(" ", program)}"""
                | Error issues -> $"program on {os}: {issues.Length} issues"

            let broken =
                { p with
                    Id = "Bad Id"
                    Assets = [ { Name = "../x"; Content = "" } ]
                    Requires = [ { Id = "bun"; Version = "newest" } ]
                }

            for issue in InterpreterProvider.validate broken do
                "  issue " + escape (string issue)

            let t = InterpreterProviders.contributionType
            $"contribution type {t.Name} ({t.Kind}, privileged {t.Privileged})"

            let surface: BindingSurface =
                {
                    Global = Product.kernelGlobal
                    Description = None
                    Groups = [ "shell", "Commands." ]
                    Bindings =
                        [
                            {
                                Path = "shell.run"
                                Description = Some "Runs a command."
                                Params =
                                    [
                                        {
                                            Name = "command"
                                            Description = None
                                            Schema = Codec.string.Schema
                                            Optional = false
                                        }
                                    ]
                                Returns = Codec.int.Schema
                            }
                        ]
                    Finish = turnCodec.Schema
                }

            let files = p.Bindings surface

            for file in files.Files do
                $"binding file {file.Name}: {file.Content.Length} chars"

            "prompt:"

            for line in (PromptPack.render p.PromptPack files).Split '\n' do
                "  " + line
        ])

let assets =
    Scenario.task "interpreters-assets" (fun () ->
        task {
            let env, fs = fakes ()
            let lines = ResizeArray<string>()
            let p = BunTs.provider
            let root = rooted [ "home"; "kernels" ]
            let digest = InterpreterProvider.assetsDigest p
            lines.Add $"digest has {digest.Length} chars, starts {digest.Substring(0, 7)}"

            let name (dir: string) =
                dir.Substring(dir.LastIndexOfAny([| '/'; '\\' |]) + 1)

            let! first = Eff.run env (Assets.ensure os root "launch1" p)

            match first with
            | Ok dir ->
                lines.Add $"extracted: {name dir = name (Assets.directory os root p)}"

                let! entries = Eff.run env (Fs.list root)

                match entries with
                | Ok entries -> lines.Add $"root: {entries.Length} entries"
                | Error e -> lines.Add $"root: {e}"

                let! listed = Eff.run env (Fs.list dir)

                match listed with
                | Ok files ->
                    for file in files do
                        let expected = p.Assets |> List.find (fun a -> a.Name = file.Name)

                        lines.Add $"  {file.Name} {file.Size = int64 (Encoding.UTF8.GetByteCount expected.Content)}"
                | Error e -> lines.Add $"list: {e}"

                let main = dir + (if os = Os.Windows then @"\" else "/") + "main.ts"
                fs.AddText(main, "process.exit(3)\n")
                let! again = Eff.run env (Assets.ensure os root "launch2" p)
                lines.Add $"after tampering: {again = first}"
                let! repaired = Eff.run env (Fs.readText main)

                match repaired with
                | Ok text ->
                    let original = (p.Assets |> List.find (fun a -> a.Name = "main.ts")).Content
                    lines.Add $"repaired: {text = original}"
                | Error e -> lines.Add $"repaired: {e}"
            | Error e -> lines.Add $"extract: {e}"

            let session: InterpreterSession =
                {
                    Provider = p
                    Runtime = Fx.Ext.RuntimeBun.RuntimeBun.runtime
                    Installed =
                        [
                            {
                                Id = "bun"
                                Version = Fx.Ext.RuntimeBun.RuntimeBun.bunComponent.Version
                                Dir = rooted [ "rt"; "bun" ]
                                Bin =
                                    [
                                        "bun", rooted [ "rt"; "bun"; "bun" + (if os = Os.Windows then ".exe" else "") ]
                                    ]
                                Env = []
                            }
                        ]
                    AssetsRoot = root
                    Launch =
                        {
                            Target = TargetTriple.create os Arch.X64 (if os = Os.Linux then Some Libc.Glibc else None)
                            Owned = OwnedDirs.under (rooted [ "owned"; "k1" ])
                            Workspace = Some workspace
                            ProjectTrusted = false
                            HostEnvironment =
                                [ "PATH", "/usr/bin"; "BUN_OPTIONS", "--preload=x.ts"; "TOKEN", "secret" ]
                        }
                }

            let surface = Bridge.surface (bridge (ResizeArray()))

            let! prepared = Eff.run env (Interpreter.prepare session surface)

            match prepared with
            | Ok launch ->
                match launch.Process.Program with
                | Exec(_, args) ->
                    let flags, program =
                        args
                        |> List.partition (fun a -> a.StartsWith "--" && not (a.StartsWith "--bindings"))

                    lines.Add $"launch: {flags.Length} runtime flags, then {program.Length} program arguments"
                | Program.Script _ -> lines.Add "launch: a script"

                match launch.Process.Env with
                | Exactly vars -> lines.Add $"""env: {String.Join(",", vars |> List.map fst)}"""
                | Inherit _ -> lines.Add "env: inherited"

                lines.Add $"hygiene: {(KernelLaunch.check launch).Length} issues"
            | Error e -> lines.Add $"prepare: {e}"

            let! missing =
                Eff.run env (Interpreter.prepare { session with Installed = [] } surface)

            match missing with
            | Ok _ -> lines.Add "without bun: prepared"
            | Error e -> lines.Add $"without bun: {e}"

            return List.ofSeq lines
        })

let scenarios = [ bridgeScenario; provider; assets ]
