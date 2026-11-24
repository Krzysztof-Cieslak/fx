/// Access control for local listeners (spec §18: "localhost listeners require token + Origin check").
///
/// Pure: the server reduces a request to `RequestFacts` and acts on the decision. A request must
/// name an allowed host (against DNS rebinding), must not come from a foreign browser origin, and
/// must carry the bearer token. There is no CORS: responses never carry `Access-Control-Allow-*`.
module Fx.Transport.Http.LocalAccess

open System
open System.Security.Cryptography
open System.Text
open Fx

/// Who may use a local listener.
type AccessPolicy =
    {
        /// The secret every request must present. Use `LocalAccess.policy` to create a policy: it
        /// checks that the token is long enough and fits in a WebSocket subprotocol.
        Token: string
        /// Browser origins that may connect, as serialized origins (`scheme://host[:port]`), compared
        /// case-insensitively. Requests without an `Origin` header (non-browser clients) pass.
        AllowedOrigins: string list
        /// Host names, without a port, that the `Host` header may name.
        AllowedHosts: string list
    }

    // Never shows the token.
    override this.ToString() =
        let origins = String.Join(", ", this.AllowedOrigins)
        let hosts = String.Join(", ", this.AllowedHosts)
        $"token (hidden), origins [{origins}], hosts [{hosts}]"

/// What the access check reads from a request.
type RequestFacts =
    {
        /// The `Host` header.
        Host: string option
        /// The `Origin` header.
        Origin: string option
        /// Every `Authorization` header value.
        Authorization: string list
        /// The subprotocols offered in `Sec-WebSocket-Protocol` headers, in order.
        WebSocketProtocols: string list
    }

    // Never shows credentials.
    override this.ToString() =
        let show = Option.defaultValue "-"
        $"host {show this.Host}, origin {show this.Origin}"

/// An allowed request.
type Granted =
    {
        /// The offered WebSocket subprotocols without the one that carries the token. A server must
        /// select from these and never echo the token protocol back.
        Protocols: string list
    }

    override this.ToString() =
        "granted, protocols [" + String.Join(", ", this.Protocols) + "]"

/// Why a request was rejected.
type Denied =
    /// The `Host` header is missing or names another host, e.g. after DNS rebinding.
    | ForbiddenHost of host: string
    /// A browser page from another origin.
    | ForbiddenOrigin of origin: string
    | MissingToken
    | InvalidToken

    override this.ToString() =
        match this with
        | ForbiddenHost host -> $"forbidden host '{host}'"
        | ForbiddenOrigin origin -> $"forbidden origin '{origin}'"
        | MissingToken -> "missing token"
        | InvalidToken -> "invalid token"

/// Host names of the loopback interface, as they appear in a `Host` header.
let loopbackHosts = [ "localhost"; "127.0.0.1"; "[::1]" ]

/// Prefix of the WebSocket subprotocol that carries the token, for clients that cannot set an
/// `Authorization` header (browsers): `<product>.bearer.<token>`.
let tokenProtocolPrefix = Product.Name + ".bearer."

/// Shortest accepted token: 32 characters is 192 bits of base64url.
let minTokenLength = 32

let private isTokenChar (c: char) =
    Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'

/// A policy for `token` that allows loopback host names and no browser origins.
/// The token must be base64url (so it fits in a subprotocol) and at least `minTokenLength` long.
let policy (token: string) : AccessPolicy =
    if token.Length < minTokenLength || not (String.forall isTokenChar token) then
        invalidArg (nameof token) $"a token must be at least {minTokenLength} base64url characters"

    {
        Token = token
        AllowedOrigins = []
        AllowedHosts = loopbackHosts
    }

/// The host name in a `Host` header value, without the port: `[::1]:80` is `[::1]`.
let hostName (host: string) : string =
    if host.StartsWith '[' then
        match host.IndexOf ']' with
        | -1 -> host
        | close -> host.Substring(0, close + 1)
    else
        match host.LastIndexOf ':' with
        | -1 -> host
        | colon -> host.Substring(0, colon)

let private bearer = "Bearer "

/// The token in an `Authorization: Bearer <token>` value. The scheme is case-insensitive.
let private bearerToken (value: string) : string option =
    if value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) then
        Some(value.Substring(bearer.Length).Trim())
    else
        None

/// Compares in constant time: hashing first hides the length as well.
let private sameToken (expected: string) (actual: string) : bool =
    let hash (s: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes s)

    CryptographicOperations.FixedTimeEquals(ReadOnlySpan(hash expected), ReadOnlySpan(hash actual))

let private contains (allowed: string list) (value: string) =
    allowed
    |> List.exists (fun a -> String.Equals(a, value, StringComparison.OrdinalIgnoreCase))

/// Decides whether a request may proceed. Checks the host, then the origin, then the token, so a
/// rejected host or origin reveals nothing about the token.
let decide (policy: AccessPolicy) (request: RequestFacts) : Result<Granted, Denied> =
    let host = defaultArg request.Host ""

    let isTokenProtocol (p: string) =
        p.StartsWith(tokenProtocolPrefix, StringComparison.Ordinal)

    let tokens =
        (request.Authorization |> List.choose bearerToken)
        @ (request.WebSocketProtocols
           |> List.filter isTokenProtocol
           |> List.map (fun p -> p.Substring tokenProtocolPrefix.Length))

    if not (contains policy.AllowedHosts (hostName host)) then
        Error(ForbiddenHost host)
    else
        match request.Origin with
        | Some origin when not (contains policy.AllowedOrigins origin) -> Error(ForbiddenOrigin origin)
        | _ ->
            if tokens.IsEmpty then
                Error MissingToken
            // Every token offered must be right, so a wrong one cannot hide behind a right one.
            elif not (tokens |> List.forall (sameToken policy.Token)) then
                Error InvalidToken
            else
                Ok
                    {
                        Protocols = request.WebSocketProtocols |> List.filter (isTokenProtocol >> not)
                    }

/// The response text for a rejection. It does not echo the request's host or origin.
let reason (denied: Denied) : string =
    match denied with
    | ForbiddenHost _ -> "forbidden host"
    | ForbiddenOrigin _ -> "forbidden origin"
    | MissingToken -> "missing token"
    | InvalidToken -> "invalid token"

/// The HTTP status code for a rejection.
let statusCode (denied: Denied) : int =
    match denied with
    | ForbiddenHost _
    | ForbiddenOrigin _ -> 403
    | MissingToken
    | InvalidToken -> 401
