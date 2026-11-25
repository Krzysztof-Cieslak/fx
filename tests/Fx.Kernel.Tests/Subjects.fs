/// The kernels the conformance suite runs against: the F# script kernel in process and as a child
/// process (`Fx.Aot.Scenarios kernel-peer`), and the reference TypeScript kernel
/// (`packages/kernel-bun-ts`) on the pinned Bun.
module Fx.Kernel.Tests.Subjects

open System
open System.IO
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

/// The owned bunfig (docs/spikes/bun-kernel.md): the same switches as the flags.
let private bunfig =
    "telemetry = false\nenv = false\n\n[install]\nauto = \"disable\"\n\n[run]\nnoOrphans = true\n"

/// The reference kernel on the pinned Bun, with the spike's hardened launch (T114 owns the real one).
let bunKernel (prepared: PinnedBun.Prepared) : KernelSubject =
    {
        Name = "bun-ts reference kernel"
        Snippets = typeScriptSnippets
        IsProcess = true
        Start =
            fun env host setup variables ->
                task {
                    let n = Threading.Interlocked.Increment ownedCounter

                    let root =
                        Repo.path [ "artifacts"; "kernel-bun-ts"; "owned"; $"{Environment.ProcessId}-{n}" ]

                    let dirs =
                        [ "cwd"; "home"; "tmp"; "cache"; "config" ]
                        |> List.map (fun d -> d, Path.Combine(root, d))
                        |> Map.ofList

                    for _, dir in Map.toSeq dirs do
                        Directory.CreateDirectory dir |> ignore

                    let config = Path.Combine(dirs["config"], "bunfig.toml")
                    File.WriteAllText(config, bunfig)

                    let owned =
                        [
                            "DO_NOT_TRACK", "1"
                            "NO_COLOR", "1"
                            "HOME", dirs["home"]
                            "USERPROFILE", dirs["home"]
                            "XDG_CONFIG_HOME", Path.Combine(dirs["home"], "config")
                            "XDG_CACHE_HOME", dirs["cache"]
                            "TMPDIR", dirs["tmp"]
                            "TEMP", dirs["tmp"]
                            "TMP", dirs["tmp"]
                            "BUN_RUNTIME_TRANSPILER_CACHE_PATH", Path.Combine(dirs["cache"], "transpiler")
                            "BUN_INSTALL_CACHE_DIR", Path.Combine(dirs["cache"], "install")
                        ]

                    let basics =
                        hostBasics ()
                        |> List.filter (fun (name, _) -> not (name.StartsWith("DOTNET_", StringComparison.Ordinal)))

                    let launch =
                        {
                            Process =
                                {
                                    Program =
                                        Exec(
                                            prepared.Bun,
                                            [
                                                $"--config={config}"
                                                "--no-env-file"
                                                "--no-install"
                                                "--no-orphans"
                                                "--no-macros"
                                                prepared.Kernel
                                            ]
                                        )
                                    Cwd = Some dirs["cwd"]
                                    Env = Exactly(basics @ owned @ variables)
                                }
                            EnvDeny =
                                [
                                    "BUN_INSPECT*"
                                    "BUN_CONFIG_*"
                                    "BUN_JSC_*"
                                    "BUN_FEATURE_FLAG_*"
                                    "BUN_DEBUG*"
                                    "NODE_PATH"
                                    "NODE_CHANNEL_*"
                                    "LD_LIBRARY_PATH"
                                    "DYLD_*"
                                ]
                            IgnoreEnvCase = OperatingSystem.IsWindows()
                        }

                    let cleanup () =
                        try
                            Directory.Delete(root, true)
                        with _ ->
                            ()

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
    }

/// The reference kernel, prepared on first use. Without the pinned Bun (offline), its tests are
/// skipped locally and fail in CI.
let bunKernelLazy: KernelSubject =
    let subject =
        lazy
            (task {
                match! PinnedBun.prepare () with
                | Ok prepared -> return bunKernel prepared
                | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
                    return failtest $"the pinned Bun is not available: {reason}"
                | Error reason -> return skiptest $"the pinned Bun is not available: {reason}"
            })

    {
        Name = "bun-ts reference kernel"
        Snippets = typeScriptSnippets
        IsProcess = true
        Start =
            fun env host setup variables ->
                task {
                    let! subject = subject.Force()
                    return! subject.Start env host setup variables
                }
    }
