// Source positions for replMode output.
//
// `Bun.Transpiler` has no source maps (1.4.5 accepts a `sourcemap` option but ignores it), and
// replMode reprints the code: hoisted `var` lines, an IIFE wrapper and `value:` capture. Runtime
// stack frames therefore point into the transpiled text. `mapToSource` maps them back by finding
// the tokens around the reported column in the original cell. It is a heuristic; see the spike doc.

export interface SourcePosition {
  line: number;
  column: number;
}

/** Names replMode hoisted as `var a, b;` before the IIFE: the cell's top-level declarations. */
export function hoistedNames(js: string): string[] {
  const names: string[] = [];
  for (const line of js.split("\n")) {
    const m = /^var (.+);$/.exec(line);
    if (!m) break;
    for (const name of m[1]!.split(",")) names.push(name.trim());
  }
  return names;
}

interface Token {
  text: string;
  start: number;
  end: number;
  isString: boolean;
}

const TOKEN = /[A-Za-z_$][\w$]*|\d+(?:\.\d+)?|"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`|[^\s\w]/g;

function tokenize(text: string): Token[] {
  const tokens: Token[] = [];
  for (const m of text.matchAll(TOKEN)) {
    const t = m[0];
    tokens.push({ text: t, start: m.index, end: m.index + t.length, isString: /^["'`]/.test(t) });
  }
  return tokens;
}

function tokenPattern(t: Token): string {
  if (!t.isString) return t.text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  // Bun reprints strings with double quotes; the source may use any quote style.
  const body = t.text.slice(1, -1).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return `(?:"${body}"|'${body}'|\`${body}\`)`;
}

// Tokens that replMode adds around the cell's own code.
const WRAPPER = new Set(["value", "__proto__", "null", ":", "return"]);

function lineColumnAt(text: string, offset: number): SourcePosition {
  const before = text.slice(0, offset);
  const line = before.split("\n").length;
  return { line, column: offset - before.lastIndexOf("\n") };
}

function offsetOf(text: string, line: number, column: number): number {
  const lines = text.split("\n");
  let offset = 0;
  for (let i = 0; i < line - 1 && i < lines.length; i++) offset += lines[i]!.length + 1;
  return offset + column - 1;
}

/**
 * Maps a 1-based (line, column) in the transpiled cell to the original cell source, or returns
 * undefined when no unambiguous match exists.
 */
export function mapToSource(source: string, js: string, line: number, column: number): SourcePosition | undefined {
  const jsLines = js.split("\n");
  const jsLine = jsLines[line - 1];
  if (jsLine === undefined) return undefined;
  const tokens = tokenize(jsLine);
  if (tokens.length === 0) return undefined;
  let k = tokens.findIndex((t) => column - 1 < t.end);
  if (k < 0) k = tokens.length - 1;
  const jsOffset = offsetOf(js, line, 1) + tokens[k]!.start;
  // Try wide windows first (more context, fewer false matches), then narrower ones.
  for (const [before, after] of [[3, 3], [2, 2], [1, 2], [0, 2], [1, 1], [0, 1], [1, 0], [0, 0]] as const) {
    let lo = Math.max(0, k - before);
    while (lo < k && WRAPPER.has(tokens[lo]!.text)) lo++;
    const hi = Math.min(tokens.length - 1, k + after);
    const window = tokens.slice(lo, hi + 1);
    const anchor = k - lo;
    const pattern = window.map((t, i) => (i === anchor ? `(${tokenPattern(t)})` : tokenPattern(t))).join("\\s*");
    const sourceMatches = [...source.matchAll(new RegExp(pattern, "gd"))];
    if (sourceMatches.length === 0) continue;
    const jsMatches = [...js.matchAll(new RegExp(pattern, "g"))];
    let chosen: RegExpExecArray | undefined;
    if (jsMatches.length === sourceMatches.length) {
      // Statement order survives transpilation: take the occurrence with the same rank.
      const rank = jsMatches.findIndex((m) => m.index <= jsOffset && jsOffset < m.index + m[0].length);
      if (rank >= 0) chosen = sourceMatches[rank];
    } else if (sourceMatches.length === 1 && window.length > 1) {
      // replMode may repeat a single token (e.g. hoisted `var x`), but rarely a sequence.
      chosen = sourceMatches[0];
    }
    if (!chosen) continue;
    return lineColumnAt(source, chosen.indices![1]![0]);
  }
  return undefined;
}

export interface CellSource {
  cellId: string;
  source: string;
  js: string;
}

export interface MappedStack {
  stack: string;
  top?: { cellId: string; line: number; column: number };
}

const FRAME = /^(\s*at (?:.*? \()?)(.+?):(\d+):(\d+)(\)?)$/;

/**
 * Keeps the header and the frames that belong to cells, rewriting their positions to cell source
 * positions (`cell <id>:line:col`). Kernel-internal frames are dropped.
 */
export function rewriteStack(stack: string, cells: Map<string, CellSource>): MappedStack {
  const out: string[] = [];
  let top: MappedStack["top"];
  let inHeader = true;
  for (const line of stack.split("\n")) {
    const m = FRAME.exec(line);
    if (!m) {
      if (inHeader) out.push(line);
      continue;
    }
    inHeader = false;
    const cell = cells.get(m[2]!);
    if (!cell) continue;
    const pos = mapToSource(cell.source, cell.js, Number(m[3]), Number(m[4]));
    // The frame that invokes replMode's IIFE wrapper is not the cell's code.
    if (!pos && /^\s*\}\)\(\);?\s*$/.test(cell.js.split("\n")[Number(m[3]) - 1] ?? "")) continue;
    const where = pos ? `cell ${cell.cellId}:${pos.line}:${pos.column}` : `cell ${cell.cellId}:?`;
    out.push(`${m[1]}${where}${m[5]}`);
    if (!top && pos) top = { cellId: cell.cellId, ...pos };
  }
  return { stack: out.join("\n"), top };
}
