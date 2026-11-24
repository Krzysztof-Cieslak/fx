module Fx.Transport.Http.Tests.LocalServerTests

open System
open System.Threading.Tasks
open Expecto
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer

let private handler (_: Request) : Task = Task.CompletedTask

let private endpoints =
    [
        Endpoint.get "/status" handler
        Endpoint.post "/rpc" handler
        Endpoint.webSocket "/rpc" [ "jsonrpc" ] (fun _ _ -> Task.CompletedTask)
        Endpoint.get "/events" handler
    ]

let private routed method path = string (route endpoints method path)

[<Tests>]
let tests =
    testList
        "LocalServer"
        [
            testList
                "route"
                [
                    test "finds an endpoint by method and path" {
                        Expect.equal (routed "GET" "/status") "found GET /status" "get"
                        Expect.equal (routed "POST" "/rpc") "found POST /rpc" "post"
                    }

                    test "a WebSocket endpoint is a GET" {
                        Expect.equal (routed "GET" "/rpc") "found WebSocket /rpc" "websocket"
                    }

                    test "another method on a known path lists the allowed methods" {
                        Expect.equal (routed "PUT" "/rpc") "wrong method, allowed POST, GET" "put"
                        Expect.equal (routed "OPTIONS" "/status") "wrong method, allowed GET" "preflight"
                    }

                    test "paths and methods match exactly" {
                        Expect.equal (routed "GET" "/Status") "not found" "case"
                        Expect.equal (routed "GET" "/status/") "not found" "trailing slash"
                        Expect.equal (routed "get" "/status") "wrong method, allowed GET" "method case"
                        Expect.equal (routed "GET" "") "not found" "empty"
                    }
                ]

            test "options have safe defaults" {
                let access = LocalAccess.policy (String('t', 32))
                let o = options (Loopback 0) access endpoints
                Expect.equal o.MaxRequestBodySize (4L * 1024L * 1024L) "body limit"
                Expect.equal o.ShutdownTimeout (TimeSpan.FromSeconds 5.0) "shutdown"
                Expect.equal (string o) "loopback port 0, 4 endpoints" "shown"
            }
        ]
