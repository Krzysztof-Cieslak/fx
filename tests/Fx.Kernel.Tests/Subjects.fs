/// The kernels the conformance suite runs against: the F# script kernel in process and as a child
/// process (`Fx.Aot.Scenarios kernel-peer`), and the bun-ts kernel (`fx.interpreter.bun-ts`, from
/// `packages/kernel-bun-ts`) on the pinned Bun, launched as the product launches it.
module Fx.Kernel.Tests.Subjects

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Rpc
open Fx.Kernel
open Fx.Kernel.Tests.KernelConformance
open Fx.Aot.Scenarios.Kernel
open Fx.Interpreters
open Fx.Sdk

let private quote (text: string) = Json.quote text

/// The script kernel's language (see `ScriptKernel`).
let scriptSnippets: Snippets =
    {
        Assign = fun name json -> $"let {name} = {Json.serialize json}"
        Read = id
        Literal = fun json -> "value " + Json.serialize json
        Print = fun text -> "print " + text
        PrintLines = fun count text -> String.concat "\n" (List.replicate count ("print " + text))
        Call = fun name binding args -> $"call {name} = {binding} {Json.serialize (Json.Array args)}"
        CallWith = fun name binding variable -> $"call {name} = {binding} [\"${variable}\"]"
        Finish = fun json -> "finish " + Json.serialize json
        Throw = fun message -> "throw " + message
        SleepForever = "sleep"
        BusyLoop = "loop"
        Freeze = "spin"
        LateOutput = fun text -> "later " + text
        Activity = Some(fun kind title -> $"activity {kind} {title}")
        ReadEnv = fun name variable -> $"env {name} = {variable}"
        Stray = Some(fun text -> "stray " + text)
        ReadOut = fun name cellId -> $"out {name} = {cellId}"
        Unencodable = None
        Unparsable = "this is not a statement"
        Statements = String.concat "\n"
    }

/// TypeScript, as the reference kernel runs it.
let typeScriptSnippets: Snippets =
    let bridge = Product.kernelGlobal

    let args (values: Json list) =
        String.Join(", ", values |> List.map Json.serialize)

    {
        Assign = fun name json -> $"let {name} = ({Json.serialize json})"
        Read = id
        Literal = fun json -> $"({Json.serialize json})"
        Print = fun text -> $"console.log({quote text})"
        PrintLines = fun count text -> $"for (let i = 0; i < {count}; i++) console.log({quote text})"
        Call = fun name binding values -> $"let {name} = await {bridge}.{binding}({args values})"
        CallWith = fun name binding variable -> $"let {name} = await {bridge}.{binding}({variable})"
        Finish = fun json -> $"await {bridge}.finish({Json.serialize json})"
        Throw = fun message -> $"throw new Error({quote message})"
        SleepForever = "await new Promise(() => {})"
        BusyLoop = "while (true) {}"
        // After an await (it follows a host call): the VM's timeout no longer applies, and the
        // event loop never reads cell/cancel.
        Freeze = "while (true) {}"
        LateOutput = fun text -> $"setTimeout(() => console.log({quote text}), 50)"
        Activity = Some(fun kind title -> $"{bridge}.activity({quote kind}, {quote title})")
        ReadEnv = fun name variable -> $"let {name} = (await import(\"node:process\")).env[{quote variable}] ?? null"
        Stray = Some(fun text -> $"(await import(\"node:fs\")).writeSync(1, {quote text})")
        ReadOut = fun name cellId -> $"let {name} = Out[{quote cellId}]"
        Unencodable = Some(fun name -> $"let {name} = () => 1")
        Unparsable = "let = ;"
        Statements = String.concat "\n"
    }

/// What a .NET program needs from the host's environment to start (the runtime's location, and
/// the Windows basics); nothing else of the host reaches a kernel.
let private hostBasics () : (string * string) list =
    let names =
        [
            "PATH"
            "DOTNET_ROOT"
            "DOTNET_ROOT_X64"
            "DOTNET_ROOT_ARM64"
            "SystemRoot"
            "SystemDrive"
            "windir"
            "ComSpec"
            "PATHEXT"
            "NUMBER_OF_PROCESSORS"
            "PROCESSOR_ARCHITECTURE"
            "OS"
            "LANG"
            "TZ"
        ]

    names
    |> List.choose (fun name ->
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | value -> Some(name, value))

