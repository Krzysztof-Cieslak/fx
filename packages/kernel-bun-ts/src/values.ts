// Values across the kernel boundary (spec §6.6, docs/modules/kernel-protocol.md "Values"):
//
// - `decode` turns a protocol `Value` into what cells see: inline JSON becomes plain data of the
//   cell context's realm; a blob or a handle becomes a frozen reference object. A reference's id
//   lives only in this module's WeakMaps, so cells can neither read nor forge one: plain data never
//   encodes as a reference.
// - `encode` turns what a cell holds back into a `Value`: references as they came, everything else
//   as strict JSON (no functions, cycles, bigints, non-finite numbers or references inside data).
// - `preview` renders any value within `Limits`, in the style of the host's previews.

import type { BlobRef, Limits, Preview, Value, ValueInfo } from "./protocol.d.ts";

interface HandleInfo {
  handle: string;
  type: string;
  preview: Preview;
}

interface BlobInfo {
  blob: BlobRef;
  preview: Preview;
}

const handles = new WeakMap<object, HandleInfo>();
const blobs = new WeakMap<object, BlobInfo>();

/** Creates objects in the cells' realm (`instanceof Array` and friends hold there). */
export interface Realm {
  fromJson(text: string): unknown;
  object(): Record<string, unknown>;
}

function reference(realm: Realm, fields: Record<string, unknown>, text: string): object {
  const target = realm.object();
  for (const [key, value] of Object.entries(fields)) target[key] = value;
  Object.defineProperty(target, "toString", { value: () => text, enumerable: false });
  return Object.freeze(target);
}

/** What cells see of a value. */
export function decode(realm: Realm, value: Value): unknown {
  switch (value.kind) {
    case "inline":
      return value.value === undefined ? null : realm.fromJson(JSON.stringify(value.value));
    case "blob": {
      const target = reference(realm, { size: value.blob.size, preview: value.preview.text }, `Blob(${value.preview.text})`);
      blobs.set(target, { blob: value.blob, preview: value.preview });
      return target;
    }
    case "handle": {
      const target = reference(realm, { type: value.type, preview: value.preview.text }, `${value.type}(${value.preview.text})`);
      handles.set(target, { handle: value.handle, type: value.type, preview: value.preview });
      return target;
    }
  }
}

/** Why a value has no `Value` form. */
export class NotEncodable extends Error {
  constructor(
    readonly typeName: string,
    reason: string,
  ) {
    super(reason);
  }
}

export class TooLarge extends Error {
  constructor(
    readonly bytes: number,
    readonly limit: number,
  ) {
    super(`${bytes} bytes`);
  }
}

function isPlainObject(value: object): boolean {
  const proto = Object.getPrototypeOf(value);
  // Objects of any realm: their prototype is a realm's Object.prototype (whose prototype is null).
  return proto === null || Object.getPrototypeOf(proto) === null;
}

function at(path: string): string {
  return path === "" ? "" : ` at ${path}`;
}

/**
 * Strict JSON: what `JSON.stringify` would write, or `NotEncodable` where it would lose data. With
 * `omitUndefined`, object members that are `undefined` are left out (optional fields of arguments).
 */
function toJson(value: unknown, path: string, seen: Set<object>, omitUndefined: boolean): unknown {
  switch (typeof value) {
    case "string":
    case "boolean":
      return value;
    case "number":
      if (!Number.isFinite(value)) throw new NotEncodable("number", `${value} has no JSON form${at(path)}`);
      return value;
    case "undefined":
      throw new NotEncodable("undefined", `undefined has no JSON form${at(path)}`);
    case "bigint":
      throw new NotEncodable("bigint", `a bigint has no JSON form${at(path)}`);
    case "symbol":
      throw new NotEncodable("symbol", `a symbol has no JSON form${at(path)}`);
    case "function":
      throw new NotEncodable("function", `a function has no JSON form${at(path)}`);
  }
  if (value === null) return null;
  const object = value as object;
  if (handles.has(object) || blobs.has(object)) {
    throw new NotEncodable(typeName(object), `a reference inside a value${at(path)}: pass it on its own`);
  }
  if (seen.has(object)) throw new NotEncodable(typeName(object), `a cycle${at(path)}`);
  const toJSON = (object as { toJSON?: unknown }).toJSON;
  if (typeof toJSON === "function") return toJson(toJSON.call(object), path, seen, omitUndefined);
  seen.add(object);
  try {
    if (Array.isArray(object)) return object.map((item, i) => toJson(item, `${path}[${i}]`, seen, omitUndefined));
    if (!isPlainObject(object)) throw new NotEncodable(typeName(object), `a ${typeName(object)} has no JSON form${at(path)}`);
    const result: Record<string, unknown> = {};
    for (const key of Object.keys(object)) {
      const member = (object as Record<string, unknown>)[key];
      if (member === undefined && omitUndefined) continue;
      result[key] = toJson(member, path === "" ? key : `${path}.${key}`, seen, omitUndefined);
    }
    return result;
  } finally {
    seen.delete(object);
  }
}

/**
 * A value as the protocol carries it: a reference as it came, or inline JSON within `maxBytes`.
 * `omitUndefined` leaves out object members that are `undefined`, as arguments of host calls and
 * results of `finish` are written (`{ verify: undefined }` is `{}`); `value/encode` stays strict.
 */
