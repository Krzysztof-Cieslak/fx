/// The local-listener security prototype (spec §18): bearer token, Origin allowlist and Host check on
/// HTTP requests and WebSocket upgrades, and no CORS. Rejected requests never reach a handler.
module Fx.Aot.Scenarios.Kestrel.Access

open System
open System.Net.Http
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Net.Http.Headers
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Kestrel.Support
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer

/// The only browser origin allowed, e.g. a desktop client's web view.
let private allowedOrigin = "tauri://localhost"

let private foreignOrigin = "https://evil.example"

let access =
    Scenario.task "kestrel-local-access" (fun () ->
        task {
            let mutable handled = 0

            let endpoints =
                [
                    Endpoint.get "/status" (fun r ->
                        Interlocked.Increment &handled |> ignore
                        r.Context.Response.WriteAsync "ok")
                    Endpoint.webSocket "/rpc" [ "jsonrpc" ] (fun r socket ->
                        task {
                            Interlocked.Increment &handled |> ignore
                            let! message = WebSocketMessages.receive 1024 socket r.Stopping

                            match message with
                            | WebSocketMessages.Message m -> do! WebSocketMessages.send socket m r.Stopping
                            | _ -> ()

                            // CloseAsync waits for the client's close frame: the handshake completes.
                            do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", r.Stopping)
                        })
                ]

            let allowOrigin (o: Options) =
                { o with
                    Access =
                        { o.Access with
                            AllowedOrigins = [ allowedOrigin ]
                        }
                }

            use! server = startLoopback allowOrigin endpoints
            let address = baseUri server
            use http = client Tcp address
            let log = ResizeArray<string>()

            let send (label: string) (configure: HttpRequestMessage -> unit) =
                task {
                    use request = new HttpRequestMessage(HttpMethod.Get, "/status")
                    configure request
                    use! response = within (http.SendAsync request)
                    let! described = describe response
                    let challenge = header response HeaderNames.WWWAuthenticate
                    let cors = header response HeaderNames.AccessControlAllowOrigin
                    log.Add $"HTTP {label}: {described} (challenge {challenge}, CORS {cors})"
                }

            let bearer (value: string) (request: HttpRequestMessage) =
                request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, value)
                |> ignore

            let authorized = bearer ("Bearer " + token)

            let andAlso (second: HttpRequestMessage -> unit) (request: HttpRequestMessage) =
                authorized request
                second request

            let origin (value: string) (request: HttpRequestMessage) =
                request.Headers.TryAddWithoutValidation(HeaderNames.Origin, value) |> ignore

            do! send "no token" ignore
            do! send "wrong token" (bearer "Bearer wrong-token")
            do! send "basic credentials" (bearer "Basic dXNlcjpwYXNz")
            do! send "token" authorized
            do! send "token, lower-case scheme" (bearer ("bearer " + token))

            do!
                send "token in the query string" (fun r ->
                    r.RequestUri <- Uri("/status?access_token=" + token, UriKind.Relative))

            do! send "token, foreign origin" (andAlso (origin foreignOrigin))
            do! send "token, allowed origin" (andAlso (origin allowedOrigin))
            do! send "token, origin null" (andAlso (origin "null"))
            do! send "token, rebound host" (andAlso (fun r -> r.Headers.Host <- $"evil.example:{address.Port}"))
            do! send "token, host localhost" (andAlso (fun r -> r.Headers.Host <- $"localhost:{address.Port}"))

            // A CORS preflight carries no credentials, so it never succeeds and never gets CORS headers.
            let preflight (originValue: string) (r: HttpRequestMessage) =
                r.Method <- HttpMethod.Options
                origin originValue r

                r.Headers.TryAddWithoutValidation(HeaderNames.AccessControlRequestMethod, "POST")
                |> ignore

                r.Headers.TryAddWithoutValidation(HeaderNames.AccessControlRequestHeaders, "authorization")
                |> ignore

            do! send "preflight, allowed origin" (preflight allowedOrigin)
            do! send "preflight, foreign origin" (preflight foreignOrigin)

            let rpc = webSocketUri Tcp server "/rpc"

            let upgrade (label: string) (configure: ClientWebSocketOptions -> unit) =
                task {
                    match! connectWebSocket Tcp rpc configure with
                    | Error status -> log.Add $"WebSocket {label}: rejected {status}"
                    | Ok socket ->
                        use socket = socket

                        let selected =
                            match socket.SubProtocol with
                            | null -> "none"
                            | p -> p

                        do! WebSocketMessages.send socket (WebSocketMessages.Text "ping") CancellationToken.None
                        let! echo = within (WebSocketMessages.receive 1024 socket CancellationToken.None)
                        let! closed = within (WebSocketMessages.receive 1024 socket CancellationToken.None)
                        do! socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                        log.Add $"WebSocket {label}: connected, subprotocol {selected}, echo {echo}, then {closed}"
                }

            do! upgrade "no token" ignore
            do! upgrade "wrong token" (fun o -> o.SetRequestHeader(HeaderNames.Authorization, "Bearer wrong-token"))
            do! upgrade "token" withToken

            do!
                upgrade "token, foreign origin" (fun o ->
                    withToken o
                    o.SetRequestHeader(HeaderNames.Origin, foreignOrigin))

            do!
                upgrade "token, allowed origin" (fun o ->
                    withToken o
                    o.SetRequestHeader(HeaderNames.Origin, allowedOrigin))

            // Browsers cannot set headers on a WebSocket: the token travels as a subprotocol instead.
            do!
                upgrade "token subprotocol, allowed origin" (fun o ->
                    o.AddSubProtocol "jsonrpc"
                    o.AddSubProtocol(LocalAccess.tokenProtocolPrefix + token)
                    o.SetRequestHeader(HeaderNames.Origin, allowedOrigin))

            do!
                upgrade "wrong token subprotocol" (fun o ->
                    o.AddSubProtocol "jsonrpc"
                    o.AddSubProtocol(LocalAccess.tokenProtocolPrefix + "wrong-token"))

            do!
                upgrade "token, rebound host" (fun o ->
                    withToken o
                    o.SetRequestHeader(HeaderNames.Host, "evil.example"))

            log.Add $"handler calls = {handled} (only allowed requests reach handlers)"
            return List.ofSeq log
        })

let scenarios = [ access ]
