namespace Fx.Effects

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Time limits of one HTTP request. `None` waits as long as the scope lets it.
type HttpTimeouts =
    {
        /// From sending the request until the response headers have arrived (connecting included).
        Response: TimeSpan option
        /// Between two pieces of the body: a body that stalls for longer fails.
        Idle: TimeSpan option
    }

    override this.ToString() =
        let show =
            function
            | Some(t: TimeSpan) -> string t.TotalMilliseconds + " ms"
            | None -> "none"

        $"response {show this.Response}, idle {show this.Idle}"

/// An HTTP request: data in. Headers keep their order; names compare without case.
type HttpRequest =
    {
        /// `GET`, `POST`, …
        Method: string
        /// An absolute `http` or `https` URL.
        Url: string
        Headers: (string * string) list
        /// Empty for none.
        Body: byte array
        Timeouts: HttpTimeouts
    }

    override this.ToString() =
        $"{this.Method} {this.Url} ({this.Headers.Length} headers, {this.Body.Length} bytes)"

/// Where a request timed out.
[<RequireQualifiedAccess>]
type HttpPhase =
    /// Waiting for the response headers.
    | Response
    /// Waiting for the next piece of the body.
    | Body

    override this.ToString() =
        match this with
        | Response -> "response"
        | Body -> "body"

/// Why a request got no (complete) response: an expected failure. Reasons name the kind of failure,
/// never an exception's message (those differ between runtimes).
[<RequireQualifiedAccess>]
type HttpError =
    /// The request is malformed, e.g. its URL is not an absolute `http` or `https` URL.
    | InvalidRequest of reason: string
    /// The request was refused before it left, e.g. by a wrapper that allows only some origins.
    | Refused of reason: string
    /// No connection: name resolution, a refused connection, TLS.
    | ConnectFailed of reason: string
    /// The connection broke off before the response or its body was complete.
    | Reset of reason: string
    | TimedOut of HttpPhase

    override this.ToString() =
        match this with
        | InvalidRequest reason -> "invalid request: " + reason
        | Refused reason -> "refused: " + reason
        | ConnectFailed reason -> "connection failed: " + reason
        | Reset reason -> "connection reset: " + reason
        | TimedOut phase -> $"timed out waiting for the {phase}"

/// The body of a response, streamed: a record of functions like `Proc`, so that every read can be
/// recorded and replayed.
type HttpBody =
    {
        /// The next piece of the body; `None` at its end (and after `Close`). Cancelling the token
        /// fails it with an `OperationCanceledException`.
        Read: CancellationToken -> Task<Result<byte array option, HttpError>>
        /// Releases the connection. Idempotent; call it when done or when abandoning the body.
        Close: unit -> Task<unit>
    }

    override _.ToString() = "HttpBody"

/// A response whose headers have arrived. Any status is a response, not an error. Header names are
/// lower case; a header sent several times appears several times.
type HttpResponse =
    {
        Status: int
        Headers: (string * string) list
        Body: HttpBody
    }

    override this.ToString() =
        $"HTTP {this.Status} ({this.Headers.Length} headers)"

/// The HTTP client capability (spec §10, §11.3): what model wire adapters and auth flows use to
/// reach services. No redirects are followed: a redirect is a response like any other, so a request
/// never goes somewhere its sender did not name.
///
/// Interpreters: `SystemHttp` (Fx.Effects.Live), `InMemoryHttp` (Fx.Effects.Fakes), and the model
/// cassettes (`CassetteHttp` in Fx.Models), which replay recorded exchanges.
type Http =
    {
        /// Sends a request; completes when the response headers have arrived. The body streams.
        Send: HttpRequest -> CancellationToken -> Task<Result<HttpResponse, HttpError>>
    }

    override _.ToString() = "Http"

/// An environment that can make HTTP requests.
type IHttp =
    abstract Http: Http

module HttpTimeouts =
    /// No limits beyond the scope's cancellation.
    let none: HttpTimeouts = { Response = None; Idle = None }

