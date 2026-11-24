// Kernel process entry point: `bun <hardened flags> kernel.js --global=<name>`.
//
// The protocol owns stdout. Frames are written with the stdout writer captured at startup, each
// on its own line (a leading newline ends any partial line a cell wrote to fd 1). Everything cells
// write through process.stdout/stderr or the main realm's console is captured as cell output.

import { Kernel } from "./kernel.ts";
import { RpcPeer, readLines } from "../rpc.ts";

function argValue(name: string): string | undefined {
  const prefix = `--${name}=`;
  return process.argv.find((a) => a.startsWith(prefix))?.slice(prefix.length);
}

const globalName = argValue("global");
if (!globalName) {
  process.stderr.write("kernel: --global=<name> is required\n");
  process.exit(2);
}

const writeStdout = process.stdout.write.bind(process.stdout);
const writeStderr = process.stderr.write.bind(process.stderr);

const peer = new RpcPeer({
  write: (frame) => writeStdout("\n" + frame + "\n"),
  onStray: (text) => writeStderr(`kernel: ignored input: ${text.slice(0, 200)}\n`),
});
const kernel = new Kernel(globalName, peer);

const toText = (chunk: unknown) => (typeof chunk === "string" ? chunk : new TextDecoder().decode(chunk as Uint8Array));
process.stdout.write = ((chunk: unknown) => (kernel.capture(toText(chunk)), true)) as typeof process.stdout.write;
process.stderr.write = ((chunk: unknown) => (kernel.capture(toText(chunk)), true)) as typeof process.stderr.write;
for (const method of ["log", "info", "debug", "warn", "error", "trace", "dir"] as const) {
  console[method] = (...args: unknown[]) => kernel.capture(args.map(String).join(" ") + "\n");
}

process.on("uncaughtException", (error) => kernel.backgroundError("uncaughtException", error));
process.on("unhandledRejection", (error) => kernel.backgroundError("unhandledRejection", error));

peer
  .on("initialize", (p) => kernel.initialize(p))
  .on("cell/run", (p) => kernel.runCell(p))
  .on("cell/check", (p) => kernel.checkCell(p))
  .on("cell/cancel", (p) => kernel.cancelCell(p))
  .on("value/preview", (p) => kernel.previewValue(p))
  .on("shutdown", () => {
    setTimeout(() => process.exit(0), 10);
    return null;
  });

await readLines(Bun.stdin.stream(), (line) => peer.handleLine(line));
// The host closed stdin: nothing more can arrive.
process.exit(0);
