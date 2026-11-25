// Codecs for TypeScript authors (spec §4.1): hand-written combinators that mirror Fx.Contracts'
// `Codec` combinators, so that a TypeScript codec and the F# codec it mirrors have the same schema
// (the wire form of `Schema`), write the same JSON, and decode it with the same issues (paths and
// messages). The cross-language fixtures (test/fixtures/codecs.json, written by the F# tests) prove
// it. Values are the JSON shapes themselves: a record is a plain object with the record's fields, a
// union an object with its tag. See docs/modules/sdk.md.

import type { Issue } from "./generated/contracts.d.ts";
import { type Json, type PathSegment, describe as describeJson, isObject, quote, renderPath, serializeCanonical } from "./json.ts";
import { type Schema, type SchemaCase, type SchemaField, checkTypeName, typeName } from "./schema.ts";

export type { Issue } from "./generated/contracts.d.ts";

/** A decoded value, or every issue found. */
export type Validation<T> = { readonly ok: true; readonly value: T } | { readonly ok: false; readonly issues: Issue[] };

export const Validation = {
  ok<T>(value: T): Validation<T> {
    return { ok: true, value };
  },
  fail<T>(issues: Issue[]): Validation<T> {
    return { ok: false, issues };
  },
};

/** An issue at a path (`Issue.at`). */
export function issueAt(path: readonly PathSegment[], message: string): Issue {
  return { path: [...path], message };
}

/** `expected WHAT, got DESCRIPTION`, e.g. `expected string, got number 42`. */
export function expected(what: string, got: unknown): Issue {
  return issueAt([], `expected ${what}, got ${describeJson(got)}`);
}

function under(segment: PathSegment, issues: readonly Issue[]): Issue[] {
  return issues.map((i) => ({ path: [segment, ...i.path], message: i.message }));
}

/** An issue as text: `$.items[2].name: expected string, got number 42` (`Issue.ToString`). */
export function renderIssue(issue: Issue): string {
  return renderPath(issue.path) + ": " + issue.message;
}

/** At most `max` issues, one per line, then `… and N more issues` (`Issue.renderAtMost`). */
export function renderIssues(issues: readonly Issue[], max = Number.MAX_SAFE_INTEGER): string {
  if (issues.length <= max) return issues.map(renderIssue).join("\n");
  const shown = issues.slice(0, Math.max(max, 0));
  const rest = issues.length - shown.length;
  return [...shown.map(renderIssue), rest === 1 ? "… and 1 more issue" : `… and ${rest} more issues`].join("\n");
}

/** Sets a member, even one named `__proto__`. */
function setMember(target: Record<string, unknown>, key: string, value: unknown): void {
  Object.defineProperty(target, key, { value, enumerable: true, writable: true, configurable: true });
}

function member(json: Record<string, unknown>, key: string): unknown {
  return Object.hasOwn(json, key) ? json[key] : undefined;
}

const codecBrand: unique symbol = Symbol("codec");

/**
 * A typed contract: the JSON shape of `T` (`schema`, the wire form of Fx.Contracts' `Schema`), how to
 * write it (`encode`) and how to read and validate it, with issues worded for a model (`decode`).
 */
export interface Codec<T> {
  readonly [codecBrand]: true;
  readonly schema: Schema;
  encode(value: T): Json;
  /** Accumulates every issue it finds, with paths relative to the value. */
  decode(json: unknown): Validation<T>;
  /** Reads JSON text and decodes it; a syntax error is one issue at the root. */
  decodeText(text: string): Validation<T>;
  encodeText(value: T): string;
  /** Documents the value for models and generated code. */
  describe(description: string): Codec<T>;
  /** Makes it a named type (an identifier), defined once and referred to by name. */
  named(name: string): Codec<T>;
  /** Adds a check after decoding: a message is an issue at the value's path. */
  refine(check: (value: T) => string | undefined): Codec<T>;
  /** The same JSON shape for another type, through a two-way conversion. */
  map<U>(decode: (value: T) => U, encode: (value: U) => T): Codec<U>;
  /** Like `map`, but reading can fail with a message (an issue at the value's path). */
  tryMap<U>(decode: (value: T) => { ok: true; value: U } | { ok: false; message: string }, encode: (value: U) => T): Codec<U>;
  /** Rejects members that are not fields of the record (or of the decoded union case). */
  strict(): Codec<T>;
  /** Documents one case of a union. */
  describeCase(tag: string, description: string): Codec<T>;
  /** Strings of at least `n` characters (code points). */
  minLength(this: Codec<string>, n: number): Codec<string>;
  /** Strings of at most `n` characters (code points). */
  maxLength(this: Codec<string>, n: number): Codec<string>;
  nonEmpty(this: Codec<string>): Codec<string>;
  /** Strings in which the regular expression matches (unanchored, as JSON Schema's `pattern`). */
  pattern(this: Codec<string>, regex: string): Codec<string>;
  /** Documents a JSON Schema `format`; not validated. */
  format(this: Codec<string>, name: string): Codec<string>;
  /** Values from `low` to `high`, inclusive. */
  between(this: Codec<number>, low: number, high: number): Codec<number>;
  atLeast(this: Codec<number>, low: number): Codec<number>;
  atMost(this: Codec<number>, high: number): Codec<number>;
  /** Arrays of at least `n` items. */
  minItems<I>(this: Codec<I[]>, n: number): Codec<I[]>;
  /** Arrays of at most `n` items. */
  maxItems<I>(this: Codec<I[]>, n: number): Codec<I[]>;
}

