# fx.exthost.bun

The bundled Bun extension host (spec §6.3, §16.1): the extension runtime `bun`, which runs TypeScript
extensions written with `@fx/sdk` on the pinned Bun (`fx.runtime.bun`). Module spec:
[docs/modules/extensions.md](../../docs/modules/extensions.md#runtimes).

A process extension: one Bun process, started with `fx.runtime.bun`'s hardened launch, serves
`fx.exthost.bun`, whose only contribution is the runtime `bun`
(`ExtensionRuntimes.contributionType`). The host activates a package whose manifest names `bun` through
this process (`extension/activate` with the manifest and the package directory): the process imports the
package's `extension.js` (one ES module, bundled with `bun build --target=bun`, whose default export is
the extension), checks that it is the manifest's extension (id and version), activates it in its own
environment, and serves it from then on. Like every package here it references `src/Fx.Sdk` and nothing
else.

| File | Contents |
|---|---|
| `ExthostBun.fs` | The id, the runtime's name, requirements (`bun 1.4.5`), the entry file of a package, the files of its package directory, its launch on a `RuntimeSpec` (owned directories and the runtime's files inside the package directory) and its manifest (`manifestOn`) |
| `ExthostFiles.fs` | **Generated**: the TypeScript of `packages/exthost-bun/src` and `packages/sdk/src` (without declaration files), by path. Do not edit: change the sources, then run `UPDATE_GOLDEN=1 dotnet test --project tests/Fx.Extensions.Tests` and rebuild |

## Writing a package for the `bun` runtime

A directory with the manifest (`runtime: "bun"`) and `extension.js`:

```ts
// src/extension.ts, bundled with: bun build src/extension.ts --target=bun --outfile=extension.js
import { type Extension, Manifest } from "@fx/sdk";
const extension: Extension = { manifest: { ...Manifest.create("acme.tools", "1.2.0", "bun"), … }, activate: … };
export default extension;
```

The hardened launch installs nothing (`--no-install`), so the bundle carries its dependencies. Every
package of a host runs in the same process.

## Changing the extension host or the SDK

1. Edit `packages/exthost-bun/src` (or `packages/sdk/src`) and check it there: `bun install
   --frozen-lockfile`, `bun check --no-script`, `bun test`.
2. Regenerate `ExthostFiles.fs` (above), rebuild, and run `dotnet test --project tests/Fx.Extensions.Tests`:
   the parity suite loads the TypeScript sample on the `bun` runtime through these files, on the pinned Bun.

## Left to T115b

Loading it in the host: writing its files into the data directory with hash checks, installing the
pinned Bun in the bootstrap's components phase and putting the installed Bun's path in its launch
(`${bin.bun}`), and adding it to the built-ins.
