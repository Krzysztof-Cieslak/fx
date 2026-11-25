// The sample's process: serves it on standard input and output (`process` runtime) until the host
// closes standard input. The tests bundle this file with the pinned Bun and launch it hardened.

import { runStdio } from "../../sdk/src/index.ts";
import { extension } from "./sample.ts";

await runStdio([extension], { peer: { name: "sample-extension", version: extension.manifest.version } });