/** The value type of a codec. */
export type Infer<C> = C extends Codec<infer T> ? T : never;

function codePoints(text: string): number {
  let count = 0;
  for (const _ of text) count++;
  return count;
}

function noun(schema: Schema): string {
  return schema.kind === "integer" ? "an integer" : schema.kind === "number" ? "a number" : "a value";
}

class CodecImpl<T> implements Codec<T> {
  readonly [codecBrand] = true as const;

  constructor(
    readonly schema: Schema,
    readonly encode: (value: T) => Json,
    readonly decode: (json: unknown) => Validation<T>,
  ) {}

  decodeText(text: string): Validation<T> {
    let json: unknown;
    try {
      json = JSON.parse(text);
    } catch (e) {
      return Validation.fail([issueAt([], `invalid JSON: ${e instanceof Error ? e.message : String(e)}`)]);
    }
    return this.decode(json);
  }

  encodeText(value: T): string {
    return JSON.stringify(this.encode(value));
  }

  withSchema(schema: Schema): CodecImpl<T> {
    return new CodecImpl(schema, this.encode, this.decode);
  }

  describe(description: string): Codec<T> {
    return this.withSchema({ ...this.schema, description });
  }

  named(name: string): Codec<T> {
    checkTypeName(name);
    return this.withSchema({ kind: "named", name, body: this.schema });
  }

  refine(check: (value: T) => string | undefined): Codec<T> {
    const decode = this.decode;
    return new CodecImpl(this.schema, this.encode, (json) => {
      const decoded = decode(json);
      if (!decoded.ok) return decoded;
      const message = check(decoded.value);
      return message === undefined ? decoded : Validation.fail([issueAt([], message)]);
    });
  }

  map<U>(decode: (value: T) => U, encode: (value: U) => T): Codec<U> {
    const inner = this;
    return new CodecImpl<U>(
      this.schema,
      (value) => inner.encode(encode(value)),
      (json) => {
        const decoded = inner.decode(json);
        return decoded.ok ? Validation.ok(decode(decoded.value)) : decoded;
      },
    );
  }

  tryMap<U>(decode: (value: T) => { ok: true; value: U } | { ok: false; message: string }, encode: (value: U) => T): Codec<U> {
    const inner = this;
    return new CodecImpl<U>(
      this.schema,
      (value) => inner.encode(encode(value)),
      (json) => {
        const decoded = inner.decode(json);
        if (!decoded.ok) return decoded;
        const mapped = decode(decoded.value);
        return mapped.ok ? Validation.ok(mapped.value) : Validation.fail([issueAt([], mapped.message)]);
      },
    );
  }

  strict(): Codec<T> {
    const [schema, check] = strictSchema(this.schema);
    const decode = this.decode;
    return new CodecImpl(schema, this.encode, (json) => {
      const unknown = isObject(json) ? check(json) : [];
      const decoded = decode(json);
      if (unknown.length === 0) return decoded;
      return Validation.fail(decoded.ok ? unknown : [...decoded.issues, ...unknown]);
    });
  }

