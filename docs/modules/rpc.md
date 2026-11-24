# Module spec: JSON-RPC (`src/Fx.Rpc`)

Our own JSON-RPC 2.0 (spec §17, §20): the client protocol (clients ↔ host, over WebSocket), the
environment protocol (host ↔ envd, over stdio or WebSocket, with binary channels), the kernel and
extension protocols (over stdio), and embedded mode (in process, spec §3). Task: T107.

- **Dependencies:** `Fx.Prelude`, `Fx.Contracts` (JSON and codecs; no System.Text.Json), `Fx.Effects`
  (`Eff`, `IClock`, scopes). No ASP.NET Core: the WebSocket transport takes any
  `System.Net.WebSockets.WebSocket`; `Fx.Transport.Http` provides the Kestrel server
  (`LocalServer`) and the client that dials it (`LocalClient`).
- **No I/O of its own.** Transports wrap streams and sockets that a composition root opened; the clock
  comes from the environment. So `src/Fx.Rpc` needs no `analyzers.json` entry.
- **Tests:** `tests/Fx.Rpc.Tests` (unit tests, properties, and the conformance suite over every
  transport) and the `rpc-*` scenarios of the JIT-vs-native diff.

## Files

| File | Contents |
|---|---|
| `Protocol.fs` | Versions, reserved names (`initialize`, `$/cancelRequest`), error codes, method-name rules, extension methods, `_meta` keys, the WebSocket subprotocol |
| `Wire.fs` | `RequestId`, `RpcError`, `Meta`, `Message`; reading (`Wire.parse`) and writing messages and batches |
| `Method.fs` | `Method<'P,'R,'E>`, `Notification<'P>`, `Reply`, `CallError`, `SendError`, `CloseReason` |
| `Handshake.fs` | `PeerInfo`, `Capabilities`, `ChannelSettings`, the `initialize` codecs, version choice |
| `Frames.fs` | `Frame`, `Received`, `TransportError`; channel frame layout; the stream framing |
| `Transport.fs` | The `Transport` record |
| `Queues.fs` | The bounded queue every buffer uses, and a task group |
| `InProcess.fs`, `StreamTransport.fs`, `ProcTransport.fs`, `WebSocketTransport.fs` | The transports |
| `Channels.fs` | `Channel` (credit flow control) and the per-connection channel table |
| `Connection.fs` | `ConnectionLimits`, `Peer` (the engine: reading, dispatching, writing, closing) |
| `Rpc.fs` | `Handler`, `Service`, `CallOptions` and the typed `Eff` API (`Rpc.connect`, `accept`, `call`, `notify`, channels) |

## Using it

```fsharp
type Span = { Start: int; End: int }
type ReadError = NotFound of path: string | TooLarge of size: int64   // override ToString

let read = Method.define "fs/read" readParams fileText |> Method.withErrors readError
let changed = Notification.define "fs/changed" changedParams

// Server: handlers are typed; Error is the method's expected failure.
let handlers =
    [ Handler.request read (fun context p -> Fs.readText p.Path |> ...)
      Handler.notification changed (fun context p -> ...) ]

let service = Service.create { Name = Product.binaryName; Version = Product.Version } handlers
let! peer = Rpc.accept service transport              // Eff<'R, Result<Peer, ConnectError>>

// Client: the same values build the stub.
let! peer = Rpc.connect clientService [ "identity", identityJson ] transport
let! text = Rpc.call peer read { Path = "/src/a.fs" }  // Eff<_, Result<FileText, CallError<ReadError>>>
let! reply = Rpc.callWith { CallOptions.timeout (TimeSpan.FromSeconds 30.0) with Meta = meta } peer read p
```

- The environment is `'R :> IClock and 'R :> IScopeWith<'R>`. A connection lives in the scope of the
  effect that opened it: cancelling that scope closes it. Each request handler runs in a child scope,
  cancelled with the request (`$/cancelRequest`) or the connection.
