// The entry point of a TypeScript extension process (spec §16.1 `process` runtime): serves extensions
// on standard input and output until the host closes standard input. The protocol owns standard
// output: frames are written with the writer captured at startup, and whatever extension code writes
// to `process.stdout` or the console goes to standard error.

import type { Extension } from "./extension.ts";
import { RpcPeer, readLines } from "./rpc.ts";
import { type ServerOptions, serveExtensions } from "./server.ts";

/**
 * Serves the extensions on this process's standard input and output, then exits with 0 once the host
 * has closed standard input and every request it sent is answered.
 */
export async function runStdio(extensions: readonly Extension[], options: ServerOptions): Promise<never> {
  const stdout = process.stdout.write.bind(process.stdout);
  const stderr = process.stderr.write.bind(process.stderr);
  process.stdout.write = ((chunk: unknown, ...rest: unknown[]) => stderr(chunk as string, ...(rest as []))) as typeof process.stdout.write;
  for (const method of ["log", "info", "debug", "warn", "error", "trace", "dir"] as const) {
    console[method] = (...args: unknown[]) => void stderr(args.map(String).join(" ") + "\n");
  }

  const peer = new RpcPeer({
    write: (frame) => void stdout(frame + "\n"),
    onStray: (text) => void stderr(`ignored input: ${text.slice(0, 200)}\n`),
  });
  serveExtensions(peer, extensions, options);

  await readLines(Bun.stdin.stream(), (line) => peer.handleLine(line));
  await peer.drain();
  await new Promise<void>((resolve) => stdout("", () => resolve()));
  process.exit(0);
}
