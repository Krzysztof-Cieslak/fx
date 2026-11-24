/// Kestrel HTTP endpoints as RequestDelegate values: methods, query strings, request bodies,
/// status codes, limits and failures.
module Fx.Aot.Scenarios.Kestrel.Http

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Net.Http.Headers
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Kestrel.Support
open Fx.Transport.Http.LocalServer

let private text (context: HttpContext) (body: string) : Task =
    context.Response.ContentType <- "text/plain; charset=utf-8"
    context.Response.WriteAsync(body, context.RequestAborted)

let private readBody (context: HttpContext) : Task<byte array> =
    task {
        use buffer = new MemoryStream()
        do! context.Request.Body.CopyToAsync(buffer, context.RequestAborted)
        return buffer.ToArray()
    }

let private endpoints =
    [
        Endpoint.get "/hello" (fun r -> text r.Context "hello")
        Endpoint.get "/greet" (fun r ->
            let name =
                match r.Context.Request.Query["name"] |> Seq.tryHead with
                | Some(NonNull name) -> name
                | _ -> "nobody"

            text r.Context $"hello, {name}")
        Endpoint.post "/echo" (fun r ->
            task {
                let! body = readBody r.Context
                r.Context.Response.ContentType <- r.Context.Request.ContentType
                do! r.Context.Response.Body.WriteAsync(ReadOnlyMemory body, r.Context.RequestAborted)
            })
        Endpoint.post "/digest" (fun r ->
            task {
                let! body = readBody r.Context
                do! text r.Context $"{body.Length} bytes, sha256 {digest body}"
            })
        Endpoint.post "/sync-read" (fun r ->
            // Kestrel disallows synchronous I/O by default (AllowSynchronousIO = false).
            let outcome =
                try
                    r.Context.Request.Body.ReadByte() |> ignore
                    "allowed"
                with :? InvalidOperationException ->
                    "rejected (InvalidOperationException)"

            text r.Context $"synchronous read {outcome}")
        Endpoint.post "/items" (fun r ->
            r.Context.Response.StatusCode <- 201
            r.Context.Response.Headers.Location <- "/items/1"
            text r.Context "created")
        Endpoint.get "/empty" (fun r ->
            r.Context.Response.StatusCode <- 204
            Task.CompletedTask)
        Endpoint.get "/fail" (fun _ -> raise (InvalidOperationException "handler failed"))
    ]

let http =
    Scenario.task "kestrel-http" (fun () ->
        task {
            use! server = startLoopback id endpoints
            let address = baseUri server
            use http = authorizedClient Tcp address
            let log = ResizeArray<string>()

            let record (label: string) (response: Task<HttpResponseMessage>) =
                task {
                    use! r = within response
                    let! described = describe r
                    log.Add $"{label}: {described}"
                    return r
                }

            log.Add $"listening on 127.0.0.1 = {address.Host = Net.IPAddress.Loopback.ToString()}"
            log.Add $"ephemeral port = {address.Port > 0}"

            let! hello = record "GET /hello" (http.GetAsync "/hello")
            log.Add $"  content type = {header hello HeaderNames.ContentType}"
            log.Add $"  server header = {header hello HeaderNames.Server}"

            let! _ = record "GET /greet" (http.GetAsync "/greet?name=W%C3%B6rld%20%E2%9C%93")
            let! _ = record "GET /greet (no query)" (http.GetAsync "/greet")

            let echo = new StringContent("ping ✓", Encoding.UTF8, "text/plain")

            let! echoed = record "POST /echo" (http.PostAsync("/echo", echo))
            log.Add $"  content type = {header echoed HeaderNames.ContentType}"

            let large = Array.init (1024 * 1024) (fun i -> byte (i % 251))

            let! _ =
                record "POST /digest (1 MiB)" (http.PostAsync("/digest", new ByteArrayContent(large)))

            log.Add $"  expected sha256 {digest large}"

            // Over the 4 MiB limit. With Expect: 100-continue Kestrel answers before the body is sent.
            let tooLarge =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    "/digest",
                    Content = new ByteArrayContent(Array.zeroCreate (5 * 1024 * 1024))
                )

            tooLarge.Headers.ExpectContinue <- true
            let! _ = record "POST /digest (5 MiB)" (http.SendAsync tooLarge)

            let! _ =
                record "POST /sync-read" (http.PostAsync("/sync-read", new ByteArrayContent(utf8 "x")))

            let! created =
                record "POST /items" (http.PostAsync("/items", new ByteArrayContent([||])))

            log.Add $"  location = {header created HeaderNames.Location}"
            let! empty = record "GET /empty" (http.GetAsync "/empty")
            log.Add $"  content length = {header empty HeaderNames.ContentLength}"
            let! _ = record "GET /missing" (http.GetAsync "/missing")

            let! wrongMethod =
                record "PUT /echo" (http.PutAsync("/echo", new ByteArrayContent([||])))

            log.Add $"  allow = {header wrongMethod HeaderNames.Allow}"
            let! _ = record "GET /fail" (http.GetAsync "/fail")
            let! _ = record "GET /hello after a failure" (http.GetAsync "/hello")
            return List.ofSeq log
        })

let scenarios = [ http ]