  describeCase(tag: string, description: string): Codec<T> {
    const describeIn = (schema: Schema): Schema => {
      if (schema.kind !== "union") throw new TypeError("describeCase applies to union codecs.");
      if (!schema.cases.some((c) => c.tag === tag)) throw new TypeError(`The union has no case ${quote(tag)}.`);
      return { ...schema, cases: schema.cases.map((c) => (c.tag === tag ? { ...c, description } : c)) };
    };
    const schema = this.schema.kind === "named" ? { ...this.schema, body: describeIn(this.schema.body) } : describeIn(this.schema);
    return this.withSchema(schema);
  }

  minLength(this: CodecImpl<string>, n: number): Codec<string> {
    return refineWith(
      this,
      (s) => {
        const length = codePoints(s);
        return length >= n ? undefined : n === 1 ? "expected a non-empty string" : `expected at least ${n} characters, got ${length}`;
      },
      (schema) => (schema.kind === "string" ? { ...schema, minLength: n } : schema),
    );
  }

  maxLength(this: CodecImpl<string>, n: number): Codec<string> {
    return refineWith(
      this,
      (s) => {
        const length = codePoints(s);
        return length <= n ? undefined : `expected at most ${n} characters, got ${length}`;
      },
      (schema) => (schema.kind === "string" ? { ...schema, maxLength: n } : schema),
    );
  }

  nonEmpty(this: CodecImpl<string>): Codec<string> {
    return this.minLength(1);
  }

  pattern(this: CodecImpl<string>, regex: string): Codec<string> {
    const compiled = new RegExp(regex);
    return refineWith(
      this,
      (s) => (compiled.test(s) ? undefined : `expected a string matching /${regex}/, got ${describeJson(s)}`),
      (schema) => (schema.kind === "string" ? { ...schema, pattern: regex } : schema),
    );
  }

  format(this: CodecImpl<string>, name: string): Codec<string> {
    return this.withSchema(this.schema.kind === "string" ? { ...this.schema, format: name } : this.schema);
  }

  between(this: CodecImpl<number>, low: number, high: number): Codec<number> {
    const what = noun(this.schema);
    const lowJson = this.encode(low);
    const highJson = this.encode(high);
    const encode = this.encode;
    return refineWith(
      this,
      (value) =>
        value >= low && value <= high
          ? undefined
          : `expected ${what} from ${JSON.stringify(lowJson)} to ${JSON.stringify(highJson)}, got ${describeJson(encode(value))}`,
      (schema) => withBounds(schema, lowJson, highJson),
    );
  }

  atLeast(this: CodecImpl<number>, low: number): Codec<number> {
    const what = noun(this.schema);
    const lowJson = this.encode(low);
    const encode = this.encode;
    return refineWith(
      this,
      (value) => (value >= low ? undefined : `expected ${what} of at least ${JSON.stringify(lowJson)}, got ${describeJson(encode(value))}`),
      (schema) => withBounds(schema, lowJson, undefined),
    );
  }

  atMost(this: CodecImpl<number>, high: number): Codec<number> {
    const what = noun(this.schema);
    const highJson = this.encode(high);
    const encode = this.encode;
    return refineWith(
      this,
      (value) => (value <= high ? undefined : `expected ${what} of at most ${JSON.stringify(highJson)}, got ${describeJson(encode(value))}`),
      (schema) => withBounds(schema, undefined, highJson),
    );
  }

  minItems<I>(this: CodecImpl<I[]>, n: number): Codec<I[]> {
    return refineWith(
      this,
      (items) =>
        items.length >= n ? undefined : n === 1 ? "expected a non-empty array" : `expected at least ${n} items, got ${items.length}`,
      (schema) => (schema.kind === "array" ? { ...schema, minItems: n } : schema),
    );
  }

  maxItems<I>(this: CodecImpl<I[]>, n: number): Codec<I[]> {
    return refineWith(
      this,
      (items) => (items.length <= n ? undefined : `expected at most ${n} items, got ${items.length}`),
      (schema) => (schema.kind === "array" ? { ...schema, maxItems: n } : schema),
    );
  }
}

function refineWith<T>(codec: CodecImpl<T>, check: (value: T) => string | undefined, update: (schema: Schema) => Schema): Codec<T> {
  const refined = codec.refine(check) as CodecImpl<T>;
  return refined.withSchema(update(codec.schema));
}

