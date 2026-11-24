// A stand-in for the F# host: prepares the owned directories, builds the kernel bundle with the
// pinned Bun, launches it from the hardened LaunchSpec and speaks the kernel protocol.

import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { type LaunchSpec, type OwnedLayout, bunKernelLaunch, ownedBunfig, ownedLayout, ownedTsconfig, ownedVars, resolveLaunch } from "./launch-spec.ts";
import { type PinnedBun, ensurePinnedBun, sha256Hex } from "./pinned-bun.ts";
import { kernelGlobal, spikeArtifacts } from "./product.ts";
import { ErrorCodes, RpcError, RpcPeer, readLines } from "./rpc.ts";
import type {
  CellLateParams,
  CellRunParams,
  CellRunResult,
  FinishParams,
  FinishResult,
  HostCallParams,
  InitializeParams,
  InitializeResult,
} from "./kernel/protocol.ts";
import { protocolVersion } from "./kernel/protocol.ts";
import { kernelTypings, spikeBindings } from "./typings.ts";

let pinned: Promise<PinnedBun> | undefined;
export function pinnedBun(): Promise<PinnedBun> {
  return (pinned ??= ensurePinnedBun(join(spikeArtifacts, "runtimes")));
}

export interface KernelBundle {
  path: string;
  sha256: string;
  bytes: number;
}

let bundle: Promise<KernelBundle> | undefined;
/** Bundles src/kernel/main.ts into one file with the pinned Bun, as the release build would. */
export function kernelBundle(): Promise<KernelBundle> {
  return (bundle ??= (async () => {
    const bun = await pinnedBun();
    const outfile = join(spikeArtifacts, "kernel", "kernel.js");
    const proc = Bun.spawn([bun.exe, "build", join(import.meta.dir, "kernel", "main.ts"), "--target=bun", `--outfile=${outfile}`], {
      env: { DO_NOT_TRACK: "1", PATH: process.env.PATH ?? "" },
      stdout: "pipe",
      stderr: "pipe",
    });
    const [code, err] = await Promise.all([proc.exited, new Response(proc.stderr).text()]);
    if (code !== 0) throw new Error(`kernel bundle failed: ${err}`);
    const bytes = await Bun.file(outfile).bytes();
    return { path: outfile, sha256: sha256Hex(bytes), bytes: bytes.byteLength };
  })());
}

let ownedCounter = 0;
/** A fresh owned root per kernel, with the launcher's bunfig and tsconfig. */
export function prepareOwned(name = `k${process.pid}-${++ownedCounter}`): OwnedLayout {
  const layout = ownedLayout(join(spikeArtifacts, "owned", name));
  for (const dir of [layout.cwd, layout.home, layout.config, layout.transpilerCache, layout.installCache, layout.tmp, layout.check, join(layout.root, "config")]) {
    mkdirSync(dir, { recursive: true });
  }
  writeFileSync(layout.bunfig, ownedBunfig);
  writeFileSync(layout.tsconfig, ownedTsconfig);
  return layout;
}

export type HostCallHandler = (params: HostCallParams) => unknown;

/** Stub host capabilities: enough to show the round trip. */
export const stubHostCall: HostCallHandler = ({ method, args }) => {
  switch (method) {
    case "shell.run": {
      const command = String(args[0]);
      const stdout = `ran: ${command}\nok`;
      return { exitCode: 0, stdout, stderr: "", lines: stdout.split("\n") };
    }
    case "fs.read":
      return `contents of ${String(args[0])}`;
    default:
      throw new RpcError(ErrorCodes.methodNotFound, `no host capability ${method}`);
  }
};

export const acceptFinish = ({ result }: FinishParams): FinishResult => {
  const kind = (result as { kind?: unknown } | null)?.kind;
  return kind === "done" || kind === "failed" ? { accepted: true } : { accepted: false, errors: ["result.kind must be 'done' or 'failed'"] };
};

export interface LaunchOptions {
  globalName?: string;
  parentEnv?: Record<string, string | undefined>;
  trusted?: boolean;
  spec?: LaunchSpec;
  /** Runs the kernel in this directory instead of the owned one (hostile-cwd tests). */
  workspace?: string;
  layout?: OwnedLayout;
  hostCall?: HostCallHandler;
  finish?: (params: FinishParams) => FinishResult;
  initialize?: Partial<InitializeParams> | false;
  /** Pass --trace to the kernel (frames logged to its stderr). Defaults to on in CI. */
  trace?: boolean;
}

export class KernelHost {
  readonly stray: string[] = [];
  readonly late: CellLateParams[] = [];
  readonly finishes: FinishParams[] = [];
  readonly stderrLines: string[] = [];
  info: InitializeResult | undefined;
  /** Keep the owned directories after `close` (for inspection). */
  keepOwned = false;

