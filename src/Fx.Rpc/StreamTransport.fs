namespace Fx.Rpc

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// A transport over a pair of byte streams: standard input and output, pipes, sockets. The framing is
/// `StreamFraming`: newline-delimited JSON for messages, zero-byte-prefixed binary frames for channels.
///
/// Nothing else may write to the output stream, and nothing that logs may use it (see
/// docs/spikes/kestrel.md: a console logger on stdout corrupts the protocol).
module StreamTransport =

    /// Bytes requested from the input per read.
    let private readSize = 64 * 1024

    let private failure (e: exn) = TransportError.Broken(e.GetType().Name)

    /// A transport that reads frames from `input` and writes them to `output`, and owns both: closing
    /// the output disposes `output` (the peer reads the end of its input), aborting disposes both.
    let create (limits: FrameLimits) (input: Stream) (output: Stream) : Transport =
        // Room for the largest frame and its framing.
        let capacity =
            max (limits.MaxText + 2) (limits.MaxBinary + StreamFraming.binaryHeaderSize)

        let mutable buffer = Array.zeroCreate<byte>(min readSize capacity)
        let mutable start = 0
        let mutable count = 0
        let mutable atEnd = false
        let mutable failed = None

        let makeRoom () =
            if start > 0 then
                Buffer.BlockCopy(buffer, start, buffer, 0, count)
                start <- 0

            if count = buffer.Length && buffer.Length < capacity then
                let larger = Array.zeroCreate<byte>(min capacity (buffer.Length * 2))
                Buffer.BlockCopy(buffer, 0, larger, 0, count)
                buffer <- larger

        let receive (cancellation: CancellationToken) : Task<Received> =
            task {
                let mutable result = failed

                while result.IsNone do
                    match StreamFraming.decode limits buffer start count atEnd with
                    | Decoded.Frame(frame, consumed) ->
                        start <- start + consumed
                        count <- count - consumed
                        result <- Some(Received.Frame frame)
                    | Decoded.Skip consumed ->
                        start <- start + consumed
                        count <- count - consumed
                    | Decoded.Invalid error -> result <- Some(Received.Failed error)
                    | Decoded.Incomplete when atEnd -> result <- Some Received.End
                    | Decoded.Incomplete ->
                        makeRoom ()

                        try
                            let! read =
                                input.ReadAsync(
                                    buffer.AsMemory(start + count, buffer.Length - start - count),
                                    cancellation
                                )

                            if read = 0 then atEnd <- true else count <- count + read
                        with e ->
                            result <- Some(Received.Failed(failure e))

                // Failures are final; the end is too.
                match result with
                | Some(Received.Failed _) -> failed <- result
                | _ -> ()

                return result.Value
            }

        let send (frame: Frame) (cancellation: CancellationToken) : Task<Result<unit, TransportError>> =
            task {
                try
                    do! output.WriteAsync(ReadOnlyMemory(StreamFraming.encode frame), cancellation)
                    do! output.FlushAsync cancellation
                    return Ok()
                with e ->
                    return Error(failure e)
            }

        let closeOutput (_: Shutdown) (cancellation: CancellationToken) : Task<unit> =
            task {
                try
                    do! output.FlushAsync cancellation
                    do! output.DisposeAsync()
                with _ ->
                    ()
            }

        let abort () =
            for stream in [ input; output ] do
                try
                    stream.Dispose()
                with _ ->
                    ()

        {
            Name = "stream"
            Send = send
            Receive = receive
            CloseOutput = closeOutput
            Abort = abort
        }