function withBounds(schema: Schema, low: Json | undefined, high: Json | undefined): Schema {
  if (schema.kind !== "integer" && schema.kind !== "number") return schema;
  const { minimum, maximum, ...rest } = schema;
  const updated: Schema = { ...rest };
  const lower = low === undefined ? minimum : typeof low === "number" ? { kind: "inclusive" as const, value: low } : undefined;
  const upper = high === undefined ? maximum : typeof high === "number" ? { kind: "inclusive" as const, value: high } : undefined;
  if (lower !== undefined) updated.minimum = lower;
  if (upper !== undefined) updated.maximum = upper;
  return updated;
}

/** The unknown-field check of `strict`, and the schema with unknown fields rejected. */
function strictSchema(schema: Schema): [Schema, (members: Record<string, unknown>) => Issue[]] {
  const unknown = (known: readonly string[], members: Record<string, unknown>): Issue[] => {
    const knownSet = new Set(known);
    const text = known.map(quote).join(", ");
    return Object.keys(members)
      .filter((key) => !knownSet.has(key))
      .map((key) => issueAt([key], `unknown field; known fields are ${text}`));
  };
  switch (schema.kind) {
    case "object": {
      const known = schema.fields.map((f) => f.name);
      return [{ ...schema, unknownFields: "reject" }, (members) => unknown(known, members)];
    }
    case "union": {
      const known = new Map(schema.cases.map((c) => [c.tag, [schema.discriminator, ...c.fields.map((f) => f.name)]]));
      const discriminator = schema.discriminator;
      return [
        { ...schema, cases: schema.cases.map((c) => ({ ...c, unknownFields: "reject" as const })) },
        (members) => {
          const tag = member(members, discriminator);
          const fields = typeof tag === "string" ? known.get(tag) : undefined;
          return fields === undefined ? [] : unknown(fields, members);
        },
      ];
    }
    case "named": {
      const [body, check] = strictSchema(schema.body);
      return [{ ...schema, body }, check];
    }
    default:
      throw new TypeError("strict applies to record and union codecs.");
  }
}

/** A codec from its parts (`Codec.create`). */
export function createCodec<T>(schema: Schema, encode: (value: T) => Json, decode: (json: unknown) => Validation<T>): Codec<T> {
  return new CodecImpl(schema, encode, decode);
}

function isCodec(value: unknown): value is Codec<any> {
  return value instanceof CodecImpl;
}

// ---- Records -------------------------------------------------------------------------------------

/** A field that may be absent; `null` also reads as absent, and absent values are not written. */
export interface OptionalField<T> {
  readonly presence: "optional";
  readonly codec: Codec<T>;
}

/** A field that reads as `value` when absent or `null`; it is always written. */
export interface DefaultedField<T> {
  readonly presence: "defaulted";
  readonly codec: Codec<T>;
  readonly value: T;
}

/** A record field: a codec (required), `Codec.optional(c)` or `Codec.withDefault(c, value)`. */
export type Field = Codec<any> | OptionalField<any> | DefaultedField<any>;

/** Fields by their JSON names, in the order they are written. */
export type Fields = { readonly [name: string]: Field };

type FieldValue<F> = F extends OptionalField<infer T> ? T : F extends DefaultedField<infer T> ? T : F extends Codec<infer T> ? T : never;
type RequiredKeys<F> = { [K in keyof F]: F[K] extends OptionalField<any> ? never : K }[keyof F];
type OptionalKeys<F> = { [K in keyof F]: F[K] extends OptionalField<any> ? K : never }[keyof F];
type Simplify<T> = { [K in keyof T]: T[K] } & {};

/** The value of a record with these fields. */
export type RecordOf<F extends Fields> = Simplify<{ -readonly [K in RequiredKeys<F>]: FieldValue<F[K]> } & { -readonly [K in OptionalKeys<F>]?: FieldValue<F[K]> }>;

interface FieldPlan {
  readonly name: string;
  readonly codec: Codec<unknown>;
  readonly presence: "required" | "optional" | "defaulted";
  readonly fallback?: unknown;
  readonly schema: SchemaField;
}

function plan(fields: Fields): FieldPlan[] {
  return Object.entries(fields).map(([name, field]): FieldPlan => {
    if (isCodec(field)) {
      return { name, codec: field, presence: "required", schema: { name, schema: field.schema, presence: { kind: "required" } } };
    }
    if (field.presence === "optional") {
      return { name, codec: field.codec, presence: "optional", schema: { name, schema: field.codec.schema, presence: { kind: "optional" } } };
    }
    return {
      name,
      codec: field.codec,
      presence: "defaulted",
      fallback: field.value,
      schema: { name, schema: field.codec.schema, presence: { kind: "defaulted", value: field.codec.encode(field.value) } },
    };
  });
}

