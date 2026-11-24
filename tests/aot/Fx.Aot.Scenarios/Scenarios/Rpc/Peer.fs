/// The `rpc-peer` command: serves `TestService` on standard input and output, as envd and kernels
/// talk to their host. The `rpc-stdio-child` scenario and tests/Fx.Rpc.Tests start it as a child
/// process, so the stdio transport is tested across a real process boundary, on the JIT and natively.
module Fx.Aot.Scenarios.Rpc.Peer

open System
open System.Diagnostics
open System.IO
open Fx.Effects
open Fx.Effects.Live
open Fx.Rpc
open Fx.Aot.Scenarios.Rpc.TestService

/// Serves one connection on stdio until it ends; returns the exit code.
let serve () : int =
    let input = Console.OpenStandardInput()
    let output = Console.OpenStandardOutput()
    let transport = StreamTransport.create (ConnectionLimits.frames limits) input output

    let env =
        {
            Clock = SystemClock.clock
            Scope = Scope.root
        }

    match (Eff.run env (Rpc.accept (service ()) transport)).GetAwaiter().GetResult() with
    | Ok peer ->
        peer.Completion.GetAwaiter().GetResult() |> ignore
        0
    | Error e ->
        stderr.WriteLine("rpc-peer: " + string e)
        1

/// How to start this program in `rpc-peer` mode: from itself (natively or on the JIT) or from a test
/// host that references it, whose output directory then holds this program's apphost and assembly.
let startInfo () : ProcessStartInfo =
    let self =
        match Environment.ProcessPath with
        | null -> ""
        | path -> path

    let directory = AppContext.BaseDirectory

    let file, args =
        if String.Equals(Path.GetFileNameWithoutExtension self, "dotnet", StringComparison.OrdinalIgnoreCase) then
            self, [ Path.Combine(directory, "Fx.Aot.Scenarios.dll"); "rpc-peer" ]
        else
            // The native binary, or the apphost next to a test host: it finds the runtime as the host did.
            let suffix = if OperatingSystem.IsWindows() then ".exe" else ""
            Path.Combine(directory, "Fx.Aot.Scenarios" + suffix), [ "rpc-peer" ]

    let info = ProcessStartInfo(file, args)
    info.RedirectStandardInput <- true
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- false
    info.UseShellExecute <- false
    info

/// A started peer process and the client's transport to it.
type Child =
    {
        Process: Process
        Transport: Transport
    }

    override this.ToString() = "rpc-peer child"

/// Starts the peer; the transport's limits are `clientLimits`.
let start () : Child =
    let child =
        match Process.Start(startInfo ()) with
        | null -> invalidOp "rpc-peer did not start"
        | p -> p

    {
        Process = child
        Transport =
            StreamTransport.create
                (ConnectionLimits.frames clientLimits)
                child.StandardOutput.BaseStream
                child.StandardInput.BaseStream
    }
