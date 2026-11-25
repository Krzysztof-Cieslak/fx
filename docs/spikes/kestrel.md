# Spike: Kestrel, WebSocket and SSE under Native AOT (T004)

Spec: §3 (topology), §17 (protocols), §18 (localhost listeners), §20 (Kestrel + `RequestDelegate`).
Feeds T107 (JSON-RPC transports) and T129 (host server).

## Verdict

**Kestrel works under Native AOT from F#, on all eight targets.** HTTP endpoints, WebSocket, server-sent
events, Unix domain sockets (Linux, macOS and Windows) and Windows named pipes behave identically on the JIT
and as a native binary. The AOT publish has no trim or AOT warnings. Evidence:

- six `kestrel-*` scenarios in the JIT-vs-native diff pass on `linux-x64`, `linux-arm64`, `linux-musl-x64`,
  `linux-musl-arm64`, `osx-arm64`, `osx-x64`, `win-x64` and `win-arm64` (CI's Native AOT steps);
- the same scenarios run twice and in parallel on the JIT in `Fx.Aot.Tests` on Linux, macOS and Windows;
- the probe (`docs/spikes/kestrel-probe`) publishes each feature on its own, and any IL2xxx/IL3xxx
  warning fails it.

Changes to the spec:

- §20: build the host with **`WebApplication.CreateEmptyBuilder`**, not `CreateSlimBuilder`.
  `CreateSlimBuilder` works under AOT, but it reads `appsettings.json` from the working directory, which
  is a project directory for fx, and binds the `Kestrel:Endpoints` listeners it finds there (see
  [below](#the-slim-builder-reads-the-working-directory)).
- §18: localhost listeners also check the `Host` header (DNS rebinding) and send no CORS headers.

## What was built

| Path | What |
|---|---|
| `src/Fx.Transport.Http/LocalAccess.fs` | Pure access policy: `Host` allowlist, `Origin` allowlist, bearer token (spec §18) |
| `src/Fx.Transport.Http/Sse.fs` | Pure `text/event-stream` framing |
| `src/Fx.Transport.Http/WebSocketMessages.fs` | Whole messages over WebSocket frames, with a size limit |
| `src/Fx.Transport.Http/LocalServer.fs` | The Kestrel shell: listeners, one `RequestDelegate`, exact-path routing, SSE writer |
| `tests/Fx.Transport.Http.Tests` | Unit tests: policy decisions, framing round-trips through the BCL `SseParser`, in-memory WebSockets |
| `tests/aot/Fx.Aot.Scenarios/Scenarios/Kestrel/` | The `kestrel-*` scenarios below |
| `docs/spikes/kestrel-probe/` | Size and startup probe; `kestrel-probe.yml` runs it on every target |

| Scenario | Proves |
|---|---|
| `kestrel-http` | `RequestDelegate` endpoints: GET/POST, query strings, request bodies (1 MiB), 413 over the body limit, 201/204/404/405/500, no `Server` header, synchronous I/O rejected, the server survives a failing handler |
| `kestrel-local-access` | Token, `Origin` and `Host` checks on HTTP and on WebSocket upgrades; token as a subprotocol; no CORS headers; only allowed requests reach handlers |
| `kestrel-websocket` | Text/binary echo, subprotocol selection, a 1 MiB message in 3 fragments, 512 KiB text, close handshake from either side, 1009 over the size limit, invalid UTF-8, an aborted client |
| `kestrel-sse` | Headers, one flush per event (the server writes event *n+1* only after the client acknowledged event *n*), comments, `Last-Event-ID` resume, client disconnect seen by the server, server stop ends open streams cleanly |
| `kestrel-unix-socket` | HTTP, SSE and WebSocket over a Unix domain socket, on all three OSes; the socket file is removed on stop, so a restart on the same path works |
| `kestrel-named-pipe` | The same over a Windows named pipe. **Gated to Windows**: Kestrel's named-pipe transport exists only there |
| `kestrel-slim-builder` | `CreateSlimBuilder` with endpoint routing (`MapGet`/`MapPost` with `RequestDelegate`, route values) works; it binds `Kestrel:Endpoints` from `appsettings.json`, `CreateEmptyBuilder` does not |

## Recipe

```fsharp
let builder = WebApplication.CreateEmptyBuilder(WebApplicationOptions())   // no configuration sources
builder.WebHost.UseKestrelCore() |> ignore                                  // Kestrel, sockets transport
// builder.WebHost.UseNamedPipes() |> ignore                                // only for ListenNamedPipe

builder.WebHost.ConfigureKestrel(fun kestrel ->
    kestrel.AddServerHeader <- false
    kestrel.Limits.MaxRequestBodySize <- 4L * 1024L * 1024L
    kestrel.Listen(IPAddress.Loopback, 0, fun l -> l.Protocols <- HttpProtocols.Http1))
    // or kestrel.ListenUnixSocket(path, ...) / kestrel.ListenNamedPipe(name, ...)
|> ignore

builder.Services.Configure<HostOptions>(fun (o: HostOptions) -> o.ShutdownTimeout <- TimeSpan.FromSeconds 5.0)
|> ignore
// Replace ConsoleLifetime, which takes over Ctrl+C and SIGTERM (see "Process signals").
builder.Services.AddSingleton<IHostLifetime>(LibraryLifetime()) |> ignore   // no-op IHostLifetime

let app = builder.Build()
app.UseWebSockets(WebSocketOptions(KeepAliveInterval = TimeSpan.FromSeconds 30.0)) |> ignore
app.Run(RequestDelegate(fun context -> (* access check, then dispatch *) ...))
do! app.StartAsync()
let address = app.Urls |> Seq.head   // http://127.0.0.1:<port>, http://unix:<path>, http://pipe:/<name>
```

`LocalServer.start` is this recipe, with the access check and exact-path routing. Endpoints are values:
`Endpoint.get` and `Endpoint.post` take a `Request -> Task`, `Endpoint.webSocket` a
`Request -> WebSocket -> Task` that runs after the upgrade.

## APIs under Native AOT in F#

**Use**

| API | Notes |
|---|---|
| `WebApplication.CreateEmptyBuilder` + `WebHost.UseKestrelCore()` | Minimal host: no configuration, no logging providers, no HTTPS, no hosting startup assemblies |
| `ConfigureKestrel`, `Listen(IPAddress.Loopback, 0)`, `ListenUnixSocket`, `ListenNamedPipe` (+ `UseNamedPipes`) | Port 0 gives an ephemeral port; read it from `app.Urls` after `StartAsync` |
| `ListenOptions.Protocols <- HttpProtocols.Http1` | Cleartext HTTP/2 needs prior knowledge, and WebSocket over HTTP/2 uses `CONNECT` instead of `GET`: HTTP/1.1 keeps one code path |
| `app.Run(RequestDelegate ...)`, `app.Use(Func<RequestDelegate, RequestDelegate>)`, `app.Use(fun ctx next -> ...)` | No warnings |
| `MapGet`/`MapPost`/`Map` with a `RequestDelegate` | Endpoint routing works (+0.8–1.0 MiB, needs `Services.AddRoutingCore()` with the empty builder). Not needed for a few exact paths |
| `app.UseWebSockets`, `context.WebSockets.AcceptWebSocketAsync(WebSocketAcceptContext(SubProtocol = ...))` | Server `WebSocket` is the BCL `ManagedWebSocket` |
| `Response.WriteAsync(string)`, `Response.Body.WriteAsync`/`FlushAsync`, `Request.Body.CopyToAsync`, `Response.StartAsync` | Async only: Kestrel rejects synchronous I/O (`InvalidOperationException`) |
| `IHttpResponseBodyFeature.DisableBuffering()` | For event streams |
| `app.Lifetime.ApplicationStopping` | Long-lived handlers must end on it (see [Shutdown](#shutdown)) |
| `Services.Configure<HostOptions>(Action<_>)` | Configuring options with code is fine; binding them from `IConfiguration` is not |
| Clients: `HttpClient(SocketsHttpHandler)`, `ClientWebSocket.ConnectAsync(uri, HttpMessageInvoker, ct)`, `SocketsHttpHandler.ConnectCallback`, `System.Net.ServerSentEvents.SseParser` | All AOT-clean; `ConnectCallback` reaches Unix sockets and named pipes |

**Avoid**

| API | Why |
|---|---|
| `WebApplication.CreateBuilder`, `CreateSlimBuilder` | Read `appsettings*.json` from the content root (default: the working directory), environment variables and the command line; add console logging that writes "Now listening on: ..." to **stdout** |
| `MapGet("/", Func<...>)` and other `Delegate` overloads | `RequestDelegateFactory`: IL2026 + IL3050, so the publish fails. The Request Delegate Generator would fix it but is a C# source generator |
| `Results.Json`, `TypedResults.Ok<T>`, `WriteAsJsonAsync`, `ReadFromJsonAsync` | Reflection-based `System.Text.Json`; use our codecs |
| `IConfiguration.Bind`, `Get<T>`, `Configure<T>(IConfiguration)` | Reflection-based binder (C# has a binder source generator; F# does not) |
| The default `IHostLifetime` (`ConsoleLifetime`) | Registered even by the empty builder; takes over Ctrl+C and SIGTERM. Register a no-op `IHostLifetime` |
| `ListenLocalhost(0)` | Kestrel refuses dynamic ports for `localhost`; listen on `IPAddress.Loopback` and connect to `127.0.0.1`, since `localhost` may resolve to `::1` first |
| `WebSocketAcceptContext.DangerousEnableCompression` | Compressing secrets next to attacker-chosen text leaks them (CRIME/BREACH) |
| MVC, Razor, SignalR | Not AOT-compatible or not needed |

**F# specifics**

- A lambda `fun (ctx: HttpContext) -> ctx.Response.WriteAsync "x"` passed to `MapGet` picks the
  `RequestDelegate` overload (no warning), but a `task { }` lambda (`Task<unit>`) matches no overload.
  Always write `RequestDelegate(fun ctx -> task { ... })`: it compiles, and it never falls through to the
  `Delegate` overload.
- Nullness: header values (`StringValues` items), `Features.Get<T>()`, `PathString.Value`,
  `WebSocket.CloseStatusDescription` and `SubProtocol` are nullable; handle them explicitly.
- `ValueTask(t)` needs `t :> Task` when `t` is a `task { }`.
- `IAsyncDisposable` works with `use!` in `task { }`.

## Security approach (spec §18)

Every request goes through `LocalAccess.decide` before routing, and before a WebSocket upgrade, so a
rejected request never reaches a handler and never gets a `101`:

1. **Host**: the `Host` header must name `localhost`, `127.0.0.1` or `[::1]` (any port). This stops DNS
   rebinding, where a page from `evil.example` resolves its own name to 127.0.0.1. `403`.
2. **Origin**: if present, it must be in the allowlist (empty by default; e.g. `tauri://localhost` for a
   desktop web view). Browsers always send `Origin` on WebSocket handshakes and cross-origin requests;
   non-browser clients send none and pass. `null` is never allowed. `403`.
3. **Token**: `Authorization: Bearer <token>`, or for browsers, which cannot set headers on a WebSocket,
   a subprotocol `<product>.bearer.<token>` offered next to the real one (Kubernetes uses the same trick).
   The server selects a real subprotocol and never echoes the token one. Every token offered must match;
   comparison is constant-time over SHA-256 digests. `401` with `WWW-Authenticate: Bearer`.
   Tokens are at least 32 base64url characters; a host should draw 256 random bits per listener.
   No query-string tokens (they leak into logs and `Referer`), no cookies (CSRF).
4. **No CORS**: responses never carry `Access-Control-Allow-*`; a preflight has no credentials, so it gets
   `401` (allowed origin) or `403` (foreign origin). Cross-origin browser clients use WebSocket, which needs
   no CORS. Browsers cannot send headers with `EventSource` either: a browser SSE fallback must use
   `fetch` with a streamed body.

Rejections answer with a fixed reason ("forbidden origin"), never the request's own values. Also:
no `Server` header, request bodies limited to 4 MiB (Kestrel), WebSocket messages limited by
`WebSocketMessages.receive` (Kestrel's body limit does not apply to them), no WebSocket compression.

**Local sockets.** A Unix domain socket is reachable only through the file system, and browsers cannot
reach it at all: no port, no DNS rebinding, no firewall. Put it in a directory only the user can enter
(`0700`; the scenario does this), and keep the token anyway (defence in depth, same code path). Kestrel's
named pipes default to `CurrentUserOnly = true`: only the same user, at the same elevation, can connect.

Not covered here: remote access (Dialback with Noise, §3), principals behind tokens (T122), rate limits.

## Measurements

### Binary size

The probe (`docs/spikes/kestrel-probe`) publishes one variant per feature; each adds to the previous one.
Size of the native executable in MiB, from CI run
[45493322062](https://github.com/Krzysztof-Cieslak/fx/actions/runs/45493322062) (exact bytes in its job
summaries):

| Variant (MiB) | linux-x64 | linux-arm64 | linux-musl-x64 | linux-musl-arm64 | osx-arm64 | osx-x64 | win-x64 | win-arm64 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `console` | 1.16 | 1.20 | 1.15 | 1.20 | 1.18 | 1.20 | 0.91 | 0.93 |
| `client` | 5.61 | 5.87 | 5.61 | 5.87 | 4.99 | 5.11 | 4.41 | 4.54 |
| `server` | 9.23 | 9.55 | 9.23 | 9.55 | 8.39 | 8.66 | 8.55 | 8.78 |
| `server-ws` | 9.55 | 9.86 | 9.55 | 9.86 | 8.64 | 8.96 | 8.84 | 9.05 |
| `server-ws-routing` | 10.40 | 10.70 | 10.40 | 10.70 | 9.46 | 9.81 | 9.85 | 10.08 |
| `slim-ws-routing` | 10.88 | 11.20 | 10.88 | 11.20 | 9.91 | 10.27 | 10.37 | 10.61 |

**Kestrel + WebSocket costs about 4 MiB** on top of `HttpClient`, which fx needs anyway for the model
APIs (`server-ws` − `client`: 3.65–4.51 MiB, most on Windows). Endpoint routing adds 0.82–1.03 MiB more
and the slim builder another 0.44–0.53 MiB; neither is needed. So a host-capable fx binary will be about
9–10 MiB (`server-ws`: 8.6–9.9 MiB).

CI's Native AOT steps report the real binaries (run
[45493321826](https://github.com/Krzysztof-Cieslak/fx/actions/runs/45493321826)). `fx` has no ASP.NET Core
yet. `Fx.Aot.Scenarios` (all scenarios, ASP.NET Core with routing and the slim builder, `HttpClient`,
`ClientWebSocket`, named pipes) grew from 4.75 MiB to 13.34 MiB on `linux-x64` with this spike:

| Target | `fx` (MiB) | `Fx.Aot.Scenarios` (MiB) |
|---|---:|---:|
| linux-x64 | 2.11 | 13.34 |
| linux-arm64 | 2.20 | 13.68 |
| linux-musl-x64 | 2.11 | 13.34 |
| linux-musl-arm64 | 2.20 | 13.68 |
| osx-arm64 | 2.11 | 12.31 |
| osx-x64 | 2.16 | 12.77 |
| win-x64 | 1.95 | 13.12 |
| win-arm64 | 2.02 | 13.43 |

The fx CLI does not reference ASP.NET Core yet; T129 adds it.

### Startup

Median of 11 runs of the probe on the CI runners, in ms from `main` to listening on 127.0.0.1 and to the
first response to its own `HttpClient` request (which includes the client's first connection):

| Target | `server`: listening | `server`: first response | `server-ws`: listening | `server-ws`: first response |
|---|---:|---:|---:|---:|
| linux-x64 | 5.9 | 8.4 | 6.7 | 7.9 |
| linux-arm64 | 7.0 | 8.7 | 8.0 | 9.4 |
| linux-musl-x64 | 8.7 | 9.8 | 9.9 | 10.7 |
| linux-musl-arm64 | 9.2 | 10.2 | 9.6 | 10.9 |
| osx-arm64 | 7.3 | 14.8 | 8.0 | 16.0 |
| osx-x64 | 16.5 | 30.3 | 14.7 | 26.2 |
| win-x64 | 13.3 | 19.4 | 13.9 | 20.2 |
| win-arm64 | 12.4 | 20.2 | 12.7 | 19.7 |

Starting Kestrel costs **6–17 ms** to listen and 8–30 ms to the first response. Locally (linux-x64,
2 cores) `server-ws` listens after 7–8 ms. The probe's `client` variant connects to a closed port on
purpose; on Windows a refused connection is retried for about 2 s, so its run takes that long there.

## Behaviour and caveats found

### The slim builder reads the working directory

`CreateSlimBuilder` (and `CreateBuilder`) add `appsettings.json` and `appsettings.<Environment>.json` from
the content root, which defaults to the working directory, plus `ASPNETCORE_*`/`DOTNET_*` environment
variables. Kestrel binds every `Kestrel:Endpoints` entry it finds **in addition to** the listeners set in
code. With `{"Kestrel":{"Endpoints":{"x":{"Url":"http://0.0.0.0:5099"}}}}` in a cloned repository, a
slim-builder fx host started there would listen on all interfaces. Verified locally with the probe, and
in CI by `kestrel-slim-builder` (2 listeners vs 1). `ASPNETCORE_URLS` is ignored once code calls `Listen`.
Configuration must come through `IVariants` anyway, so the empty builder loses nothing.

The slim builder also logs to the console. For a process whose stdout carries a protocol (JSON-RPC over
stdio, T107), any console logger corrupts the stream: never add one there.

### Process signals

The generic host registers `ConsoleLifetime` even with `CreateEmptyBuilder`. It handles Ctrl+C and
SIGTERM: it cancels the signal and stops the server, and the process keeps running. Verified on Linux:
with `ConsoleLifetime`, SIGTERM stopped the server and `main` carried on; with the no-op lifetime that
`LocalServer` registers, SIGTERM ends the process (exit 143) as in any .NET program. How fx reacts to
signals is the entry point's decision (T129, e.g. `PosixSignalRegistration`), never a library's.

### Shutdown

`StopAsync` signals `ApplicationStopping`, then waits for in-flight requests until
`HostOptions.ShutdownTimeout` (30 s by default; `LocalServer` uses 5 s), then aborts them. Event streams
and WebSocket handlers run until the client leaves, so they must end on `Request.Stopping`;
`kestrel-sse` shows that the stream then ends cleanly. The probe first forgot this and every WebSocket
run took 30 s to exit.

### WebSocket

- One receive and one send at a time per socket: a protocol on top (T107) must queue its sends
  (e.g. a `Channel`).
- `ManagedWebSocket` validates UTF-8 in text messages. On invalid text the server's receive throws
  (`Faulted`), it sends a 1007 close and aborts at once; the abort can overtake the close frame, so a
  client sees either a 1007 close or `ConnectionClosedPrematurely`.
- `CloseAsync` waits for the peer's close frame, and discards data until then; `CloseOutputAsync` does not
  wait. Answer a peer's close with `CloseOutputAsync`; start a close with `CloseAsync`.
- A client that vanishes gives `WebSocketException(ConnectionClosedPrematurely)` on the server's receive.
- Ping/pong: `WebSocketOptions.KeepAliveInterval` (server) and `ClientWebSocketOptions.KeepAliveInterval`.

### Server-sent events

- Kestrel sends each `Response.Body.WriteAsync` + `FlushAsync` at once (chunked); `kestrel-sse` proves it
  by making the server wait for the client's acknowledgement of each event.
- `RequestAborted` fires when the client closes the connection, even while the handler only waits.
- `HttpClient` drains up to 1 MB for up to 2 s when a response is disposed early, before closing the
  connection: the server sees the disconnect late. Clients that abandon streams should set
  `SocketsHttpHandler.MaxResponseDrainSize = 0`.
- Send `Cache-Control: no-cache` and `X-Accel-Buffering: no` (proxies), and start the response before
  the first event so that clients see the headers at once.
- The BCL has `System.Net.ServerSentEvents` (`SseParser`, `SseFormatter`). Clients use `SseParser`; its
  `SseItem.EventId` is the event's own id, while `SseParser.LastEventId` persists across events. The
  server uses our 20-line `Sse.format`, which flushes per event and needs no `IAsyncEnumerable`.
- Data lines split only on CR, LF and CRLF. `String.ReplaceLineEndings` also splits U+2028, U+0085 and
  form feeds, which would corrupt data.

### HTTP

- Use `Expect: 100-continue` when a client may exceed the body limit: Kestrel then answers 413 before the
  body is sent. Without it the client may see a connection reset instead of the 413.
- An exception in a handler becomes a 500 when the response has not started; the server keeps serving.

### Platforms

- **Unix domain sockets** work with Kestrel on Linux (glibc and musl), macOS and Windows (10 1803+),
  under AOT. Kestrel deletes the socket file on stop, so a restart on the same path works on all three.
  After a crash the stale file stays, and the next bind fails with "address already in use" (verified on
  Linux with a file left by another process); a host should delete it when nothing answers on it. A
  second server cannot take over a path that is still served. On Linux the socket file gets the umask's
  mode (`srwxr-xr-x` with `022`) and connecting needs write permission, so other users cannot connect;
  rely on the `0700` directory everywhere. `sun_path` is 108 bytes on Linux and Windows and 104 on macOS,
  whose temp directory alone is about 50 characters: keep socket paths short (e.g. `~/.fx/run/<id>.sock`).
- **Named pipes** (Windows only): `UseNamedPipes()` + `ListenNamedPipe`; clients use
  `NamedPipeClientStream` through `ConnectCallback`. The address reads `http://pipe:/<name>`.
- **Firewall prompts**: the Windows Defender Firewall and the macOS application firewall prompt when a
  program accepts connections on a network interface; loopback traffic is not filtered, so a listener on
  127.0.0.1 only, as `LocalServer` makes, should not prompt. CI runners have no interactive prompts, so
  this could not be checked there. Unix sockets and named pipes avoid the question entirely.
- **musl** behaves exactly like glibc; sizes are the same.

### Analyzers

The `EFF` rules do not cover server listeners (`WebApplication.Create*`, `KestrelServerOptions.Listen*`),
so `LocalServer.fs` needed no `analyzers.json` entry, although it is an imperative shell. Ban those APIs
in `EFF007` when the network capability exists (T103/T107), and allow them only in the interpreter file.

## Recommendations

**T107 (JSON-RPC transports)**

- Model a transport as a message stream like `WebSocketMessages`: `receive` (bounded) and `send`, with a
  single writer task fed by a `Channel` so that responses and notifications never send concurrently.
- Map JSON-RPC to WebSocket text frames and binary channels to binary frames with a channel header;
  credits for backpressure live in the protocol, not in WebSocket.
- Use `Fx.Transport.Http` for the WebSocket and HTTP/SSE transports; keep stdio free of any logging.
- The HTTP/SSE fallback (§17 "offset-read mirror") maps naturally: event id = log offset, resume with
  `Last-Event-ID` (`Events.lastEventId`), POST for requests.

**T129 (host server)**

- Start the listener only in host mode; embedded mode uses in-process transports (Kestrel costs ~10 ms).
- Local clients (TUI attach, CLI): a Unix domain socket in a `0700` run directory under the user's data
  directory, with a fresh token per host start written next to it in a `0600` file. Browser-based clients
  (web, Tauri, VS Code web views): loopback TCP with `Origin` allowlisting and the subprotocol token.
- Keep `CreateEmptyBuilder`; add `AddRoutingCore` only if exact-path dispatch stops being enough.
- Handle a stale socket file before binding; end long-lived handlers on `Request.Stopping`.
- Decide signal handling (Ctrl+C, SIGTERM, console close) at the entry point; `LocalServer` leaves
  signals alone on purpose.
- The listener token only gates the connection; the identity and session token from `initialize` (§17,
  T122) layer on top, e.g. through `Granted`. Tokens are secrets: keep them out of the event log,
  recordings and logs.