function encodeFields(plans: readonly FieldPlan[], value: Record<string, unknown>, target: Record<string, unknown>): void {
  for (const p of plans) {
    const v = value[p.name];
    switch (p.presence) {
      case "required":
        setMember(target, p.name, p.codec.encode(v));
        break;
      case "optional":
        if (v !== undefined && v !== null) setMember(target, p.name, p.codec.encode(v));
        break;
      case "defaulted":
        setMember(target, p.name, p.codec.encode(v === undefined ? p.fallback : v));
        break;
    }
  }
}

function decodeFields(plans: readonly FieldPlan[], members: Record<string, unknown>, target: Record<string, unknown>): Issue[] {
  const issues: Issue[] = [];
  for (const p of plans) {
    const present = Object.hasOwn(members, p.name);
    const json = present ? members[p.name] : undefined;
    if (p.presence === "required" && !present) {
      issues.push(issueAt([p.name], "missing required field"));
      continue;
    }
    if (p.presence !== "required" && (!present || json === null)) {
      if (p.presence === "defaulted") setMember(target, p.name, p.fallback);
      continue;
    }
    const decoded = p.codec.decode(json);
    if (decoded.ok) setMember(target, p.name, decoded.value);
    else issues.push(...under(p.name, decoded.issues));
  }
  return issues;
}

function checkUnique(what: string, names: readonly string[]): void {
  const seen = new Set<string>();
  for (const name of names) {
    if (seen.has(name)) throw new TypeError(`Duplicate ${what} ${quote(name)}.`);
    seen.add(name);
  }
}

function record<F extends Fields>(fields: F): Codec<RecordOf<F>> {
  const plans = plan(fields);
  checkUnique(
    "field",
    plans.map((p) => p.name),
  );
  return new CodecImpl<RecordOf<F>>(
    { kind: "object", fields: plans.map((p) => p.schema), unknownFields: "ignore" },
    (value) => {
      const out: Record<string, unknown> = {};
      encodeFields(plans, value as Record<string, unknown>, out);
      return out as Json;
    },
    (json) => {
      if (!isObject(json)) return Validation.fail([expected("object", json)]);
      const out: Record<string, unknown> = {};
      const issues = decodeFields(plans, json, out);
      return issues.length === 0 ? Validation.ok(out as RecordOf<F>) : Validation.fail(issues);
    },
  );
}

// ---- Unions --------------------------------------------------------------------------------------

/** A union case whose fields are those of a record codec (`Codec.caseOf`). */
export interface CaseOf<P> {
  readonly caseOf: Codec<P>;
}

/** A union case: its fields (`{}` for none), or `Codec.caseOf(recordCodec)`. */
export type CaseSpec = Fields | CaseOf<any>;

type CaseValue<S> = S extends CaseOf<infer P> ? P : S extends Fields ? RecordOf<S> : never;

/** The value of a union tagged by `D` with these cases. */
export type UnionOf<D extends string, C extends { readonly [tag: string]: CaseSpec }> = {
  [K in keyof C & string]: Simplify<{ -readonly [P in D]: K } & CaseValue<C[K]>>;
}[keyof C & string];

interface CasePlan {
  readonly tag: string;
  readonly schema: SchemaCase;
  readonly encode: (value: Record<string, unknown>, target: Record<string, unknown>) => void;
  readonly decode: (members: Record<string, unknown>, target: Record<string, unknown>) => Issue[];
}

function isCaseOf(spec: CaseSpec): spec is CaseOf<any> {
  return Object.hasOwn(spec, "caseOf") && isCodec((spec as CaseOf<unknown>).caseOf);
}

