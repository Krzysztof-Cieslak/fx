// The cells of CodeAct sessions, read from a host's event store and classified: what kinds of cells a
// model writes, how often a cell uses data or helpers an earlier cell kept, and how much text cells
// retype from earlier receipts (docs/research/kernel-first-prompting.md).
//
//   bun scripts/cells.ts                     lists the sessions in the data directory
//   bun scripts/cells.ts <session>…          reports on sessions (ids, or prefixes that name one)
//   bun scripts/cells.ts --latest <n>        reports on the n latest sessions
//   options: --data <dir> (the host's data directory by default), --json (with each cell's kind)
//
// It opens the event database read-only, so a running host is no obstacle. Cells are bun-ts cells
// (TypeScript); the classification is a heuristic for comparing prompts and binaries over the same
// kind of work, not a parser.

import { Database } from "bun:sqlite";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

// ---- Reading cells ------------------------------------------------------------------------------

/** A cell's code with comments removed and string literals blanked, and the literals' values. */
export type Scanned = {
  /** The code without comments; every string literal is `""`, every regular expression `/re/`. */
  code: string;
  /** Each string literal's value with its escapes decoded; a template literal gives its text between expressions. */
  literals: string[];
};

/** Decodes a JavaScript string literal's escapes. */
export function decode(raw: string): string {
  return raw.replace(/\\(u\{[0-9a-fA-F]+\}|u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|\r\n|[\s\S])/g, (_, e: string) => {
    if (e === "\r\n" || e === "\n" || e === "\r") return "";
    if (e.length > 1 && e[0] === "u") return String.fromCodePoint(parseInt(e[1] === "{" ? e.slice(2, -1) : e.slice(1), 16));
    if (e.length > 1 && e[0] === "x") return String.fromCharCode(parseInt(e.slice(1), 16));
    switch (e) {
      case "n":
        return "\n";
      case "t":
        return "\t";
      case "r":
        return "\r";
      case "b":
        return "\b";
      case "f":
        return "\f";
      case "v":
        return "\v";
      case "0":
        return "\0";
      default:
        return e;
    }
  });
}

const REGEX_AFTER = new Set(["(", ",", "=", ":", "[", "!", "&", "|", "?", "{", "}", ";", "+", "-", "*", "%", "<", ">", "~", "^"]);
const REGEX_AFTER_WORD = /(?:^|[^\w$])(?:return|typeof|case|do|else|in|of|void|yield|await|throw|delete|instanceof|new)$/;

/** Splits TypeScript source into code and string literals: comments, strings, templates (with their expressions) and regular expressions. */
export function scan(source: string): Scanned {
  const n = source.length;
  const literals: string[] = [];
  let code = "";
  let i = 0;

  const regexAllowed = () => {
    const before = code.trimEnd();
    return before === "" || REGEX_AFTER.has(before[before.length - 1]!) || REGEX_AFTER_WORD.test(before);
  };

  const readString = (quote: string) => {
    let j = i + 1;
    let raw = "";
    while (j < n && source[j] !== quote && source[j] !== "\n") {
      if (source[j] === "\\" && j + 1 < n) {
        raw += source.slice(j, j + 2);
        j += 2;
      } else raw += source[j++];
    }
    literals.push(decode(raw));
    code += '""';
    i = j + 1;
  };

  const readRegex = () => {
    let j = i + 1;
    let inClass = false;
    while (j < n && source[j] !== "\n" && (inClass || source[j] !== "/")) {
      if (source[j] === "\\") j++;
      else if (source[j] === "[") inClass = true;
      else if (source[j] === "]") inClass = false;
      j++;
    }
    j++;
    while (j < n && /[a-z]/.test(source[j]!)) j++;
    code += "/re/";
    i = j;
  };

  // Reads code until `}` closes the block it started in (a template's `${…}`), or to the end.
  const readCode = (inExpression: boolean) => {
    let depth = 0;
    while (i < n) {
      const c = source[i]!;
      if (c === "}" && inExpression && depth === 0) {
        i++;
        return;
      }
      if (c === '"' || c === "'") readString(c);
      else if (c === "`") readTemplate();
      else if (c === "/" && source[i + 1] === "/") {
        const end = source.indexOf("\n", i);
        i = end < 0 ? n : end;
      } else if (c === "/" && source[i + 1] === "*") {
        const end = source.indexOf("*/", i + 2);
        i = end < 0 ? n : end + 2;
        code += " ";
      } else if (c === "/" && regexAllowed()) readRegex();
      else {
        if (c === "{") depth++;
        else if (c === "}") depth--;
        code += c;
        i++;
      }
    }
  };

  const readTemplate = () => {
    let raw = "";
    i++;
    code += "`";
    while (i < n && source[i] !== "`") {
      if (source[i] === "\\" && i + 1 < n) {
        raw += source.slice(i, i + 2);
        i += 2;
      } else if (source[i] === "$" && source[i + 1] === "{") {
        if (raw !== "") literals.push(decode(raw));
        raw = "";
        i += 2;
        code += "${";
        readCode(true);
        code += "}";
      } else raw += source[i++];
    }
    if (raw !== "") literals.push(decode(raw));
    code += "`";
    i++;
  };

  readCode(false);
  return { code, literals };
}

