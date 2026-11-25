// JSON as the F# side writes and describes it (Fx.Contracts, docs/modules/contracts.md): values,
// quoting, the descriptions in issue messages, paths, and the canonical form content hashes are
// taken over. Numbers are JavaScript numbers here: exact up to 2^53, and described with JavaScript's
// shortest text (`1e3` reads as `1000`).

/** A JSON value. */
export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

/** One step into a JSON value: an object member or an array element. */
export type PathSegment = string | number;

/** A JSON string literal, escaped as the F# writer does (minimal escaping, as `JSON.stringify`). */
export function quote(text: string): string {
  return JSON.stringify(text);
}

/** The JSON type of a value: `null`, `boolean`, `number`, `string`, `array` or `object`. */
export function kindOf(json: unknown): "null" | "boolean" | "number" | "string" | "array" | "object" | "undefined" {
  if (json === null) return "null";
  if (Array.isArray(json)) return "array";
  switch (typeof json) {
    case "boolean":
      return "boolean";
    case "number":
      return "number";
    case "string":
      return "string";
    case "object":
      return "object";
    default:
      return "undefined";
  }
}

const describeChars = 40;

function isHighSurrogate(code: number): boolean {
  return code >= 0xd800 && code <= 0xdbff;
}

function shorten(text: string): string {
  if (text.length <= describeChars) return text;
  const cut = isHighSurrogate(text.charCodeAt(describeChars - 1)) ? describeChars - 1 : describeChars;
  return text.slice(0, cut) + "…";
}

/** The text of a number, as `JSON.stringify` writes it. */
export function numberText(n: number): string {
  return JSON.stringify(n);
}

/**
 * A short description of a value for messages, e.g. `number 42`, `string "abc"`, `array of 3 items`
 * or `object`; long numbers and strings are cut after 40 characters (`Json.describe`).
 */
export function describe(json: unknown): string {
  switch (kindOf(json)) {
    case "null":
      return "null";
    case "boolean":
      return json ? "boolean true" : "boolean false";
    case "number":
      return "number " + shorten(numberText(json as number));
    case "string":
      return "string " + quote(shorten(json as string));
    case "array": {
      const length = (json as unknown[]).length;
      return length === 0 ? "empty array" : length === 1 ? "array of 1 item" : `array of ${length} items`;
    }
    case "object":
      return Object.keys(json as object).length === 0 ? "empty object" : "object";
    default:
      return "undefined";
  }
}

const identifier = /^[A-Za-z_$][A-Za-z0-9_$]*$/;

/** A path in JavaScript accessor syntax: `$`, `$.items[2].name`, `$["a key"]` (`JsonPath.render`). */
export function renderPath(path: readonly PathSegment[]): string {
  let text = "$";
  for (const segment of path) {
    if (typeof segment === "number") text += `[${segment}]`;
    else if (identifier.test(segment)) text += "." + segment;
    else text += `[${quote(segment)}]`;
  }
  return text;
}

/** Whether a value is a JSON object (not an array, not null). */
export function isObject(json: unknown): json is { [key: string]: unknown } {
  return typeof json === "object" && json !== null && !Array.isArray(json);
}

function compareKeys(a: string, b: string): number {
  // UTF-16 code units, as RFC 8785 and the F# side (ordinal) compare them.
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * The canonical form (`Json.canonicalize`): object members sorted by key (UTF-16 code units), arrays
 * in order. Numbers are JavaScript numbers, written as `JSON.stringify` does, which is RFC 8785's
 * form; the F# side agrees for every number that is an exact double.
 */
export function canonicalize(json: Json): Json {
  if (Array.isArray(json)) return json.map(canonicalize);
  if (isObject(json)) {
    const sorted: { [key: string]: Json } = {};
    for (const key of Object.keys(json).sort(compareKeys)) sorted[key] = canonicalize(json[key] as Json);
    return sorted;
  }
  return json;
}

/** The canonical text, the basis for content hashes (`Json.serializeCanonical`). */
export function serializeCanonical(json: Json): string {
  if (Array.isArray(json)) return "[" + json.map(serializeCanonical).join(",") + "]";
  if (isObject(json)) {
    const members = Object.keys(json)
      .sort(compareKeys)
      .map((key) => quote(key) + ":" + serializeCanonical(json[key] as Json));
    return "{" + members.join(",") + "}";
  }
  if (typeof json === "number" && !Number.isFinite(json)) throw new RangeError("JSON has no NaN or infinities");
  return JSON.stringify(json);
}

/** Whether two JSON values are equal in canonical form (member order does not matter). */
export function equalJson(a: Json, b: Json): boolean {
  return serializeCanonical(a) === serializeCanonical(b);
}
