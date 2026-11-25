module Fx.Transport.Http.Tests.LocalAccessTests

open System
open Expecto
open Fx.Transport.Http
open Fx.Transport.Http.LocalAccess

let private token = "0123456789abcdefghijABCDEFGHIJ-_xyz"

let private policy =
    { LocalAccess.policy token with
        AllowedOrigins = [ "http://127.0.0.1:7000"; "tauri://localhost" ]
    }

/// A request from a non-browser client with the right token.
let private request =
    {
        Host = Some "127.0.0.1:7000"
        Origin = None
        Authorization = [ "Bearer " + token ]
        WebSocketProtocols = []
    }

let private granted protocols =
    Ok
        {
            Protocols = protocols
            Bearer = Bearer.Listener
        }

/// A session token the host issued, by its id.
let private sessionToken = "S3ss10n-t0ken-0123456789abcdefghijklmnopq"

let private withSessions =
    { policy with
        Sessions =
            fun presented ->
                if presented = sessionToken then
                    Some "01JDB8Z3M4X2Q9V7K5N6R8T0WA"
                else
                    None
    }

[<Tests>]
let tests =
    testList
        "LocalAccess"
        [
            testList
                "policy"
                [
                    test "accepts a long base64url token and allows only loopback hosts" {
                        Expect.equal policy.AllowedHosts [ "localhost"; "127.0.0.1"; "[::1]" ] "hosts"
                        Expect.isEmpty (LocalAccess.policy token).AllowedOrigins "no origins by default"
                    }

                    test "rejects short tokens" {
                        Expect.throwsT<ArgumentException> (fun () -> LocalAccess.policy "short" |> ignore) "short"
                    }

                    test "rejects tokens that do not fit in a subprotocol" {
                        Expect.throwsT<ArgumentException>
                            (fun () -> LocalAccess.policy (String('a', 40) + "/=") |> ignore)
                            "base64 padding"
                    }

                    test "never shows the token" {
                        Expect.isFalse ((string policy).Contains token) (string policy)
                        Expect.isFalse ((string request).Contains token) (string request)
                    }
                ]

            testList
                "hostName"
                [
                    for host, expected in
                        [
                            "localhost", "localhost"
                            "localhost:8080", "localhost"
                            "127.0.0.1:1", "127.0.0.1"
                            "[::1]", "[::1]"
                            "[::1]:443", "[::1]"
                            "", ""
                        ] do
                        test $"'{host}'" { Expect.equal (hostName host) expected "host name" }
                ]

            testList
                "host"
                [
                    test "loopback names pass, with or without a port, in any case" {
                        for host in [ "localhost"; "LocalHost:1"; "127.0.0.1:65535"; "[::1]:2" ] do
                            Expect.equal (decide policy { request with Host = Some host }) (granted []) host
                    }

                    test "other hosts are rejected (DNS rebinding)" {
                        Expect.equal
                            (decide
                                policy
                                { request with
                                    Host = Some "evil.example:7000"
                                })
                            (Error(ForbiddenHost "evil.example:7000"))
                            "rebinding"

                        Expect.equal
                            (decide
                                policy
                                { request with
                                    Host = Some "127.0.0.2:7000"
                                })
                            (Error(ForbiddenHost "127.0.0.2:7000"))
                            "other address"
                    }

                    test "a missing host is rejected" {
                        Expect.equal (decide policy { request with Host = None }) (Error(ForbiddenHost "")) "missing"
                    }
                ]

            testList
                "origin"
                [
                    test "allowed origins pass, case-insensitively" {
                        for origin in [ "http://127.0.0.1:7000"; "TAURI://localhost" ] do
                            Expect.equal (decide policy { request with Origin = Some origin }) (granted []) origin
                    }

                    test "other origins are rejected, even with the right token" {
                        for origin in [ "https://evil.example"; "http://127.0.0.1:7001"; "null" ] do
                            Expect.equal
                                (decide policy { request with Origin = Some origin })
                                (Error(ForbiddenOrigin origin))
                                origin
                    }
                ]

            testList
                "token"
                [
                    test "a request without a token is rejected" {
                        Expect.equal (decide policy { request with Authorization = [] }) (Error MissingToken) "none"

                        Expect.equal
                            (decide
                                policy
                                { request with
                                    Authorization = [ "Basic dXNlcjpwYXNz" ]
                                })
                            (Error MissingToken)
                            "other scheme"
                    }

                    test "a wrong or empty token is rejected" {
                        for value in
                            [
                                "Bearer wrong"
                                "Bearer " + token + "x"
                                "Bearer "
                                "Bearer " + token.ToUpperInvariant()
                            ] do
                            Expect.equal
                                (decide
                                    policy
                                    { request with
                                        Authorization = [ value ]
                                    })
                                (Error InvalidToken)
                                value
                    }

                    test "the scheme is case-insensitive" {
                        Expect.equal
                            (decide
                                policy
                                { request with
                                    Authorization = [ "bearer " + token ]
                                })
                            (granted [])
                            "lower case"
                    }

                    test "a token subprotocol is accepted and not offered for selection" {
                        let viaProtocol =
                            { request with
                                Authorization = []
                                WebSocketProtocols = [ "jsonrpc"; tokenProtocolPrefix + token; "other" ]
                            }

                        Expect.equal (decide policy viaProtocol) (granted [ "jsonrpc"; "other" ]) "protocols"
                    }

                    test "every token offered must be right" {
                        let mixed =
                            { request with
                                WebSocketProtocols = [ tokenProtocolPrefix + "wrong" ]
                            }

                        Expect.equal (decide policy mixed) (Error InvalidToken) "header right, protocol wrong"
                    }

                    test "a session token the host issued is a bearer too" {
                        let viaSession =
                            { request with
                                Authorization = [ "Bearer " + sessionToken ]
                            }

                        Expect.equal
                            (decide withSessions viaSession)
                            (Ok
                                {
                                    Protocols = []
                                    Bearer = Bearer.Session "01JDB8Z3M4X2Q9V7K5N6R8T0WA"
                                })
                            "by its id"

                        Expect.equal (decide policy viaSession) (Error InvalidToken) "a listener without sessions"

                        Expect.equal
                            (decide
                                withSessions
                                { viaSession with
                                    WebSocketProtocols = [ "jsonrpc"; tokenProtocolPrefix + sessionToken ]
                                })
                            (Ok
                                {
                                    Protocols = [ "jsonrpc" ]
                                    Bearer = Bearer.Session "01JDB8Z3M4X2Q9V7K5N6R8T0WA"
                                })
                            "the same token twice"

                        Expect.equal (decide withSessions request) (granted []) "the listener's token still works"
                    }

                    test "two different tokens are refused, even if both are good" {
                        let both =
                            { request with
                                Authorization = [ "Bearer " + sessionToken ]
                                WebSocketProtocols = [ tokenProtocolPrefix + token ]
                            }

                        Expect.equal (decide withSessions both) (Error InvalidToken) "ambiguous"
                    }

                    test "the host and origin are checked before the token" {
                        let bad =
                            {
                                Host = Some "evil.example"
                                Origin = Some "https://evil.example"
                                Authorization = []
                                WebSocketProtocols = []
                            }

                        Expect.equal (decide policy bad) (Error(ForbiddenHost "evil.example")) "host first"

                        Expect.equal
                            (decide policy { bad with Host = Some "localhost" })
                            (Error(ForbiddenOrigin "https://evil.example"))
                            "origin second"
                    }
                ]

            test "reasons do not echo the request" {
                Expect.equal (reason (ForbiddenHost "evil.example")) "forbidden host" "host"
                Expect.equal (reason (ForbiddenOrigin "https://evil.example")) "forbidden origin" "origin"
                Expect.equal (reason MissingToken) "missing token" "missing"
                Expect.equal (reason InvalidToken) "invalid token" "invalid"
            }

            test "status codes" {
                Expect.equal (statusCode MissingToken) 401 "missing token"
                Expect.equal (statusCode InvalidToken) 401 "invalid token"
                Expect.equal (statusCode (ForbiddenHost "x")) 403 "host"
                Expect.equal (statusCode (ForbiddenOrigin "x")) 403 "origin"
            }
        ]
