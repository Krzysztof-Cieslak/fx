namespace Fx.Rpc

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// Reads a process's standard output through `Proc.Read`; standard error goes to a sink.
type private ProcOutput(proc: Proc, stderr: byte array -> unit) =
    inherit Stream()

    let closed = new CancellationTokenSource()
    let mutable pending: byte array = [||]
    let mutable offset = 0

    let take (destination: Memory<byte>) =
        let count = min destination.Length (pending.Length - offset)
        pending.AsMemory(offset, count).CopyTo destination
        offset <- offset + count
        count

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_: byte array, _: int, _: int) = raise (NotSupportedException())

    override this.Read(buffer: byte array, index: int, count: int) =
        this.ReadAsync(buffer.AsMemory(index, count), CancellationToken.None).AsTask().GetAwaiter().GetResult()

    override _.ReadAsync(destination: Memory<byte>, cancellation: CancellationToken) : ValueTask<int> =
        if offset < pending.Length then
            ValueTask<int>(take destination)
        else
            ValueTask<int>(
                task {
                    use linked =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellation, closed.Token)

                    let mutable result = -1

                    while result < 0 do
                        match! proc.Read linked.Token with
                        | Some(Stdout bytes) when bytes.Length > 0 ->
                            pending <- bytes
                            offset <- 0
                            result <- take destination
                        | Some(Stdout _) -> ()
                        | Some(Stderr bytes) -> stderr bytes
                        | None -> result <- 0

                    return result
                }
            )

    override _.Dispose(disposing: bool) =
        if disposing then
            closed.Cancel()

        base.Dispose disposing

/// Writes to a process's standard input through `Proc.Write`; disposing closes it.
type private ProcInput(proc: Proc) =
    inherit Stream()

    let mutable closed = 0

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.FlushAsync(_: CancellationToken) = Task.CompletedTask
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Read(_: byte array, _: int, _: int) = raise (NotSupportedException())

    override this.Write(buffer: byte array, index: int, count: int) =
        this.WriteAsync(ReadOnlyMemory(buffer, index, count), CancellationToken.None).AsTask().GetAwaiter().GetResult()

    override _.WriteAsync(source: ReadOnlyMemory<byte>, cancellation: CancellationToken) : ValueTask =
        if Volatile.Read &closed = 1 then
            ValueTask(Task.FromException(ObjectDisposedException(nameof ProcInput)))
        else
            ValueTask(proc.Write (source.ToArray()) cancellation :> Task)

    override _.Dispose(disposing: bool) =
        if disposing && Interlocked.Exchange(&closed, 1) = 0 then
            proc.CloseStdin().GetAwaiter().GetResult()

        base.Dispose disposing

    override this.DisposeAsync() =
        if Interlocked.Exchange(&closed, 1) = 0 then
            ValueTask(proc.CloseStdin() :> Task)
        else
            ValueTask.CompletedTask

/// A transport over a child process's standard input and output, with the stream framing of
/// `StreamTransport` (NDJSON and binary channel frames). The process is a `Proc` from the shell
/// capability (`Shell.spawn`), so the composition root decides how processes start, and tests can
/// pass a fake. Its standard error is not part of the protocol: it goes to `stderr` (logs).
///
/// Closing the output closes the process's standard input; aborting also stops reading. Neither
/// kills the process: its owner waits for it to exit, or kills it.
module ProcTransport =
    let create (limits: FrameLimits) (proc: Proc) (stderr: byte array -> unit) : Transport =
        let transport =
            StreamTransport.create limits (new ProcOutput(proc, stderr)) (new ProcInput(proc))

        { transport with Name = "process" }
