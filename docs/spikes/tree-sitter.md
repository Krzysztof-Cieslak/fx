# Spike: tree-sitter under Native AOT (T008)

Spec: §6.7 (reading and navigation: outlines, symbols), §20 (tree-sitter in the stack). It was
meant to feed T202 (`fx.repo`), T203b (outlines of large files in previews) and T204b (the edit guard).

## Verdict

**It works, and fx does not adopt it now.** tree-sitter builds from pinned sources, links statically
into a Native AOT binary and parses F#, F# signatures, C#, Python, TypeScript and TSX identically on
the JIT and natively (`linux-x64`; macOS and Windows were not run). An outline prototype gives the
outlines §6.7 sketches. But nothing measured asks for it, and it costs a lot:

- **No evidence it helps.** It came into the spec with the first draft, after NOOA's `RepoTools`.
  NOOA treats it as optional (`nooa-cli[ast]`, "falls back to regex"); its paper (arXiv 2607.20709)
  mentions it once, as one of BenchAgent's tools, with no ablation; its one published finding on tools
  is that agents grep and eyeball text (about 12 searches a session, structured matches used 0 times,
  `src/nooa/tools/shell_tools.py`). fx's dogfooding found problems with output, edits, search
  matches and jobs (docs/plans/t139-cells-and-bridge.md), all fixed since; none with missing outlines
  or symbols.
- **No task needs it.** Of the tasks after it, only T202's outlines and symbols and T203b's outlines
  of large files used it; T204b's guard works on seen ranges and grep anchors.
- **It is large.** The grammars are about 25 MB of parse tables, two thirds of today's 37 MB `fx`, and
  people's projects use languages far beyond these four ("Size").

