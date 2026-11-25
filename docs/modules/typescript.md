# TypeScript declarations (`Fx.Contracts.TypeScript`)

Spec: §4.1 (the same codecs produce client and SDK types), §17 (generated types for clients, extensions,
kernels and the workflow worker), §6.5 (the generated `fx` host bridge), §16.1 (`@fx/sdk`), §20 (naming).
Task: T102. Code: [`src/Fx.Contracts.TypeScript`](../../src/Fx.Contracts.TypeScript) (pure: no I/O, no
reflection). Tests: `tests/Fx.Contracts.TypeScript.Tests`, `bun scripts/declarations.ts` and the
`typescript-*` scenarios of the JIT-vs-native diff.

The generator turns `Schema` values (spec §4.1, [contracts.md](contracts.md)) into a TypeScript declaration
file (`.d.ts`): one string, deterministic, formatted, with a header that marks it generated. AGENTS.md:
types for `@fx/sdk` and `fx.d.ts` come from the F# codecs, never by hand.

| Consumer | Layout | What it declares |
|---|---|---|
| `@fx/sdk` (`packages/sdk/src/generated/contracts.d.ts`, [sdk.md](sdk.md)), clients, the workflow worker | `Layout.Module` | `export interface` / `export type` for contract types; `export declare` members for RPC surfaces |
| The bun-ts kernel's `<global>.d.ts` (T117) | `Layout.Global(Product.kernelGlobal, doc)` | The host bridge (`fx.shell.run`, `fx.finish`, …) and the types it uses, in `declare namespace fx` |
| The kernel protocol's TypeScript side (T116) | `Layout.Module` | The protocol's message types, if T116 defines them as codecs |

The host generates a kernel's declarations at run time (bindings are generated host-side, spec §6.2), so the
generator lives in `src/`, is Native AOT-safe and is covered by the JIT-vs-native diff.

## API

```fsharp
type DeclarationFile = { Layout: Layout; Types: Schema list; Members: Member list }
type Layout = Module | Global of name: string * description: string option
type Member = Method of Method | Group of Group
type Method = { Name: string; Description: string option; Parameters: Parameter list; Returns: Schema }
type Parameter = { Name: string; Description: string option; Schema: Schema; Optional: bool }

DeclarationFile.render : DeclarationFile -> string
Member.ofPaths : groups: (string * string) list -> Method list -> Member list   // "shell.run" → shell { run }
```

```fsharp
let kernelTypings (bindings: Method list) =
    DeclarationFile.render
        { Layout = Layout.Global(Product.kernelGlobal, Some "The host bridge.")
          Types = [ turnResult.Schema ]                    // declared even if no member uses them
          Members = Member.ofPaths [ "shell", "Commands in the workspace's shell." ] bindings }
```

- `Types` are named schemas (`Codec.named`, `Codec.recursive`). Every named type that the types and the
  members use is declared too, so a file needs no imports. Name an anonymous schema to declare it:
  `Schema.ofKind (SchemaKind.Named { Name = "VariantsConfig"; Body = schema })`.
- A **method** is a remote call: TypeScript sees `name(parameters): Promise<Returns>`. Arguments are passed
  by position, so parameter names only document them. Methods that take a callback (`fx.fs.edit(matches,
  fn)`, spec §6.8) are kernel-side wrappers around a host call; T117 writes them, and declares them in its
  own file or extends this model.
- A **group** is an object of members: `const shell: { run(…): … }` at the top level, a property inside
  another group. `Member.ofPaths` builds groups from the dotted names kernels use (`shell.run`).
- The **global** is the kernel global, `Fx.Product.kernelGlobal`: never hardcode the product name. The
  file name is the caller's choice (`<global>.d.ts` for kernels).

## Mapping