  private constructor(
    readonly proc: Bun.Subprocess<"pipe", "pipe", "pipe">,
    readonly peer: RpcPeer,
    readonly layout: OwnedLayout,
    readonly cmd: string[],
    readonly env: Record<string, string>,
    readonly globalName: string,
  ) {}

  static async launch(options: LaunchOptions = {}): Promise<KernelHost> {
    const bun = await pinnedBun();
    const kernel = await kernelBundle();
    const layout = options.layout ?? prepareOwned();
    const globalName = options.globalName ?? kernelGlobal;
    const spec = options.spec ?? (options.workspace ? { ...bunKernelLaunch, cwdPolicy: "workspace" as const } : bunKernelLaunch);
    const vars = { ...ownedVars(layout), "bun.exe": bun.exe, "kernel.entry": kernel.path, "kernel.global": globalName };
    const launch = resolveLaunch(spec, vars, options.parentEnv ?? process.env, { trusted: options.trusted ?? false, workspace: options.workspace });
    const tracing = options.trace ?? process.env.CI === "true";
    if (tracing) launch.cmd.push("--trace");
    const proc = Bun.spawn(launch.cmd, { cwd: launch.cwd, env: launch.env, stdin: "pipe", stdout: "pipe", stderr: "pipe" });
    let host!: KernelHost;
    const peer = new RpcPeer({
      write: (frame) => {
        if (tracing) host.stderrLines.push(`[host ${performance.now().toFixed(1)}] -> ${frame.slice(0, 200)}`);
        proc.stdin.write(frame + "\n");
        proc.stdin.flush();
      },
      onStray: (text) => host.stray.push(text),
    });
    host = new KernelHost(proc, peer, layout, launch.cmd, launch.env, globalName);
    const hostCall = options.hostCall ?? stubHostCall;
    const finish = options.finish ?? acceptFinish;
    peer
      .on("host/call", (p: HostCallParams) => hostCall(p))
      .on("finish", (p: FinishParams) => (host.finishes.push(p), finish(p)))
      .on("cell/late", (p: CellLateParams) => host.late.push(p));
    void readLines(proc.stdout, (line) => {
      if (tracing && line.length > 0) host.stderrLines.push(`[host ${performance.now().toFixed(1)}] <- ${line.slice(0, 200)}`);
      peer.handleLine(line);
    }).then(
      () => peer.failAll(new Error("kernel stdout closed")),
      (e) => peer.failAll(new Error(`kernel stdout failed: ${e}`)),
    );
    void readLines(proc.stderr, (line) => host.stderrLines.push(line));
    if (options.initialize !== false) {
      host.info = (await host.peer.request("initialize", {
        protocolVersion,
        bindings: spikeBindings,
        moduleBase: layout.cwd,
        check: { dir: layout.check, typings: kernelTypings(globalName) },
        ...options.initialize,
      } satisfies InitializeParams)) as InitializeResult;
    }
    return host;
  }

  get stderr(): string {
    return this.stderrLines.join("\n");
  }

  /** Requests that take long print what the kernel said last (diagnostics for CI). */
  request<T>(method: string, params?: unknown): Promise<T> {
    const pending = this.peer.request(method, params) as Promise<T>;
    const watchdog = setTimeout(() => {
      console.error(`[host] ${method} unanswered after 4 s (kernel pid ${this.pid}, exit ${this.proc.exitCode}); stray: ${JSON.stringify(this.stray.slice(-5))}`);
      console.error(this.stderrLines.slice(-40).join("\n"));
    }, 4000);
    return pending.finally(() => clearTimeout(watchdog));
  }

  private cellCounter = 0;
  run(code: string, extra: Partial<CellRunParams> = {}): Promise<CellRunResult> {
    return this.request<CellRunResult>("cell/run", { cellId: `c${++this.cellCounter}`, code, ...extra });
  }

  get pid(): number {
    return this.proc.pid;
  }

  /** Graceful shutdown, then kill if the kernel does not exit. */
  async close(): Promise<number> {
    if (this.proc.exitCode === null) {
      try {
        await Promise.race([this.request("shutdown"), Bun.sleep(2000)]);
      } catch {
        // The kernel may already be gone.
      }
      const exited = await Promise.race([this.proc.exited, Bun.sleep(3000).then(() => undefined)]);
      if (exited === undefined) this.proc.kill("SIGKILL");
    }
    const code = await this.proc.exited;
    if (!this.keepOwned) rmSync(this.layout.root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    return code;
  }
}

export function processAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
