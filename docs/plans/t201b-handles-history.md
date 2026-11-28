# T201b: handles and history read-back

Decided with the person on 2026-11-29 (option a of three). T201b makes handles usable and gives the
model its collapsed history back; handles nested inside data move to T213 (Agents), the first task
whose results need them.

## What T201b found built

`Value.Handle`, `HandleTable` (unforgeable, session-scoped), kernels holding handles opaquely (the
conformance suite checks it), but no binding returns one: every `HostBinding` answers inline JSON.
Compaction keeps the originals of a collapsed range in the context log (`ContextEvent.Collapsed`).

## Steps

1. `Originals` (Fx.Prompt, pure): the collapsed ranges of a transcript (`r1`, `r2`, … in the order
   applied) and their originals rendered as text, content exclusions withheld as the render does.
2. `fx.history.list()` and `fx.history.read(range, {from, maxChars})`: bounded, pageable by character
   offset (as `blobs.read`), from the session's log as the turn loads it.
3. Handles as binding results: the hands get `IIds` (journaled and replayed like the clock: a handle's
   id reaches the kernel in the host's reply, which replay checks byte for byte); `HostBinding` gains
   handle arguments and results over a session's `HandleTable`; `fx.history.open(id)` gives a
   `HistoryRange` handle that `history.read` takes too.
4. Handles in receipts (`[handle: HistoryRange] preview`, never the id) and in the journal: kept by
   reference and restored into a new kernel while the host's table still has them; after a host
   restart they are reported gone, with why.
5. Docs, tasks.md (T201b done; handles inside data to T213, with the wire design: an inline value with
   the paths of the references it holds, so look-alike data never becomes one).

## Outcome

All five steps landed in the lane. Adding the history bindings changes the frame, so the golden replay
session was re-recorded (as for T140, T143 and T201a).
