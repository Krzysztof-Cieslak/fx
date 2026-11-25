# kernel-bun-ts: the reference kernel

The reference implementation of the kernel protocol ([`docs/modules/kernel-protocol.md`](../../docs/modules/kernel-protocol.md))
for TypeScript cells on the pinned Bun. It is the kernel the conformance suite
(`tests/Fx.Kernel.Tests`) runs against on Linux, macOS and Windows, and the start of the
`bun-ts` kernel that T117 turns into the `fx.interpreter.bun-ts` extension package (spec §6.3).

It grew out of the Bun kernel spike ([`spikes/bun-kernel`](../../spikes/bun-kernel),
[`docs/spikes/bun-kernel.md`](../../docs/spikes/bun-kernel.md)): the same transpile and `node:vm` design,
upgraded to the protocol (handshake, values, streaming, cancellation of host calls). The spike stays as
it was, as the record of what was measured.

```sh
bun install --frozen-lockfile
bun check --no-script   # type-check (with the generated protocol types)
bun test                # unit tests of previews, values and output
```

The kernel's behaviour is tested by the conformance suite: `dotnet test --project tests/Fx.Kernel.Tests`
downloads the pinned Bun (digest-checked) into `artifacts/kernel-bun-ts/`, bundles `src/main.ts` with it
into one file, and launches that with the spike's hardened flags. It never runs on a Bun from `PATH`.

| Path | Contents |
|---|---|
| `src/protocol.d.ts` | The protocol's messages, **generated** from the F# codecs (`Fx.Kernel.KernelDeclarations`). Do not edit: change the codecs and run the tests with `UPDATE_GOLDEN=1` |
| `src/rpc.ts` | JSON-RPC as the host speaks it: the `initialize` handshake, `$/cancelRequest` both ways, error codes, frames on lines of their own |
| `src/kernel.ts` | Cells, the host bridge global, values, cancellation |
| `src/values.ts` | `Value` decode and encode (references held in WeakMaps, so cells cannot forge them), bounded previews |
| `src/output.ts` | Bounded output (head and tail) and streamed output |
| `src/positions.ts` | Stack positions mapped back to cell lines (from the spike; replMode has no source maps) |
| `src/main.ts` | The process: stdio, output capture |

## For T117

- Keep the protocol in `src/protocol.d.ts` generated, and keep this kernel passing the conformance suite.
- Not here yet: `cell/check` (the spike's `bun check` checker), the generated `<global>.d.ts` and prompt
  pack, kernel-side wrappers that report activity (cells can report one with `<global>.activity(kind,
  title, detail?)`), content exclusions, and packages. The launch (flags, owned directories, environment)
  is T114's `LaunchSpec`; the tests use the spike's.
- `Out[cellId]` holds each cell's completion value; `recall` restores entries after a restart.