| Schema | TypeScript |
|---|---|
| `Any` | `unknown` |
| `Null`, `Boolean`, `String` | `null`, `boolean`, `string` |
| `Integer`, `Number` | `number`; integers are documented as such (see Numbers) |
| `Const` | a literal type: `"2.0"`, `42.5` (normalized), `[1, "x"]`, `{ on: true }`; `{}` as `{ [key: string]: never }` |
| `Enum` | `"low" \| "medium" \| "high"`; no values: `never` |
| `Array` | `T[]`, `(A \| B)[]` |
| `Tuple` | `[A, B]` |
| `Map` | `{ [key: string]: T }` |
| `Object` | an object type; a named one is an `interface` |
| `Union` (tagged) | a union of object types with a literal tag: `{ kind: "done"; summary: string } \| { kind: "cancelled" }`; no cases: `never` |
| `AnyOf` | `A \| B`, flattened, without repeated members; no members: `never` |
| `Nullable` | `T \| null` |
| `Named`, `Ref` | the type's name; the definition is emitted once. Recursive types refer to themselves |

**Fields.** `Required` → `name: T`. `Optional` → `name?: T`. `Defaulted v` → `name?: T` with
`@default <json>`. `Nullable` → `name: T | null`, which is not optional. One type serves both directions:

- everything the F# encoders write type-checks (the fixtures prove it). Optional fields are left out,
  defaulted ones are always written, and `?` covers both;
- what type-checks decodes, unless it breaks a constraint that the documentation states (an integer, a
  bound, a length, a pattern);
- the decoders accept more than the types allow: `null` for an optional field, `3.0` for an integer,
  unknown members. Types describe the canonical form.

TypeScript object types are open, so `Codec.strict` (unknown fields rejected) is not visible in the types;
object literals still get TypeScript's excess-property check.

**Numbers.** Fx.Contracts writes `int64` and `decimal` as exact JSON numbers, so they are `number` here.
`JSON.parse` rounds them beyond 2^53 or 15–17 significant digits. Contracts that carry such values keep them
in range (store instants are microseconds, sequence numbers count events), or a TypeScript reader that
needs them exactly parses with a lossless JSON reader. Known gap: `Codec.float` writes NaN and the
infinities as the strings `"NaN"`, `"Infinity"` and `"-Infinity"`, but its schema says `number`, so neither
JSON Schema nor these types allow them (a follow-up for Fx.Contracts: an `AnyOf` schema for `float`).

## Documentation

Descriptions and constraints become JSDoc, written for people in editors and for models reading a kernel's
prompt pack:

```ts
/** A line that matched a search. */
export interface Match {
  /**
   * Workspace-relative.
   * Non-empty.
   */
  path: string;
  /** Integer, at least 1. */
  line: number;
  /** @default [] */
  tags?: string[];
  /** Values: Integer, at least 0. */
  counts: { [key: string]: number };
}
```

- A **type** is documented by its body's description, or else by the description of the `Types` root that
  declares it (a codec described after `Codec.named`, as `Schema.toJsonSchema` does for its root). Describe
  a codec before naming it, or declare it as a root, to document its type.
- A **field** or parameter shows the descriptions of its use (and of a nullable's inner value), unless one
  repeats its type's documentation, then its constraints in words: `Integer, from 0 to 100.`,
  `Number, greater than 0 and less than 1.`, `Non-empty.`, `From 3 to 63 characters.`,
  `Matches /^sha256:[0-9a-f]{64}$/.`, `Format: uri.`, `At most 4 items.` One level into array items and
  map values (`Items: …`, `Values: …`); named types document their own.
- Standard tags where JSDoc has them: `@default`, `@param`, `@returns`. A union case's description goes on
  its tag property.
- Brands (`string & { __brand }`) are not used: values are written as plain literals by cells and
  extensions, and the host's decoders check the constraints and say what is wrong.
- `*/` in text is written `*\/` (the same in a regular expression or JSON); line breaks become comment
  lines; regular expressions are shown as JavaScript literals.

## Names

- **Type names** come from `Codec.named`. Names TypeScript cannot use are renamed: other characters become
  `_`, a leading digit gets `_` in front, and reserved words get `_` after (`string_`, `default_`,
  `unknown_`). Reserved for types: JavaScript's reserved words (strict mode and modules included),
  TypeScript's predefined types and type keywords, and the globals the output refers to (`Promise`,
  `globalThis`). Names that are already valid keep themselves; the others are renamed in name order, with
  a number appended if the result is taken (`Promise` → `Promise_2` when `Promise_` exists). Identifiers
  are ASCII (`[A-Za-z_$][A-Za-z0-9_$]*`), so the output does not depend on Unicode tables.
