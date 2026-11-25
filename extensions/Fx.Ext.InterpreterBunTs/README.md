# fx.interpreter.bun-ts

The bundled interpreter for TypeScript cells (spec §6.3): a persistent kernel on the pinned Bun
(`fx.runtime.bun`), its launch, the host bridge's declarations, the prompt pack and `bun check`. Module
spec: [docs/modules/interpreters.md](../../docs/modules/interpreters.md).

A native extension: its manifest declares one contribution, the interpreter `bun-ts`
(`InterpreterProviders.contributionType`), and its activation provides `BunTs.provider`. Like every
package here it references `src/Fx.Sdk` and nothing else.

| File | Contents |
|---|---|
| `BunTs.fs` | The provider: requirements (`bun 1.4.5`), caps, launch, the declarations of a binding surface (`<global>.d.ts`), the prompt pack, the manifest and the extension |
| `KernelFiles.fs` | **Generated**: the kernel's sources from `packages/kernel-bun-ts/src`, as F# string literals. Do not edit: change the kernel, then run `UPDATE_GOLDEN=1 dotnet test --project tests/Fx.Ext.InterpreterBunTs.Tests` and rebuild |

## Changing the kernel

1. Edit `packages/kernel-bun-ts/src` and check it there: `bun install --frozen-lockfile`,
   `bun check --no-script`, `bun test`.
2. Regenerate `KernelFiles.fs` (above), rebuild, and run `dotnet test --project tests/Fx.Kernel.Tests`
   (the kernel conformance suite on the pinned Bun) and `--project tests/Fx.Ext.InterpreterBunTs.Tests`
   (end to end, with the core bindings and `cell/check`).
3. The assets' digest changes, so the new kernel is extracted next to the old one; nothing else
   versions it.

## Changing the prompt or the declarations

The golden files in `tests/Fx.Ext.InterpreterBunTs.Tests/golden` (`<global>.d.ts`, `prompt.md`) show
what a model reads for the core bridge. Review their diff (`UPDATE_GOLDEN=1`); the live tests check that
the prompt's examples type-check against the declarations.

## Bumping Bun

The kernel relies on `Bun.Transpiler`'s replMode (and `macro: false`), `Bun.ModuleGraph`, `bun check`
and its output format, all as Bun 1.4.5 has them (docs/spikes/bun-kernel.md). When
`extensions/Fx.Ext.RuntimeBun` moves to another Bun, change `BunTs.requires` with it and run the suites
above on every operating system.
