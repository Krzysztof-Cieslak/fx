// A hand-written stand-in for the generated kernel typings (`<global>.d.ts`). T102 generates the
// real file from the F# codecs; only the global's name is a parameter here, as it is there.

export const spikeBindings = ["shell.run", "fs.read"];

export function kernelTypings(globalName: string): string {
  return `// Kernel typings for the spike. Generated from codecs in the real system.
interface ShellOptions { readonly cwd?: string; readonly timeoutMs?: number }
interface ShellResult { readonly exitCode: number; readonly stdout: string; readonly stderr: string; readonly lines: string[] }
type TurnResult =
  | { kind: "done"; summary: string; evidence?: unknown[]; verify?: string }
  | { kind: "failed"; reason: string };
interface KernelApi {
  readonly shell: { run(command: string, options?: ShellOptions): Promise<ShellResult> };
  readonly fs: { read(path: string): Promise<string> };
  finish(result: TurnResult): Promise<void>;
}
declare const ${globalName}: KernelApi;
`;
}