export function encode(value: unknown, maxBytes: number, omitUndefined = false): Value {
  if (typeof value === "object" && value !== null) {
    const handle = handles.get(value);
    if (handle) return { kind: "handle", handle: handle.handle, type: handle.type, preview: handle.preview };
    const blob = blobs.get(value);
    if (blob) return { kind: "blob", blob: blob.blob, preview: blob.preview };
  }
  const json = toJson(value, "", new Set(), omitUndefined);
  const bytes = Buffer.byteLength(JSON.stringify(json));
  if (bytes > maxBytes) throw new TooLarge(bytes, maxBytes);
  return { kind: "inline", value: json };
}

/** The type of a value as TypeScript names it (across realms). */
export function typeName(value: unknown): string {
  if (value === null) return "null";
  if (typeof value === "object") {
    const handle = handles.get(value);
    if (handle) return handle.type;
    if (blobs.has(value)) return "Blob";
    if (Array.isArray(value)) return "Array";
    const ctor = (value as { constructor?: { name?: unknown } }).constructor;
    return typeof ctor?.name === "string" && ctor.name !== "" ? ctor.name : "Object";
  }
  return typeof value;
}

const ELLIPSIS = "…";

/** Cuts text to `max` characters ending with `…`, never splitting a surrogate pair. */
export function cut(text: string, max: number): string {
  if (max <= 0) return "";
  if (text.length <= max) return text;
  let n = max - 1;
  const code = text.charCodeAt(n - 1);
  if (n > 0 && code >= 0xd800 && code <= 0xdbff) n--;
  return text.slice(0, n) + ELLIPSIS;
}

interface Rendering {
  text: string;
  truncated: boolean;
}

function render(value: unknown, limits: Limits, depth: number, budget: number): Rendering {
  const room = Math.max(0, budget);
  switch (typeof value) {
    case "string": {
      if (value.length <= limits.maxStringChars) return fit(JSON.stringify(value), room);
      const quoted = JSON.stringify(cut(value, limits.maxStringChars));
      return fit(`${quoted}(len=${value.length})`, room, true);
    }
    case "number":
    case "boolean":
      return fit(String(value), room);
    case "bigint":
      return fit(`${value}n`, room);
    case "undefined":
      return fit("undefined", room);
    case "symbol":
      return fit(value.toString(), room);
    case "function":
      return fit(`[Function ${value.name || "anonymous"}]`, room);
  }
  if (value === null) return fit("null", room);
  const object = value as object;
  const handle = handles.get(object);
  if (handle) return fit(`${handle.type}(${handle.preview.text})`, room, handle.preview.truncated);
  const blob = blobs.get(object);
  if (blob) return fit(`Blob(${blob.preview.text})`, room, blob.preview.truncated);
  const name = typeName(object);
  let entries: [string | undefined, unknown][];
  let open: string;
  let close: string;
  if (Array.isArray(object)) {
    entries = object.map((item) => [undefined, item]);
    open = "[";
    close = "]";
  } else if (object instanceof Map || name === "Map") {
    entries = [...(object as Map<unknown, unknown>).entries()].map(([k, v]) => [typeof k === "string" ? JSON.stringify(k) : String(k), v]);
    open = "Map{";
    close = "}";
  } else if (object instanceof Set || name === "Set") {
    entries = [...(object as Set<unknown>).values()].map((v) => [undefined, v]);
    open = "Set[";
    close = "]";
  } else if (object instanceof Error || (typeof (object as Error).message === "string" && typeof (object as Error).stack === "string")) {
    return fit(`${name}(${JSON.stringify((object as Error).message)})`, room);
  } else if (object instanceof Date || name === "Date") {
    return fit(`Date(${(object as Date).toISOString?.() ?? "?"})`, room);
  } else {
    entries = Object.keys(object).map((k) => [JSON.stringify(k), (object as Record<string, unknown>)[k]]);
    open = isPlainObject(object) ? "{" : `${name}{`;
    close = "}";
  }
  const summary = `${open}${ELLIPSIS}${close}(len=${entries.length})`;
  if (depth >= limits.maxDepth && entries.length > 0) return fit(summary, room, true);
  const parts: string[] = [];
  let used = open.length + close.length;
  let truncated = false;
  const shown = Math.min(entries.length, limits.maxItems);
  for (let i = 0; i < shown; i++) {
    const [key, item] = entries[i]!;
    const prefix = key === undefined ? "" : `${key}: `;
    const separator = parts.length === 0 ? 0 : 2;
    // Keep room for a `, …+N` marker after this element.
    const reserve = i + 1 < entries.length ? 12 : 0;
    const child = render(item, limits, depth + 1, room - used - separator - prefix.length - reserve);
    if (child.text === "" || used + separator + prefix.length + child.text.length + reserve > room) {
      truncated = true;
      break;
    }
    parts.push(prefix + child.text);
    used += separator + prefix.length + child.text.length;
    truncated ||= child.truncated;
  }
  const rest = entries.length - parts.length;
  if (rest > 0) {
    parts.push(`${ELLIPSIS}+${rest}`);
    truncated = true;
  }
  const text = `${open}${parts.join(", ")}${close}`;
  if (text.length > room) return fit(summary, room, true);
  return { text, truncated };
}

function fit(text: string, room: number, truncated = false): Rendering {
  if (text.length <= room) return { text, truncated };
  return { text: cut(text, room), truncated: true };
}

/** A bounded preview: never longer than `maxChars`; truncated when something was left out. */
export function preview(value: unknown, limits: Limits): Preview {
  const rendering = render(value, limits, 0, limits.maxChars);
  return { text: rendering.text, truncated: rendering.truncated };
}

export function info(value: unknown, limits: Limits): ValueInfo {
  return { type: typeName(value), preview: preview(value, limits) };
}
