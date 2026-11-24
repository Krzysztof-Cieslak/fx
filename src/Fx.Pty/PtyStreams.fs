namespace Fx.Pty

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// The two directions of a terminal as blocking operations on its handle, with native memory.
type internal PtyIo =
    {
        /// Reads at most `count` bytes to `buffer`; returns how many, or 0 once the terminal has closed.
        Read: nativeint -> int -> int
        /// Writes `count` bytes from `buffer`; returns false once the terminal has closed.
        Write: nativeint -> int -> bool
        /// Runs once both directions have stopped: releases the handles.
        Release: unit -> unit
    }

    override _.ToString() = "PtyIo"

/// Moves bytes between a terminal and its users on two dedicated threads, so that blocking reads
/// and writes never hold thread-pool threads. Output goes through a bounded channel: a terminal
/// whose output is not read stops being read, and its program blocks once the kernel buffer fills,
/// as on a real terminal.
[<Sealed>]
type internal PtyStreams(name: string, io: PtyIo) =
    let output =
        Channel.CreateBounded<byte array>(BoundedChannelOptions(64, SingleReader = false, SingleWriter = true))

    let input =
        Channel.CreateUnbounded<struct (byte array * TaskCompletionSource<unit>)>(
            UnboundedChannelOptions(SingleReader = true, SingleWriter = false)
        )

    /// Set by `Discard`: output read from now on is dropped.
    let mutable discarding = false
    let mutable running = 2

    let stopped () =
        if Interlocked.Decrement(&running) = 0 then
            io.Release()

    let readLoop () =
        try
            try
                // On the pinned object heap: the address stays valid without `fixed`.
                let buffer = GC.AllocateArray<byte>(16384, pinned = true)
                let address = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0)
                let mutable reading = true

                while reading do
                    let count = io.Read address buffer.Length

                    if count <= 0 then
                        reading <- false
                    elif not (Volatile.Read &discarding) then
                        let chunk = Array.sub buffer 0 count

                        try
                            output.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult()
                        with :? ChannelClosedException ->
                            // Discarded while waiting for room: keep reading to the end, dropping.
                            ()

                output.Writer.TryComplete() |> ignore
            with e ->
                output.Writer.TryComplete e |> ignore
        finally
            // The terminal has closed: nothing written from now on can arrive.
            input.Writer.TryComplete() |> ignore
            stopped ()

    let writeLoop () =
        try
            let mutable open' = true

            while input.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult() do
                match input.Reader.TryRead() with
                | true, struct (bytes, written) ->
                    if open' then
                        let pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned)

                        try
                            try
                                open' <- io.Write (pinned.AddrOfPinnedObject()) bytes.Length
                            with e ->
                                open' <- false
                                written.TrySetException e |> ignore
                        finally
                            pinned.Free()

                    written.TrySetResult() |> ignore
                | false, _ -> ()
        finally
            stopped ()

    let start (loop: unit -> unit) (suffix: string) =
        // Small stacks: the loops only call into the OS.
        let thread = Thread(ThreadStart loop, 256 * 1024)
        thread.IsBackground <- true
        thread.Name <- $"{name} {suffix}"
        thread.Start()

    do
        start readLoop "output"
        start writeLoop "input"

    /// The next piece of output; `None` at the end.
    member _.Read(cancellation: CancellationToken) : Task<byte array option> =
        task {
            let mutable chunk = None
            let mutable waiting = true

            while waiting do
                match output.Reader.TryRead() with
                | true, next ->
                    chunk <- Some next
                    waiting <- false
                | false, _ ->
                    let! more = output.Reader.WaitToReadAsync cancellation
                    waiting <- more

            return chunk
        }

    /// Queues bytes for the terminal; completes when they were written (or dropped after the end).
    member _.Write (bytes: byte array) (cancellation: CancellationToken) : Task<unit> =
        if bytes.Length = 0 then
            Task.FromResult()
        else
            let written =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            if input.Writer.TryWrite(struct (Array.copy bytes, written)) then
                written.Task.WaitAsync cancellation
            else
                // The terminal has closed: drop the input, as for a process that exited.
                Task.FromResult()

    /// Drops unread and future output, and stops accepting input. The threads end when the terminal
    /// closes.
    member _.Discard() =
        Volatile.Write(&discarding, true)
        output.Writer.TryComplete() |> ignore
        input.Writer.TryComplete() |> ignore

        // Unblock a reader thread that waits for room, and free what nobody will read.
        let mutable chunk = Unchecked.defaultof<byte array>

        while output.Reader.TryRead(&chunk) do
            ()

    override _.ToString() = $"PtyStreams({name})"
