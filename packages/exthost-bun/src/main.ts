// The Bun extension host's process: serves `fx.exthost.bun`, and the packages its runtime loads, on
// standard input and output until the host closes standard input. The host launches it on the pinned
// Bun with `fx.runtime.bun`'s hardened launch (Fx.Ext.ExthostBun), from the files carried in the binary.

import { runStdio } from "../../sdk/src/index.ts";
import { extension } from "./exthost.ts";

await runStdio([extension], { peer: { name: extension.manifest.id, version: extension.manifest.version } });
