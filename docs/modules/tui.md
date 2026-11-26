# Module spec: the TUI (`src/Fx.Tui`, `src/Fx.Tui.Live`, `fx`, `fx attach`)

Spec: §3 (clients; embedded mode: host, envd and TUI in one process), §17 (the client protocol),
§18 (unsandboxed is a persistent warning), §19.1–19.5 (a turn in the TUI, the grants view, sessions
that persist across disconnects), §20 (an Elm-style F# TUI). Task: T130.
Code: [`src/Fx.Tui`](../../src/Fx.Tui) (the pure core), [`src/Fx.Tui.Live`](../../src/Fx.Tui.Live)
(the terminal, the client, the composition roots); `fx` and `fx attach` in [`src/Fx.Cli`](../../src/Fx.Cli).
Tests: `tests/Fx.Tui.Tests`, `tests/Fx.Tui.Live.Tests`, the `tui-*` scenarios of the JIT-vs-native diff.

`fx` with no command starts a host for the current directory in its own process and the TUI on the
terminal it was started from. The TUI is a client of the client protocol like any other: in process
in embedded mode (`HostClient.embedded`), over the local listener with `fx attach` (the host `fx
serve` runs). Without a terminal (input or output redirected) `fx` prints the usage, as before.

## Architecture

Elm-style (spec §20): a model, messages, a pure `update` that gives the next model and the commands
to send, and a pure `view` that draws the model as a frame.

| Piece | Where |
|---|---|
| Columns of text: grapheme clusters, wide and zero-width characters, cutting, wrapping (`Text`) | `Fx.Tui/Text.fs` |
| Styles, spans, rows, frames (`Ui`) | `Fx.Tui/Ui.fs` |
| VT output: entering and leaving, SGR, the difference between two frames (`Vt`) | `Fx.Tui/Vt.fs` |
| A small terminal emulator for tests and the fake (`VtScreen`) | `Fx.Tui/VtScreen.fs` |
| Key decoding: CSI, SS3, CSI u, modifyOtherKeys, Alt, bracketed paste (`Keys`) | `Fx.Tui/Keys.fs` |
| The prompt editor (`Editor`) | `Fx.Tui/Editor.fs` |
| The session as a client sees it: state and the merged log (`Timeline`) | `Fx.Tui/Timeline.fs` |
| Model, messages, commands, cards, `update` (`App`) | `Fx.Tui/App.fs` |
| The screen (`View`) | `Fx.Tui/View.fs` |
| The terminal capability (`Tty`, `ITty`) and its fake (`FakeTty`) | `Fx.Tui/Tty.fs` |
| The loop (`Runtime`, `Inbox`) | `Fx.Tui/Runtime.fs` |
| The real terminal (`LocalTty`) and its P/Invokes | `Fx.Tui.Live/LocalTty.fs`, `Native/` |
| The client (`TuiClient`) and the composition roots (`TuiMain`) | `Fx.Tui.Live/TuiClient.fs`, `TuiMain.fs` |

- **The loop** (`Runtime.run`) folds messages from an `Inbox` (keys, ticks, the host's
  notifications, commands' outcomes), draws after each batch, and writes only the rows that changed
  (`Vt.diff`, in a synchronized update). Commands run one at a time in order on a worker; those sent
  before quitting go out before it returns. Keys come from the terminal through `Keys.decode`; a lone
  `ESC` waits 40 ms for the rest of a sequence. Ticks (100 ms while something moves, else 500 ms)
  move the clock on and notice resizes.
- **The model folds the protocol's events.** The session's state is the session machine's own
  `evolve` over `session/subscribe`'s feed (a snapshot replaces what came before); the transcript is
  the session's events and its context events merged as `ContextLog.merge` merges them (a context
  event after the session event its cause names). Events arrive once: a replay after a reconnect
  changes nothing. Pending grant requests are `grants/pending` and then `grants/subscribe`'s events,
  for this session or the host (a session's install asks before the session exists).
- **Commands are data** (`Cmd`): prompt (steer or queue), interrupt, answer a request, answer a grant,
  redraw, quit. Their idempotency keys start with the run's prefix (`tui/<id>`), so a key is one
  intent: prompts are numbered, an interrupt is keyed by its turn, an answer by its request.
- **The terminal is a capability** (`Tty`: read, write, size, the token last). `LocalTty` is the real
  one: raw mode with termios (`cfmakeraw` on an opaque copy of the modes) on Linux and macOS; on
  Windows virtual terminal input and processing and UTF-8 code pages on the console, input read as
  key records (`ReadConsoleInputW`) and output with `WriteConsoleW`. A reader thread polls (100 ms)
  so it stops when the terminal is given back. The terminal is given back (`Vt.leave`, the saved modes)
  on quit, on the process's exit and on an unhandled exception. While the TUI draws, .NET's console
  writers write nowhere, and nothing else reads standard input (the host's processes have their own).
- `FakeTty` is the deterministic one: input the test types, output on a `VtScreen`.

## A turn on the screen (spec §19.2)

```
 fx · …/src/billing · env: default (local) · sandbox: none · anthropic/claude-sonnet-5-5 · session …  ● idle
 ! runs unsandboxed: commands can read and change everything the user can
› replace deprecated Http.retry with RetryPolicy in billing
  I'll look at the call sites first.
  ◇ Read     src/Billing/Client.fs                                                         ✓ 0.2s
  ◆ Edit     src/Billing/Client.fs                                                         ✓ 0.3s
  ◇ Run      dotnet test tests/Billing                                                     ✗ 48s
      exit 1
  ◇ Cell     rerun the tests                                                               ✓ 46s
  ▸ 1 cell (3 lines)                                                             [Ctrl+O expand]

  ✓ Done  Migrated 37 call sites to RetryPolicy.
    Evidence  dotnet test tests/Billing → 214 passed
    Verify    dotnet test tests/Billing
    1m40s
                                                  Enter send · Alt+Enter newline · Ctrl+O detail · Ctrl+C×2 exit
› _
```

- **Header**: the product, the workspace, the environment (name and provider), the sandbox driver
  that confines it (`env/get`'s `sandbox`: `bubblewrap`, `seatbelt`, `none`), the main model, the
  session, and its state (idle, the running turn, held, needs input, closed).
- **Banner**: the environment's warnings (`env/get`), every one on every screen while it exists:
  "runs unsandboxed" on Windows (no sandbox in v1) and wherever no sandbox driver confines it
  (`Envs.warnings`, T113).
- **Transcript**: each turn's prompts (`›`), what the model wrote, one activity line per action
  (`◇` reads and runs, `◆` changes; `Cell` with its `// intent:` line, `Read`, `Edit`, `Run`), its
  status (a spinner and the time while it runs, `✓`/`✗` and how long it took), the first line of an
  error under a failed one, steering delivered at a step (`↳`), a compaction (`◇ Compact`: the earlier
  conversation is summarized; the summary in verbose), requests asked and settled, and the
  result card from the turn's `TurnEnd` (`✓ Done` with evidence and how to verify; `? Needs input`
  with the questions; `… Waiting`; `⏹ Interrupted`; `✗ Failed`; `✗ Abandoned`). The cells' code is
  folded into one line (`▸ N cells (M lines)`).
- **Streaming**: what the model writes in the running turn shows (dimmed) as it arrives
  (`session/streaming`), and the status line says when it writes a cell; the recorded reply replaces it
  (a compaction's summary streams too, and its recorded collapse replaces it).
- **Verbosity** (`Ctrl+O` cycles): *quiet* (prompts, activity lines, result cards), *normal* (also what
  the model wrote and errors; the default), *verbose* (also every cell's code and what each action
  returned, and a compaction's summary).
- **Pending inputs**: steered prompts waiting for the next step and queued prompts, under the
  transcript, until a turn takes them.
- **Status line**: what happens (starting, the running action or "thinking"/"writing", a notice), and
  the keys that apply.

## Keys

| Key | Does |
|---|---|
| `Enter` | sends the prompt: **steered** into the running turn (delivered at its next step), or a new turn when idle; on the agent's question it is the answer |
| `Tab` | sends the prompt **queued**: its own turn after the work in progress |
| `Alt+Enter`, `Shift+Enter`, `Ctrl+Enter`, `Ctrl+J` | a new line in the prompt (Shift and Ctrl only where the terminal reports them) |
| `Esc` | interrupts the running turn (the control path) |
| `Ctrl+C` | clears the prompt, else interrupts the running turn; pressed again within 3 s, exits |
| `Ctrl+D` | exits when the prompt is empty |
| `↑` `↓` | moves between the prompt's lines; past the first or last, goes through the prompts sent |
| `←` `→` `Home` `End`, `Ctrl+A/E/B/F`, `Ctrl/Alt+←/→`, `Alt+B/F` | moves the cursor |
| `Backspace` `Delete`, `Ctrl+W`, `Alt+Backspace`, `Ctrl+U`, `Ctrl+K` | deletes |
| `PgUp` `PgDn` | scrolls the transcript |
| `Ctrl+O` | cycles the detail (quiet, normal, verbose) |
| `Ctrl+L` | draws everything again |

Steering by default matches spec §5 (a prompt reaches the agent at its next step boundary; with no
turn running it starts one); queueing is the deliberate second key.

## Cards: what the person is asked (spec §19.4)

The first open card has the keyboard; `(1 of N)` says more wait (batched, oldest first). Permissions
come first (a tool call waits on them), then the session's open requests the TUI can answer
(`initialize` offers `confirm`, `choice`, `agent.question`, `decision.outcomeUnknown`); requests only
another client can answer are listed as waiting.

| Card | Shows | Answers |
|---|---|---|
| Permission (`grants/pending`, this session's or the host's: an install, T127b's shell commands, writes and connections) | summary, action and resource, evidence (up to six: which turn and call asked, the command line, …) | `[y]` once, `[s]` this session, `[r]` this repo, `[a]` always on this host (only the scopes the request offers; the keys `fx run` uses, `GrantPrompts`), `[n]` deny once |
| Decision (`decision.outcomeUnknown`, crash recovery) | the question, the call (its code), the end of what it printed, the selected option's detail | `[c]`ompleted, `[f]`ailed, `[r]`erun, `[i]`nspect (an option's first letter, or its number) |
| Confirm | the message | `[y]` `[n]` |
| Choice | the question | `[1]`…`[n]` |
| Question (`agent.question`) | the question | the prompt's text and `Enter` |

`←` `→` (or `Tab`, `Shift+Tab`) select an option and `Enter` answers with it. An answer being sent
shows as sending and is not sent twice; one the host refuses can be given again.

## The client protocol

The TUI uses `session/create` (or `session/list` to resume), `env/get`, `session/subscribe`,
`session/context`, `session/prompt`, `session/interrupt`, `session/answer`, `grants/pending`,
`grants/subscribe` and `grants/answer` (host.md). T130 added:

| Method | Params → result | Least role |
|---|---|---|
| `session/context` | `{session, after}` → runs until cancelled, sending `session/contextEvent` `{session, item}` (a context event with its envelope) after `after`, then live, and `session/streaming` `{session, turn, fresh, text, tool?}` as the model writes | viewer |

Streaming is not durable: the host taps the model streams of running turns (`Streams.runner` around
CodeAct's runner, `ChatStream.tap`: text deltas and tool calls started) and sends them to the
session's `session/context` subscribers; the complete reply arrives as its context event.

## `fx`, `fx attach`

```
fx [--session <id> | --continue] [--title <text>] [--model <provider/model>]
   [--surface cells-only|hybrid] [--variant <slot>=<value>]...
fx attach [--session <id> | --continue]
```

- `fx` starts the host (its errors are printed before the TUI starts), then a new session; `--session`
  resumes one, `--continue` the workspace's newest. A new session creates the workspace's environment,
  which may first ask for permissions (installing Bun): they show as cards while the TUI says it is
  starting.
- An untrusted project: the TUI says that its config is not read and that `fx trust` trusts it (the
  host reads project config only at its start).
- The host's warnings (an extension that failed to load, …) are shown at the top of the transcript.
- On exit the terminal is given back and the session's id is printed with how to continue it; the
  host stops (turns are interrupted; they are recovered at the next start).
- `fx attach` finds the listener in the data directory (`listener.json` and its token) and shows a
  session of that host; sessions persist across disconnects (spec §19.5): the TUI subscribes from the
  start of the session's log and the context log, so what happened while away is there.

## Native AOT

`tui-view` (every scripted screen at two sizes, widths of sample text, wrapping, key decoding and a
frame difference), `tui-loop` (the loop on a fake terminal: a permission given, an interrupt, Ctrl+C
twice, the commands in order) and `tui-protocol` (the new notices' JSON, the least role, and the
terminal check's P/Invoke without a terminal).

## Testing

- `Fx.Tui.Tests`: text widths (CJK, emoji, combining marks), wrapping, rows; key decoding (sequences
  cut between reads, pastes); the editor over grapheme clusters; frames on a `VtScreen` and the frame
  difference; golden renders (`golden/`, `UPDATE_GOLDEN=1`) of scripted sessions (made by the session
  machine itself) at 80×24, 40×20 and 24×14 (starting, a finished turn, a running turn with steering
  and a queued prompt, the agent's question, permissions, a decision, wide characters and a failure, a
  compacted turn, verbose); keys and cards through `update`; streaming; snapshots; resizes; the loop on a `FakeTty`.
- `Fx.Tui.Live.Tests`: the TUI on an embedded host through the client protocol on a `FakeTty` (a
  session started, a prompt to its result card, a permission given on the card, Ctrl+C twice); with the
  bun-ts kernel (network; required in CI) the first install's permission given on the card and a turn
  of cells to `✓ Done`; and the `fx` binary on a PTY (ConPTY on Windows): keys, a resize, Ctrl+C twice,
  exit code 0 and the terminal given back.

## Left to later tasks

| What | Task |
|---|---|
| Changes and `[d]iff [u]ndo [r]eview` on the result card, activity lines from cells' host calls (`Refs`, `Read` inside cells) | T205 |
| `/` commands (`/context`, `/compact`, `/env`, `/model`, `/goal`), the environment picker | T215, later |
| The grants view (active grants, revoke) and the decisions panel | T303 |
| Reconnecting `fx attach` after the listener restarts; notifications outside the terminal | T507 |
| Mouse, themes and the TUI style variant | later |
