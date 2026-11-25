namespace Fx.Kernel

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Rpc

/// The transport to a kernel process: its standard input and output as a `Proc` from the shell
/// capability, read with `KernelLineReader` (messages after stray bytes are recovered, the rest is
/// reported). Kernels have no binary channels. Its standard error is not part of the protocol.
///
/// Closing the output closes the kernel's standard input (which ends it); aborting stops reading.
/// Neither kills the process: its owner (`Kernel`) does, after a grace period.
module KernelTransport =
    /// Stray text is reported in pieces of at most this many bytes.
    [<Literal>]
    let MaxStrayPiece = 64 * 1024

    let private failure (e: exn) = TransportError.Broken(e.GetType().Name)

    /// A transport over a kernel process. `stray` gets what the kernel wrote to standard output that is
    /// not a message, `stderr` its standard error.
    let create (limits: FrameLimits) (proc: Proc) (stray: string -> unit) (stderr: byte array -> unit) : Transport =
        let reader = KernelLineReader(limits.MaxText, MaxStrayPiece)
        let frames = Queue<string>()
        let closed = new CancellationTokenSource()
        let mutable final: Received option = None

        let deliver (lines: KernelLine list) =
            for line in lines do
                match line with
                | KernelLine.Frame text -> frames.Enqueue text
                | KernelLine.Stray text -> stray text

        let receive (cancellation: CancellationToken) : Task<Received> =
            task {
                let mutable result = None

                while result.IsNone do
                    if frames.Count > 0 then
                        result <- Some(Received.Frame(Frame.Text(frames.Dequeue())))
                    else
                        match final with
                        | Some outcome -> result <- Some outcome
                        | None ->
                            try
                                use linked =
                                    CancellationTokenSource.CreateLinkedTokenSource(cancellation, closed.Token)

                                match! proc.Read linked.Token with
                                | Some(Stdout bytes) ->
                                    match reader.Feed(ReadOnlySpan bytes) with
                                    | Ok lines -> deliver lines
                                    | Error e -> final <- Some(Received.Failed e)
                                | Some(Stderr bytes) -> stderr bytes
                                | None ->
                                    match reader.End() with
                                    | Ok lines ->
                                        deliver lines
                                        final <- Some Received.End
                                    | Error e -> final <- Some(Received.Failed e)
                            with
                            | :? OperationCanceledException when closed.IsCancellationRequested ->
                                final <- Some Received.End
                            | e when not (e :? OperationCanceledException) -> final <- Some(Received.Failed(failure e))

                return result.Value
            }

        let send (frame: Frame) (cancellation: CancellationToken) : Task<Result<unit, TransportError>> =
            task {
                match frame with
                | Frame.Binary _ -> return Error(TransportError.Broken "kernels have no binary channels")
                | Frame.Text _ ->
                    if closed.IsCancellationRequested then
                        return Error(TransportError.Broken "aborted")
                    else
                        try
                            do! proc.Write (StreamFraming.encode frame) cancellation
                            return Ok()
                        with e ->
                            return Error(failure e)
            }

        let closeOutput (_: Shutdown) (_: CancellationToken) : Task<unit> =
            task {
                try
                    do! proc.CloseStdin()
                with _ ->
                    ()
            }

        let abort () =
            try
                closed.Cancel()
            with :? ObjectDisposedException ->
                ()

        {
            Name = "kernel process"
            Send = send
            Receive = receive
            CloseOutput = closeOutput
            Abort = abort
        }