function casePlan(discriminator: string, tag: string, spec: CaseSpec): CasePlan {
  if (isCaseOf(spec)) {
    const payload = spec.caseOf;
    const schema = payload.schema;
    const body = schema.kind === "named" ? schema.body : schema;
    if (body.kind !== "object") throw new TypeError(`The payload of case ${quote(tag)} must be an object codec.`);
    const description = schema.kind === "named" ? (body.description ?? schema.description) : schema.description;
    const caseSchema: SchemaCase = { tag, fields: body.fields, unknownFields: body.unknownFields ?? "ignore" };
    if (description !== undefined) caseSchema.description = description;
    return {
      tag,
      schema: caseSchema,
      encode: (value, target) => {
        const encoded = payload.encode(value);
        if (!isObject(encoded)) throw new TypeError(`The payload of case ${quote(tag)} did not encode to an object.`);
        for (const [key, v] of Object.entries(encoded)) setMember(target, key, v);
      },
      decode: (members, target) => {
        const rest: Record<string, unknown> = {};
        for (const [key, v] of Object.entries(members)) if (key !== discriminator) setMember(rest, key, v);
        const decoded = payload.decode(rest);
        if (!decoded.ok) return decoded.issues;
        if (isObject(decoded.value)) for (const [key, v] of Object.entries(decoded.value)) setMember(target, key, v);
        return [];
      },
    };
  }
  const plans = plan(spec as Fields);
  checkUnique(
    "field",
    plans.map((p) => p.name),
  );
  if (plans.some((p) => p.name === discriminator)) {
    throw new TypeError(`Case ${quote(tag)} has a field named like the discriminator ${quote(discriminator)}.`);
  }
  return {
    tag,
    schema: { tag, fields: plans.map((p) => p.schema), unknownFields: "ignore" },
    encode: (value, target) => encodeFields(plans, value, target),
    decode: (members, target) => decodeFields(plans, members, target),
  };
}

function union<const D extends string, const C extends { readonly [tag: string]: CaseSpec }>(discriminator: D, cases: C): Codec<UnionOf<D, C>> {
  const plans = Object.entries(cases).map(([tag, spec]) => casePlan(discriminator, tag, spec));
  const byTag = new Map(plans.map((p) => [p.tag, p]));
  const tags = plans.map((p) => quote(p.tag)).join(", ");
  const tagIssue = (message: string) => Validation.fail<UnionOf<D, C>>([issueAt([discriminator], message)]);
  return new CodecImpl<UnionOf<D, C>>(
    { kind: "union", discriminator, cases: plans.map((p) => p.schema) },
    (value) => {
      const tag = (value as Record<string, unknown>)[discriminator];
      const p = typeof tag === "string" ? byTag.get(tag) : undefined;
      if (p === undefined) throw new TypeError(`The union has no case ${JSON.stringify(tag)}.`);
      const out: Record<string, unknown> = {};
      setMember(out, discriminator, p.tag);
      p.encode(value as Record<string, unknown>, out);
      return out as Json;
    },
    (json) => {
      if (!isObject(json)) return Validation.fail([expected("object", json)]);
      if (!Object.hasOwn(json, discriminator)) return tagIssue(`missing required field; expected one of ${tags}`);
      const tag = json[discriminator];
      const p = typeof tag === "string" ? byTag.get(tag) : undefined;
      if (p === undefined) return tagIssue(`expected one of ${tags}, got ${describeJson(tag)}`);
      const out: Record<string, unknown> = {};
      setMember(out, discriminator, p.tag);
      const issues = p.decode(json, out);
      return issues.length === 0 ? Validation.ok(out as UnionOf<D, C>) : Validation.fail(issues);
    },
  );
}

// ---- Collections ---------------------------------------------------------------------------------

function array<T>(item: Codec<T>): Codec<T[]> {
  return new CodecImpl<T[]>(
    { kind: "array", items: item.schema },
    (items) => items.map((i) => item.encode(i)),
    (json) => {
      if (!Array.isArray(json)) return Validation.fail([expected("array", json)]);
      const values: T[] = [];
      const issues: Issue[] = [];
      json.forEach((element, index) => {
        const decoded = item.decode(element);
        if (decoded.ok) values.push(decoded.value);
        else issues.push(...under(index, decoded.issues));
      });
      return issues.length === 0 ? Validation.ok(values) : Validation.fail(issues);
    },
  );
}

