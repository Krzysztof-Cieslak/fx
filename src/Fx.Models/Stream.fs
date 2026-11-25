namespace Fx.Models

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// A streamed model response, pulled one event at a time (spec §10.2 `ChatWire.Stream`).
///
/// A record of functions like `Proc` and `HttpBody`, rather than an `IAsyncEnumerable` or a channel:
/// nothing runs in the background (the next event is read when it is asked for, so cancellation and
/// failures stay in the caller's scope), every pull is data out (recordable and replayable), and it
/// needs no library or reflection under Native AOT.
type ChatStream =
    {
        /// The next event; `None` after the last one (`Finished` or `Failed`). Cancelling the token
        /// fails it with an `OperationCanceledException`; the stream is then unusable: close it.
        Next: CancellationToken -> Task<ChatEvent option>
        /// Releases the response (the connection). Idempotent; call it when done or abandoning it.
        Close: unit -> Task<unit>
    }

    override _.ToString() = "ChatStream"

[<RequireQualifiedAccess>]
module ChatStream =
    let private rethrow (e: exn) : 'T =
        Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<'T>

    /// A stream of these events, then `None`.
    let ofEvents (events: ChatEvent list) : ChatStream =
        let gate = obj ()
        let mutable remaining = events

        {
            Next =
                fun cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<ChatEvent option> cancellation
                    else
                        lock gate (fun () ->
                            match remaining with
                            | event :: rest ->
                                remaining <- rest
                                Task.FromResult(Some event)
                            | [] -> Task.FromResult None)
            Close =
                fun () ->
                    lock gate (fun () -> remaining <- [])
                    Task.FromResult()
        }

    /// A stream that fails at once (e.g. a request refused before it left).
    let failed (error: ChatError) : ChatStream = ofEvents [ ChatEvent.Failed error ]

    /// Calls `observe` with every event the stream gives, as it gives it.
    let tap (observe: ChatEvent -> unit) (stream: ChatStream) : ChatStream =
        { stream with
            Next =
                fun cancellation ->
                    task {
                        let! next = stream.Next cancellation

                        match next with
                        | Some event -> observe event
                        | None -> ()

                        return next
                    }
        }

    /// Reads the stream to its end and folds it, then closes it (also on failure or cancellation).
    let collect (stream: ChatStream) (cancellation: CancellationToken) : Task<ChatAggregate> =
        task {
            let mutable state = ChatAggregate.empty
            let mutable reading = true
            let mutable failure = None

            try
                while reading do
                    match! stream.Next cancellation with
                    | Some event -> state <- ChatAggregate.apply state event
                    | None -> reading <- false
            with e ->
                failure <- Some e

            do! stream.Close()

            match failure with
            | Some e -> return rethrow e
            | None -> return state
        }

    /// The next event. Cancelled with the scope.
    let next<'R when 'R :> IScope> (stream: ChatStream) : Eff<'R, ChatEvent option> =
        fun env -> stream.Next env.Scope.Cancellation

    let close (stream: ChatStream) : Eff<'R, unit> = fun _ -> stream.Close()

    /// Reads the stream to its end and folds it; closes it. Cancelled with the scope.
    let fold<'R when 'R :> IScope> (stream: ChatStream) : Eff<'R, ChatAggregate> =
        fun env -> collect stream env.Scope.Cancellation