let private release (env: Env) (kernel: Kernel) (extra: unit -> unit) : unit -> Task =
    fun () ->
        task {
            try
                let! _ = Eff.run env (Kernel.close kernel) |> within
                ()
            finally
                extra ()
        }

/// The script kernel in this process, over the in-process transport.
let scriptInProcess: KernelSubject =
    {
        Name = "script kernel (in process)"
        Snippets = scriptSnippets
        IsProcess = false
        StartFromLauncher = None
        Start =
            fun env host setup _ ->
                task {
                    let kernel = ScriptKernel.Kernel ignore
                    let frames = ConnectionLimits.frames KernelOptions.defaults.Limits
                    let clientEnd, serverEnd = InProcess.pair (1 <<< 20) frames frames

                    let serverEnv: Fx.Aot.Scenarios.Rpc.TestService.Env =
                        {
                            Clock = SystemClock.clock
                            Scope = Scope.root
                        }

                    Eff.run serverEnv (Rpc.accept kernel.Service serverEnd) |> ignore

                    // As a killed process: nothing it does after this reaches the host.
                    let stop () =
                        serverEnd.Abort()
                        kernel.Kill()

                    let terminate () =
                        stop ()
                        Task.FromResult()

                    match!
                        Eff.run env (Kernel.connect KernelOptions.defaults host setup terminate clientEnd)
                        |> within
                    with
                    | Ok k ->
                        return
                            Ok
                                {
                                    Kernel = k
                                    Dispose = release env k stop
                                }
                    | Error e ->
                        stop ()
                        return Error e
                }
    }

/// The script kernel as a child process (`Fx.Aot.Scenarios kernel-peer`, its apphost next to the
/// tests), started through the shell capability.
let scriptProcess: KernelSubject =
    {
        Name = "script kernel (process)"
        Snippets = scriptSnippets
        IsProcess = true
        StartFromLauncher = None
        Start =
            fun env host setup variables ->
                task {
                    let info = Fx.Aot.Scenarios.Rpc.Peer.startInfo ()

                    let args =
                        info.ArgumentList
                        |> Seq.map id
                        |> List.ofSeq
                        |> List.map (fun a -> if a = "rpc-peer" then "kernel-peer" else a)

                    let launch =
                        {
                            Process =
                                {
                                    Program = Exec(info.FileName, args)
                                    Cwd = Some AppContext.BaseDirectory
                                    Env = Exactly(hostBasics () @ variables)
                                }
                            EnvDeny = []
                            IgnoreEnvCase = OperatingSystem.IsWindows()
                        }

                    match! Eff.run env (Kernel.start KernelOptions.defaults host setup launch) |> within with
                    | Ok k ->
                        return
                            Ok
                                {
                                    Kernel = k
                                    Dispose = release env k ignore
                                }
                    | Error e -> return Error e
                }
    }

let private ownedCounter = ref 0

/// The local filesystem, for preparing launches (the kernel runs on this machine: embedded mode).
type private LocalFiles =
    {
        FsRead: FsRead
        Fs: Fs
        Scope: Scope
    }

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IScope with
        member env.Scope = env.Scope

    override _.ToString() = "LocalFiles"

/// The surface the suite's bindings make: any arguments, any results, its result for `finish`.
let private surfaceOf (setup: KernelSetup) : BindingSurface =
    let unknown = Schema.ofKind SchemaKind.Any

    {
        Global = setup.Global
        Description = None
        Groups = []
        Bindings =
            setup.Bindings
            |> List.map (fun path ->
                {
                    Path = path
                    Description = None
                    Params =
                        [
                            for name in [ "a"; "b"; "c" ] do
                                {
                                    Name = name
                                    Description = None
                                    Schema = unknown
                                    Optional = true
                                }
                        ]
                    Returns = unknown
                })
        Finish = turnCodec.Schema
    }

let private launcherEnvironment () : (string * string) list =
    Environment.GetEnvironmentVariables()
    |> Seq.cast<Collections.DictionaryEntry>
    |> Seq.choose (fun entry ->
        match entry.Key, entry.Value with
        | (:? string as name), (:? string as value) -> Some(name, value)
        | _ -> None)
    |> List.ofSeq