/** What a call of the bridge does, by binding. */
export type CallKind = "shell" | "read" | "write" | "edit" | "jobs" | "finish" | "recall";

const BINDING_KINDS: Record<string, CallKind> = {
  "shell.run": "shell",
  "fs.read": "read",
  "fs.list": "read",
  "fs.stat": "read",
  "fs.write": "write",
  "fs.patch": "write",
  "fs.edit": "edit",
  "fs.editRange": "edit",
};

/** The bridge calls in scanned code, as kinds. */
export function bridgeCalls(code: string, global: string): Set<CallKind> {
  const kinds = new Set<CallKind>();
  const g = global.replace(/[$]/g, "\\$");
  for (const m of code.matchAll(new RegExp(`(?<![\\w$.])${g}\\s*\\.\\s*(shell|fs|jobs)\\s*\\.\\s*(\\w+)\\s*\\(`, "g"))) {
    if (m[1] === "jobs") kinds.add("jobs");
    else {
      const kind = BINDING_KINDS[`${m[1]}.${m[2]}`];
      if (kind) kinds.add(kind);
    }
  }
  for (const m of code.matchAll(new RegExp(`(?<![\\w$.])${g}\\s*\\.\\s*(finish|recall)\\s*\\(`, "g"))) kinds.add(m[1] as CallKind);
  return kinds;
}

/** The end of the block that opens at `open` (a `{` or `(`), in scanned code. */
function closing(code: string, open: number): number {
  const pair = code[open] === "(" ? ")" : "}";
  let depth = 0;
  for (let k = open; k < code.length; k++) {
    if (code[k] === code[open]) depth++;
    else if (code[k] === pair && --depth === 0) return k + 1;
  }
  return code.length;
}

