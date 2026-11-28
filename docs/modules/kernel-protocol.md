# Module spec: kernel protocol (`src/Fx.Kernel`), version 1

How the host drives a CodeAct kernel (spec §6.4–6.6): a process, in any language, that runs the
model's cells and keeps their state. Task: T116. The protocol is language-neutral; the first kernel is
TypeScript on the pinned Bun ([`packages/kernel-bun-ts`](../../packages/kernel-bun-ts), the reference
kernel, which is `fx.interpreter.bun-ts`'s kernel: [interpreters.md](interpreters.md)).

- **Code:** `src/Fx.Kernel` (pure: no I/O of its own; kernel processes start through the shell
  capability). Dependencies: `Fx.Contracts` (codecs), `Fx.Contracts.TypeScript` (declarations),
  `Fx.Effects`, `Fx.Rpc` (the wire), `Fx.Store` (`BlobRef`), `Fx.Sdk` (the host's deny list, and
  `InterpreterCaps`, which interpreters declare).
- **Tests:** `tests/Fx.Kernel.Tests`: the conformance suite v1 against three kernels, unit tests, and a
  golden test of the TypeScript declarations; the `kernel-*` scenarios of the JIT-vs-native diff.
- **Versions:** this document is protocol version **1** (`KernelProtocol.versions`). A change that an
  old kernel or host would misread is a new version: add it to `versions`, keep version 1 working
  while kernels that speak it exist, and record the differences in a section here.

## Files

| File | Contents |
|---|---|
| `Values.fs` | `Value` (`Inline`, `Blob`, `Handle`), `HandleId`, `TypeRef`, `ValueRef`, `ValueInfo`, their codecs, declared-type decoding |
| `Handles.fs` | `HandleTable`: a session's handles, issued from random bytes and resolved only by the table that issued them |
| `Protocol.fs` | The message types: setup, caps, cells, outcomes, errors, host calls, finish, notifications |
| `KernelProtocol.fs` | Their codecs, the methods and notifications (`Method.define`, `Notification.define`), `Finish.validate` |
| `KernelDeclarations.fs` | The TypeScript declarations of every message, for kernels written in TypeScript |
| `Framing.fs` | `KernelLineReader`: messages after stray bytes on a kernel's standard output |
| `KernelTransport.fs` | The transport over a kernel process (`Proc`) with that framing |
| `Kernel.fs` | The host's client: `KernelHost`, `KernelLaunch` and the hygiene check, `Kernel.start`/`connect`, calls, staged interrupts |

## Wire

JSON-RPC 2.0 as `src/Fx.Rpc` speaks it ([rpc.md](rpc.md)), on the kernel process's standard input and
output: one message per line, `$/cancelRequest`, the error codes, the `initialize` handshake. Kernels
differ from other peers in three ways:

- **No binary channels and no batches.** Bulk data travels as blobs, not in channels.
- **Framing.** Code running in a kernel may write to file descriptor 1 directly (a child process that
  inherits it, `fs.writeSync(1, …)`). So every message starts with the literal `{"jsonrpc":"2.0",` (the
  `jsonrpc` member first) on a line of its own (writers put a newline before each message, which ends
  any partial line). The host's reader (`KernelLineReader`) takes a message from where that prefix
  starts, if the rest of the line is JSON, and reports everything else as **stray output**: text before
  the prefix, lines without it, invalid UTF-8 (as U+FFFD), and long lines without a newline in pieces.
  A zero byte is text. Only a message larger than the host's `maxMessageSize` ends the connection.
  Stray output is kept for diagnostics (`Kernel.stray`), never interpreted.
- **Shutdown.** The host closes the kernel's standard input; the kernel exits. After `ExitGrace` the
  host kills it.

A kernel is not a security boundary (spec non-goal): code in it can write a well-formed message too.
The host treats every message from a kernel as untrusted input: codecs check its shape, `finish` is
validated, handles are checked against the session's table.

## Handshake

The host is the client. Its `initialize` params carry the member `kernel` (`KernelSetup`):

```json
{"protocolVersions":["1"],"peer":{…},"capabilities":{…},
 "kernel":{"versions":["1"],"global":"fx","bindings":["shell.run","fs.read"],"moduleBase":"/work",
           "limits":{"outputChars":16000,"preview":{"maxChars":1000,"maxItems":10,"maxDepth":3,"maxStringChars":120},
                     "inlineBytes":65536,"argumentBytes":8388608}}}
```

- `versions`: the kernel protocol versions the host speaks, preferred first. The kernel picks one or
  refuses `initialize` with `UnsupportedVersion` (-32004, `data.supported`).
- `global`: the name of the host bridge in the kernel's language (`Fx.Product.kernelGlobal`); kernels
  never hardcode it.
- `bindings`: the host calls cells can make, as dotted paths. `shell.run` becomes
  `<global>.shell.run(…)` in TypeScript; every call is a `host/call`.
- `moduleBase`: where relative imports in cells resolve, if the language has them.
- `limits`: output, preview and inline bounds (`KernelLimits`); their values are variants (spec §6.6).
  `inlineBytes` bounds the values a kernel encodes (`value/encode`, `finish`). `argumentBytes`
  (optional, T139) bounds each argument of a `host/call`, so a cell can pass a file's text or a
  command's input beyond `inlineBytes`; a host that sends none gets `inlineBytes` for arguments too. An
  argument over the limit never reaches the host: the cell gets an error with the code
  `invalidArguments` that names the binding, the size and the limit. `overflowChars` (optional,
  T201a): when a cell's output is cut, the kernel also returns all of it as `output.whole` if it has
  at most that many characters; a kernel that does not know the limit returns none (nothing is
  misread).

The result carries `kernel` (`KernelInfo`): the chosen `version`, the cells' `language`, `caps`
(`InterpreterCaps`, spec §6.2) and free-form `details` (runtime versions):

```json
"kernel":{"version":"1","language":"typescript",
          "caps":{"snapshot":false,"asyncHostCalls":"await","deterministic":false},
          "details":{"bun":"1.4.5","typescript":"7.0.2","moduleGraph":true}}
```

| Cap | Meaning |
|---|---|
| `snapshot` | The kernel handles `snapshot` and `restore` (both, or neither) |
| `asyncHostCalls` | `await`: cells await host calls; `transparent`: calls look synchronous to cells |
| `packages` | `{manager}`: the kernel installs packages for a session (spec §6.3); absent if not |
| `deterministic` | The same cells with the same host answers give the same results |

The kernel announces the methods it handles in `capabilities.methods` (Fx.Rpc). The host refuses a
kernel without the `kernel` member, with a version it did not offer, or without a required method
(`StartError.Incompatible`).

## Methods

Every method's params, result and typed error are codecs in `KernelProtocol`; typed errors travel as
`ApplicationError` (-32000) with the error in `data` (rpc.md). TypeScript sees them as the maps
`KernelRequests`, `HostRequests` and `KernelNotifications` in the generated declarations.

### Host to kernel

| Method | Params | Result | Required |
|---|---|---|---|
| `cell/run` | `CellRun {cellId, code, intent?, timeoutMs?, check?}` | `CellResult` | yes |
| `cell/cancel` | `{cellId}` | `{wasRunning}` | yes |
| `value/preview` | `{ref, limits?}` | `ValueInfo {type, preview}` | yes |
| `value/encode` | `{ref, maxBytes?}` | `Value` | yes |
| `value/decode` | `{name, value, type?}` | `ValueInfo` | yes |
| `recall` | `{cellId, value}` | `ValueInfo` | yes |
| `cell/check` | `{cellId, code}` | `{diagnostics}` | no |
| `snapshot` | none | `KernelState {state}` | with `caps.snapshot` |
| `restore` | `KernelState` | `null` | with `caps.snapshot` |

Their typed error is `KernelError`: `busy {running}`, `duplicateCell`, `invalidCellId`, `unknownCell`
(`{cellId}`), `notFound {ref}`, `notEncodable {type, reason}`, `tooLarge {bytes, limit}`,
`invalidName {name}`, `invalidValue {reason}`.

**`cell/run`.** Cell ids are 1 to 64 of `A-Z a-z 0-9 _ -` and unique per kernel (`duplicateCell`). A
kernel runs one cell at a time (`busy`). `intent` is the cell's `// intent:` line (spec §19.2); it does
not change what the cell does. `check: true` asks a kernel that handles `cell/check` not to run a cell
with type errors. `timeoutMs` limits the cell's **own** time: the kernel stops counting while any of the
cell's host calls is in flight and goes on with what was left when the last one returns, so a host call
that waits for a person's approval never times the cell out (T141). The host bounds what its host calls
do itself (CodeAct gives a cell's `shell.run` calls at most the cell's timeout between them,
codeact.md "Kernels"). A cell stopped by `cell/cancel` or an interrupt stops at once, host calls in
flight or not. A kernel written before this counted host calls too: it is still version 1 (its cells
time out sooner; nothing is misread). The result:

```json
{"cellId":"c3","outcome":{"kind":"completed","value":{"type":"Array","preview":{"text":"[1, 2, …+5]","truncated":true}}},
 "output":{"text":"hi\n","truncated":false,"totalChars":3},"declared":["x"],"finished":false,"notified":1,"durationMs":4}
```

- `outcome` (`CellOutcome`): `completed {value?}` (the value of the last expression, if the cell has
  one; the kernel keeps it as `Out[cellId]`), `syntaxError {message, line?, column?, lineText?}` (nothing
  ran), `typeError {diagnostics}`, `runtimeError {name, message, code?, location?, stack}` (frames in
  cells only, `cell <id>:line:column`), `timedOut {afterMs}`, `cancelled`.
- `output`: what the cell printed, bounded: at most `outputChars` characters of it, the head and the
  tail, plus one marker line of at most 64 characters where the middle was left out; `totalChars`
  counts everything; `whole` (optional, T201a): all of it, when it was cut and the host's
  `overflowChars` allows. The host stores it in the environment's blob store and names the blob's file
  in the marker line (`… [n chars omitted; all of it: <file>] …`).
- `declared`: top-level names the cell declared (they persist).
- `unassigned` (optional, absent when empty): the names of `declared` that hold no value
  (`undefined` in TypeScript, unbound in the script kernel) because the cell stopped before it assigned
  them: after a runtime error, a timeout or a cancellation. A name an earlier cell gave a value keeps it
  (replMode hoists declarations as `var`s, which do not reset), so it is not listed.
- `finished`: the cell called `finish` and the host accepted it.
- `notified`: how many `cell/output` and `cell/activity` notifications (not late) the kernel sent about
  this cell before the result. Fx.Rpc completes a call in its reader while notifications wait for the
  dispatcher, so `Kernel.run` returns only after the host handled that many: the transcript never shows
  a result before its output.
- `durationMs`: wall time (0 in deterministic kernels).

**Values.** `ref` is `{kind: "variable", name}` or `{kind: "output", cellId}` (`Out[cellId]`).
`value/preview` renders within the kernel's preview limits or the ones given. `value/encode` returns
the value as a `Value` (below): a reference as the kernel holds it, otherwise inline JSON within
`maxBytes` (default `inlineBytes`; beyond, `tooLarge`), and `notEncodable` for what has no JSON form.
`value/decode` binds a `Value` to a top-level variable (task inputs, the prefill cell, a cell's texts:
spec §6.6, §6.9); `type` is the value's declared type in the kernel's language, for its type checker
(later cells' checks see the name). A name the language cannot have as a variable (a reserved word),
or one the kernel keeps (the bridge's global, its own globals such as `Out`), is `invalidName`: binding
it would break later cells. There is no size limit beyond the transport's; the host bounds what it
sends (a cell's text by `values.argumentMaxBytes`). `recall` restores a
cell's completion value (`Out[cellId]`), e.g. from the journal into a restarted kernel (spec §5).

**Snapshots.** `snapshot` returns the kernel's state as a `Value` in its own format (inline JSON, or a
blob), which `restore` accepts in any process of the same kernel. The host stores it.

### Kernel to host

| Method | Params | Result | Typed error |
|---|---|---|---|
| `host/call` | `HostCall {cellId?, binding, args: Value[]}` | `Value` | `HostCallError` |
| `finish` | `Finish {cellId?, result: Value}` | `null` | `FinishRejected {issues}` |

**`host/call`.** A cell calls a binding from `initialize`, arguments by position, each a `Value`. The
host decodes them into the binding's declared types (spec §6.6, §18) and answers a `Value`. Typed
errors: `unknownBinding {binding}`, `invalidArguments {issues}`, `unknownHandle {handle}`, `denied
{reason}` (a grant refused it), `failed {code, message, data?}`. The kernel throws in the cell an error
whose `code` is the error's `kind` and whose `data` is the error. When a cell is cancelled, times out
or is interrupted, the kernel cancels its host calls in flight (`$/cancelRequest`), and the host's
handlers see their scope cancelled.

**`finish`.** A cell ends the turn with a result (spec §4.5) that the host validates: it decodes the
value with the result's codec, then runs the checks (`Ensure`), with `KernelProtocol.Finish.validate`.
Evidence stays the values the cell passed, never retyped (spec §6.5). A rejection throws in the cell
with the issues (`code: "finishRejected"`), so the model can fix the result and try again.

### Notifications, kernel to host

| Notification | Params |
|---|---|
| `cell/output` | `{cellId?, kind: "text" \| "error", text, late}` |
| `cell/activity` | `{cellId?, activity: {kind, title, detail?}, late}` |

- Output is streamed as it happens, coalesced, and at most `outputChars` per cell. `kind: "error"` is an
  error that escaped a cell (background work, an unhandled rejection).
- **Late** notifications come after the cell's result, from work it left running (a timer). They carry
  the cell they belong to when the kernel can tell, and are not counted in `notified`.
- Activity (spec §6.5) is what a cell did that the host cannot see in its own calls, reported by
  kernel-side wrappers: `kind` is `read`, `refs`, `edit`, `run`, `test`, `review`, `agent`, `applies`, or
  another word; `title` is one line for the transcript. Activity of host calls is the host's (T205).

## Values (spec §6.6)

```fsharp
type Value =
  | Inline of Json                          // small: copied
  | Blob   of BlobRef * Preview             // large: content-addressed in envd, lazy, host-cached
  | Handle of HandleId * TypeRef * Preview  // host-resident live object
```

```json
{"kind":"inline","value":{"a":1}}
{"kind":"blob","blob":{"hash":"sha256:…","size":123456},"preview":{"text":"…","truncated":true}}
{"kind":"handle","handle":"h-9f2c…","type":"RunView","preview":{"text":"run 42 (active)","truncated":false}}
```

- **Handles** are host-resident objects (history, ledger results, agent transcripts, runs). The host
  issues them from a session's `HandleTable`: ids are `h-` and 128 random bits from `IIds` (the live
  interpreter is cryptographically strong), registered in the table; only ids it issued resolve
  (`HandleTable.resolve`, `resolveAs` with the expected `TypeRef`), so handles are **unforgeable and
  session-scoped**: one table per session. A kernel holds a handle opaquely and passes it back as it
  came. The reference kernel keeps ids in a `WeakMap`: cells see a frozen object with `type` and
  `preview`, cannot read the id, and plain data never encodes as a handle (the conformance suite checks
  that a look-alike object reaches the host as inline data).
- **Blobs** are references to content in the environment's blob store. Cells read stored output with
  the `blobs.read` binding (T201a), or as files where the store keeps them; values that live as blobs
  in kernels wait for remote environments (T501).
- **Inline** JSON is strict: kernels refuse (`notEncodable`) functions, cycles, big integers, non-finite
  numbers and references inside data instead of dropping them as `JSON.stringify` would. Numbers are
  exact in the host's JSON; TypeScript kernels hold them as doubles (exact to 2^53).
- **Declared-type decoding** (spec §18): the host decodes a value only into the type it expects, with
  that type's codec (`Value.decode`, `Kernel.encodeAs`); references never decode into data, and
  mismatches are model-readable issues.
- **Previews** are bounded by `Limits` (contracts.md): never longer than `maxChars`, `truncated` when
  something was left out, never half a surrogate pair. Kernels render them in their language's terms, in
  the host's style (`[1, 2, …+5]`, `{"a": 1, …+3}`, `"text…"(len=5000)`).

## Cancellation

A cell stops in three stages (`Kernel.interrupt`), as the Bun kernel spike found:

1. `cell/cancel` (or `$/cancelRequest` on the `cell/run`): the kernel interrupts the cell (in the
   reference kernel, disposing its `ModuleGraph`: timers, sockets, child processes), cancels its host
   calls, and answers the `cell/run` with `cancelled`. The kernel and its state live on.
2. `timeoutMs`: the kernel interrupts the cell itself, a synchronous busy loop included (the VM's
   watchdog), and answers `timedOut`. Only the cell's own time counts: the clock stops while a host
   call of the cell is in flight (the reference kernel's `CellClock`; the script kernel pauses its
   `CancelAfter`). The host never tells the kernel to hold the clock (a `cell/hold` notification was
   planned and dropped): replay answers the host's grants guard from one journal entry without running
   it, so a message the guard sent would be missing in replay and the kernel's input would diverge.
3. A cell the kernel cannot interrupt (a busy loop after an `await` blocks the event loop, so the
   kernel never reads `cell/cancel`): after `ExitGrace` the host kills the kernel (`Interruption.Killed`):
   it ends the connection, then the process, so how the connection ended never depends on whether the
   killed process's output ended first (replay.md). Calls in flight end `KernelFailure.Exited`. The host
   starts a new kernel: Jupyter semantics, its state is gone; the host restores what it journaled
   (`recall`, `value/decode`) or a snapshot (`restore`) if the kernel has them (spec §6.5 "Durability").

## Launch hygiene (spec §18)

`Kernel.start` takes a `KernelLaunch`: the resolved process (`ProcSpec`) and the runtime's deny list.
Before it starts anything, `KernelLaunch.check` refuses a launch whose environment is inherited
(`EnvVars.Inherit`; it must be `Exactly`, built from an allowlist) or sets a variable matching the
host's deny list (`Fx.Sdk.Manifest.deniedEnvironment`: `BUN_OPTIONS`, `NODE_OPTIONS`,
`DOTNET_STARTUP_HOOKS`, `LD_PRELOAD`, …) or the runtime's (`EnvDeny`, e.g. `BUN_JSC_*`), case-insensitively
on Windows. The launch itself (flags, owned directories, allowlist) is the runtime's `LaunchSpec`
(T114); nothing project-local runs before trust.

## The host's client

```fsharp
let host: KernelHost<Env> =
    { HostCall = fun call -> …                      // Eff<Env, Result<Value, HostCallError>>
      Finish = fun finish -> Eff.ret (Finish.validate turnResult ensures finish |> Result.map ignore)
      Output = fun output -> …
      Activity = fun activity -> … }

let! kernel = Kernel.start KernelOptions.defaults host setup launch   // Result<Kernel, StartError>
let! result = Kernel.run kernel { CellId = "c1"; Code = code; Intent = Some intent; TimeoutMs = None; Check = false }
let! value = Kernel.encodeAs kernel runResult (ValueRef.Variable "run")
let! stopped = Kernel.interrupt kernel                                 // Idle | Stopped | Killed
let! exitCode = Kernel.close kernel
```

- The environment is `'R :> IShell and IClock and IScopeWith<'R>` (`IShell` only to start a process).
  The connection lives in the scope of the effect that started it. `Kernel.connect` connects over any
  transport (in process, or a kernel reached otherwise), with a function that terminates it.
- Calls return `KernelFailure`: `Refused KernelError`, `Exited`, `Unanswered`, `Broken` (a protocol
  violation). Starting returns `StartError`: `UnsafeLaunch`, `NotStarted`, `Handshake` (with the end of
  the kernel's standard error), `Incompatible`.
- `Kernel.stderr` and `Kernel.stray` keep the last `DiagnosticsBytes` of each, for diagnostics.

## Conformance (spec §6.4)

`KernelConformance.tests subject` (`tests/Fx.Kernel.Tests/Conformance`) is the definition of done for a
kernel: required before an interpreter can be selected, on Linux, macOS and Windows. A subject is a
way to start the kernel and the code of each kind of cell in its language (`Snippets`), so the suite
stays language-neutral and checks everything through the protocol. Version 1 covers:

| Area | What |
|---|---|
| Handshake | protocol 1, a language, the required methods, `caps.snapshot` ⇔ snapshot and restore, refusing a host without a common version |
| State | persistence across cells, redefinition, declared names, completion values and `Out`, cell ids run once, one cell at a time |
| Output | in the result, streamed before it in order, bounded (head, tail, counts, streamed bound), late output attributed |
| Errors | runtime errors with their message, syntax errors that run nothing, state surviving errors, `unassigned` names after a throw and a timeout (none after a cell that completed) |
| Previews | within default and given limits for long strings, long lists and deep values; small values whole; `notFound` |
| Marshalling | JSON round trips (Unicode, int64 within 2^53, nesting), values visible to cells, `tooLarge`, `notEncodable`, `invalidName` (also the bridge's global), handles back to the host unchanged, plain data never a handle, blobs as references |
| Host calls | results, typed errors with their kind as code, missing bindings, cancellation with the cell, arguments beyond `inlineBytes` within `argumentBytes` whole, beyond `argumentBytes` refused in the cell (`invalidArguments`) |
| Finish | accepted, rejected by the codec, rejected by an `Ensure` check, with the issues in the cell |
| Cancellation | `cell/cancel`, `$/cancelRequest`, timeouts of waits and busy loops (the kernel survives), a host call answered after three times the cell's timeout (the cell completes: waits for the host do not count), the cell's own time going on after a host call and running out, a busy loop the kernel cannot interrupt: killed, a new kernel, `recall` |
| Activity | reported for its cell before the result (if cells can report it), intent lines |
| Optional | snapshot/restore into a new kernel, `cell/check`, determinism (when announced) |
| Launch hygiene | (process kernels) exactly the launch's environment, injection vectors refused, stray stdout survived, exit on close |

Subjects launched through a runtime's launch spec also start with every kind of injection vector in
the launcher's own environment (`StartFromLauncher`), and none may reach the kernel; the positive
controls that show the vectors work are `fx.runtime.bun`'s (tests/Fx.Ext.RuntimeBun.Tests).

**Content exclusions** are enforced by the host ([egress.md](egress.md)): bindings refuse excluded
files, and the egress hooks redact by provenance. The suite checks the kernel's part: a read the host
refuses as excluded (`test.read` of a `*.env` file, failing like `fs.read` with code `excluded`) fails
in the cell with the reason and no content, and other reads still work.

The suite runs against three kernels: the **script kernel** (F#, `tests/aot/Fx.Aot.Scenarios/Scenarios/Kernel`)
in process and as a child process (`Fx.Aot.Scenarios kernel-peer`), and the **bun-ts kernel**
(`fx.interpreter.bun-ts`) as the product launches it: its assets extracted and checked, on the pinned
Bun 1.4.5 through `fx.runtime.bun`'s hardened launch (`Interpreter.prepare`, [interpreters.md](interpreters.md)).
The tests download that Bun and check it against the pinned SHA-256 (`artifacts/kernel-bun-ts/`;
skipped offline, required in CI, whose full tier also runs it in an Alpine container for musl). The script
kernel is a second implementation, so the protocol is proven language-neutral.

## Native AOT

`kernel-codecs`, `kernel-framing`, `kernel-declarations`, `kernel-in-process` and `kernel-child` in the
JIT-vs-native diff: every message's codec, the framing, the generated declarations, and the client
driving the script kernel in process and as a child process through `LocalShell` (with stray output,
a refused launch, a snapshot and a killed kernel).

## Left to later tasks

- **T205:** kernel-side wrappers reporting activity. **Later:** packages (interpreters.md).
- **T213:** handles inside data: an inline value with the paths of the references it holds, so data
  that only looks like a reference never becomes one. **T501:** values as blobs in kernels.
- **Done in T201b:** bindings hand out and take back handles (interpreters.md); kept handles in the
  journal, back in a new kernel through `recall` while the host's table has them (codeact.md).
- **Done in T201a:** output overflow (`overflowChars`, `whole`); `fx.recall(callId)` was done in T128.
- **Done in T128** ([codeact.md](codeact.md#kept-values-and-recall-spec-65-durability)): cells' values
  journaled (`value/encode`) and restored into new kernels with `recall`; their streamed output journaled
  as evidence for calls in doubt.
