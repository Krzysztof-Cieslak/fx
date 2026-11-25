/// The entry point of an F# extension process (spec §16.1 `process` runtime): serves extensions on
/// the process's standard input and output until the host closes standard input. The process's
/// `main` is a composition root; this is the one place it touches the console (analyzers.json).
[<RequireQualifiedAccess>]
module Fx.Sdk.Process.ExtensionProcess

open System
open Fx.Effects
open Fx.Effects.Live
open Fx.Rpc
open Fx.Sdk

/// The server's own environment: the system clock and the root scope.
type private ProcessEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<ProcessEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"ProcessEnv({env.Scope})"

/// Serves the extensions on standard input and output, each in its `BasicExtensionEnv` with the
/// system clock, until the host closes standard input; returns the exit code. The protocol owns
/// standard output: anything the extensions write to `Console.Out` goes to standard error.
let run (name: string) (version: string) (extensions: Extension list) : int =
    let input = Console.OpenStandardInput()
    let output = Console.OpenStandardOutput()
    Console.SetOut Console.Error

    let transport =
        StreamTransport.create (ConnectionLimits.frames ConnectionLimits.defaults) input output

    let env =
        {
            Clock = SystemClock.clock
            Scope = Scope.root
        }

    let served =
        ExtensionServer.serve
            { Name = name; Version = version }
            extensions
            (BasicExtensionEnv.forManifest SystemClock.clock)
            transport

    match (Eff.run env served).GetAwaiter().GetResult() with
    | Ok peer ->
        peer.Completion.GetAwaiter().GetResult() |> ignore
        0
    | Error e ->
        Console.Error.WriteLine(name + ": " + string e)
        1
