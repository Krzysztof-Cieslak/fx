namespace Fx.Effects.Live

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Effects

/// The real HTTP client, over `HttpClient`. The only file of the core capabilities that uses the
/// network.
///
/// - Redirects are not followed: a request goes only where its sender named (model payloads must not
///   be forwarded elsewhere, spec §18). A redirect is a response like any other.
/// - The body streams: `Send` returns once the headers have arrived, and each `Read` returns what
///   the connection has (at most 64 KiB).
/// - Timeouts (`HttpRequest.Timeouts`) and the caller's cancellation are separate: a timeout is an
///   `HttpError`, the caller's cancellation an `OperationCanceledException`.
/// - Failures carry no exception messages (they differ between runtimes), only their kind.
module SystemHttp =

    let private bufferSize = 64 * 1024

    /// A client: no redirects, no automatic decompression (bodies are what the server sent; adapters
    /// that want compression ask for it), a connect timeout, and the product's user agent.
    let client () : HttpClient =
        let handler =
            new SocketsHttpHandler(
                AllowAutoRedirect = false,
                AutomaticDecompression = Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds 30.0,
                PooledConnectionLifetime = TimeSpan.FromMinutes 5.0,
                UseCookies = false
            )

        let client = new HttpClient(handler, true)
        client.Timeout <- Timeout.InfiniteTimeSpan
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Product.Name + "/" + Product.Version)
        client

    let private failure (e: exn) : HttpError =
        match e with
        | :? HttpRequestException as e ->
            match e.HttpRequestError with
            | HttpRequestError.NameResolutionError -> ConnectFailed "name resolution failed"
            | HttpRequestError.ConnectionError -> ConnectFailed "the connection failed"
            | HttpRequestError.SecureConnectionError -> ConnectFailed "TLS failed"
            | HttpRequestError.ProxyTunnelError -> ConnectFailed "the proxy tunnel failed"
            | HttpRequestError.ResponseEnded -> Reset "the response ended early"
            | HttpRequestError.InvalidResponse -> Reset "the response was invalid"
            | other -> Reset $"the request failed ({other})"
        | :? IOException -> Reset "the connection broke off"
        | e -> Reset $"the request failed ({e.GetType().Name})"

    /// A token source cancelled with the caller or after the limit.
    let private limited (cancellation: CancellationToken) (limit: TimeSpan option) =
        let source = CancellationTokenSource.CreateLinkedTokenSource cancellation

        match limit with
        | Some limit -> source.CancelAfter limit
        | None -> ()

        source

    let private message (request: HttpRequest) (uri: Uri) : HttpRequestMessage =
        let message = new HttpRequestMessage(HttpMethod(request.Method), uri)
        let content = new ByteArrayContent(request.Body)

        for name, value in request.Headers do
            if not (message.Headers.TryAddWithoutValidation(name, value)) then
                content.Headers.TryAddWithoutValidation(name, value) |> ignore

        if request.Body.Length > 0 || request.Method <> "GET" && request.Method <> "HEAD" then
            message.Content <- content
        else
            content.Dispose()

        message

    let private body (response: HttpResponseMessage) (stream: Stream) (idle: TimeSpan option) : HttpBody =
        let gate = obj ()
        let mutable closed = false

        let close () =
            let first =
                lock gate (fun () ->
                    let first = not closed
                    closed <- true
                    first)

            if first then
                stream.Dispose()
                response.Dispose()

        let read (cancellation: CancellationToken) : Task<Result<byte array option, HttpError>> =
            task {
                if lock gate (fun () -> closed) then
                    return Ok None
                else
                    use source = limited cancellation idle
                    let buffer = Array.zeroCreate<byte> bufferSize

                    try
                        let! n = stream.ReadAsync(buffer, 0, buffer.Length, source.Token)

                        if n = 0 then
                            close ()
                            return Ok None
                        else
                            return Ok(Some(Array.sub buffer 0 n))
                    with
                    | :? OperationCanceledException as e when cancellation.IsCancellationRequested ->
                        close ()
                        return raise e
                    | :? OperationCanceledException ->
                        close ()
                        return Error(TimedOut HttpPhase.Body)
                    | :? ObjectDisposedException -> return Ok None
                    | e ->
                        close ()
                        return Error(failure e)
            }

        {
            Read = read
            Close =
                fun () ->
                    close ()
                    Task.FromResult()
        }

    let private send
        (client: HttpClient)
        (request: HttpRequest)
        (cancellation: CancellationToken)
        : Task<Result<HttpResponse, HttpError>> =
        task {
            cancellation.ThrowIfCancellationRequested()

            let built =
                match HttpRequest.tryUri request.Url with
                | Some uri ->
                    try
                        Ok(message request uri)
                    with
                    | :? FormatException
                    | :? ArgumentException -> Error(InvalidRequest "the method is not a valid HTTP method")
                | None -> Error(InvalidRequest "the URL is not an absolute http or https URL")

            match built with
            | Error e -> return Error e
            | Ok message ->
                use message = message
                use source = limited cancellation request.Timeouts.Response

                try
                    let! response =
                        client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, source.Token)

                    try
                        let! stream = response.Content.ReadAsStreamAsync source.Token

                        let headers =
                            [
                                for header in response.Headers do
                                    for value in header.Value do
                                        header.Key.ToLowerInvariant(), value
                                for header in response.Content.Headers do
                                    for value in header.Value do
                                        header.Key.ToLowerInvariant(), value
                            ]

                        return
                            Ok
                                {
                                    Status = int response.StatusCode
                                    Headers = headers
                                    Body = body response stream request.Timeouts.Idle
                                }
                    with e ->
                        response.Dispose()
                        return raise e
                with
                | :? OperationCanceledException as e when cancellation.IsCancellationRequested -> return raise e
                | :? OperationCanceledException -> return Error(TimedOut HttpPhase.Response)
                | :? HttpRequestException as e -> return Error(failure e)
                | :? IOException as e -> return Error(failure e)
        }

    /// The capability over a client (`client ()`), which it does not dispose.
    let create (client: HttpClient) : Http = { Send = send client }
