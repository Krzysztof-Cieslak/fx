# T201a: blobs and output overflow

T201 was split into T201a (blobs and output overflow), T201b (handles and history read-back) and
T201c (context pins), one lane each, landed as a stack in that order. The person asked for the split
and to work through them one by one.

## What T201 found built

- The `Value` wire type (`Inline | Blob | Handle`), `HandleTable` (unforgeable, session-scoped ids),
  declared-type decoding (T116).
- `Out[id]`, kept values in the journal (inline, or in the host's blob store beyond
  `values.inlineMaxBytes`), `recall` into new kernels and `fx.recall(id)` by cell or call id (T128).

## The problem T201a solves

Everything a turn sees is bounded, and what is cut is gone: `shell.run` keeps 16 000 characters of
each stream, a cell's printed output `outputChars`, and a receipt cuts a string value in the middle
(`… [2566 chars omitted] …`). The model then runs the command again, or prints the value again in
pieces. Spec §6.6: "overflow → blob with a grep-able path".

## Decisions

1. **Where overflow goes: the environment's blob store** (envd's `FileBlobStore`), not the host's:
   the content belongs to the environment, and the model reads it there with the tools it already
   uses (`grep`, `sed -n`, `fs.read` with lines). A blob's path is a pure function of the layout:
   `<EnvLayout.EnvdState>/blobs/sha256/<2 hex>/<62 hex>` (`EnvLayout.blobPath`). A store with no files
   (fakes) has no path; the hash still names it.
2. **What names it: an `Overflow` record** `{hash, bytes, path?}` next to the bounded text, present
   only when something was cut: `RunResult.stdoutOverflow` / `stderrOverflow`, the cell's output, a
   receipt's value. Receipts say where the whole text is in one line after the cut text.
3. **Reading it needs no grant**: the blob directory is a read root of the workplace
   (`Workplace.Readable`): `fs.read` and the path arguments of commands under it count as in the
   workspace for reads; writes there stay writes outside the workspace.
4. **Redaction holds**: overflow is stored from the text the hands already redacted (T138), so a blob
   never holds a credential the receipt would not show.
5. **`fx.blobs.read(hash, {from, maxChars})`** reads a blob's text from a character offset, bounded,
   for kernels whose environment has no shell worth using and for remote environments later.
6. **Not in T201a**: values that live as blobs in kernels (`Value.Blob` from `value/encode`) and
   host-call arguments as blobs only pay off when the environment is remote (the content need not
   travel to the host): they move to T501 (SSH provider), the first remote environment. Overflow
   records are plain data, so kept values that hold them are journaled as they are.

## Steps (commits in the lane)

1. `EnvLayout.blobPath`; the hands get `IBlobs`; `shell.run` stores a cut stream whole and returns
   its `Overflow`; receipts name the path.
2. `Workplace.Readable`: reads under the blob directory need no grant.
3. Receipt values and a cell's printed output beyond their bounds go to the blob store too.
4. `fx.blobs.read`.
5. Docs (spec §6.6, interpreters.md, kernel-protocol.md, codeact.md), tasks.md: T201a done.
