import { Database } from "bun:sqlite";
import { describe, expect, test } from "bun:test";
import { join } from "node:path";
import { productName as generatedName } from "../packages/sdk/src/generated/product";
import {
  analyse,
  bridgeCalls,
  contextOf,
  dataDirectory,
  definitions,
  productName,
  receiptText,
  retyped,
  RETYPED_MIN,
  scan,
  sessions,
  total,
  type ContextEvent,
} from "./cells";

const g = productName();
const long = (seed: string, n = 300) => Array.from({ length: n }, (_, k) => seed[k % seed.length]).join("") + seed;

describe("reading cells", () => {
  test("the product name is the naming constant", () => {
    expect(g).toBe(generatedName);
  });

  test("literals are decoded and blanked; comments go", () => {
    const s = scan(`// intent: x\nconst a = "one\\ntwo\\u0041" /* note */ + 'b\\'c'`);
    expect(s.literals).toEqual(["one\ntwoA", "b'c"]);
    expect(s.code).toBe(`\nconst a = ""   + ""`);
  });

  test("a template literal gives its text between expressions, and its expressions stay code", () => {
    const s = scan("const t = `head ${name + `inner`} tail`");
    expect(s.literals).toEqual(["head ", "inner", " tail"]);
    expect(s.code).toContain("${name + ``}");
  });

  test("regular expressions are not strings; division is not a regular expression", () => {
    expect(scan(`x.replace(/"/g, "'")`).literals).toEqual(["'"]);
    expect(scan(`const r = a / b / c; "s"`).literals).toEqual(["s"]);
  });

  test("bridge calls by binding", () => {
    const code = scan(
      `await ${g}.shell.run("ls"); await ${g} . fs . editRange(r, ""); ${g}.jobs.start("x"); ${g}.finish({}); other.${g}.fs.write("a", "b")`,
    ).code;
    expect([...bridgeCalls(code, g)].sort()).toEqual(["edit", "finish", "jobs", "shell"]);
  });

  test("functions a cell defines, with their bodies", () => {
    const code = scan(
      [
        `async function sh(c: string) { return (await ${g}.shell.run(c)).stdout }`,
        `const put = async (p: string, t: string): Promise<void> => { await ${g}.fs.write(p, t) }`,
        `const line = (s: string) => s.split("\\n")[0]`,
        `const sum = (1 + 2)`,
      ].join("\n"),
    ).code;
    const defs = definitions(code);
    expect(defs.map((d) => d.name).sort()).toEqual(["line", "put", "sh"]);
    const sh = defs.find((d) => d.name === "sh")!;
    expect(code.slice(sh.start, sh.end)).toContain("shell.run(c)).stdout }");
    const line = defs.find((d) => d.name === "line")!;
    expect(code.slice(line.start, line.end)).not.toContain("sum");
  });

  test("retyped text is what probes of it find verbatim", () => {
    // 310 characters: probes at 0, 60, 120 and 180 cover the first 300.
    const text = long("abcdefghij");
    expect(text.length).toBe(310);
    expect(retyped(text, "…" + text + "…")).toBe(300);
    expect(retyped(text.slice(0, RETYPED_MIN - 1), text)).toBe(0);
    expect(retyped(long("0123456789"), long("abcdefghij"))).toBe(0);
  });

  test("a receipt's file pieces count without their line numbers", () => {
    const text = receiptText([
      { kind: "text", text: "head" },
      { kind: "file", path: "a.txt", digest: "sha256:0", text: "     1\tone\n     2\ttwo" },
      { kind: "withheld", path: "b.txt" },
    ]);
    expect(text).toBe("head\none\ntwo\n");
  });
});

/** A session: each cell is its code (or `{code, texts}`), the receipt its text. */
function session(...steps: [string | { code: string; texts?: Record<string, string>; tool?: string }, string][]): ContextEvent[] {
  const events: ContextEvent[] = [{ kind: "framed", sections: [] }];
  steps.forEach(([call, receipt], k) => {
    const c = typeof call === "string" ? { code: call } : call;
    const name = c.tool ?? "cell";
    const args = name === "cell" ? { code: c.code, ...(c.texts ? { texts: c.texts } : {}) } : { command: c.code };
    events.push({ kind: "replied", turn: 1, step: k + 1, used: { model: "test/model" }, parts: [{ kind: "toolCall", id: `c${k}`, name, arguments: JSON.stringify(args) }] });
    events.push({ kind: "returned", call: `c${k}`, name, isError: false, turn: 1, step: k + 1, pieces: [{ kind: "text", text: receipt }] });
  });
  return events;
}

