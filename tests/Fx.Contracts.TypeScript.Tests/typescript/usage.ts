// How code uses the generated declarations. Every `@ts-expect-error` line must fail to type-check:
// if the types were `any`, the directive would be unused and the check would fail.

import type * as contracts from "../golden/contracts";
import type * as domain from "../golden/domain";
import type * as store from "../golden/store";
import type * as variants from "../golden/variants";

// A tagged union narrows on its tag, exhaustively.
export function describe(result: domain.TurnResult): string {
  switch (result.kind) {
    case "done":
      return result.summary + (result.verify ?? "") + String(result.evidence?.length ?? 0);
    case "needInput":
      return result.decisions.join(", ");
    case "waiting":
      return result.note + result.on.join(" ");
    case "moved":
      return `${result.start}-${result.end}`;
    case "cancelled":
      return "cancelled";
    default: {
      const unreachable: never = result;
      return unreachable;
    }
  }
}

// Optional fields may be absent; nullable ones are present and may be null.
export function lineOf(m: domain.Match): string {
  const note: string | undefined = m.note;
  const span: domain.Span | null = m.span;
  // @ts-expect-error -- `span` is nullable, not a Span
  const strict: domain.Span = m.span;
  // @ts-expect-error -- `note` is optional
  const required: string = m.note;
  // @ts-expect-error -- not a priority
  const priority: domain.Priority = "urgent";
  // @ts-expect-error -- maps hold integers here
  const counts: domain.Match["counts"] = { calls: "37" };
  return `${m.path}:${m.line} ${note} ${span?.start} ${strict.start} ${required} ${priority} ${counts["calls"]}`;
}

// Values the generated types accept, written by hand.
export const minimal: domain.Match = {
  path: "a.fs",
  line: 1,
  text: "x",
  score: 0.5,
  weight: 1,
  sequence: 7,
  span: null,
  priority: "low",
  counts: {},
};

// @ts-expect-error -- `span` may not be left out
export const missingSpan: domain.Match = { path: "a.fs", line: 1, text: "x", score: 0, weight: 0, sequence: 0, priority: "low", counts: {} };

// @ts-expect-error -- `note` is optional, not nullable
export const nullNote: domain.Match = { ...minimal, note: null };

// @ts-expect-error -- unknown fields are excess properties
export const extra: domain.Span = { start: 1, end: 2, length: 1 };

export const tree: domain.Tree = { kind: "node", label: "root", children: [{ kind: "leaf", value: 1 }] };

// @ts-expect-error -- a leaf has no children
export const badTree: domain.Tree = { kind: "leaf", value: 1, children: [] };

export const issues: contracts.Issue[] = [{ path: ["items", 2, "name"], message: "expected string, got number 42" }];

export const blob: store.BlobRef = { hash: "sha256:00", size: 3 };

export const config: variants.VariantsConfig = { "edit.guard": "require", "model.role.main": null };

// @ts-expect-error -- not a value of the slot
export const badConfig: variants.VariantsConfig = { "edit.guard": "sometimes" };

// The kernel's host bridge, through the global (the product name, generated from the constant).
export async function cell(): Promise<string> {
  const run = await fx.shell.run("dotnet test", { cwd: "tests", timeoutMs: 60000 });
  const lines: string[] = run.lines;
  const refs: fx.Match[] = await fx.repo.refs("Http.retry");
  await fx.fs.delete("old.txt");
  await fx.tools["my-tool"]("a", 1);
  await fx.ledger.search(undefined, "retry");
  const review = await fx.agents.reviewer({ diff: "", intent: "fix" });
  const verdict: "approve" | "changes" = review.verdict;
  const finished = await fx.finish({ kind: "done", summary: `${lines.length} ${refs.length} ${verdict}`, verify: "dotnet test" });
  const first: fx.Issue | undefined = finished.accepted ? undefined : finished.issues?.[0];

  // @ts-expect-error -- no such method
  await fx.shell.exec("ls");
  // @ts-expect-error -- the command is a string
  await fx.shell.run(42);
  // @ts-expect-error -- `text` is required after the optional `scope`
  await fx.ledger.search("repo");
  // @ts-expect-error -- not a tag of TurnResult
  await fx.finish({ kind: "finished", summary: "x" });
  // @ts-expect-error -- `done` needs a summary
  await fx.finish({ kind: "done" });
  // @ts-expect-error -- the result is a promise
  const notAwaited: fx.ShellResult = fx.shell.run("ls");
  return `${first?.message} ${notAwaited.stdout}`;
}

// Renamed types keep working under their TypeScript names.
import type * as naming from "../golden/naming";
export const renamed: naming.Uses = ["x", 1, "s", true, [[]], null, { a: "b" }];
