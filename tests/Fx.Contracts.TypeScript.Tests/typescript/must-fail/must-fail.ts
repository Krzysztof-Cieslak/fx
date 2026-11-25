// Each marked line must fail to type-check with the code in its `expect:` comment, and nothing else
// may fail. `bun scripts/declarations.ts` checks this.

import type * as domain from "../../golden/domain";

export const priority: domain.Priority = "urgent"; // expect: TS2322
export const span: domain.Span = { start: 1 }; // expect: TS2741
export const line: domain.Match["line"] = "14"; // expect: TS2322
export const unknownTag: domain.TurnResult = { kind: "finished", summary: "x" }; // expect: TS2322

export async function cell(): Promise<unknown> {
  await fx.shell.run(42); // expect: TS2345
  await fx.shell.exec("ls"); // expect: TS2339
  return fx.finish({ kind: "done" }); // expect: TS2345
}
