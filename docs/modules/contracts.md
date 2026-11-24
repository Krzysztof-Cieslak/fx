# Module spec: contracts (`src/Fx.Contracts`)

Typed contracts (spec §4.1): our own JSON, and `Codec<'T>`, one value that gives a type's schema,
encoder, validating decoder and bounded preview. Everything that crosses a boundary builds on it:
wire protocols (T107), events and their hashes (T105), variants and profile hashes (T106), generated
TypeScript (T102), kernel values (T116, T201) and model wire adapters (T118 and later).

- **Dependencies:** FSharp.Core only (Fx.Prelude may be added). No System.Text.Json, spans or reflection,
  so the code stays Fable-compatible (spec §20) and Native AOT-safe.
- **Tests:** `tests/Fx.Contracts.Tests` (Expecto + FsCheck properties and golden files) and the
  `contracts-*` scenarios of the JIT-vs-native diff (`tests/aot/Fx.Aot.Scenarios/Scenarios/Contracts.fs`).

## Files

| File | Contents |
|---|---|
| `JsonNumber.fs` | Exact JSON numbers: grammar, normalization, conversions |
| `Json.fs` | The `Json` AST and the writer; `JsonError`, `ReadOptions` |
| `JsonReader.fs` | The strict, iterative reader |
| `JsonModule.fs` | `Json.parse`, `serialize`, `serializeIndented`, `canonicalize`, `describe`, … |
| `Issue.fs` | `PathSegment`, `JsonPath.render`, `Issue` |
| `Validation.fs` | `Validation<'T,'E>` and the `validation { }` builder |
| `Schema.fs` | The `Schema` ADT, `Schema.render`, `Schema.definitions`, `Schema.toJsonSchema` |
| `Preview.fs` | `Limits`, `Preview` and the rendering combinators |
| `Codec.fs` | `Codec<'T>`, `Field`, and the `Codec` combinators |
| `Codecs.fs` | Codecs of the contract types themselves (`Issue`, `Limits`, `Preview`) |

## JSON

```fsharp
type Json = Null | Bool of bool | Number of JsonNumber | String of string
          | Array of Json list | Object of (string * Json) list
```

- **Numbers** keep their exact text (`JsonNumber.Text`), so int64 and decimal values never lose
  precision. Conversions: `tryToInt`, `tryToInt64` (exact, any notation of a whole number in range),
  `tryToDecimal` (rounds beyond decimal precision), `toFloat` (nearest; infinities beyond range),
  `ofFloat` (shortest round-trip text, as `JSON.stringify`; throws for NaN and infinities).
- **Objects** keep member order; equality is structural and compares number text, so compare
  `Json.canonicalize` results to ignore order and notation.

### Reader (`Json.parse`, `Json.parseWith`)

Strict RFC 8259: no comments, trailing commas, single quotes, unquoted keys, `NaN`/`Infinity`, leading
zeros or `+` signs. On top of the grammar:

- **duplicate keys are errors** (I-JSON, RFC 7493): a duplicated key could otherwise be read differently
  by two parsers;
- **nesting is limited** by `ReadOptions.MaxDepth` (default 128; `[]` has depth 1). The reader is
  iterative, so the limit is a policy, never a stack overflow;
- exponents with more than 9 significant digits are rejected (RFC 8259 §9 allows range limits);
- one leading byte order mark is ignored;
- escaped and raw **lone surrogates are accepted** (RFC 8259 allows them; `JSON.stringify` emits them)
  and kept, so every string the writer produces reads back unchanged.

Errors are `JsonError { Message; Offset; Line; Column }` (1-based line and column, columns in UTF-16
code units, `\r\n`, `\n` and `\r` all end a line), e.g.
`unexpected character ']'; expected a value at line 1, column 4`.

### Writer (`Json.serialize`, `Json.serializeIndented`)

- Compact: no whitespace. Indented: two spaces, `\n` on every platform, `[]` and `{}` for empty
  containers; the same layout as `JSON.stringify(value, null, 2)`.