- Both sides may call, notify and open channels; `RequestContext.Peer` lets a handler call back.
- `Peer.Request`, `Peer.Notify` and `Peer.RequestBatch` are the untyped forms (for proxies and batches).

## Wire format (JSON-RPC 2.0)

- Strict JSON from `Fx.Contracts` (duplicate keys rejected, depth 128). Messages are written compactly
  with members in a fixed order: `jsonrpc`, `id`, `method`, `params` / `result` / `error`.
- **Ids** are strings or integers that fit int64; the writer allocates integers. `null` ids and
  fractional numbers are rejected as invalid requests (JSON-RPC discourages both, and a response to a
  `null` id could not be told from a parse-error response).
- **Params** are an object or an array, or absent. Typed methods use objects; absent params decode as
  `{}` (or `null` for `Codec.unit`), so records whose fields are all optional accept them.
- **Batches** (non-empty arrays) are answered with one array in entry order; notifications in a batch
  get no entry, and a batch of only notifications gets no answer. An empty batch is one
  `InvalidRequest`. Requests in a batch run concurrently; the array is sent when all are answered.
  `Rpc.batch` sends one.
- Malformed responses are dropped (responses are never answered); a response with an unknown id (a
  call that timed out) is dropped.

### Errors

| Code | Name | When |
|---|---|---|
| -32700 | ParseError | The text is not JSON (`id: null`) |
| -32600 | InvalidRequest | Not a request object, an empty batch, a `null` id, a duplicate id in flight, `initialize` twice |
| -32601 | MethodNotFound | No handler (a notification handler does not answer requests) |
| -32602 | InvalidParams | The params do not decode: `message` lists the issues (at most 10), `data.issues` has them all as `{path, message}` |
| -32603 | InternalError | A defect (exception) in the handler: `internal error (<exception type>)`, never the exception's message |
| -32000 | ApplicationError | The method's typed error (`Result.Error`): `message` is its `ToString()`, `data` its encoding |
| -32001 | RequestCancelled | Cancelled by `$/cancelRequest` or by the connection closing |
| -32002 | NotInitialized | A request before `initialize` completed |
| -32003 | Overloaded | More requests in flight than `MaxPendingRequests` |
| -32004 | UnsupportedVersion | No common protocol version; `data.supported` lists them |
| -32005 | ResponseTooLarge | The response would exceed the peer's `maxMessageSize` |
| -32006 | ShuttingDown | The connection is closing |

On the calling side these become `CallError`: `Failed 'E` (typed error), `Rejected RpcError` (any
other error), `InvalidReply issues` (a result or error that does not decode), `TooLarge` (not sent),
`TimedOut`, `Disconnected CloseReason`.

### Names, `_meta` and extensions

- Method names are non-empty, without control characters. `rpc.` is reserved by JSON-RPC 2.0 and `$/`
  by this protocol (`$/cancelRequest`). Applications use groups such as `session/*` (spec §17).
- **Extension methods** are `_<namespace>/<name>`; the product's own namespace is
  `Product.protocolNamespace` (`Protocol.ownExtensionMethod`), third parties use theirs (a reverse
  domain name). Peers announce the methods they handle in `initialize`, extension methods included,
  and `Peer.Supports` checks them.
- **`_meta`** is an object on params and results, keyed `<namespace>/<name>` (`Protocol.metaKey`).
  The engine splits it off before the codecs see the params (so `Codec.strict` codecs work), and
  attaches it when sending (`CallOptions.Meta`, `Handler.requestReply` returning a `Reply`). It can
  only be attached to objects; receivers ignore keys they do not know.

## Handshake

The client sends `initialize`; nothing else is accepted before it (`NotInitialized`):

```json
{"protocolVersions":["1"],"peer":{"name":"…","version":"…"},
 "capabilities":{"maxMessageSize":16777216,"maxPendingRequests":256,
                 "channels":{"initialWindow":262144,"maxFramePayload":65536,"maxChannels":256},
                 "methods":["fs/read", "_fx/debug/ping", …]},
 "identity": …, "_meta": {…}}
```