- **Property names** (fields, tags, group and nested method names) are written as they are if they are
  identifiers, reserved words included (`default`, `delete`), and quoted otherwise (`"agent.actionSurface"`,
  `"my-tool"`).
- **Parameter names** that are reserved or invalid are renamed the same way (`class` → `class_`), and
  repeated ones numbered.
- **Top-level member names** and the global must be identifiers that are not reserved words: they are what
  code calls, so they cannot be renamed.

## Layout and determinism

- The header line, then the members, then the named types, each sorted by name (ordinal), separated by
  blank lines; in a global script all of it inside `declare namespace <global> { … }`. An empty module is
  `export {};`. Two-space indentation, `\n` line ends, a final newline.
- The text depends only on the input: not on the order of `Types` or `Members`, the platform, or time.
- Object types and unions stay on one line when no member is documented and they fit in 80 characters;
  tagged unions always get a line per case; signatures longer than 100 characters get a line per parameter.
- Records are `interface`s (readable hovers and errors), other named types `type` aliases. An optional
  parameter before a required one is declared `name: T | undefined`, since TypeScript only allows trailing
  optional parameters.

## Errors

Invalid input is a defect and throws `ArgumentException`, like `Schema.toJsonSchema`: a type that is not
named, two different types with one name, a reference without a definition, two members with one name at
one level, a top-level member or global that is not a bindable identifier, and (in `Member.ofPaths`) a path
that is empty, has an empty segment, is both a method and a group, or a group description for a path that
is not a group. Check extension-contributed schemas and bindings when the contribution is registered
(T108, T117), for example by rendering them, so that generating a kernel's declarations cannot fail later.

## Decisions

- **Types only, no generated runtime codecs.** The F# codecs are the authority: the host decodes every
  value it receives with them and answers with model-readable issues, and the values it sends were encoded
  by them. TypeScript authors who define *their own* contracts use `@fx/sdk`'s hand-written codec
  combinators, which produce the same `Schema` in its wire form (`Codecs.schema`); the few F# contracts
  `@fx/sdk` decodes at run time (manifests, the extension protocol's messages) are hand-written mirrors
  whose schemas are compared with the F# ones by cross-language fixtures ([sdk.md](sdk.md)).
- **`unknown` for any JSON value**, not a recursive `JsonValue` type: interfaces lack implicit index
  signatures, so typed values would not be assignable to a `JsonValue` parameter.
- **Self-contained files.** Every file declares every type it uses. Files that share types (several SDK
  modules) can be split later with an import map (name → module) passed to the generator.

## Testing

- **Golden files** (`tests/Fx.Contracts.TypeScript.Tests/golden`): a domain that uses every schema
  construct, names TypeScript cannot use, the real contracts (Fx.Contracts issues, limits and previews;
  Fx.Store envelopes and blob references; Fx.Variants slot declarations, resolved values, profiles,
  policies, recorded reads and the core slots' config), and a kernel's host bridge as a global script.
  After an intended change, run the tests with `UPDATE_GOLDEN=1` and review the diff. Adding a core slot
  changes `variants.d.ts` and `fixtures.ts`.
- **Shared fixtures** (`golden/fixtures.ts`, generated by the tests): JSON the F# codecs write, which must
  type-check, and JSON they reject for its shape, which must not (under `@ts-expect-error`, one line each).
  The tests check the F# side; every rejected fixture's type also has an accepted one, so a misspelt type
  cannot satisfy `@ts-expect-error`.
- **Type-checking** (`bun scripts/declarations.ts`, in CI's Lint job with Bun 1.4.5): the golden files, the
  fixtures and `typescript/usage.ts` (narrowing, optional versus nullable, renamed types, the host bridge,
  and wrong uses under `@ts-expect-error`) must type-check with `strict`, `noUncheckedIndexedAccess`,
  `exactOptionalPropertyTypes` and `skipLibCheck: false` (which would skip `.d.ts` files); and
  `typescript/must-fail` must fail with exactly the errors its `// expect: TSxxxx` comments list. `bun check`
  always runs with `--no-script` (see [the Bun kernel spike](../spikes/bun-kernel.md)).
- **Native AOT**: `typescript-module`, `typescript-global` and `typescript-invalid-input` in the JIT-vs-native
  diff.