- Strings are escaped minimally, as RFC 8785 and `JSON.stringify` do: `"` and `\`, control characters as
  `\b \f \n \r \t` or lowercase `\u00xx`, and lone surrogates as lowercase `\udxxx`. Everything else,
  including non-ASCII and `/`, is written as is. The output is always well-formed UTF-16.
- Numbers are written exactly as they are held.

### Canonical form (`Json.canonicalize`, `Json.serializeCanonical`)

The basis for content hashes (events, ledger files, profiles). Hash the UTF-8 bytes of
`serializeCanonical`. Rules:

1. Object members are sorted by key, comparing UTF-16 code units (ordinal), as in RFC 8785.
   Objects with duplicate keys have no canonical form: `canonicalize` throws.
2. Numbers are normalized **exactly**, never through binary floating point: no leading zeros, no
   trailing fractional zeros, `-0` becomes `0`, and the notation follows ECMAScript's
   `Number.prototype.toString` thresholds over the exact digits: `100`, `1.5`, `0.000001`, `1e-7`,
   `1e+21`, `1.2345678901234567890123456789e+29`. Equal values always get equal text.
3. Strings use the writer's minimal escaping; array order is kept; no whitespace.

The result equals RFC 8785 (JCS) output for every document whose numbers are exact doubles. Other
numbers differ on purpose: JCS would round `333333333.33333329` to `333333333.3333333`, which would let
two different int64 or decimal values hash the same.

Changing these rules changes every stored hash: treat it as a schema migration.

## Validation and issues

`Validation<'T,'E> = Result<'T, 'E list>`. `Validation.map2`, `traverse` and `and!` in
`validation { }` accumulate errors; `bind` and `let!` stop at the first.

An `Issue` is `{ Path: PathSegment list; Message: string }`, rendered as
`$.items[2].name: expected string, got number 42`. Paths use JavaScript accessor syntax
(`$.a.b[0]["a key"]`). Messages are written for a model to read and fix its output:

| Situation | Message |
|---|---|
| Wrong type | `expected string, got number 42` (`Json.describe` of the value) |
| Missing field | `missing required field` (the path names the field) |
| Unknown field (`Codec.strict`) | `unknown field; known fields are "start", "end"` |
| Union tag | `expected one of "done", "waiting", got string "don"` |
| Refinement | `expected an integer from 0 to 100, got number 101`, `expected a non-empty string` |
| Invalid JSON text | `invalid JSON: <JsonError>` |

Decoders report every issue they find. Use `Issue.renderAtMost` to bound what is sent to a model.

## Schema

`Schema = { Kind: SchemaKind; Description: string option }` with kinds `Any`, `Null`, `Boolean`,
`Integer`/`Number` (with bounds), `String` (lengths, ECMAScript `Pattern`, `Format`), `Const`, `Enum`
(strings), `Array` (item counts), `Tuple`, `Map` (string keys), `Object` (fields with `Presence`
`Required | Optional | Defaulted`, and an unknown-field policy), `Union` (a discriminator field and
cases with their fields), `AnyOf`, `Nullable`, `Ref` and `Named`.

- **Optional versus nullable:** an optional field may be absent (TypeScript `name?: T`); a nullable
  value may be `null` (`T | null`).
- **Names:** `Named` defines a type once (an identifier, usable as a TypeScript name); `Ref` refers to
  it from inside its own body (recursion). Two different bodies with one name are an error.
- **Descriptions** document a node. On a `Named` node the description is about that use; the type's own
  documentation is the body's description, so describe a codec before naming it.

`Schema.toJsonSchema` emits draft 2020-12 for model tool definitions: a named root is inlined and refers
to itself as `"$ref": "#"`; other named types go to `$defs`; unions become `anyOf` of objects whose tag
is a `const`; nullable becomes `anyOf` with `{"type": "null"}`; strict objects get
`"additionalProperties": false`; defaults become `default`. Provider-specific dialects (for example
OpenAI strict mode, where every field is required and optional ones are nullable) are transformations
for the model adapters (T118 and later).

`Schema.render` gives a TypeScript-like expression (`{ path: string; line?: integer }`) and
`Schema.typeName` a short name for messages and previews (`Match[]`, `string | null`).

## Codecs

```fsharp
type Codec<'T> = { Schema: Schema; Encode: 'T -> Json
                   Decode: Json -> Validation<'T, Issue>; Preview: Limits -> 'T -> Preview }
```

| Kind | Combinators |
|---|---|
| Scalars | `unit` (`null`), `bool`, `string`, `int`, `int64`, `float`, `decimal`, `number` (exact `JsonNumber`), `json` (any value), `literal`, `enum` |
| Collections | `list`, `array`, `option` (nullable), `stringMap`, `keyValues` (ordered), `tuple2`, `tuple3` |
| Records | `record { let! … and! … return … }` with `field`, `optionalField`, `defaultField`, `fieldOf` |
| Unions | `union`, `case0` … `case4`, `caseOf`, `buildUnion`, `describeCase` |
| Refinements | `refine`, `minLength`, `maxLength`, `nonEmpty`, `pattern`, `format`, `between`, `atLeast`, `atMost`, `minItems`, `maxItems` |
| Shape | `map`, `tryMap`, `named`, `describe`, `recursive`, `strict`, `withPreview`, `create` |
| Use | `encode`, `decode`, `preview`, `encodeText`, `decodeText` |

```fsharp
let span =
    Codec.record {
        let! start = Codec.field "start" (fun s -> s.Start) Codec.int
        and! finish = Codec.field "end" (fun s -> s.End) Codec.int
        return { Start = start; End = finish } }
    |> Codec.describe "A range of lines, end exclusive."
    |> Codec.named "Span"

