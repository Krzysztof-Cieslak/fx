/// Prints how long the variant takes from `main` to listening and to its first response, in ms.
/// Not formatted with Fantomas, which cannot format code with several `#if` branches.
module Probe

open System
open System.Diagnostics
open System.Net.Http
#if SERVER
open System.Net
open System.Net.WebSockets
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
#endif

let private ms (stopwatch: Stopwatch) = stopwatch.Elapsed.TotalMilliseconds.ToString "F1"

[<EntryPoint>]
let main _ =
    let stopwatch = Stopwatch.StartNew()
#if !CLIENT
    Console.WriteLine "hello"
#elif !SERVER
    use client = new HttpClient()

    try
        client.GetStringAsync("http://127.0.0.1:1/").GetAwaiter().GetResult() |> ignore
    with :? HttpRequestException ->
        Console.WriteLine "no server, as expected"
#else
#if SLIM
    let builder = WebApplication.CreateSlimBuilder()
    builder.Logging.ClearProviders() |> ignore
#else
    let builder = WebApplication.CreateEmptyBuilder(WebApplicationOptions())
    builder.WebHost.UseKestrelCore() |> ignore
#if ROUTING
    builder.Services.AddRoutingCore() |> ignore
#endif
#endif
    builder.WebHost.ConfigureKestrel(fun kestrel -> kestrel.Listen(IPAddress.Loopback, 0)) |> ignore
    let app = builder.Build()

    let handle =
        RequestDelegate(fun context ->
#if WS
            if context.WebSockets.IsWebSocketRequest then
                task {
                    use! socket = context.WebSockets.AcceptWebSocketAsync()
                    let buffer = Array.zeroCreate<byte> 1024
                    let! received = socket.ReceiveAsync(ArraySegment buffer, CancellationToken.None)
                    let echo = ArraySegment(buffer, 0, received.Count)
                    do! socket.SendAsync(echo, received.MessageType, true, CancellationToken.None)
                    do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                }
            else
#endif
                context.Response.WriteAsync "ok")
#if WS
    app.UseWebSockets() |> ignore
#endif
#if ROUTING
    app.MapGet("/", handle) |> ignore
#else
    app.Run handle
#endif
    app.StartAsync().GetAwaiter().GetResult()
    let listening = ms stopwatch
    let address = app.Urls |> Seq.find (fun a -> a.StartsWith "http://127.0.0.1:")
    use client = new HttpClient()
    let body = client.GetStringAsync(address).GetAwaiter().GetResult()
    let firstResponse = ms stopwatch
#if WS
    use socket = new ClientWebSocket()
    let none = CancellationToken.None
    socket.ConnectAsync(Uri(address.Replace("http://", "ws://")), none).GetAwaiter().GetResult()
    socket.SendAsync(ArraySegment "ping"B, WebSocketMessageType.Text, true, none).GetAwaiter().GetResult()
    let echo = Array.zeroCreate<byte> 16
    let received = socket.ReceiveAsync(ArraySegment echo, none).GetAwaiter().GetResult()

    if received.Count <> 4 then
        failwith "no WebSocket echo"

    // Answer the server's close, or the stop waits for the handler until the shutdown timeout.
    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", none).GetAwaiter().GetResult()
#endif
    app.StopAsync().GetAwaiter().GetResult()
    Console.WriteLine $"{body}: listening {listening} ms, first response {firstResponse} ms"
#endif
    Console.WriteLine $"total {ms stopwatch} ms"
    0