So T202 became `fx.repo.grep` and `fx.repo.files`, T203b previews large files without outlines, and
language-server navigation moved to T607. If measurements later show agents struggling to find
definitions or skim large files (T219's capability tests, `bun scripts/cells.ts` on sessions), start
from "If tree-sitter comes back" below. The code is kept, unmerged, on the branch
[`spike/t008-tree-sitter`](https://github.com/Krzysztof-Cieslak/fx/tree/spike/t008-tree-sitter)
(commit `0d748254`); the paths below are on that branch.

Evidence on that branch: `tests/Fx.TreeSitter.Tests` (30 tests: the pin, every grammar's ABI,
positions in UTF-8 bytes, CRLF, empty and invalid input, error nodes, cancellation, disposed trees, 64
parallel parses, query errors and matches, golden outlines of a sample per grammar), and six
`tree-sitter-*` scenarios identical on the JIT and natively; the native binary carries the library,
with none published next to it.

## What was built

On the branch `spike/t008-tree-sitter`:

| Path | What |
|---|---|
| `native/tree-sitter/TreeSitter.props` | The pin: the core and four grammar archives (URL, SHA-256, `tar --strip-components`), the six grammars (archive, source directory, language function), the library name |
| `native/tree-sitter/library.c` | The core as one translation unit (`#include "lib.c"`), then a shim: a cancellable parse, the pin string, the ABI range |
| `native/tree-sitter/TreeSitter.targets` | Download and verify each archive, compile the core and every grammar for the RID, archive and link them into one static and one shared library, cache; wire into the binding and into Native AOT publishes |
| `src/Fx.TreeSitter` | The binding: `Native` (imports), `Library`, `Language`, `Syntax` (trees, nodes, parse), `Query`, `Queries` and `Outline` (the prototype) |
| `tests/Fx.TreeSitter.Tests` | Unit tests, and `Samples/` (a source per grammar with its expected outline) |
| `tests/aot/Fx.Aot.Scenarios/Scenarios/TreeSitter.fs` | The `tree-sitter-*` scenarios (they parse the samples, copied next to the binary) |

| Scenario | Proves |
|---|---|
| `tree-sitter-library` | The library is built from the pinned archives; the core's ABI range; each grammar's ABI and name |
| `tree-sitter-syntax` | Each sample: root kind, node and child counts, errors, positions; UTF-8 byte columns; CRLF; error nodes of a broken file; empty sources; a disposed tree throws |
| `tree-sitter-queries` | Syntax, node-type and field errors with their offsets; capture names; every outline query compiles; matches and their captures |
| `tree-sitter-outline` | The outline of each sample |
| `tree-sitter-cancel` | A token cancelled before, or 100 ms into a ~10 s parse, stops it; the next parse works |
| `tree-sitter-threads` | 48 parses and outlines on the thread pool agree |

## Pins

| Archive | Version | ABI | Source |
|---|---|---|---|
| tree-sitter (core) | 0.27.2 | accepts 13–15 | GitHub tag archive (no release tarball of sources) |
| tree-sitter-fsharp (`fsharp`, `fsharp_signature`) | 0.3.13 (ionide) | 15 | release tarball |
| tree-sitter-c-sharp | 0.24.0 | 15 | release tarball |
| tree-sitter-python | 0.25.0 | 15 | release tarball |
| tree-sitter-typescript (`typescript`, `tsx`) | 0.23.2 | 14 | GitHub tag archive: its release tarball is `.tar.xz`, which Windows' `tar` may not read |

All are MIT. Release tarballs are uploaded files; tag archives are generated by GitHub, stable in
practice but not promised, so a changed digest there means "regenerated", not necessarily "tampered":
the build fails either way and deletes the download. Grammars commit their generated `parser.c`,
so no grammar generator (Node, the tree-sitter CLI) is needed.

Bumping: take the release, record its SHA-256 in `TreeSitter.props`; a grammar's `parser.c` states its
ABI (`LANGUAGE_VERSION`), which the core must accept; the tests check every grammar loads and that the
loaded library's pin string matches the props.

## How it is built

Like SQLite (docs/spikes/sqlite.md "How SQLite is built"), with these differences:

- **Several archives.** Each is downloaded once, verified and extracted with `tar` into
  `artifacts/native/tree-sitter/<archive>/`. On Windows the targets call
  `%SystemRoot%\System32\tar.exe` (bsdtar) explicitly: Git's GNU tar, often first on `PATH`, reads
  `C:` as a remote host.
- **One object per grammar file.** Each `parser.c` defines static tables with the same names, so
  grammars cannot share a translation unit; the core is one (`library.c` includes `lib.c`). Each
  grammar is compiled with `-I` its own `src/` (its own `tree_sitter/parser.h`).
- **Symbols.** The core is compiled with `-fvisibility=hidden` as upstream does (its API is marked
  visible in `api.h`); the grammars with `TREE_SITTER_HIDE_SYMBOLS`, which only stops their language
  function being `__declspec(dllexport)` on Windows. Not defining it for the core matters: on Linux and
  macOS it also turns off `api.h`'s visibility, and the shared library would export no API. The
  Windows DLL's exports come from a `.def` file the targets write from the core's own
  (`lib/src/tree_sitter.def`), the shim's functions and the grammars' functions, so each grammar is
  compiled once for both libraries.
- **The pin is checked at run time.** The targets write `pin.h` (the archive names) into the build;
  `fx_ts_pin()` returns it and `Library.ensurePinned` compares it with the binding's generated
  `Pinned.Archives`.
- **Build time.** About 22 s on this machine for everything, once per build key (the F# grammar's
  `parser.c` alone takes 8 s: 57 MB of C). Serial on purpose: compiling the large grammars in parallel
  takes gigabytes of memory.

## Size

| Object | Code and tables (MB) |
|---|---|
| core | 0.16 |
| F# | 11.97 |
| C# | 7.22 |
| F# signatures | 2.16 |
| TypeScript | 1.41 |
| TSX | 1.45 |
| Python | 0.46 |
| **Total** | **≈ 24.8** |

99% of a grammar is read-only parse tables (F#: 11.84 MB `.rodata`, 0.13 MB code), so `-Os` changes
nothing (11,966,036 vs 11,964,638 bytes for F#). For scale, `fx` itself is 37.5 MB on `linux-x64`.

How grammars would ship, if tree-sitter comes back:

1. **The core linked (about 0.2 MB), each grammar a component installed on demand** (§6.1): CI
   builds a shared library per grammar and target from the pinned archive and publishes it with its
   digest; a grammar catalog in `fx` (an `Archive` method per target) and fx's queries for it; the
   binding loads an installed grammar by path (`NativeLibrary.Load`, `GetExport` for
   `tree_sitter_<name>`) after an ABI check. Grammars use their own allocator unless built with
   `TREE_SITTER_REUSE_ALLOCATOR`, so they need nothing from the core. Install on the first parse of a
   language's file, with plain text and `rg` until then; the installer runs in `envd`, so grammars
   install where the files are. This is the recommended shape.
2. **Everything linked**: a ~62 MB `fx`, and still only the languages linked in.
3. **WebAssembly grammars**: a wasm runtime in the binary, and the tables no smaller.

## The binding

- **Blittable throughout.** `TSNode` (four `uint32` and two pointers) and `TSPoint` cross by value as
  plain structs; `TSQueryMatch` and `TSQueryCapture` are read through a pointer. C's `bool` is
  declared `byte`. `DisableRuntimeMarshalling` makes any signature that would need a stub fail on the
  JIT too. Structs by value work identically on the JIT and natively.
- **No callbacks into managed code.** tree-sitter 0.26 removed `ts_parser_set_timeout_micros` and the
  cancellation flag in favour of a progress callback. Rather than a managed function pointer (F# has
  no syntax for `UnmanagedCallersOnly` function pointers, and none of the repo's bindings needs one),
  the shim's `fx_ts_parse_utf8` passes a C callback that reads an `int32` flag in native memory;
  `Syntax.parse` sets it from the token's registration. tree-sitter checks it about every hundred
  steps: a 20 MB F# parse stops within milliseconds. A cancelled parser is reset in the shim.
- **Lifetimes.** Trees and queries are `SafeHandle`s; a `Node` is a struct holding its tree, and every
  read checks the tree is alive (`ObjectDisposedException`, never freed memory) and keeps it alive
  across the call. A parser is created per parse: `ts_parser_new` is cheap, and parsers are not
  thread-safe. Compiled queries are immutable and shared (the outline queries are compiled once per
  language); a query cursor is per call. Grammars (`TSLanguage*`) are static data.
- **Text is UTF-8 bytes.** The tree keeps the bytes it parsed; offsets and columns are in bytes, lines
  are one-based and end at `\n` (so CRLF counts the same). Invalid UTF-8 parses (it becomes U+FFFD in
  the lexer). Callers that show columns to people must convert them.
- **Predicates are not applied.** `#eq?`, `#match?` and friends are parsed by the core but evaluated by
  bindings; this one evaluates none, so fx's queries use none.
- **No I/O.** tree-sitter parses bytes it is given. The binding needs an `EFF008` entry for its
  imports, but nothing like SQLite's `EFF009`: callers parse files they have read themselves.
- **Embedded resources are reflection** for the analyzers (`AOT003`), so the queries are F# literals
  (`Queries.fs`) and the scenarios read the samples from files next to the binary.

## The outline prototype

`Outline.ofTree` runs a query per language whose patterns capture a definition as
`@definition.<kind>` and its name as `@name`, then nests the definitions by where they are.
`Outline.render` prints them as §6.7 sketches:

```
module Billing.Retry                                                            2–40
  type RetryConfig = { Attempts: int; Delay: TimeSpan }                         8
  type Outcome<'T> =                                                            10–12
  let execute (cfg: RetryConfig) (op: unit -> Task<'T>) : Task<Outcome<'T>> =   23–28
  type Clock() =                                                                30–33
    member _.Now = DateTimeOffset.UnixEpoch                                     31
  module Http =                                                                 35–40
    let retry n f =                                                             36–37
```

Findings:

- **The grammars' own `tags.scm` are not outlines.** They serve code navigation: C#'s has no
  properties, records, structs or enums; TypeScript's leaves classes and functions to JavaScript's
  file (another package); F# signatures have three patterns. So the outline queries are fx's own:
  5 to 27 patterns per language.
- **The header is the line the name is on**, trimmed: not the definition's first line, which is an
  attribute (`[<Sealed>]`, `[Serializable]`) or a decorator (`@dataclass`) as often as not.
- **One item per name.** Patterns overlap: an `export` and the declaration in it, a decorated
  definition and the definition, a `const` holding a function and the value pattern. Items with the
  same name node keep the outermost (so an export spans its `export` keyword).
- **Queries cannot alternate parents.** `([(file) (namespace)] (child))` is a sequence of siblings, not
  "a child of either"; module-level `let`s need one pattern per parent kind.
- **tree-sitter-fsharp puts a doc comment inside the definition before it**, so a definition's range
  would run to the next one's `///`. Items end at their last token that is not a comment.
- **Inline object types look like interfaces.** TypeScript's `property_signature` makes up both
  interface bodies and `{ a: string }` types; the query takes interface bodies only.
- **C# file-scoped namespaces** span their declaration line only; their types are not nested in them.

## Grammar quality on fx's own code

The repository's F# and TypeScript, parsed with the pinned grammars (JIT, this machine):

| Grammar | Files | Lines | MB | Files with errors | Error nodes | Parse | Outline items |
|---|---|---|---|---|---|---|---|
| F# | 806 | 230,458 | 9.43 | 10 (1.2%) | 18 | 3.2 s (3.0 MB/s) | 14,661 (0.7 s) |
| TypeScript | 77 | 15,749 | 0.58 | 1 (1.3%) | 1 | 56 ms (10 MB/s) | 2,509 |

What tree-sitter-fsharp 0.3.13 gets wrong in fx: a qualified interface in an object expression or
class (`interface Fx.Store.IBlobs with`), a primary constructor with `as self`, `[<EntryPoint>]` before
`let main`, some triple-quoted strings with JSON inside. TypeScript: `import("./codec.ts").Issue` as
a type. Errors stay local (tree-sitter recovers), so the rest of such a file still outlines; an
unclosed C# class becomes an `ERROR` node whose members are still found. For an outline this is good
enough; refs and symbols built on it should report a file's error count rather than hide it, and a
language server (T607) stays the precise answer.

## If tree-sitter comes back

- Measure first: how often agents look for definitions, skim large files or misread structure.
- Ship grammars as components ("Size", option 1); `fx` links the core only.
- Keep the binding's shape: parse with a token, outlines from fx's queries, a parser per parse and
  queries shared. Parse on the thread pool, never on a session's actor.
- Cap what is parsed (a size limit as a variant) and pass a token with a deadline: 3 MB/s for F#
  means a large generated file takes a second.
- Show the outline header as the name's line, and mark items in files with parse errors.
- Refs and symbols: start from the same queries and the grammars' `locals.scm`; incremental parsing
  (`ts_tree_edit`) is not needed for previews and was not tried.
- Consider NOOA's cheaper shape first: symbols and refs by regex per language, tree-sitter optional.
