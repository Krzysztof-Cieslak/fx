namespace Fx.Rpc

open System.Threading
open System.Threading.Tasks

/// How a side ends its half of a connection. WebSocket maps these to close codes.
[<RequireQualifiedAccess>]
type Shutdown =
    | Normal
    /// The peer broke the protocol.
    | ProtocolError
    /// The peer sent a frame over the limit.
    | TooLarge

    override this.ToString() =
        match this with
        | Normal -> "normal"
        | ProtocolError -> "protocol error"
        | TooLarge -> "too large"

/// A bidirectional, ordered connection that moves frames: the only part of the protocol that touches
/// the world. Like a capability, it is a record of functions with data in and out, so it can be
/// wrapped (recording, fault injection) and implemented over anything that keeps order.
///
/// The connection engine calls `Send` from one writer and `Receive` from one reader at a time.
/// Transports bound what they buffer: `Send` waits while the other side does not read.
type Transport =
    {
        /// What the transport is, for diagnostics (e.g. `in-process`, `stream`, `websocket`).
        Name: string
        /// Sends one frame. `Error` once the connection is broken.
        Send: Frame -> CancellationToken -> Task<Result<unit, TransportError>>
        /// The next frame, `End` once the peer closed its side, or why it failed. Bounded by the
        /// transport's `FrameLimits`.
        Receive: CancellationToken -> Task<Received>
        /// Ends this side's output after the frames already sent: the peer receives `End`. The input
        /// stays open until the peer closes its side too.
        CloseOutput: Shutdown -> CancellationToken -> Task<unit>
        /// Releases the transport at once. A pending or later `Receive` returns `End` or `Failed`, a
        /// later `Send` fails. Frames already sent stay readable by the peer, as on a pipe: the
        /// connection closes gracefully by `CloseOutput`, then `Abort`, and the peer must still get
        /// everything sent before. Idempotent.
        Abort: unit -> unit
    }

    override this.ToString() = this.Name