/** The functions a cell defines at its top level (declarations, and arrow or function expressions bound to a name), with their spans in the scanned code. */
export function definitions(code: string): { name: string; start: number; end: number }[] {
  const found: { name: string; start: number; end: number }[] = [];
  for (const m of code.matchAll(/(?:async\s+)?function\s*\*?\s*([A-Za-z_$][\w$]*)\s*\(/g)) {
    const params = closing(code, m.index! + m[0].length - 1);
    const body = code.indexOf("{", params);
    found.push({ name: m[1]!, start: m.index!, end: body < 0 ? params : closing(code, body) });
  }
  const bound =
    /(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=;\n]+)?=\s*(?:async\s+)?(?:function\b[^(]*\(|(\()|[A-Za-z_$][\w$]*\s*=>)/g;
  for (const m of code.matchAll(bound)) {
    let at = m.index! + m[0].length;
    if (m[2] !== undefined) {
      // `(params) =>` or `(params): T =>`: only an arrow makes it a function.
      at = closing(code, at - 1);
      const arrow = /^\s*(?::[^=;\n]+)?=>/.exec(code.slice(at));
      if (!arrow) continue;
      at += arrow[0].length;
    } else if (m[0].endsWith("(")) at = closing(code, at - 1);
    const rest = code.slice(at);
    const open = rest.search(/\S/);
    let end: number;
    if (open >= 0 && rest[open] === "{") end = closing(code, at + open);
    else {
      // An expression body: to the end of its line, or past the brackets it opens on that line.
      let depth = 0;
      end = at;
      while (end < code.length && !(depth === 0 && (code[end] === "\n" || code[end] === ";"))) {
        if ("([{".includes(code[end]!)) depth++;
        else if (")]}".includes(code[end]!)) depth--;
        end++;
      }
    }
    found.push({ name: m[1]!, start: m.index!, end });
  }
  return found;
}

const IDENTIFIER = /(?<![\w$.])[A-Za-z_$][\w$]*/g;

// ---- Classifying a session's cells --------------------------------------------------------------

/** A context event (prompt.md): only the members this script reads. */
export type ContextEvent = { kind: string; [member: string]: any };

/** The kinds of cells, in the order a cell is matched against them. */
export const KINDS = [
  "finish",
  "background jobs",
  "poll or wait",
  "edit in memory and write back",
  "write a big literal",
  "write text from texts",
  "edit through fs.edit",
  "write",
  "define helpers",
  "process in the kernel",
  "run and read, then print",
  "run a command and print",
  "read and print",
  "look at an earlier value",
  "other",
] as const;

export type Kind = (typeof KINDS)[number];

export type Report = {
  /** The models that replied. */
  models: string[];
  /** Cells (calls of the `cell` tool). */
  cells: number;
  /** Calls of the other tools, by name (hybrid's `read`, `edit`, `bash`). */
  tools: Record<string, number>;
  kinds: Record<Kind, number>;
  /** Cells that use a value an earlier cell declared (not a function). */
  earlierData: number;
  /** Cells that call a function an earlier cell declared. */
  earlierHelpers: number;
  /** Cells whose code or texts retype text an earlier receipt showed. */
  retypedCells: number;
  /** About how many characters they retyped (the probes' span). */
  retypedChars: number;
  /** Each cell: where it is, its kind, the characters it retyped, and its `// intent:` line. */
  list: { turn: number; step: number; kind: Kind; retyped: number; intent: string }[];
};

/** A literal or text counts as retyped from this many characters on. */
export const RETYPED_MIN = 200;
/** It is probed with windows of this many characters… */
export const PROBE = 120;
/** …this many characters apart. */
export const STRIDE = 60;
/** A literal this long, written to a file, is a big literal. */
export const BIG_LITERAL = 1000;

/** What a receipt shows, as text: a file's lines without their numbers. */
export function receiptText(pieces: any[]): string {
  return pieces
    .map((p) => {
      if (p?.kind === "file") return String(p.text ?? "").replace(/^ *\d+\t/gm, "");
      if (p?.kind === "text" || p?.kind === "quote") return String(p.text ?? "");
      if (p?.kind === "ref") return String(p.ref?.preview?.text ?? "");
      return "";
    })
    .join("\n");
}

/** How many characters of `text` appear verbatim in `seen`, probed with windows of `PROBE` characters `STRIDE` apart. */
export function retyped(text: string, seen: string): number {
  if (text.length < RETYPED_MIN || seen === "") return 0;
  let covered = 0;
  let coveredTo = 0;
  for (let at = 0; at + PROBE <= text.length; at += STRIDE) {
    if (!seen.includes(text.slice(at, at + PROBE))) continue;
    covered += at + PROBE - Math.max(at, coveredTo);
    coveredTo = at + PROBE;
  }
  return covered;
}

/** Classifies the cells of one session's context log (`context/<session>`, in order). */
export function analyse(events: ContextEvent[], global: string): Report {
  const receipts = new Map<string, ContextEvent>();
  for (const e of events) if (e.kind === "returned") receipts.set(e.call, e);

  const kinds = Object.fromEntries(KINDS.map((k) => [k, 0])) as Record<Kind, number>;
  const report: Report = { models: [], cells: 0, tools: {}, kinds, earlierData: 0, earlierHelpers: 0, retypedCells: 0, retypedChars: 0, list: [] };
  const models = new Set<string>();
  // What the kernel holds: names earlier cells declared, which of them are functions (and what they
  // call), and which hold a literal (a path, a number): using one is not using data a cell found.
  let declared = new Set<string>();
  let helpers = new Map<string, Set<CallKind>>();
  let constants = new Set<string>();
  let seen = "";

  for (const reply of events) {
    if (reply.kind !== "replied") continue;
    if (reply.used?.model) models.add(reply.used.model);
    for (const part of reply.parts ?? []) {
      if (part.kind !== "toolCall") continue;
      const receipt = receipts.get(part.id);
      const shown = receipt ? receiptText(receipt.pieces ?? []) : "";
      if (part.name !== "cell") {
        report.tools[part.name] = (report.tools[part.name] ?? 0) + 1;
        seen += "\u0000" + shown;
        continue;
      }
      let args: any = {};
      try {
        args = JSON.parse(part.arguments);
      } catch {}
      const source = typeof args.code === "string" ? args.code : "";
      const texts: string[] = args.texts && typeof args.texts === "object" ? Object.values(args.texts).map(String) : [];
      report.cells++;

      if (shown.includes("[A new kernel started")) {
        declared = new Set();
        helpers = new Map();
        constants = new Set();
      }

      const { code, literals } = scan(source);
      const defined = definitions(code);
      // The helpers this cell defines call what their bodies call, and the helpers they call.
      const known = new Map(helpers);
      for (const d of defined) known.set(d.name, bridgeCalls(code.slice(d.start, d.end), global));
      for (let pass = 0; pass < 3; pass++)
        for (const d of defined) {
          const body = code.slice(d.start, d.end);
          for (const [name, calls] of known) if (name !== d.name && new RegExp(`(?<![\\w$.])${name.replace(/[$]/g, "\\$")}\\s*\\(`).test(body)) for (const c of calls) known.get(d.name)!.add(c);
        }
      // The cell outside its definitions: what it does now.
      let rest = code;
      for (const d of [...defined].sort((a, b) => b.start - a.start)) rest = rest.slice(0, d.start) + rest.slice(d.end);
      const calls = bridgeCalls(rest, global);
      const used = new Set(rest.match(IDENTIFIER) ?? []);
      for (const name of used) {
        const helper = known.get(name);
        if (helper && new RegExp(`(?<![\\w$.])${name.replace(/[$]/g, "\\$")}\\s*\\(`).test(rest)) for (const c of helper) calls.add(c);
      }
      const ownNames = new Set(defined.map((d) => d.name));
      const earlier = [...used].filter((name) => declared.has(name) && !ownNames.has(name));
      const usesHelper = earlier.some((name) => helpers.has(name));
      const usesData = earlier.some((name) => !helpers.has(name) && !constants.has(name));
      if (usesHelper) report.earlierHelpers++;
      if (usesData) report.earlierData++;

      const longest = Math.max(0, ...literals.map((l) => l.length));
      const processing = /\b(?:for|while)\s*\(|\.forEach\s*\(|\bif\s*\(|\.(?:map|filter|find|findIndex|some|every|reduce|flatMap|match|matchAll|test|split)\s*\(|JSON\.parse\s*\(/.test(rest);
      const polls = /\bsetTimeout\s*\(|\bBun\.sleep\s*\(/.test(rest) || literals.some((l) => /(?:^|[;&|]\s*)sleep\s+\d/.test(l));
      const replaces = /\.(?:replace|replaceAll|splice)\s*\(/.test(rest);
      const looksBack = usesData || calls.has("recall");

      const kind: Kind = calls.has("finish")
        ? "finish"
        : calls.has("jobs")
          ? "background jobs"
          : polls
            ? "poll or wait"
            : calls.has("write") && replaces
              ? "edit in memory and write back"
              : calls.has("write") && longest >= BIG_LITERAL
                ? "write a big literal"
                : calls.has("write") && texts.length > 0
                  ? "write text from texts"
                  : calls.has("edit")
                    ? "edit through fs.edit"
                    : calls.has("write")
                      ? "write"
                      : defined.length > 0 && calls.size === 0
                        ? "define helpers"
                        : (calls.has("shell") || calls.has("read")) && processing
                          ? "process in the kernel"
                          : calls.has("shell") && calls.has("read")
                            ? "run and read, then print"
                            : calls.has("shell")
                              ? "run a command and print"
                              : calls.has("read")
                                ? "read and print"
                                : looksBack
                                  ? "look at an earlier value"
                                  : "other";
      report.kinds[kind]++;

      const retypedHere = [...literals, ...texts].reduce((sum, text) => sum + retyped(text, seen), 0);
      if (retypedHere > 0) {
        report.retypedCells++;
        report.retypedChars += retypedHere;
      }
      const intent = /^\s*\/\/\s*intent:\s*(.*)$/m.exec(source)?.[1]?.trim() ?? "";
      report.list.push({ turn: reply.turn, step: reply.step, kind, retyped: retypedHere, intent });

      for (const d of defined) helpers.set(d.name, known.get(d.name)!);
      for (const m of code.matchAll(/(?<![\w$.])(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=;\n]+)?=\s*(?:""|-?\d[\d_.]*)\s*(?:;|\n|$)/g)) constants.add(m[1]!);
      const line = /^Declared: (.*)$/m.exec(shown);
      for (const name of line ? line[1]!.split(",").map((s) => s.trim()) : []) if (name) declared.add(name);
      for (const d of defined) declared.add(d.name);
      seen += "\u0000" + shown;
    }
  }
  report.models = [...models].sort();
  return report;
}

/** Adds reports up. */
export function total(reports: Report[]): Report {
  const kinds = Object.fromEntries(KINDS.map((k) => [k, 0])) as Record<Kind, number>;
  const sum: Report = { models: [], cells: 0, tools: {}, kinds, earlierData: 0, earlierHelpers: 0, retypedCells: 0, retypedChars: 0, list: [] };
  for (const r of reports) {
    sum.models = [...new Set([...sum.models, ...r.models])].sort();
    sum.cells += r.cells;
    for (const [tool, n] of Object.entries(r.tools)) sum.tools[tool] = (sum.tools[tool] ?? 0) + n;
    for (const k of KINDS) sum.kinds[k] += r.kinds[k];
    sum.earlierData += r.earlierData;
    sum.earlierHelpers += r.earlierHelpers;
    sum.retypedCells += r.retypedCells;
    sum.retypedChars += r.retypedChars;
  }
  return sum;
}

/** A report as text. */
export function render(title: string, r: Report): string {
  const share = (n: number) => (r.cells === 0 ? "" : `${Math.round((100 * n) / r.cells)}%`.padStart(5));
  const lines = [`${title}: ${r.cells} cells${r.models.length ? ` (${r.models.join(", ")})` : ""}`];
  const tools = Object.entries(r.tools);
  if (tools.length) lines.push(`  other tools: ${tools.map(([t, n]) => `${t} ${n}`).join(", ")}`);
  for (const k of KINDS) if (r.kinds[k] > 0) lines.push(`  ${k.padEnd(32)}${String(r.kinds[k]).padStart(6)}${share(r.kinds[k])}`);
  lines.push(`  ${"uses earlier data".padEnd(32)}${String(r.earlierData).padStart(6)}${share(r.earlierData)}`);
  lines.push(`  ${"calls earlier helpers".padEnd(32)}${String(r.earlierHelpers).padStart(6)}${share(r.earlierHelpers)}`);
  lines.push(`  ${"retypes receipt text".padEnd(32)}${String(r.retypedCells).padStart(6)}${share(r.retypedCells)}  (${r.retypedChars.toLocaleString("en-US")} characters)`);
  return lines.join("\n");
}

// ---- The event store ----------------------------------------------------------------------------

/** The product name (the naming constant in Directory.Build.props). */
export function productName(root = join(import.meta.dir, "..")): string {
  const props = readFileSync(join(root, "Directory.Build.props"), "utf8");
  const match = /<FxProductName>([^<]+)<\/FxProductName>/.exec(props);
  if (!match) throw new Error("Directory.Build.props has no FxProductName");
  return match[1]!;
}

/** The host's data directory, as the host finds it (HostPaths.dataDirectory). */
export function dataDirectory(name: string, env = process.env): string {
  const home = env[`${name.toUpperCase()}_HOME`];
  if (home) return home;
  if (process.platform === "win32" && env.LOCALAPPDATA) return join(env.LOCALAPPDATA, name);
  return join(env.HOME ?? homedir(), `.${name}`);
}

/** The sessions with a context log: id, when it started (UTC), and how many events it has. */
export function sessions(db: Database): { id: string; started: string; events: number }[] {
  const rows = db
    .query(
      `SELECT s.name AS name, (SELECT count(*) FROM events e WHERE e.stream = s.id) AS events,
              (SELECT c.at FROM commits c WHERE c.stream = s.id AND c.first_seq = 1) AS at
       FROM streams s WHERE s.name LIKE 'context/%' ORDER BY s.id`,
    )
    .all() as { name: string; events: number; at: number | null }[];
  return rows.map((r) => ({
    id: r.name.slice("context/".length),
    started: r.at ? new Date(r.at / 1000).toISOString().slice(0, 16).replace("T", " ") : "",
    events: r.events,
  }));
}

/** A session's context events, in order. */
export function contextOf(db: Database, session: string): ContextEvent[] {
  const rows = db
    .query(`SELECT e.data AS data FROM events e JOIN streams s ON s.id = e.stream WHERE s.name = ? ORDER BY e.seq`)
    .all(`context/${session}`) as { data: string }[];
  return rows.map((r) => JSON.parse(r.data));
}

// ---- The command line ---------------------------------------------------------------------------

function main(argv: string[]) {
  const fail = (message: string): never => {
    console.error(message);
    process.exit(1);
  };
  const name = productName();
  let data = dataDirectory(name);
  let json = false;
  let latest = 0;
  const asked: string[] = [];
  for (let k = 0; k < argv.length; k++) {
    const a = argv[k]!;
    if (a === "--data") data = argv[++k] ?? fail("--data needs a directory");
    else if (a === "--json") json = true;
    else if (a === "--latest") latest = Number(argv[++k] ?? fail("--latest needs a number"));
    else if (a.startsWith("-")) fail(`unknown option ${a}`);
    else asked.push(a);
  }
  const file = join(data, "state", "events.db");
  if (!existsSync(file)) fail(`no event store at ${file}`);
  const db = new Database(file, { readonly: true });
  const all = sessions(db);

  if (asked.length === 0 && latest === 0) {
    for (const s of all) console.log(`${s.id}  ${s.started}  ${s.events} context events`);
    console.log(`\n${all.length} sessions; bun scripts/cells.ts <session>… or --latest <n> reports on them.`);
    return;
  }

  const chosen = asked.map((prefix) => {
    const matches = all.filter((s) => s.id.startsWith(prefix));
    if (matches.length !== 1) fail(`${prefix} names ${matches.length} sessions`);
    return matches[0]!.id;
  });
  if (latest > 0) chosen.push(...all.slice(-latest).map((s) => s.id).filter((id) => !chosen.includes(id)));

  const reports = chosen.map((id) => ({ session: id, ...analyse(contextOf(db, id), name) }));
  if (json) {
    console.log(JSON.stringify({ sessions: reports, total: total(reports) }, null, 2));
    return;
  }
  for (const r of reports) console.log(render(r.session, r) + "\n");
  if (reports.length > 1) console.log(render("total", total(reports)));
}

if (import.meta.main) main(process.argv.slice(2));