let turnResult =
    Codec.union "kind" (fun finished waiting value ->
        match value with
        | Finished summary -> finished summary
        | Waiting(jobs, note) -> waiting jobs note)
    |> Codec.case1 "finished" Finished (Field.required "summary" Codec.string)
    |> Codec.case2 "waiting" (fun jobs note -> Waiting(jobs, note))
        (Field.required "jobs" (Codec.list Codec.string)) (Field.required "note" Codec.string)
    |> Codec.buildUnion
```

Rules and choices:

- **JSON naming:** fields and tags are camelCase, as in the TypeScript SDK (`kind`, `schemaVersion`).
- **Records** write fields in declaration order and ignore unknown members when reading, so readers stay
  compatible with newer writers. `Codec.strict` reports unknown members instead (model input, where a
  misspelt optional field must not pass silently); apply it before `named`, and inside `recursive` to the
  body.
- **Native AOT:** `Codec.record` merges up to 12 fields into one flat tuple (`MergeSources3` …
  `MergeSources12`) and passes decoded values between fields in type-erased slots. A plain applicative
  builder nests one tuple per field, and ILC rejects generic instantiations nested deeper than 4 levels
  inside FSharp.Core closures (IL3054): the AOT publish failed at 7 fields.
- **Optional fields** are left out when `None`; reading accepts absent or `null`. Defaulted fields are
  always written. Do not nest `option`s, or combine `Field.optional` with `Codec.option`.
- **Unions** are objects tagged by a discriminator field (`kind`), with each case's fields next to the
  tag. The match function given to `Codec.union` gets one encoder per case, in the order the cases are
  added, which keeps encoding exhaustive. Cases with identical payload types can be swapped by mistake:
  round-trip tests catch it. `caseOf` flattens a record codec's fields into the case.
- **String enums** (`Codec.enum`) are for unions without payloads that should read as plain strings.
- **Numbers:** `int` and `int64` accept any number with an integral value in range (`3.0`, `1e3`), and
  are exact. JavaScript numbers are exact only up to 2^53. `float` writes the shortest round-trip text;
  NaN and infinities, which JSON lacks, are the strings `"NaN"`, `"Infinity"` and `"-Infinity"` (as in
  the protobuf JSON mapping). `decimal` keeps its scale (`1.10`).
- **Refinements** check after decoding and add what they can express to the schema (lengths count code
  points, as JSON Schema does; patterns are ECMAScript regular expressions, unanchored like JSON
  Schema's `pattern`). Messages say what was expected and what was found.
- **Defects are exceptions** at construction time: duplicate field names or tags, a field named like the
  discriminator, invalid type names, conflicting named types, and encoding a value missing from an
  `enum`.

## Previews

`Limits = { MaxChars; MaxItems; MaxDepth; MaxStringChars }` (defaults 1000, 10, 3, 120; their values
are variants). `Preview = { Text; Truncated }`.

Guarantees, checked by property tests for every combinator and for arbitrary JSON:

- `Text.Length <= MaxChars` (negative limits count as zero), and the text is well-formed UTF-16: cuts
  never split a surrogate pair, and strings are JSON-escaped;
- if `Truncated` is false, the text is the complete rendering;
- work is bounded by the limits, not by the size of the value: only shown elements are rendered.

Rendering, type-aware through the codecs:

| Value | Preview |
|---|---|
| string | `"text"`, cut: `"first chars…"(len=5000)` |
| number, boolean, null | as JSON, shown whole or replaced by `…` |
| list | `[1, 2, 3, …+97]`; collapsed: `Match[](len=37)` |
| record | `(path="a.fs", line=3)`; named: `Match(path="a.fs", line=3, …)`; collapsed: `Match(…)` |
| union case | `done(summary="…", verify="dotnet test")`, `cancelled` |
| map, JSON object | `{"a": 1, "b": 2, …+5}`; collapsed: `{…}(len=8)` |

Containers deeper than `MaxDepth` collapse. When the text would exceed `MaxChars`, whole elements are
dropped first (with a `…+N` count), then strings are cut. `Codec.withPreview` replaces a codec's
preview with a domain summary such as `Match[](len=37, files=14, …)` (spec §6.10) and clamps it.

## Fable

The shared code avoids .NET-only APIs. The few platform calls are isolated: number text conversions
in `JsonNumber` (`Double.Parse`, `Decimal.TryParse`, `ToString("R")`), `Regex` with
`RegexOptions.ECMAScript` in `Codec.pattern`, and `String.CompareOrdinal`. A Fable build is not in CI
yet; when the web client needs one (T508), shim those with `#if FABLE_COMPILER`.

## Testing

- `dotnet test --solution Fx.slnx` runs the suite. `PROPERTY_TESTS=10000` runs more cases per property.
- JSON Schema output is checked against `tests/Fx.Contracts.Tests/golden/*.json`. After an intended
  change, run the tests with `UPDATE_GOLDEN=1` and review the diff.
- Properties: write-read round trips (compact and indented), canonical idempotence and member-order
  independence, exact number normalization, codec round trips (records, unions, recursive types,
  collections, floats with NaN), error accumulation, and preview bounds.