function compareKeys(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function decodeMembers<V>(values: Codec<V>, json: Record<string, unknown>): Validation<[string, V][]> {
  const pairs: [string, V][] = [];
  const issues: Issue[] = [];
  for (const [key, v] of Object.entries(json)) {
    const decoded = values.decode(v);
    if (decoded.ok) pairs.push([key, decoded.value]);
    else issues.push(...under(key, decoded.issues));
  }
  return issues.length === 0 ? Validation.ok(pairs) : Validation.fail(issues);
}

function stringMap<V>(values: Codec<V>): Codec<Record<string, V>> {
  return new CodecImpl<Record<string, V>>(
    { kind: "map", values: values.schema },
    (map) => {
      const out: Record<string, unknown> = {};
      for (const key of Object.keys(map).sort(compareKeys)) setMember(out, key, values.encode(map[key] as V));
      return out as Json;
    },
    (json) => {
      if (!isObject(json)) return Validation.fail([expected("object", json)]);
      const decoded = decodeMembers(values, json);
      if (!decoded.ok) return decoded;
      const out: Record<string, V> = {};
      for (const [key, v] of decoded.value) setMember(out, key, v);
      return Validation.ok(out);
    },
  );
}

function keyValues<V>(values: Codec<V>): Codec<Record<string, V>> {
  return new CodecImpl<Record<string, V>>(
    { kind: "map", values: values.schema },
    (map) => {
      const out: Record<string, unknown> = {};
      for (const [key, v] of Object.entries(map)) setMember(out, key, values.encode(v));
      return out as Json;
    },
    (json) => {
      if (!isObject(json)) return Validation.fail([expected("object", json)]);
      const decoded = decodeMembers(values, json);
      if (!decoded.ok) return decoded;
      const out: Record<string, V> = {};
      for (const [key, v] of decoded.value) setMember(out, key, v);
      return Validation.ok(out);
    },
  );
}

type TupleOf<C extends readonly Codec<any>[]> = { -readonly [K in keyof C]: C[K] extends Codec<infer T> ? T : never };

function tuple<const C extends readonly Codec<any>[]>(...items: C): Codec<TupleOf<C>> {
  return new CodecImpl<TupleOf<C>>(
    { kind: "tuple", items: items.map((i) => i.schema) },
    (value) => items.map((codec, index) => codec.encode((value as unknown[])[index])),
    (json) => {
      if (!Array.isArray(json) || json.length !== items.length) return Validation.fail([expected(`array of ${items.length} items`, json)]);
      const values: unknown[] = [];
      const issues: Issue[] = [];
      items.forEach((codec, index) => {
        const decoded = codec.decode(json[index]);
        if (decoded.ok) values.push(decoded.value);
        else issues.push(...under(index, decoded.issues));
      });
      return issues.length === 0 ? Validation.ok(values as TupleOf<C>) : Validation.fail(issues);
    },
  );
}

function nullable<T>(codec: Codec<T>): Codec<T | null> {
  return new CodecImpl<T | null>(
    { kind: "nullable", schema: codec.schema },
    (value) => (value === null ? null : codec.encode(value)),
    (json) => (json === null ? Validation.ok(null) : codec.decode(json)),
  );
}

// ---- Scalars -------------------------------------------------------------------------------------

const json = new CodecImpl<Json>(
  { kind: "any" },
  (value) => value,
  (value) => Validation.ok(value as Json),
);

const unit = new CodecImpl<null>(
  { kind: "null" },
  () => null,
  (value) => (value === null ? Validation.ok(null) : Validation.fail([expected("null", value)])),
);

const bool = new CodecImpl<boolean>(
  { kind: "boolean" },
  (value) => value,
  (value) => (typeof value === "boolean" ? Validation.ok(value) : Validation.fail([expected("boolean", value)])),
);

const string = new CodecImpl<string>(
  { kind: "string" },
  (value) => value,
  (value) => (typeof value === "string" ? Validation.ok(value) : Validation.fail([expected("string", value)])),
);

const number = new CodecImpl<number>(
  { kind: "number" },
  (value) => {
    if (!Number.isFinite(value)) throw new RangeError("JSON has no NaN or infinities: use Codec.float");
    return value;
  },
  (value) => (typeof value === "number" ? Validation.ok(value) : Validation.fail([expected("number", value)])),
);

const float = new CodecImpl<number>(
  { kind: "number" },
  (value) => (Number.isNaN(value) ? "NaN" : value === Infinity ? "Infinity" : value === -Infinity ? "-Infinity" : value),
  (value) => {
    if (typeof value === "number") return Validation.ok(value);
    if (value === "NaN") return Validation.ok(Number.NaN);
    if (value === "Infinity") return Validation.ok(Infinity);
    if (value === "-Infinity") return Validation.ok(-Infinity);
    return Validation.fail([expected("number", value)]);
  },
);

const int32Min = -2147483648;
const int32Max = 2147483647;

const int = new CodecImpl<number>(
  { kind: "integer" },
  (value) => {
    if (!Number.isInteger(value) || value < int32Min || value > int32Max) throw new RangeError(`not a 32-bit integer: ${value}`);
    return value;
  },
  (value) => {
    if (typeof value !== "number" || !Number.isInteger(value)) return Validation.fail([expected("integer", value)]);
    if (value < int32Min || value > int32Max) return Validation.fail([expected(`integer from ${int32Min} to ${int32Max}`, value)]);
    return Validation.ok(value === 0 ? 0 : value);
  },
);

function literal<const T extends Json>(value: T): Codec<T> {
  const canonical = serializeCanonical(value);
  const text = JSON.stringify(value);
  return new CodecImpl<T>(
    { kind: "const", value },
    () => value,
    (json) => {
      let same = false;
      try {
        same = serializeCanonical(json as Json) === canonical;
      } catch {
        same = false;
      }
      return same ? Validation.ok(value) : Validation.fail([expected(text, json)]);
    },
  );
}

function enumOf<const T extends string>(values: readonly T[]): Codec<T> {
  checkUnique("enum value", values);
  const names = values.map(quote).join(", ");
  const allowed = new Set<string>(values);
  return new CodecImpl<T>(
    { kind: "enum", values: [...values] },
    (value) => {
      if (!allowed.has(value)) throw new TypeError(`The value ${JSON.stringify(value)} is missing from the enum codec.`);
      return value;
    },
    (json) => (typeof json === "string" && allowed.has(json) ? Validation.ok(json as T) : Validation.fail([expected(`one of ${names}`, json)])),
  );
}

function recursive<T>(name: string, build: (self: Codec<T>) => Codec<T>): Codec<T> {
  checkTypeName(name);
  let resolved: Codec<T> | undefined;
  const get = () => {
    if (resolved === undefined) throw new Error(`The recursive codec ${name} was used while it was being built.`);
    return resolved;
  };
  const self = new CodecImpl<T>(
    { kind: "ref", name },
    (value) => get().encode(value),
    (json) => get().decode(json),
  );
  resolved = build(self).named(name);
  return resolved;
}

/**
 * The codec combinators, named as in Fx.Contracts (`Codec.record`, `Codec.union`, …). A record is
 * written with its fields in the order given, a union as an object tagged by its discriminator.
 */
export const Codec = {
  /** Any JSON value, unchanged. */
  json: json as Codec<Json>,
  /** `null` (F# `Codec.unit`). */
  unit: unit as Codec<null>,
  bool: bool as Codec<boolean>,
  string: string as Codec<string>,
  /** A finite number (F# `Codec.number`, `Codec.float` without NaN and infinities). */
  number: number as Codec<number>,
  /** A number; NaN and the infinities are the strings `"NaN"`, `"Infinity"`, `"-Infinity"` (F# `Codec.float`). */
  float: float as Codec<number>,
  /** A 32-bit integer; accepts any number with an integral value in range (F# `Codec.int`). */
  int: int as Codec<number>,
  /** Exactly this value (compared in canonical form). */
  literal,
  /** One of these strings (F# `Codec.enum`, whose values are the strings themselves here). */
  enum: enumOf,
  /** A JSON array. */
  array,
  /** The F# name of `array`. */
  list: array,
  /** `null` or a value (F# `Codec.option`). Do not nest, or use with `Codec.optional`. */
  nullable,
  /** An object with any keys, written in key order (F# `Codec.stringMap`). */
  stringMap,
  /** An object with any keys, written in member order (F# `Codec.keyValues`). */
  keyValues,
  /** An array with exactly these items (F# `Codec.tuple2`, `tuple3`). */
  tuple,
  /** A record: fields by JSON name, in the order they are written. */
  record,
  /** A field that may be absent (`null` reads as absent); absent values are not written. */
  optional<T>(codec: Codec<T>): OptionalField<T> {
    return { presence: "optional", codec };
  },
  /** A field that reads as `value` when absent or `null`; it is always written. */
  withDefault<T>(codec: Codec<T>, value: T): DefaultedField<T> {
    return { presence: "defaulted", codec, value };
  },
  /** A union of objects tagged by `discriminator`, one case per tag. */
  union,
  /** A union case whose fields are a record codec's, written next to the tag. */
  caseOf<P>(payload: Codec<P>): CaseOf<P> {
    return { caseOf: payload };
  },
  /** A recursive named type: `build` gets a codec for the type itself. */
  recursive,
  /** A codec from its parts. */
  create: createCodec,
  /** A short name of a codec's type, for messages. */
  typeName<T>(codec: Codec<T>): string {
    return typeName(codec.schema);
  },
};
