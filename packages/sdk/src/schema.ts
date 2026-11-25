// Schemas: the wire form of Fx.Contracts' `Schema` (`Codecs.schema`; the types are generated from it),
// and JSON Schema emitted exactly as `Schema.toJsonSchema` does, so that a TypeScript codec and the
// F# codec it mirrors describe a value with the same JSON.

import type { Schema, SchemaCase, SchemaField } from "./generated/contracts.d.ts";
import { type Json, equalJson, quote } from "./json.ts";

export type { Schema, SchemaBound, SchemaCase, SchemaField, SchemaPresence } from "./generated/contracts.d.ts";

/** Calls `visit` on the schema and every schema nested in it (not following references). */
export function forEachSchema(schema: Schema, visit: (schema: Schema) => void): void {
  visit(schema);
  const fields = (fields: readonly SchemaField[]) => {
    for (const field of fields) forEachSchema(field.schema, visit);
  };
  switch (schema.kind) {
    case "array":
      forEachSchema(schema.items, visit);
      break;
    case "tuple":
      for (const item of schema.items) forEachSchema(item, visit);
      break;
    case "anyOf":
      for (const item of schema.schemas) forEachSchema(item, visit);
      break;
    case "map":
      forEachSchema(schema.values, visit);
      break;
    case "nullable":
      forEachSchema(schema.schema, visit);
      break;
    case "object":
      fields(schema.fields);
      break;
    case "union":
      for (const c of schema.cases) fields(c.fields);
      break;
    case "named":
      forEachSchema(schema.body, visit);
      break;
  }
}

/** A named type: its name and body. */
export interface Definition {
  name: string;
  body: Schema;
}

function compareNames(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Every named type used in the schema, including the schema itself if it is named, sorted by name
 * (`Schema.definitions`). Throws if two different types share a name.
 */
export function definitions(schema: Schema): Definition[] {
  const found = new Map<string, Schema>();
  forEachSchema(schema, (s) => {
    if (s.kind !== "named") return;
    const existing = found.get(s.name);
    if (existing === undefined) found.set(s.name, s.body);
    else if (!equalJson(existing as Json, s.body as Json)) throw new TypeError(`Two different types are named ${quote(s.name)}.`);
  });
  return [...found.entries()].map(([name, body]) => ({ name, body })).sort((a, b) => compareNames(a.name, b.name));
}

type Members = [string, Json][];

const object = (members: Members): Json => Object.fromEntries(members) as Json;

function bounds(schema: { minimum?: { kind: string; value: number }; maximum?: { kind: string; value: number } }): Members {
  const members: Members = [];
  if (schema.minimum) members.push([schema.minimum.kind === "inclusive" ? "minimum" : "exclusiveMinimum", schema.minimum.value]);
  if (schema.maximum) members.push([schema.maximum.kind === "inclusive" ? "maximum" : "exclusiveMaximum", schema.maximum.value]);
  return members;
}

function refTo(root: string | undefined, name: string): Json {
  return root === name ? "#" : "#/$defs/" + name;
}

function constType(value: Json): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  switch (typeof value) {
    case "boolean":
      return "boolean";
    case "string":
      return "string";
    case "number":
      return Number.isInteger(value) ? "integer" : "number";
    default:
      return "object";
  }
}

function emitObject(root: string | undefined, leading: readonly SchemaField[], fields: readonly SchemaField[], unknownFields: string | undefined): Members {
  const all = [...leading, ...fields];
  const properties: Members = all.map((field) => {
    const members = emit(root, field.schema);
    if (field.presence.kind === "defaulted") members.push(["default", field.presence.value as Json]);
    return [field.name, object(members)];
  });
  const required = all.filter((f) => f.presence.kind === "required").map((f) => f.name);
  const members: Members = [
    ["type", "object"],
    ["properties", object(properties)],
  ];
  if (required.length > 0) members.push(["required", required]);
  if (unknownFields === "reject") members.push(["additionalProperties", false]);
  return members;
}

