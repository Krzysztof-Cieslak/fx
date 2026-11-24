# Bun kernel spike (T009)

A TypeScript prototype of the `bun-ts` CodeAct kernel on a pinned, hardened Bun. Findings and
recommendations for T114, T116 and T117: [`docs/spikes/bun-kernel.md`](../../docs/spikes/bun-kernel.md).

```sh
bun install --frozen-lockfile
bun check --no-script   # type-check the spike itself
bun test                # downloads + verifies the pinned Bun into artifacts/, then runs the kernel on it
bun bench/bench.ts      # startup and cell latency, as a Markdown table
```

| Path | Contents |
|---|---|
| `src/pinned-bun.ts` | Pinned version, per-target archive digests, download → verify → extract → probe |
| `src/launch-spec.ts` | The hardened `LaunchSpec` as data and its pure resolution |
| `src/kernel/` | The kernel process (bundled with the pinned Bun into one file before launch) |
| `src/host.ts` | Stand-in for the F# host: owned directories, launch, protocol client, stub capabilities |
| `src/typings.ts` | Stand-in for the generated `<global>.d.ts` |
| `test/` | Kernel behaviour, type-checking and hardening tests (each hardening test has a positive control) |
