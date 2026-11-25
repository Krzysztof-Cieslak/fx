namespace Fx.Components.Live

open System
open System.Buffers
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Components
open Fx.Sdk

/// Downloads over HTTP(S) with `HttpClient`. The only file of the installer that uses the network.
///
/// - Redirects are followed (release assets redirect to a CDN); .NET refuses a redirect from
///   `https` to `http`.
/// - The body is streamed to the file and hashed as it is written: nothing is held in memory, and
///   the limit is enforced on the bytes received, whatever `Content-Length` says.
/// - A read that makes no progress for `StallTimeout` fails as a network error (transient).
/// - Failures carry no exception messages (they differ between runtimes): the error kind only.
module HttpFetch =

    let StallTimeout = TimeSpan.FromSeconds 60.0

    /// A client for downloads: redirects on, no automatic decompression (the digest is of the bytes
    /// served), a connect timeout, and the product's user agent.
    let client () : HttpClient =
        let handler =
            new SocketsHttpHandler(
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds 30.0,
                PooledConnectionLifetime = TimeSpan.FromMinutes 5.0
            )

        let client = new HttpClient(handler, true)
        client.Timeout <- Timeout.InfiniteTimeSpan
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Product.Name + "/" + Product.Version)
        client

    let private network (url: string) (e: exn) =
        match e with
        | :? HttpRequestException as e -> Network(url, $"request failed ({e.HttpRequestError})")
        | _ -> Network(url, $"the connection failed ({e.GetType().Name})")

    let private download
        (client: HttpClient)
        (request: DownloadRequest)
        (cancellation: CancellationToken)
        : Task<Result<FileHash, FetchError>> =
        task {
            let url = request.Url

            try
                use message = new HttpRequestMessage(HttpMethod.Get, url)

                use! response =
                    client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation)

                let length = response.Content.Headers.ContentLength

                if not response.IsSuccessStatusCode then
                    return Error(HttpStatus(url, int response.StatusCode))
                elif length.HasValue && length.Value > request.MaxBytes then
                    return Error(FetchError.TooLarge(url, request.MaxBytes))
                else
                    use! body = response.Content.ReadAsStreamAsync cancellation

                    let file =
                        try
                            Ok(
                                new FileStream(
                                    request.Destination,
                                    FileMode.Create,
                                    FileAccess.Write,
                                    FileShare.None,
                                    81920,
                                    true
                                )
                            )
                        with e ->
                            Error(FetchError.WriteFailed(request.Destination, e.GetType().Name))

                    match file with
                    | Error e -> return Error e
                    | Ok file ->
                        use file = file
                        use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
                        let buffer = ArrayPool<byte>.Shared.Rent 81920
                        let mutable total = 0L
                        let mutable outcome = None

                        try
                            while outcome.IsNone do
                                use stall = CancellationTokenSource.CreateLinkedTokenSource cancellation
                                stall.CancelAfter StallTimeout

                                let! read =
                                    task {
                                        try
                                            let! n = body.ReadAsync(buffer, 0, buffer.Length, stall.Token)
                                            return Ok n
                                        with
                                        | :? OperationCanceledException when not cancellation.IsCancellationRequested ->
                                            return Error(Network(url, $"no data for {StallTimeout}"))
                                        | e when (e :? IOException || e :? HttpRequestException) ->
                                            return Error(network url e)
                                    }

                                match read with
                                | Error e -> outcome <- Some(Error e)
                                | Ok 0 ->
                                    do! file.FlushAsync cancellation
                                    let digest = Convert.ToHexStringLower(hash.GetHashAndReset())
                                    outcome <- Some(Ok { Size = total; Digest = Sha256 digest })
                                | Ok n ->
                                    total <- total + int64 n

                                    if total > request.MaxBytes then
                                        outcome <- Some(Error(FetchError.TooLarge(url, request.MaxBytes)))
                                    else
                                        hash.AppendData(buffer, 0, n)

                                        try
                                            do! file.WriteAsync(buffer, 0, n, cancellation)
                                        with e when (e :? IOException || e :? UnauthorizedAccessException) ->
                                            outcome <-
                                                Some(
                                                    Error(FetchError.WriteFailed(request.Destination, e.GetType().Name))
                                                )
                        finally
                            ArrayPool<byte>.Shared.Return buffer

                        return Option.get outcome
            with
            | :? OperationCanceledException as e when cancellation.IsCancellationRequested -> return raise e
            | :? HttpRequestException as e -> return Error(network url e)
            | :? IOException as e -> return Error(network url e)
            | :? OperationCanceledException as e -> return Error(network url e)
        }

    /// A fetcher over a client (`client ()`), which it does not dispose.
    let create (client: HttpClient) : Fetch = { Download = download client }
