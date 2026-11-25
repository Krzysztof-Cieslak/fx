// Kernel process entry point: `bun <hardened flags> main.ts [--bindings=<dir>] [--check-dir=<dir>]`.
// The host learns everything else in `initialize` (the global's name, bindings, limits), so nothing
// here knows the product name. `--bindings` is the directory where the host wrote the bridge's
// declarations (`<global>.d.ts`) for the checker; `--check-dir` is where the checker writes its files
// (by default a directory in the owned temporary directory).
//
// The protocol owns stdout. Frames are written with the stdout writer captured at startup, each on
// its own line (a leading newline ends any partial line a cell wrote to fd 1). Everything cells
// write through process.stdout/stderr or the main realm's console is captured as cell output.

import { Kernel, type KernelOptions } from "./kernel.ts";
import type { KernelSetup } from "./protocol.d.ts";
import { RpcPeer, readLines } from "./rpc.ts";

const writeStdout = process.stdout.write.bind(process.stdout);
const writeStderr = process.stderr.write.bind(process.stderr);
const argument = (name: string) => process.argv.find((a) => a.startsWith(`--${name}=`))?.slice(name.length + 3);
// --trace logs every frame to stderr (diagnostics only).
const trace = process.argv.includes("--trace") ? (text: string) => writeStderr(`[kernel] ${text.slice(0, 300)}\n`) : undefined;

const peer = new RpcPeer({
  write: (frame) => {
    trace?.(`-> ${frame}`);
    writeStdout("\n" + frame + "\n");
  },
  onStray: (text) => writeStderr(`kernel: ignored input: ${text.slice(0, 200)}\n`),
});
const options: KernelOptions = {};
const bindingsDir = argument("bindings");
const checkDir = argument("check-dir");
if (bindingsDir) options.bindingsDir = bindingsDir;
if (checkDir) options.checkDir = checkDir;
const kernel = new Kernel(peer, options);

const toText = (chunk: unknown) => (typeof chunk === "string" ? chunk : new TextDecoder().decode(chunk as Uint8Array));
process.stdout.write = ((chunk: unknown) => (kernel.capture(toText(chunk)), true)) as typeof process.stdout.write;
process.stderr.write = ((chunk: unknown) => (kernel.capture(toText(chunk)), true)) as typeof process.stderr.write;
for (const method of ["log", "info", "debug", "warn", "error", "trace", "dir"] as const) {
  console[method] = (...args: unknown[]) => kernel.capture(args.map(String).join(" ") + "\n");
}

process.on("uncaughtException", (error) => kernel.backgroundError("uncaughtException", error));
process.on("unhandledRejection", (error) => kernel.backgroundError("unhandledRejection", error));

peer
  .serve({
    peer: { name: "bun-ts", version: "1.0.0" },
    accept: (params) => ({ kernel: kernel.initialize(params.kernel as KernelSetup | undefined) }),
  })
  .onRequest("cell/run", (p, signal) => kernel.runCell(p, signal))
  .onRequest("cell/cancel", (p) => kernel.cancelCell(p))
  .onRequest("cell/check", (p) => kernel.checkCell(p))
  .onRequest("value/preview", (p) => kernel.previewValue(p))
  .onRequest("value/encode", (p) => kernel.encodeValue(p))
  .onRequest("value/decode", (p) => kernel.decodeValue(p))
  .onRequest("recall", (p) => kernel.recall(p));

await readLines(Bun.stdin.stream(), (line) => {
  trace?.(`<- ${line}`);
  peer.handleLine(line);
});
trace?.("stdin closed");
// The host closed stdin: nothing more can arrive.
process.exit(0);