- The server picks the first offered version it supports, or answers `UnsupportedVersion`. The result
  has `protocolVersion`, `peer` and `capabilities`.
- **Capabilities** are what each side *accepts*: the largest message it reads, how many requests it
  takes in flight, its channel settings (absent: no channels; channels work only if both offer them),
  and the methods it handles (sorted).
- **Application members** (identity, a session token: T122, T129) ride next to the protocol's:
  `Rpc.connect` adds members to the params, `Service.Accept` sees the client's (`Remote.Members`),
  may refuse with an `RpcError`, and adds members to the result. The server stays in the handshake
  after a refusal or invalid params, so a client may retry, until `HandshakeTimeout`.
- The server handles `initialize` in its reader and sends the result before the application learns of
  the connection, and the client opens the connection as the result arrives, before reading on: so
  neither side can see a request from the other before the handshake ends.

## The connection engine (`Peer`)

Three loops per connection, plus the handlers:

- **Reader.** Never waits for a handler. Responses complete calls at once. Requests and
  notifications go to the dispatch queue (`MaxQueuedMessages`); when it is full the reader waits,
  which pushes back on the peer through the transport. Channel frames go to their channel, bounded by
  the credit it granted, so they never wait.
- **Dispatcher.** Keeps arrival order: notifications are handled one at a time, in order; a request
  starts after every earlier notification has been handled, then runs concurrently on the thread pool
  (its synchronous part never holds up the dispatcher). So a request sees the effect of the
  notifications sent before it. A notification handler that waits for a call to the same peer can
  stall the queue behind it: start the call and return instead.
- **Writer.** The only caller of `Transport.Send`, over a bounded queue (`MaxOutboundBytes`; a larger
  message waits for an empty queue). Senders wait for room. Credit, refused-channel and
  `$/cancelRequest` frames skip the queue (their number is bounded by the channels and calls).

### Requests in flight

- Each side announces `maxPendingRequests`. A caller takes a slot before sending and frees it when
  the answer arrives, even after its own timeout or cancellation (the peer counts the request until
  its handler finishes). So well-behaved peers never see `Overloaded`; it answers peers that do not
  wait and oversized batches (batches take no slots).
- **Cancellation:** cancelling the caller's scope sends `$/cancelRequest {"id": …}` and throws
  `OperationCanceledException` at once (a defect, as for every capability). The callee cancels the
  handler's scope; the handler answers `RequestCancelled`, or its result if it finished first; the
  late answer is dropped.
- **Timeouts** run on the environment's `IClock` (`CallOptions.Timeout`): `TimedOut`, and the request
  is cancelled at the peer. Tests drive them with `FakeClock`.
- **Size:** a request or notification larger than the peer's `maxMessageSize` is not sent (`TooLarge`);
  a response larger than it becomes `ResponseTooLarge`. A frame over this side's limit is a protocol
  violation and ends the connection (WebSocket close 1009).

### Closing

| How it ends | Calls in flight | Handlers | Then |
|---|---|---|---|
| `CloseAsync` / the opening scope is cancelled (`Local`) | `Disconnected` | cancelled; queued requests answered `ShuttingDown` | flush, close the output, wait (≤ `CloseTimeout`) for the peer to close its side |
| The peer closed its side (`Peer`: end of input, WebSocket close) | `Disconnected` | received requests still run and are answered (`echo request \| program`), for at most `CloseTimeout`, then cancelled | flush, close the output |
| Transport failure, protocol violation, handshake timeout | `Disconnected` | cancelled | abort (a violation first tries a WebSocket close code) |

Channels fail with `ConnectionClosed` in every case. `Peer.Completion` resolves once everything the
connection started has finished. A crashed peer looks like `Peer` on a pipe and like
`TransportFailed` on a WebSocket.

