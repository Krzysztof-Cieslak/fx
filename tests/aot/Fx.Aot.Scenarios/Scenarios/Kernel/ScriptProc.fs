/// The script kernel as a process of a shell, without an OS process: `Shell.Spawn` of the program
/// `script-kernel` starts a script kernel in this process and gives a `Proc` over in-memory pipes (its
/// standard input and output). So the host speaks to it through the shell capability, as to a kernel
/// process (`Kernel.start`), on every OS, and a recording journals that process's input and output.
module Fx.Aot.Scenarios.Kernel.ScriptProc

open System
open System.IO.Pipelines
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Effects.Live
open Fx.Rpc
open Fx.Kernel
open Fx.CodeAct
open Fx.Aot.Scenarios.Rpc.TestService

/// The program a launch names for the script kernel.
[<Literal>]
let Program = "script-kernel"

let private pids = ref 4000

/// Starts a script kernel and gives its process.
let start () : Proc =
    let input = Pipe()
    let output = Pipe()
    let kernel = ScriptKernel.Kernel ignore
    let limits = ConnectionLimits.frames kernel.Service.ConnectionLimits

    let transport =
        StreamTransport.create limits (input.Reader.AsStream()) (output.Writer.AsStream())

    let exited =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let reader = output.Reader.AsStream()
    let writes = new SemaphoreSlim(1, 1)

    let env: Env =
        {
            Clock = SystemClock.clock
            Scope = Scope.root
        }

    let finish (code: int) =
        if exited.TrySetResult code then
            output.Writer.Complete()

    task {
        match! Eff.run env (Rpc.accept kernel.Service transport) with
        | Ok peer ->
            let! _ = peer.Completion
            ()
        | Error _ -> ()

        finish 0
    }
    |> ignore

    {
        Pid = Interlocked.Increment pids
        Write =
            fun bytes cancellation ->
                task {
                    do! writes.WaitAsync cancellation

                    try
                        if not exited.Task.IsCompleted then
                            let! _ = input.Writer.WriteAsync(ReadOnlyMemory bytes, cancellation)
                            ()
                    finally
                        writes.Release() |> ignore
                }
        CloseStdin =
            fun () ->
                task {
                    do! writes.WaitAsync()

                    try
                        do! input.Writer.CompleteAsync()
                    finally
                        writes.Release() |> ignore
                }
        Read =
            fun cancellation ->
                task {
                    let buffer = Array.zeroCreate<byte> 65536
                    let! count = reader.ReadAsync(buffer, 0, buffer.Length, cancellation)

                    if count = 0 then
                        return None
                    else
                        return Some(Stdout(Array.sub buffer 0 count))
                }
        Wait = fun cancellation -> exited.Task.WaitAsync cancellation
        Kill =
            fun () ->
                kernel.Kill()
                transport.Abort()
                finish 137
                Task.FromResult()
    }

/// A command `run` answers, as a process (`shell.run` spawns its commands): it runs once its input is
/// closed, with that input; its output comes as one piece per stream; `Kill` cancels the run, which
/// then ends with exit code 137 (and nothing printed, if the run gave up on its cancellation).
let command (run: Command -> CancellationToken -> Task<Result<Output, ShellError>>) (spec: ProcSpec) : Proc =
    let gate = obj ()
    let input = new IO.MemoryStream()
    let killed = new CancellationTokenSource()

    let ran =
        TaskCompletionSource<Output>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable started = false
    let mutable pending: OutputChunk list option = None

    let killedOutput =
        {
            ExitCode = 137
            Stdout = [||]
            Stderr = [||]
        }

    {
        Pid = Interlocked.Increment pids
        Write =
            fun bytes _ ->
                lock gate (fun () ->
                    if not started then
                        input.Write(bytes, 0, bytes.Length))

                Task.FromResult()
        CloseStdin =
            fun () ->
                let stdin =
                    lock gate (fun () ->
                        if started then
                            None
                        else
                            started <- true
                            Some(input.ToArray()))

                match stdin with
                | Some stdin ->
                    task {
                        try
                            match! run { Spec = spec; Stdin = stdin } killed.Token with
                            | Ok output -> ran.TrySetResult output |> ignore
                            | Error e ->
                                ran.TrySetResult
                                    {
                                        ExitCode = 127
                                        Stdout = [||]
                                        Stderr = Text.Encoding.UTF8.GetBytes(string e)
                                    }
                                |> ignore
                        with _ ->
                            ran.TrySetResult killedOutput |> ignore
                    }
                    |> ignore
                | None -> ()

                Task.FromResult()
        Read =
            fun cancellation ->
                task {
                    let! output = ran.Task.WaitAsync cancellation

                    return
                        lock gate (fun () ->
                            let chunks =
                                pending
                                |> Option.defaultValue
                                    [
                                        if output.Stdout.Length > 0 then
                                            Stdout output.Stdout
                                        if output.Stderr.Length > 0 then
                                            Stderr output.Stderr
                                    ]

                            match chunks with
                            | chunk :: rest ->
                                pending <- Some rest
                                Some chunk
                            | [] ->
                                pending <- Some []
                                None)
                }
        Wait =
            fun cancellation ->
                task {
                    let! output = ran.Task.WaitAsync cancellation
                    return output.ExitCode
                }
        Kill =
            fun () ->
                killed.Cancel()

                if not (lock gate (fun () -> started)) then
                    ran.TrySetResult killedOutput |> ignore

                Task.FromResult()
    }

/// A shell whose processes are script kernels (the program `script-kernel`) and whose commands `run`
/// answers, whether they run or are spawned (`command`).
let shell (run: Command -> CancellationToken -> Task<Result<Output, ShellError>>) : Shell =
    {
        Run = run
        Spawn =
            fun spec _ ->
                match spec.Program with
                | Exec(program, _) when program = Program -> Task.FromResult(Ok(start ()))
                | _ -> Task.FromResult(Ok(command run spec))
    }

/// The launch of a script kernel: the same on every machine, so a recording of it replays anywhere.
let launch: KernelLaunch =
    {
        Process =
            {
                Program = Exec(Program, [])
                Cwd = None
                Env = Exactly []
            }
        EnvDeny = []
        IgnoreEnvCase = false
    }

/// The script kernel as a session's interpreter, started through the shell of the environment.
let interpreter<'K when 'K :> IShell and 'K :> IClock and 'K :> IScopeWith<'K>>
    (options: KernelOptions)
    : Interpreter<'K> =
    {
        Language = "script"
        Prompt =
            fun surface ->
                "Cells are scripts. Bindings: "
                + String.concat ", " (surface.Bindings |> List.map (fun b -> b.Path))
        Start =
            fun start ->
                eff {
                    match! Kernel.start options start.Host start.Setup launch with
                    | Ok kernel -> return Ok kernel
                    | Error e -> return Error(string e)
                }
        Launches = None
    }
