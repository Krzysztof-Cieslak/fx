# T201c: context pins

The last part of T201 (spec §6.6 "deliberate prompt inclusion"). Found built: the `Pin`/`PinSpec`
context events, their fold into `Transcript.Pins` and the `pins` view, which renders live file pins from
`Live.Files`. Missing: anything that records pins, anything that fills `Live.Files`, and a way for
people to see them.

## Decisions

- **Asks, recorded by the turn.** Bindings run in the kernel's bridge, not where the context log is
  written, so `fx.context.*` only ask (`ContextAsks`, as `history.compact` does) and the turn records
  them right after the cell's receipt: the log's order is what the model saw.
- **No `IContext` capability.** Pins are context events of the CodeAct strategy; a capability waits for a
  second strategy that needs one (eff.md).
- **Live file pins are read through the hands** before each call, as `fs.read` reads them (exclusions,
  the sandbox's policy), so replay answers them from the journal. Only workspace files can be pinned,
  and a pin shows at most `codeact.outputChars` characters (it is sent with every call).
- **`pinFile` takes a name** (`pinFile(name, path, {lines})`, not the spec's `pinFile(path, range)`): pins
  are unpinned and replaced by name. The spec follows.
- **`/context` in the TUI** is a notice: how full the context was at the last call and each pin with its
  cost; pins and unpins show as activity lines.