module HttpHeaders =
    /// The first value of a header, comparing names without case.
    let tryFind (name: string) (headers: (string * string) list) : string option =
        headers
        |> List.tryFind (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd

    /// Every value of a header, in order.
    let findAll (name: string) (headers: (string * string) list) : string list =
        headers
        |> List.filter (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
        |> List.map snd

    /// Removes a header (every value).
    let remove (name: string) (headers: (string * string) list) : (string * string) list =
        headers
        |> List.filter (fun (n, _) -> not (String.Equals(n, name, StringComparison.OrdinalIgnoreCase)))

    /// Sets a header: replaces every value it had, at the end of the list.
    let set (name: string) (value: string) (headers: (string * string) list) : (string * string) list =
        remove name headers @ [ name, value ]

module HttpRequest =
    let private utf8 = Text.UTF8Encoding(false)

    let create (method: string) (url: string) : HttpRequest =
        {
            Method = method
            Url = url
            Headers = []
            Body = [||]
            Timeouts = HttpTimeouts.none
        }

    let get (url: string) : HttpRequest = create "GET" url

    /// The URL as an absolute `http` or `https` URI, if it is one.
    let tryUri (url: string) : Uri option =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, NonNull uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> Some uri
        | _ -> None

    let post (url: string) (body: byte array) : HttpRequest = { create "POST" url with Body = body }

    /// Sets a header, replacing any value it had.
    let withHeader (name: string) (value: string) (request: HttpRequest) : HttpRequest =
        { request with
            Headers = HttpHeaders.set name value request.Headers
        }

    let withTimeouts (timeouts: HttpTimeouts) (request: HttpRequest) : HttpRequest =
        { request with Timeouts = timeouts }

    /// The body as UTF-8 text (invalid bytes become U+FFFD).
    let bodyText (request: HttpRequest) : string = utf8.GetString request.Body

    /// The first value of a header.
    let header (name: string) (request: HttpRequest) : string option =
        HttpHeaders.tryFind name request.Headers

module HttpBody =
    /// A body that has ended.
    let empty: HttpBody =
        {
            Read = fun _ -> Task.FromResult(Ok None)
            Close = fun () -> Internal.completedUnit
        }

    /// A body served from memory, piece by piece.
    let ofChunks (chunks: byte array list) : HttpBody =
        let gate = obj ()
        let mutable remaining = chunks

        {
            Read =
                fun cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<Result<byte array option, HttpError>> cancellation
                    else
                        lock gate (fun () ->
                            match remaining with
                            | chunk :: rest ->
                                remaining <- rest
                                Task.FromResult(Ok(Some chunk))
                            | [] -> Task.FromResult(Ok None))
            Close =
                fun () ->
                    lock gate (fun () -> remaining <- [])
                    Internal.completedUnit
        }

    /// Reads the whole body, keeping at most `limit` bytes (the rest is read and dropped), then
    /// closes it, also when reading fails or is cancelled. For error responses, whose bodies are
    /// small but must be bounded.
    let readAll (limit: int) (body: HttpBody) (cancellation: CancellationToken) : Task<Result<byte array, HttpError>> =
        task {
            use kept = new MemoryStream()
            let mutable outcome = None
            let mutable failure = None

            try
                while outcome.IsNone do
                    match! body.Read cancellation with
                    | Error e -> outcome <- Some(Error e)
                    | Ok None -> outcome <- Some(Ok(kept.ToArray()))
                    | Ok(Some chunk) ->
                        let room = max 0 (limit - int kept.Length)
                        kept.Write(chunk, 0, min room chunk.Length)
            with e ->
                failure <- Some e

            do! body.Close()

            match failure with
            | Some e -> return Internal.rethrow e
            | None -> return Option.get outcome
        }

module Http =
    /// Sends a request. Cancelled with the scope.
    let send<'R when 'R :> IHttp and 'R :> IScope> (request: HttpRequest) : Eff<'R, Result<HttpResponse, HttpError>> =
        fun env -> env.Http.Send request env.Scope.Cancellation

    /// The next piece of a body. Cancelled with the scope.
    let read<'R when 'R :> IScope> (body: HttpBody) : Eff<'R, Result<byte array option, HttpError>> =
        fun env -> body.Read env.Scope.Cancellation

    let close (body: HttpBody) : Eff<'R, unit> = fun _ -> body.Close()

    /// Reads a whole body, keeping at most `limit` bytes, and closes it.
    let readAll<'R when 'R :> IScope> (limit: int) (body: HttpBody) : Eff<'R, Result<byte array, HttpError>> =
        fun env -> HttpBody.readAll limit body env.Scope.Cancellation