## Binary channels

Multiplexed, ordered, bidirectional byte streams for PTY output, blobs and port forwarding (spec §17):

- `Peer.OpenChannel` (or `Rpc.openChannel`) allocates an id: odd on the client side, even on the
  server side, never reused. Send the id in a request or a result (`Rpc.channelId` is its codec); the
  peer takes it with `AcceptChannel`, once. The opener may write at once: data that arrives before the
  peer accepts is buffered, bounded by the window.
- **Credit:** each side's `initialWindow` is what the other may send on a new channel before it gets
  credit; the receiver grants more as the application reads (after half a window). A writer waits for
  credit, so a slow reader stalls only its own channel: the connection, other channels and calls go on.
  Data beyond the credit resets the channel (`FlowControl`).
- **Ending:** `CloseOutput` ends one direction after the data written (the peer reads `None`); `Reset`
  aborts both and drops buffered data (the peer gets `ResetByPeer`). A reset never overtakes data or
  the open frame. Opening more than `maxChannels` (counted on both sides) fails with `TooMany`, or the
  receiver refuses it with a reset (`Refused`).
- Writes are cut into frames of at most the receiver's `maxFramePayload`; concurrent writes to one
  channel are serialized, and frames of different channels interleave in the writer's queue.

**Frame layout** (the payload of a binary frame; big-endian): kind byte, channel id (uint32), then:

| Kind | Byte | Fields |
|---|---|---|
| open | 1 | — |
| data | 2 | the bytes |
| credit | 3 | bytes granted (uint32) |
| close | 4 | — |
| reset | 5 | code (uint32), reason (UTF-8, at most 256 bytes) |

Sequence numbers and resumable output are the environment protocol's (T111), above channels.

## Transports

A `Transport` is a record of functions (`Send`, `Receive`, `CloseOutput`, `Abort`), like a capability:
data in and out, so it can be wrapped (recording, fault injection). Receive limits come from the
connection's limits: `ConnectionLimits.frames`.

