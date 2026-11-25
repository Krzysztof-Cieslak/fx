namespace Fx.Effects.Fakes

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// One step of a scripted response body.
type BodyStep =
    /// Sends these bytes as one piece.
    | Chunk of byte array
    /// Sends nothing for a while, on the server's clock.
    | Pause of TimeSpan
    /// Sends nothing more: only the reader's cancellation or its idle timeout ends the wait.
    | Hang
    /// The connection breaks off.
    | Break

    override this.ToString() =
        match this with
        | Chunk bytes -> $"chunk ({bytes.Length} bytes)"
        | Pause duration -> $"pause {duration}"
        | Hang -> "hang"
        | Break -> "break"

/// A scripted response: what a fake server sends, and when.
type HttpReply =
    {
        Status: int
        Headers: (string * string) list
        /// How long the server takes to send the headers.
        Delay: TimeSpan
        Body: BodyStep list
    }

    override this.ToString() =
        $"HTTP {this.Status} after {this.Delay} ({this.Body.Length} body steps)"

module HttpReply =
    let private utf8 = UTF8Encoding(false)

    let create (status: int) (body: BodyStep list) : HttpReply =
        {
            Status = status
            Headers = []
            Delay = TimeSpan.Zero
            Body = body
        }

    /// A reply whose body is one piece of UTF-8 text (none if empty).
    let text (status: int) (text: string) : HttpReply =
        create status (if text = "" then [] else [ Chunk(utf8.GetBytes text) ])

    let withHeader (name: string) (value: string) (reply: HttpReply) : HttpReply =
        { reply with
            Headers = reply.Headers @ [ name.ToLowerInvariant(), value ]
        }

    let after (delay: TimeSpan) (reply: HttpReply) : HttpReply = { reply with Delay = delay }

/// A fake HTTP server in memory. `respond` answers each request with a scripted reply or an error,
/// or `None` for a request the script does not expect: that is a defect in the test, and throws.
///
/// It honours the request's timeouts on its clock and the cancellation token, so it passes the same
/// conformance suite as `SystemHttp`. `Requests` records every request in order; `OpenBodies` counts
/// bodies neither read to their end nor closed (a leak if non-zero once a test is done).
[<Sealed>]
type InMemoryHttp(clock: Clock, respond: HttpRequest -> Result<HttpReply, HttpError> option) =
    let gate = obj ()
    let requests = ResizeArray<HttpRequest>()
    let mutable openBodies = 0

    /// Completes when the token is cancelled (with an `OperationCanceledException`).
    let forever (cancellation: CancellationToken) : Task<unit> =
        let source =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        cancellation.Register(fun () -> source.TrySetCanceled cancellation |> ignore)
        |> ignore

        source.Task

    /// Waits `duration`, or fails with a timeout if the limit is shorter.
    let wait (duration: TimeSpan option) (limit: TimeSpan option) (phase: HttpPhase) (cancellation: CancellationToken) =
        task {
            match duration, limit with
            | Some d, Some l when d > l ->
                do! clock.Delay l cancellation
                return Error(TimedOut phase)
            | None, Some l ->
                do! clock.Delay l cancellation
                return Error(TimedOut phase)
            | Some d, _ ->
                do! clock.Delay d cancellation
                return Ok()
            | None, None ->
                do! forever cancellation
                return Ok()
        }

    let body (request: HttpRequest) (steps: BodyStep list) : HttpBody =
        let mutable remaining = steps
        let mutable closed = false

        let close () =
            lock gate (fun () ->
                if not closed then
                    closed <- true
                    openBodies <- openBodies - 1)

        let take () =
            lock gate (fun () ->
                if closed then
                    None
                else
                    match remaining with
                    | step :: rest ->
                        remaining <- rest
                        Some step
                    | [] -> None)

        let read (cancellation: CancellationToken) : Task<Result<byte array option, HttpError>> =
            task {
                cancellation.ThrowIfCancellationRequested()
                let mutable outcome = None

                while outcome.IsNone do
                    match take () with
                    | None ->
                        close ()
                        outcome <- Some(Ok None)
                    | Some(Chunk bytes) -> outcome <- Some(Ok(Some bytes))
                    | Some(Pause duration) ->
                        match! wait (Some duration) request.Timeouts.Idle HttpPhase.Body cancellation with
                        | Ok() -> ()
                        | Error e -> outcome <- Some(Error e)
                    | Some Hang ->
                        // The server never sends more; keep hanging on later reads too.
                        lock gate (fun () -> remaining <- Hang :: remaining)

                        match! wait None request.Timeouts.Idle HttpPhase.Body cancellation with
                        | Ok() -> ()
                        | Error e -> outcome <- Some(Error e)
                    | Some Break -> outcome <- Some(Error(Reset "the server closed the connection"))

                return Option.get outcome
            }

        {
            Read = read
            Close =
                fun () ->
                    close ()
                    Task.FromResult()
        }

    let send (request: HttpRequest) (cancellation: CancellationToken) : Task<Result<HttpResponse, HttpError>> =
        task {
            cancellation.ThrowIfCancellationRequested()
            lock gate (fun () -> requests.Add request)

            match HttpRequest.tryUri request.Url with
            | Some _ ->
                match respond request with
                | None -> return invalidOp $"InMemoryHttp: no reply scripted for {request}"
                | Some(Error e) -> return Error e
                | Some(Ok reply) ->
                    let delay =
                        if reply.Delay > TimeSpan.Zero then
                            wait (Some reply.Delay) request.Timeouts.Response HttpPhase.Response cancellation
                        else
                            Task.FromResult(Ok())

                    match! delay with
                    | Error e -> return Error e
                    | Ok() ->
                        lock gate (fun () -> openBodies <- openBodies + 1)

                        return
                            Ok
                                {
                                    Status = reply.Status
                                    Headers = reply.Headers |> List.map (fun (n, v) -> n.ToLowerInvariant(), v)
                                    Body = body request reply.Body
                                }
            | None -> return Error(InvalidRequest "the URL is not an absolute http or https URL")
        }

    member val Http: Http = { Send = send }

    /// Every request so far, in order.
    member _.Requests: HttpRequest list = lock gate (fun () -> List.ofSeq requests)

    /// Bodies that were neither read to their end nor closed.
    member _.OpenBodies: int = lock gate (fun () -> openBodies)
