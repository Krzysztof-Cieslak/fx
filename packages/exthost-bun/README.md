# exthost-bun

The Bun extension host's process (`fx.exthost.bun`, spec §6.3, §16.1), written against `@fx/sdk`: it
serves the extension runtime `bun`, which loads packages (`extension.js`, default export the extension)
and serves them in this process. The binary carries these sources (`extensions/Fx.Ext.ExthostBun`,
whose README says how to change them).

- `src/exthost.ts`: the runtime and the extension; `src/main.ts`: the process.
- `test/`: loading packages, and the process serving one (the TypeScript sample bundled as a package);
  `test/fixtures/manifest.json` is the F# package's manifest (written by tests/Fx.Extensions.Tests).
- `bun install --frozen-lockfile`, `bun check --no-script`, `bun test`.
