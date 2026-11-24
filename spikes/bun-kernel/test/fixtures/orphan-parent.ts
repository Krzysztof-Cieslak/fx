// Intermediate parent for the --no-orphans test: launches a kernel, has a cell start a long-lived
// child, prints the pids and waits to be killed.
//   bun orphan-parent.ts hardened|control|busy   (busy: the kernel is stuck in a loop when the host dies)

import { writeFileSync } from "node:fs";
import { KernelHost, prepareOwned } from "../../src/host.ts";
import { bunKernelLaunch, ownedBunfig } from "../../src/launch-spec.ts";

const mode = process.argv[2];
const layout = prepareOwned(`orphans-${mode}-${process.pid}`);
let spec = bunKernelLaunch;
if (mode === "control") {
  spec = { ...bunKernelLaunch, args: bunKernelLaunch.args.filter((a) => a !== "--no-orphans") };
  writeFileSync(layout.bunfig, ownedBunfig.replace("noOrphans = true", "noOrphans = false"));
}
const k = await KernelHost.launch({ spec, layout });
const r = await k.run(`const { spawn } = await import("bun"); const { execPath } = await import("node:process"); spawn([execPath, "-e", "await Bun.sleep(120000)"], { stdio: ["ignore", "ignore", "ignore"] }).pid`);
if (mode === "busy") {
  k.run(`await 0; while (true) {}`).catch(() => {});
  await Bun.sleep(200);
}
process.stdout.write(JSON.stringify({ kernel: k.pid, child: Number(r.value?.text) }) + "\n");
await Bun.sleep(120_000);
