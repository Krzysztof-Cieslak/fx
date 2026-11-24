// The product name for the TypeScript side of the spike.
//
// The single naming constant is `FxProductName` in Directory.Build.props (spec §20 "Naming").
// The F# side reads it at build time (Fx.Product); this harness plays the host's role, so it reads
// the same property instead of hardcoding the name. The kernel itself never knows the name: the
// launcher passes the global name on its command line.

import { readFileSync } from "node:fs";
import { join } from "node:path";

export const repoRoot = join(import.meta.dir, "..", "..", "..");

function readProductName(): string {
  const props = readFileSync(join(repoRoot, "Directory.Build.props"), "utf8");
  const match = /<FxProductName>\s*([A-Za-z][A-Za-z0-9]*)\s*<\/FxProductName>/.exec(props);
  if (!match?.[1]) throw new Error("FxProductName not found in Directory.Build.props");
  return match[1];
}

export const productName = readProductName();

/** Mirrors `Fx.Product.kernelGlobal`. */
export const kernelGlobal = productName;

/** Mirrors `Fx.Product.envPrefix`. */
export const envPrefix = productName.toUpperCase() + "_";

/** File name of the generated kernel typings, e.g. `fx.d.ts`. */
export const typingsFileName = productName + ".d.ts";

/** Where the spike keeps downloads and owned directories (ignored by git through `artifacts/`). */
export const spikeArtifacts = join(repoRoot, "artifacts", "bun-kernel-spike");