| Transport | Framing | Notes |
|---|---|---|
| `InProcess.pair` | none: frames are passed as values | Embedded mode and tests |
| `StreamTransport.create` | NDJSON lines + zero-byte-prefixed binary frames | stdio, pipes, sockets; owns the streams |
| `ProcTransport.create` | the stream framing | a child's stdin and stdout as a `Proc` from the shell capability (`Shell.spawn`), so a fake process works too; its stderr goes to a sink. Closing closes the child's stdin; it never kills the child (T108's process runtime does) |
| `WebSocketTransport.create` | text messages for JSON, binary messages for channel frames | Server: `LocalServer.Endpoint.webSocket` selecting `Protocol.webSocketSubprotocol`; client: `LocalClient.connectWebSocket` |

- **In process, frames are serialized.** The connection always writes JSON text, and the in-process
  transport moves that text (and channel frames) between two bounded queues. It skips only the byte
  framing, so codecs, the JSON reader, size limits and every error path run exactly as out of process:
  parity by construction (spec §16.4, T108). Passing typed values would skip validation, and passing
  `Json` trees would skip the reader and the limits. The cost is a serialization per message, small
  next to the work behind it; bulk data goes over channels, whose frames are passed as is.
- **Aborting keeps what was sent**, on every transport: a connection ends with `CloseOutput`, then
  `Abort`, and the peer may not have read the last answers yet. The in-process transport behaves like
  a pipe: frames already sent are still delivered, then the end (after `CloseOutput`) or a reset (a
  crash).
- **Stream framing.** A text frame is one message or batch as UTF-8 on one line ending in `\n`
  (newline-delimited JSON, as the Bun kernel speaks it; `\r\n` and empty lines are accepted; a last
  line without `\n` counts). A binary frame is a zero byte, a big-endian uint32 length, then the
  channel frame. JSON text never contains a raw zero byte or line break, so the first byte tells them
  apart, and a peer that never opens channels (a kernel, a script in any language) speaks plain
  NDJSON. Text must be valid UTF-8; an oversized or malformed frame ends the connection. Nothing else
  may write to the output stream: no logging on stdout.
- **WebSocket.** One message per frame. Closing sends a close frame (1000, or 1002 / 1009 for
  violations) without waiting; the reader sees the peer's close as the end. Kestrel's body limit does
  not apply; the transport's limits do. The listener's token, `Origin` and `Host` checks (spec §18)
  happen before the upgrade; the `initialize` identity and session token (T122) come on top.

## Bounds

| What | Bound (default) | Beyond it |
|---|---|---|
| One received text frame | `MaxMessageSize` (16 MiB), announced | the connection ends |
| One received binary frame | `maxFramePayload` + 5 bytes (64 KiB) | the connection ends |
| Requests from the peer in flight | `MaxPendingRequests` (256), announced | `Overloaded` |
| Received messages waiting for the dispatcher | `MaxQueuedMessages` (1024) | the reader waits (backpressure) |
| Bytes queued for sending | `MaxOutboundBytes` (16 MiB) | senders wait |
| Data buffered per channel | its receive window (`initialWindow`, 256 KiB) | the channel is reset |
| Channels open | `maxChannels` (256), announced | `TooMany` / refused |
| In-process transport buffer | `bufferBytes` of `InProcess.pair` | the sender waits |
| Stream transport read buffer | the largest frame | — |
| Handshake / close | `HandshakeTimeout` (30 s) / `CloseTimeout` (5 s) on the clock | handshake failed / abort |

## Testing

- `RpcConformance.tests name connect` (`tests/Fx.Rpc.Tests/Conformance`) is the protocol's
  conformance suite: a function from a way to reach the test service (`Link`: the client's transport,
  a crash, a dispose) to tests. It covers the handshake, typed results and errors, issue paths,
  `_meta`, extension methods, ordering, concurrency, batches, cancellation, timeouts on a fake clock,
  pending-request slots and `Overloaded`, large messages and both size limits, channels (echo, server
  opened, slow reader, reset, limits and multiplexing), malformed input answered as JSON-RPC says,
  oversized frames, a crashed peer, closing, and a peer that closes its output. It runs over in-process,
  stream over in-memory pipes, a child process on stdio (started directly, and through `LocalShell`
  with `ProcTransport`), and WebSocket over loopback and a Unix socket. T108's transport-parity suite
  (`docs/modules/extensions.md`) follows the same pattern for extensions.
- The test service (`TestService`) and the child peer live in `tests/aot/Fx.Aot.Scenarios`
  (`Scenarios/Rpc/`): `Fx.Aot.Scenarios rpc-peer` serves it on stdio, so the same program is the child
  on the JIT and natively.
- Scenarios: `rpc-wire-parse`, `rpc-wire-write`, `rpc-framing`, `rpc-channel-frames` (pure), and one
  conversation (`Transcript`) over `rpc-in-process`, `rpc-stream`, `rpc-stdio-child` (with a crash),
  `rpc-websocket` and `rpc-websocket-unix-socket`; the transcripts are identical across transports.

## Left to later tasks

- **Kernel protocol (T116):** the Bun kernel may write stray bytes to stdout; the spike recovers
  frames after them. The stream framing here answers a non-JSON line with a parse error; T116 decides
  whether kernels get a lenient reader that reports stray output instead.
- **HTTP/SSE fallback** (spec §17 "offset-read mirror") and **Dialback** with Noise (spec §3) are
  transports for T129 and later.
- **Record and replay (T131):** calls are recorded at the capability level (an envd-backed `IFs` is a
  capability whose interpreter uses this protocol), not here.
- **Analyzers:** `EFF007` still does not see Kestrel listeners (`WebApplication.Create*`,
  `KestrelServerOptions.Listen*`): the analyzer samples are scripts that cannot reference ASP.NET
  Core. Banning them needs a project-based sample.
