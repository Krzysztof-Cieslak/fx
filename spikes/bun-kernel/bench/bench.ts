// Startup and cell latency of the spike kernel on the pinned Bun. Prints a Markdown table.
//   bun bench/bench.ts

import os from "node:os";
import { KernelHost, kernelBundle, pinnedBun, prepareOwned } from "../src/host.ts";
import { bunKernelLaunch, ownedVars, resolveLaunch } from "../src/launch-spec.ts";
import type { CellCheckResult } from "../src/kernel/protocol.ts";
import { kernelGlobal } from "../src/product.ts";

const g = kernelGlobal;
const rows: [string, string][] = [];

function stats(samples: number[]): string {
  const sorted = [...samples].sort((a, b) => a - b);
  const at = (q: number) => sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))]!;
  return `median ${at(0.5).toFixed(2)} ms, p95 ${at(0.95).toFixed(2)} ms (n=${samples.length})`;
}

async function timed<T>(fn: () => Promise<T>): Promise<[number, T]> {
  const t0 = performance.now();
  const value = await fn();
  return [performance.now() - t0, value];
}

const bun = await pinnedBun();
const bundle = await kernelBundle();
rows.push(["platform", `${process.platform}-${process.arch}, ${os.cpus().length} CPUs, ${bun.target}`]);
rows.push(["kernel bundle", `${(bundle.bytes / 1024).toFixed(1)} KiB`]);

// Process start of the pinned Bun with the hardened flags and environment.
{
  const layout = prepareOwned("bench-start");
  const vars = { ...ownedVars(layout), "bun.exe": bun.exe, "kernel.entry": "-e", "kernel.global": g };
  const launch = resolveLaunch(bunKernelLaunch, vars, process.env, { trusted: false });
  const cmd = [...launch.cmd.slice(0, launch.cmd.indexOf("-e")), "-e", "0"];
  const samples: number[] = [];
  for (let i = 0; i < 10; i++) {
    const t0 = performance.now();
    Bun.spawnSync(cmd, { env: launch.env, cwd: launch.cwd });
    samples.push(performance.now() - t0);
  }
  rows.push(["`bun -e 0` (hardened)", stats(samples)]);
}

// Kernel start: spawn -> initialize result, and -> first cell result.
{
  const init: number[] = [];
  const first: number[] = [];
  for (let i = 0; i < 5; i++) {
    const t0 = performance.now();
    const k = await KernelHost.launch();
    init.push(performance.now() - t0);
    await k.run("1");
    first.push(performance.now() - t0);
    await k.close();
  }
  rows.push(["kernel start to `initialize` result", stats(init)]);
  rows.push(["kernel start to first cell result", stats(first)]);
}

const k = await KernelHost.launch();
async function cells(label: string, n: number, code: (i: number) => string): Promise<void> {
  const roundTrip: number[] = [];
  const inKernel: number[] = [];
  for (let i = 0; i < n; i++) {
    const [ms, r] = await timed(() => k.run(code(i)));
    if (!r.ok) throw new Error(`${label}: ${JSON.stringify(r.error)}`);
    roundTrip.push(ms);
    inKernel.push(r.durationMs);
  }
  rows.push([`cell: ${label} (round trip)`, stats(roundTrip)]);
  rows.push([`cell: ${label} (in kernel)`, stats(inKernel)]);
}

await cells("`1 + 1`", 200, () => "1 + 1");
await cells("declare + compute", 200, (i) => `const v${i} = Array.from({ length: 100 }, (_, j) => j * ${i}); v${i}.length`);
await cells("top-level await", 200, () => "await new Promise((r) => setTimeout(r, 0)); 1");
await cells("host call", 200, () => `(await ${g}.shell.run("echo")).exitCode`);
await cells("console.log x100", 50, () => `for (let i = 0; i < 100; i++) console.log("line", i)`);

{
  const samples: number[] = [];
  const inKernel: number[] = [];
  for (let i = 0; i < 20; i++) {
    const [ms, r] = await timed(() => k.request<CellCheckResult>("cell/check", { cellId: `bc${i}`, code: `const r = await ${g}.shell.run("ls"); r.lines.length + v${i}.length` }));
    if (r.diagnostics.length > 0) throw new Error(JSON.stringify(r.diagnostics));
    samples.push(ms);
    inKernel.push(r.durationMs);
  }
  rows.push(["`cell/check` after 200+ cells of history", stats(samples)]);
}

{
  const rss = async () => Number((await k.run(`(await import("node:process")).memoryUsage().rss`)).value?.text) / 1024 / 1024;
  rows.push(["kernel RSS after ~1000 cells", `${(await rss()).toFixed(0)} MiB`]);
}
await k.close();

const fresh = await KernelHost.launch();
rows.push(["kernel RSS after start", `${(Number((await fresh.run(`(await import("node:process")).memoryUsage().rss`)).value?.text) / 1024 / 1024).toFixed(0)} MiB`]);
await fresh.close();

console.log("| Measure | Result |\n|---|---|");
for (const [k, v] of rows) console.log(`| ${k} | ${v} |`);


