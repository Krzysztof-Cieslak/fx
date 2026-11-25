namespace Fx.Envd

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Fx.Rpc

/// A channel failed in the middle of a transfer: the connection or the peer went away. A defect for
/// the capability that was moving the data.
type TransferFailedException(error: ChannelError) =
    inherit IOException("A transfer over the environment protocol failed: " + string error)
    member _.Error = error

/// Moving bytes over binary channels: the caller of a method opens the channel and names it in the
/// request; the handler accepts it.
module Transfer =
    /// The reset code a caller uses when it gives up on a channel (its call failed).
    [<Literal>]
    let Abandoned = 100u

    /// Reads a channel to its end.
    let readAll (channel: Channel) (cancellation: CancellationToken) : Task<Result<byte array, ChannelError>> =
        task {
            use buffer = new MemoryStream()
            let mutable outcome = None

            while outcome.IsNone do
                match! channel.Read cancellation with
                | Ok(Some data) -> buffer.Write(data.Span)
                | Ok None -> outcome <- Some(Ok(buffer.ToArray()))
                | Error e -> outcome <- Some(Error e)

            return outcome.Value
        }

    /// Writes bytes and ends the channel's output.
    let writeAll
        (channel: Channel)
        (content: byte array)
        (cancellation: CancellationToken)
        : Task<Result<unit, ChannelError>> =
        task {
            match! channel.Write(ReadOnlyMemory content, cancellation) with
            | Error e -> return Error e
            | Ok() -> return! channel.CloseOutput cancellation
        }

    let private rethrow (e: exn) : 'T =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<'T>

    let private orThrow (result: Result<'T, ChannelError>) : 'T =
        match result with
        | Ok value -> value
        | Error e -> raise (TransferFailedException e)

    let private openChannel (peer: Peer) (cancellation: CancellationToken) : Task<Channel> =
        task {
            let! opened = peer.OpenChannel cancellation
            return orThrow opened
        }

    /// Calls a method that sends content over a channel the caller opens: returns the result and the
    /// content. A failed call resets the channel.
    let download
        (peer: Peer)
        (call: uint32 -> Task<Result<'T, 'E>>)
        (cancellation: CancellationToken)
        : Task<Result<'T * byte array, 'E>> =
        task {
            let! channel = openChannel peer cancellation
            let reading = readAll channel cancellation

            try
                match! call channel.Id with
                | Error e ->
                    channel.Reset(Abandoned, "the call failed")
                    let! _ = reading
                    return Error e
                | Ok value ->
                    let! content = reading
                    return Ok(value, orThrow content)
            with e ->
                channel.Reset(Abandoned, "the call failed")
                return rethrow e
        }

    /// Calls a method that receives content over a channel the caller opens and writes. A failed call
    /// resets the channel.
    let upload
        (peer: Peer)
        (content: byte array)
        (call: uint32 -> Task<Result<'T, 'E>>)
        (cancellation: CancellationToken)
        : Task<Result<'T, 'E>> =
        task {
            let! channel = openChannel peer cancellation
            let writing = writeAll channel content cancellation

            try
                match! call channel.Id with
                | Error e ->
                    channel.Reset(Abandoned, "the call failed")
                    let! _ = writing
                    return Error e
                | Ok value ->
                    let! written = writing
                    orThrow written
                    return Ok value
            with e ->
                channel.Reset(Abandoned, "the call failed")
                return rethrow e
        }
