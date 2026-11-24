// The pinned Bun: exact version, per-target release archive and SHA-256 digest.
//
// This is the data T114 (`fx.runtime.bun`) turns into a `Component` with an `Archive` install
// method. The digests come from the release's SHASUMS256.txt and match the GitHub release asset
// digests. The kernel only ever runs on this Bun, never on a user-installed one.

import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { extractEntry } from "./zip.ts";

export const bunPin = {
  version: "1.4.5",
  revision: "1.4.5+a7733435c",
  /** TypeScript version that `bun check` matches (`process.versions.typescript`). */
  typescript: "7.0.2",
  releaseUrl: "https://github.com/oven-sh/bun/releases/download/bun-v1.4.5",
  /** Asset name without `.zip` -> SHA-256 of the zip. */
  targets: {
    "bun-linux-x64": "94559b3b528995a49452f9f2404cec74f2efd7d06e0a000a8cd214b02c3666eb",
    "bun-linux-x64-baseline": "dcb23a18a1c2fff12d57bcef696dbbb42eb67fde29f43816ecb595459a24c463",
    "bun-linux-x64-musl": "9b6bdba1fa0914b6adfe48e4438212e2c106d9199b77ba642a9b8a3968a49a59",
    "bun-linux-x64-musl-baseline": "96fb94108eed872ff623ed39007d6b5699a83a5d0086fcfba84921c18145e3e6",
    "bun-linux-aarch64": "18656a3bf0ea5641f5796ae4213b75255579a70a0fded2f337933785568cdf84",
    "bun-linux-aarch64-musl": "d655765d3a0c0fdf707e27c7aa1a4f2c84f1bbc1f759db7e6e3d3c5be414396c",
    "bun-darwin-x64": "a4972e778cce4b6a194d7d289f326aa61576d71d80b7a53e9da2f3739566e5fd",
    "bun-darwin-x64-baseline": "fb0121739a7e80e7511b6bfe4d2116422b48ba18a013acadef0c425348e95891",
    "bun-darwin-aarch64": "b5d6bc2e13caf14e57c89661e89efd073ff16165b41bfe43882616410609cd10",
    "bun-windows-x64": "815ab45ec7f549abf87e6869584b506eab088e24225683957b7e150b47d6de30",
    "bun-windows-x64-baseline": "3c081c73865e7a3453a9d514cc2ba3d699880621bcf67bb17241ff5194cb1d4b",
    "bun-windows-aarch64": "41f57d49c3c6811888d9633c569e40b4eb1f7e92b5e5336e3abd3af03187d77a",
  } as Record<string, string>,
} as const;

export interface PinnedBun {
  exe: string;
  target: string;
  version: string;
}

function hasAvx2(): boolean {
  if (process.platform === "linux") {
    try {
      return /\bavx2\b/.test(readFileSync("/proc/cpuinfo", "utf8"));
    } catch {
      return false;
    }
  }
  if (process.platform === "darwin") {
    const r = Bun.spawnSync(["sysctl", "-n", "machdep.cpu.leaf7_features"]);
    return /AVX2/.test(r.stdout.toString());
  }
  // Windows: detection needs IsProcessorFeaturePresent; GitHub runners have AVX2. T114 can use
  // System.Runtime.Intrinsics.X86.Avx2.IsSupported on the F# side.
  return true;
}

function isMusl(): boolean {
  return process.platform === "linux" && (existsSync("/etc/alpine-release") || existsSync("/lib/ld-musl-x86_64.so.1") || existsSync("/lib/ld-musl-aarch64.so.1"));
}

/** The release asset (without `.zip`) for the current machine. */
export function detectTarget(): string {
  const os = process.platform === "win32" ? "windows" : process.platform;
  const arch = process.arch === "arm64" ? "aarch64" : process.arch;
  let target = `bun-${os}-${arch}`;
  if (isMusl()) target += "-musl";
  if (arch === "x64" && !hasAvx2()) target += "-baseline";
  if (!(target in bunPin.targets)) throw new Error(`no pinned Bun for ${target}`);
  return target;
}

export function exeName(target: string): string {
  return target.startsWith("bun-windows-") ? "bun.exe" : "bun";
}

export function sha256Hex(data: Uint8Array): string {
  return new Bun.CryptoHasher("sha256").update(data).digest("hex");
}

/** Verifies an archive against the pin; throws on mismatch. Pure, so tests can feed it bad bytes. */
export function verifyArchive(target: string, archive: Uint8Array): void {
  const expected = bunPin.targets[target];
  if (!expected) throw new Error(`no pinned digest for ${target}`);
  const actual = sha256Hex(archive);
  if (actual !== expected) throw new Error(`digest mismatch for ${target}.zip: expected sha256:${expected}, got sha256:${actual}`);
}

async function download(url: string): Promise<Uint8Array> {
  let lastError: unknown;
  for (let attempt = 1; attempt <= 3; attempt++) {
    try {
      const res = await fetch(url, { redirect: "follow" });
      if (!res.ok) throw new Error(`GET ${url}: ${res.status}`);
      return new Uint8Array(await res.arrayBuffer());
    } catch (e) {
      lastError = e;
      await Bun.sleep(attempt * 1000);
    }
  }
  throw lastError;
}

interface InstallRecord {
  version: string;
  target: string;
  archiveSha256: string;
  exeSha256: string;
}

function probe(exe: string): void {
  const r = Bun.spawnSync([exe, "--revision"], { env: { DO_NOT_TRACK: "1" } });
  const revision = r.stdout.toString().trim();
  if (revision !== bunPin.revision) throw new Error(`pinned Bun probe failed: expected ${bunPin.revision}, got '${revision}'`);
}

/**
 * Resolve -> fetch -> verify -> stage -> activate -> probe, into `<cacheRoot>/<version>/<target>/`.
 * A cached install is re-verified against the digest recorded at install time.
 */
export async function ensurePinnedBun(cacheRoot: string, target = detectTarget()): Promise<PinnedBun> {
  const dir = join(cacheRoot, bunPin.version, target);
  const exe = join(dir, exeName(target));
  const recordPath = join(dir, "install.json");
  if (existsSync(recordPath) && existsSync(exe)) {
    const record = JSON.parse(readFileSync(recordPath, "utf8")) as InstallRecord;
    if (record.version === bunPin.version && record.archiveSha256 === bunPin.targets[target] && record.exeSha256 === sha256Hex(readFileSync(exe))) {
      return { exe, target, version: bunPin.version };
    }
    rmSync(dir, { recursive: true, force: true });
  }
  const archive = await download(`${bunPin.releaseUrl}/${target}.zip`);
  verifyArchive(target, archive);
  const binary = extractEntry(archive, `${target}/${exeName(target)}`);
  const staging = `${dir}.staging-${process.pid}`;
  rmSync(staging, { recursive: true, force: true });
  mkdirSync(staging, { recursive: true });
  writeFileSync(join(staging, exeName(target)), binary);
  if (process.platform !== "win32") chmodSync(join(staging, exeName(target)), 0o755);
  const record: InstallRecord = { version: bunPin.version, target, archiveSha256: bunPin.targets[target]!, exeSha256: sha256Hex(binary) };
  writeFileSync(join(staging, "install.json"), JSON.stringify(record, null, 2));
  probe(join(staging, exeName(target)));
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(join(dir, ".."), { recursive: true });
  renameSync(staging, dir);
  return { exe, target, version: bunPin.version };
}