/// Starts the bun-ts kernel as the product does (`Interpreter.prepare`: the provider's assets
/// extracted and checked, fx.runtime.bun's owned directories and hardened launch, the bindings'
/// declarations), from this process's environment plus `launcher` as the launcher's. The suite's
/// `variables` go into the launch's environment as given, so that its hygiene check sees them.
let private startBunTs (bun: string) (launcher: (string * string) list) =
    fun (env: Env) (host: KernelHost<Env>) (setup: KernelSetup) (variables: (string * string) list) ->
        task {
            let n = Threading.Interlocked.Increment ownedCounter

            let root =
                Repo.path [ "artifacts"; "kernel-bun-ts"; "owned"; $"{Environment.ProcessId}-{n}" ]

            let target =
                match TargetTriple.ofRid RuntimeInformation.RuntimeIdentifier with
                | Some target -> target
                | None -> failtest $"no target for {RuntimeInformation.RuntimeIdentifier}"

            let session: InterpreterSession =
                {
                    Provider = Fx.Ext.InterpreterBunTs.BunTs.provider
                    Runtime = Fx.Ext.RuntimeBun.RuntimeBun.runtime
                    Installed =
                        [
                            {
                                Id = Fx.Ext.RuntimeBun.RuntimeBun.ComponentId
                                Version = Fx.Ext.RuntimeBun.RuntimeBun.bunComponent.Version
                                Dir = Path.GetDirectoryName bun |> nonNull
                                Bin = [ "bun", bun ]
                                Env = []
                            }
                        ]
                    AssetsRoot = Repo.path [ "artifacts"; "kernel-bun-ts"; "assets" ]
                    Launch =
                        {
                            Target = target
                            Owned = OwnedDirs.under root
                            Workspace = None
                            ProjectTrusted = false
                            HostEnvironment = launcherEnvironment () @ launcher
                        }
                }

            let files =
                {
                    FsRead = LocalFs.read
                    Fs = LocalFs.write
                    Scope = Scope.root
                }

            let cleanup () =
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

            match! Eff.run files (Interpreter.prepare session (surfaceOf setup)) |> within with
            | Error e ->
                cleanup ()
                return failtest $"the bun-ts launch was not prepared: {e}"
            | Ok launch ->
                let launch =
                    match launch.Process.Env with
                    | Exactly vars ->
                        { launch with
                            Process =
                                { launch.Process with
                                    Env = Exactly(vars @ variables)
                                }
                        }
                    | Inherit _ -> launch

                match!
                    Eff.run
                        env
                        (Kernel.start
                            { KernelOptions.defaults with
                                ExitGrace = TimeSpan.FromSeconds 3.0
                            }
                            host
                            setup
                            launch)
                    |> within
                with
                | Ok k ->
                    return
                        Ok
                            {
                                Kernel = k
                                Dispose = release env k cleanup
                            }
                | Error e ->
                    cleanup ()
                    return Error e
        }

/// The bun-ts kernel on the pinned Bun (`fx.interpreter.bun-ts`).
let bunTsKernel (bun: string) : KernelSubject =
    {
        Name = "bun-ts kernel"
        Snippets = typeScriptSnippets
        IsProcess = true
        Start = startBunTs bun []
        StartFromLauncher = Some(fun env host setup launcher -> startBunTs bun launcher env host setup [])
    }

let private pinnedBun =
    lazy
        (task {
            match! PinnedBun.executable () with
            | Ok bun -> return bun
            | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
                return failtest $"the pinned Bun is not available: {reason}"
            | Error reason -> return skiptest $"the pinned Bun is not available: {reason}"
        })

/// The bun-ts kernel, with the pinned Bun installed on first use. Without it (offline), its tests
/// are skipped locally and fail in CI.
let bunTsKernelLazy: KernelSubject =
    let subject =
        lazy
            (task {
                let! bun = pinnedBun.Force()
                return bunTsKernel bun
            })

    {
        Name = "bun-ts kernel"
        Snippets = typeScriptSnippets
        IsProcess = true
        Start =
            fun env host setup variables ->
                task {
                    let! subject = subject.Force()
                    return! subject.Start env host setup variables
                }
        StartFromLauncher =
            Some(fun env host setup launcher ->
                task {
                    let! subject = subject.Force()
                    return! (Option.get subject.StartFromLauncher) env host setup launcher
                })
    }