describe("classifying a session", () => {
  const output = long("the quick brown fox ");

  test("kinds, earlier data and helpers, other tools", () => {
    const r = analyse(
      session(
        [`// intent: look\nconst r = await ${g}.shell.run("ls")\nr.stdout`, `${output}\nDeclared: r`],
        [`const sh = async (c: string) => (await ${g}.shell.run(c)).stdout`, "Declared: sh"],
        [`sh("git status")`, "clean"],
        [`r.stdout.split("\\n").filter(Boolean)`, "[…]"],
        [`const W = "/work"`, "Declared: W"],
        [`await ${g}.fs.edit(W + "/a.txt", { old: "x", new: "y" })`, "Edited a.txt"],
        [{ code: `await ${g}.fs.write("b.txt", body)`, texts: { body: "new text" } }, "written"],
        [`let a = (await ${g}.fs.read("a.txt")).text\na = a.replace("y", "z")\nawait ${g}.fs.write("a.txt", a)`, "written\nDeclared: a"],
        [`const j = await ${g}.jobs.start("bun test")`, "Declared: j"],
        [{ code: "ls", tool: "bash" }, "a.txt"],
        [`await ${g}.finish({ kind: "done", summary: "done" })`, "The turn's result was accepted."],
      ),
      g,
    );
    expect(r.cells).toBe(10);
    expect(r.tools).toEqual({ bash: 1 });
    expect(r.models).toEqual(["test/model"]);
    expect(r.list.map((c) => c.kind)).toEqual([
      "run a command and print",
      "define helpers",
      "run a command and print",
      "look at an earlier value",
      "other",
      "edit through fs.edit",
      "write text from texts",
      "edit in memory and write back",
      "background jobs",
      "finish",
    ]);
    expect(r.list[0]!.intent).toBe("look");
    // `r` is data; `sh` a helper; `W` holds a literal, which is neither.
    expect(r.earlierData).toBe(1);
    expect(r.earlierHelpers).toBe(1);
    expect(r.retypedCells).toBe(0);
  });

  test("text a receipt showed and a later cell types again is retyped, from code and from texts", () => {
    const r = analyse(
      session(
        [`(await ${g}.fs.read("a.txt")).text`, output],
        [`await ${g}.fs.write("copy.txt", ${JSON.stringify(output)})`, "written"],
        [{ code: `await ${g}.fs.write("copy2.txt", t)`, texts: { t: output } }, "written"],
        [`await ${g}.fs.write("new.txt", ${JSON.stringify(long("never shown before "))})`, "written"],
      ),
      g,
    );
    expect(r.list.map((c) => c.retyped > 0)).toEqual([false, true, true, false]);
    expect(r.retypedCells).toBe(2);
    expect(r.retypedChars).toBeGreaterThan(output.length - 120);
  });

  test("a new kernel forgets the names earlier cells declared", () => {
    const r = analyse(
      session(
        [`const r = await ${g}.shell.run("ls")`, "Declared: r"],
        [`r.stdout`, "[A new kernel started for this cell (the previous kernel exited): names and values from earlier cells are gone.]"],
      ),
      g,
    );
    expect(r.earlierData).toBe(0);
  });

  test("reports add up", () => {
    const one = analyse(session([`await ${g}.shell.run("ls")`, "out"]), g);
    const sum = total([one, one]);
    expect(sum.cells).toBe(2);
    expect(sum.kinds["run a command and print"]).toBe(2);
  });
});

describe("the event store", () => {
  test("sessions and their context logs, read from the store's tables", () => {
    const db = new Database(":memory:");
    db.run("CREATE TABLE streams (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, head INTEGER NOT NULL)");
    db.run("CREATE TABLE commits (stream INTEGER NOT NULL, first_seq INTEGER NOT NULL, at INTEGER NOT NULL)");
    db.run("CREATE TABLE events (position INTEGER PRIMARY KEY, stream INTEGER NOT NULL, seq INTEGER NOT NULL, data TEXT NOT NULL)");
    db.run("INSERT INTO streams (id, name, head) VALUES (1, 'session/s1', 1), (2, 'context/s1', 2)");
    db.run("INSERT INTO commits (stream, first_seq, at) VALUES (2, 1, 1796000000000000)");
    db.run(`INSERT INTO events (stream, seq, data) VALUES (2, 2, '{"kind":"returned"}'), (2, 1, '{"kind":"framed"}'), (1, 1, '{"kind":"created"}')`);
    expect(sessions(db)).toEqual([{ id: "s1", started: "2026-11-30 00:53", events: 2 }]);
    expect(contextOf(db, "s1").map((e) => e.kind)).toEqual(["framed", "returned"]);
  });

  test("the data directory is the host's", () => {
    const name = g;
    expect(dataDirectory(name, { [`${name.toUpperCase()}_HOME`]: "/data" })).toBe("/data");
    if (process.platform !== "win32") expect(dataDirectory(name, { HOME: "/home/u" })).toBe(join("/home/u", `.${name}`));
  });
});