function emit(root: string | undefined, schema: Schema): Members {
  let body: Members;
  switch (schema.kind) {
    case "any":
      body = [];
      break;
    case "null":
      body = [["type", "null"]];
      break;
    case "boolean":
      body = [["type", "boolean"]];
      break;
    case "integer":
      body = [["type", "integer"], ...bounds(schema)];
      break;
    case "number":
      body = [["type", "number"], ...bounds(schema)];
      break;
    case "string":
      body = [["type", "string"]];
      if (schema.minLength !== undefined) body.push(["minLength", schema.minLength]);
      if (schema.maxLength !== undefined) body.push(["maxLength", schema.maxLength]);
      if (schema.pattern !== undefined) body.push(["pattern", schema.pattern]);
      if (schema.format !== undefined) body.push(["format", schema.format]);
      break;
    case "const":
      body = [
        ["type", constType(schema.value as Json)],
        ["const", schema.value as Json],
      ];
      break;
    case "enum":
      body = [
        ["type", "string"],
        ["enum", schema.values],
      ];
      break;
    case "array":
      body = [
        ["type", "array"],
        ["items", object(emit(root, schema.items))],
      ];
      if (schema.minItems !== undefined) body.push(["minItems", schema.minItems]);
      if (schema.maxItems !== undefined) body.push(["maxItems", schema.maxItems]);
      break;
    case "tuple":
      body = [
        ["type", "array"],
        ["prefixItems", schema.items.map((item) => object(emit(root, item)))],
        ["items", false],
        ["minItems", schema.items.length],
        ["maxItems", schema.items.length],
      ];
      break;
    case "map":
      body = [
        ["type", "object"],
        ["additionalProperties", object(emit(root, schema.values))],
      ];
      break;
    case "object":
      body = emitObject(root, [], schema.fields, schema.unknownFields);
      break;
    case "union": {
      const discriminator = schema.discriminator;
      const emitCase = (c: SchemaCase): Members => {
        const tag: SchemaField = { name: discriminator, schema: { kind: "const", value: c.tag }, presence: { kind: "required" } };
        const members = emitObject(root, [tag], c.fields, c.unknownFields);
        if (c.description !== undefined) members.push(["description", c.description]);
        return members;
      };
      const [single, ...rest] = schema.cases;
      body = single !== undefined && rest.length === 0 ? emitCase(single) : [["anyOf", schema.cases.map((c) => object(emitCase(c)))]];
      break;
    }
    case "anyOf":
      body = [["anyOf", schema.schemas.map((s) => object(emit(root, s)))]];
      break;
    case "nullable":
      body = [["anyOf", [object(emit(root, schema.schema)), { type: "null" }]]];
      break;
    case "ref":
      body = [["$ref", refTo(root, schema.name)]];
      break;
    case "named":
      body = [["$ref", refTo(root, schema.name)]];
      break;
  }
  if (schema.description !== undefined) body.push(["description", schema.description]);
  return body;
}

function references(schema: Schema): Set<string> {
  const names = new Set<string>();
  forEachSchema(schema, (s) => {
    if (s.kind === "ref") names.add(s.name);
  });
  return names;
}

/**
 * JSON Schema (draft 2020-12) for a schema, member for member as `Schema.toJsonSchema` writes it: a
 * named root is inlined and refers to itself as `#`; other named types go to `$defs`; unions become
 * `anyOf` of objects whose tag is a `const`; nullable values `anyOf` with `{"type": "null"}`. Throws if
 * two different types share a name or a reference has no definition.
 */
export function toJsonSchema(schema: Schema): Json {
  let body = schema;
  let rootName: string | undefined;
  if (schema.kind === "named") {
    rootName = schema.name;
    const description = schema.body.description ?? schema.description;
    body = description === undefined ? schema.body : { ...schema.body, description };
  }
  const named = definitions(schema).filter((d) => d.name !== rootName);
  const defined = new Set(named.map((d) => d.name));
  if (rootName !== undefined) defined.add(rootName);
  for (const name of references(schema)) {
    if (!defined.has(name)) throw new TypeError(`The schema refers to ${quote(name)} but does not define it.`);
  }
  const members = emit(rootName, body);
  if (named.length > 0) members.push(["$defs", object(named.map((d) => [d.name, object(emit(rootName, d.body))]))]);
  return object(members);
}

const typeNamePattern = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** Throws unless `name` can name a type (`[A-Za-z_][A-Za-z0-9_]*`). */
export function checkTypeName(name: string): void {
  if (!typeNamePattern.test(name)) throw new TypeError(`Type name ${quote(name)} must match [A-Za-z_][A-Za-z0-9_]*.`);
}

/** A short TypeScript-like name for messages: a named type's name, `string`, `number`, `Match[]`, `object`. */
export function typeName(schema: Schema): string {
  switch (schema.kind) {
    case "any":
      return "any";
    case "null":
    case "boolean":
    case "number":
    case "string":
      return schema.kind;
    case "integer":
      return "number";
    case "const":
      return JSON.stringify(schema.value);
    case "enum":
      return schema.values.map(quote).join(" | ");
    case "array": {
      const item = typeName(schema.items);
      return (item.includes(" | ") ? `(${item})` : item) + "[]";
    }
    case "tuple":
      return "[" + schema.items.map(typeName).join(", ") + "]";
    case "map":
    case "object":
    case "union":
      return "object";
    case "anyOf":
      return schema.schemas.map(typeName).join(" | ");
    case "nullable":
      return typeName(schema.schema) + " | null";
    case "ref":
    case "named":
      return schema.name;
  }
}
