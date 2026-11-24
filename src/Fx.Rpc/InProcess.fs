namespace Fx.Rpc

open System.Text
open System.Threading.Tasks

/// Transports between two connections in one process (embedded mode, spec §3, and tests).
///
/// Frames cross as they are: JSON-RPC text (already serialized by the sending connection) and channel
/// frames. So the in-process transport skips only the byte framing; everything else, codecs,
/// limits and the JSON reader included, runs as it does out of process, and the two behave the same
/// (transport parity, spec §16.4).
module InProcess =

    let private size (frame: Frame) =
        match frame with
        | Frame.Text text -> int64 text.Length
        | Frame.Binary bytes -> int64 bytes.Length

    let private tooLarge (limits: FrameLimits) (frame: Frame) =
        match frame with
        | Frame.Text text ->
            // Each UTF-16 code unit takes one to three UTF-8 bytes: count only when it matters.
            if text.Length > limits.MaxText then
                Some limits.MaxText
            elif int64 text.Length * 3L <= int64 limits.MaxText then
                None
            elif Encoding.UTF8.GetByteCount text > limits.MaxText then
                Some limits.MaxText
            else
                None
        | Frame.Binary bytes when bytes.Length > limits.MaxBinary -> Some limits.MaxBinary
        | Frame.Binary _ -> None

    let private endpoint
        (name: string)
        (limits: FrameLimits)
        (incoming: BoundedQueue<Frame>)
        (outgoing: BoundedQueue<Frame>)
        : Transport =
        {
            Name = name
            Send =
                fun frame cancellation ->
                    task {
                        let! accepted = outgoing.Enqueue(frame, size frame, cancellation)

                        return
                            if accepted then
                                Ok()
                            else
                                Error(TransportError.Broken "connection closed")
                    }
            Receive =
                fun _ ->
                    task {
                        let! next = incoming.Take()

                        match next with
                        | ValueSome(struct (frame, bytes)) ->
                            // The frame has left the pipe: like reading from a socket buffer.
                            incoming.Release bytes

                            match tooLarge limits frame with
                            | Some limit ->
                                incoming.Fail()
                                outgoing.Fail()
                                return Received.Failed(TransportError.TooLarge limit)
                            | None -> return Received.Frame frame
                        | ValueNone when incoming.IsFailed ->
                            return Received.Failed(TransportError.Broken "connection reset")
                        | ValueNone -> return Received.End
                    }
            CloseOutput =
                fun _ _ ->
                    outgoing.Complete()
                    Task.FromResult()
            Abort =
                fun () ->
                    incoming.Fail()
                    outgoing.Fail()
        }

    /// Two connected transports, each buffering at most `bufferBytes` in its direction (a sender waits
    /// beyond that, as on a pipe). `first` and `second` are the receive limits of each end; use
    /// `Limits.frames` of the connection that uses the end.
    let pair (bufferBytes: int) (first: FrameLimits) (second: FrameLimits) : Transport * Transport =
        let toSecond = BoundedQueue<Frame>(int64 bufferBytes)
        let toFirst = BoundedQueue<Frame>(int64 bufferBytes)
        endpoint "in-process" first toFirst toSecond, endpoint "in-process" second toSecond toFirst
