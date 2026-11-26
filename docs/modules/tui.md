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
| Colours (16, the 256 of the xterm palette, 24-bit) and their lowering, styles, spans, rows, frames (`Ui`) | `Fx.Tui/Ui.fs` |
| What the terminal draws (`Terminal`), themes (`Theme`), the TUI's slots (`TuiSlots`) | `Fx.Tui/Theme.fs` |
| Markdown parsed and drawn as rows (`Markdown`), code highlighting (`Highlight`) | `Fx.Tui/Markdown.fs` |
| The product's name in blocks (`Wordmark`) | `Fx.Tui/Wordmark.fs` |
| Commands (`TuiCommand`, `Commands`) | `Fx.Tui/Commands.fs` |
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
 ! runs unsandboxed: commands can read and change everything the user can

┃ replace deprecated Http.retry with RetryPolicy in billing

  I'll look at the call sites first.
  ◇ Read     src/Billing/Client.fs                                        ✓ 0.2s
  ◆ Edit     src/Billing/Client.fs                                        ✓ 0.3s
  ◇ Run      dotnet test tests/Billing                                     ✗ 48s
      exit 1
  ◇ Cell     rerun the tests                                               ✓ 46s
  ▸ 1 cell (3 lines)                                             [Ctrl+O expand]

  ✓ Done  Migrated 37 call sites to RetryPolicy.
    Evidence  dotnet test tests/Billing → 214 passed
    Verify    dotnet test tests/Billing
  ▣ fx · anthropic/claude-sonnet-5-5 · 1m39s

┃ › _
┃ anthropic/claude-sonnet-5-5 · env: default (local) · sandbox: none
fx · …/src/billing · session …00000000    Enter send · Alt+Enter newline  ● idle
```

- **Start screen**: a session without turns (nothing running, queued or asked, no history a
  snapshot replaced, not stopped) shows the product's name in blocks (`Wordmark`, drawn from
  `Product.Name`, so a rename redraws it; plain text when the terminal is too small) over the prompt's
  box in the middle of the screen, with the workspace and its branch and `Ctrl+P commands` under it
  and the notes a person should read first (an untrusted project, the host's warnings). The palette
  takes the name's place while it is open; the first prompt turns the screen into the transcript.
- **Layout**: the warnings at the top, the transcript under them from the top of the screen (its end
  when it is longer than the room), then what is asked, the prompts waiting, what happens, the
  prompt's box and the footer at the bottom.
- **Panels**: prompts sent, the prompt being typed and the cards are raised blocks: a bar at their
  left in the theme's colour (the card's for a card) and, where the theme paints panels (`dark`,
  `light`), the panel's background to the screen's edge with a row of padding above and below.
- **Prompt box**: the prompt being typed, and under it its info line: the main model, the
  environment (name and provider) and the sandbox driver that confines it (`env/get`'s `sandbox`:
  `bubblewrap`, `seatbelt`, `none`).
- **Footer**: the product, the workspace with its branch (`env/get`'s `branch`) and the session on
  the left; the keys that apply (as many as fit beside the left side), whether the transcript is
  scrolled, what the session used (the context the last call held, its share of the window, and the
  cost: `73.4K (7%) · $2.11`, unless the sidebar shows it) and the session's state (idle, the
  running turn, held, needs input, closed) on the right.
- **Turn footer**: under each ended turn, `▣` with the product, the model its last call used, how
  long the turn took and how fast the model wrote (output tokens over the time its calls took).
- **Sidebar** (terminals 120 columns wide or more, 34 of them): the session's title (the one it was
  given, else its first prompt's first line), the context (tokens the last call held, the share of
  the window used), what the session cost, the model; the workspace and its branch at the bottom.
  What it shows comes from the replies' `used` (context events), so `fx attach` shows it too.
- **Banner**: the environment's warnings (`env/get`), every one on every screen while it exists:
  "runs unsandboxed" on Windows (no sandbox in v1) and wherever no sandbox driver confines it
  (`Envs.warnings`, T113).
- **Transcript**: each turn's prompts (panels), what the model wrote, one activity line per action
  (`◇` reads and runs, `◆` changes; `Cell` with its `// intent:` line, `Read`, `Edit`, `Run`), its
  status (a spinner and the time while it runs, `✓`/`✗` and how long it took), the first line of an
  error under a failed one, steering delivered at a step (`↳`), a compaction (`◇ Compact`: the earlier
  conversation is summarized; the summary in verbose), requests asked and settled, and the
  result card from the turn's `TurnEnd` (`✓ Done` with evidence and how to verify; `? Needs input`;
  `… Waiting`; `⏹ Interrupted`; `✗ Failed`; `✗ Abandoned`). The cells' code is folded into one line
  (`▸ N cells (M lines)`).
- **Markdown**: what the model wrote, questions, and a result card's summary, evidence and check
  are Markdown (`Markdown.render`): headings, paragraphs, **strong**, *emphasis*, ~~strike~~
  (muted), `code`, links (their address after the text when it differs), lists (nested, ordered),
  quotes, fenced code with keyword highlighting for F#, TypeScript and JavaScript, JSON and shells
  (line by line), rules and GitHub's tables (columns narrowed to fit, cells cut with `…`). What it
  does not follow shows as the text it is; an unclosed fence (a reply still streaming) runs to the
  end. Words wrap at spaces, across styles; a word longer than a row is cut; code is cut, never
  wrapped at spaces.
- **A question shows once**, where the agent asked it. The agent's question (`agent.question`)
  shows as its text, never its request's title; when it repeats what the reply just before it said
  (CodeAct asks with a reply's text), the reply is all that shows. `? Needs input` and the question
  card do not repeat it (the card shows it only when the transcript does not have it: earlier
  events replaced by a snapshot).
- **Drafts**: a reply without an action (no tool call, no receipt after it) that another reply of the
  same turn followed was a draft: CodeAct nudged the model on (`codeact.policy.textOnly = nudge`)
  and what came next replaced it. Drafts show only in verbose, dimmed.
- **Streaming**: what the model writes in the running turn shows (dimmed) as it arrives
  (`session/streaming`), and the status line says when it writes a cell; the recorded reply replaces it
  (a compaction's summary streams too, and its recorded collapse replaces it).
- **Verbosity** (`Ctrl+O` cycles): *quiet* (prompts, activity lines, result cards), *normal* (also what
  the model wrote and errors; the default), *verbose* (also every cell's code and what each action
  returned, a compaction's summary and drafts).
- **Pending inputs**: steered prompts waiting for the next step and queued prompts, under the
  transcript, until a turn takes them.
- **Status line**: what happens (starting, the running action or "thinking"/"writing", a notice);
  no row when nothing does.

## Colours and themes (spec §19.6)

Every style the view draws comes from a `Theme`: colours by what they mean (text, muted, accent,
success, warning, error, the cards', panels, Markdown and code). `tui.theme` (`TuiSlots.theme`, a
core slot the host registers) picks it:

| `tui.theme` | Draws |
|---|---|
| `auto` (default) | `light` when the terminal says its background is light (`COLORFGBG`), else `dark` |
| `dark`, `light` | 24-bit colours on a background the theme paints itself (every row filled), with raised panels |
| `ansi16` | the terminal's own 16 colours and background: nothing its theme does not map |

- **Depth.** How many colours the terminal shows comes from the process's variables
  (`ColorDepth.detect`): `NO_COLOR` (any non-empty value) turns colours off and keeps bold, dim,
  italics, underline and reverse; `COLORTERM=truecolor|24bit` and the terminals known to draw 24-bit
  colour (Windows Terminal, iTerm2, WezTerm, kitty, Ghostty, VS Code; the Windows console with VT
  processing) give 24 bits; a `TERM` with `256color` the palette; anything else 16.
- **Lowering.** The frame is lowered to the depth before it is drawn (`Frame.lower`): 24-bit colours
  to the palette's nearest (the cube's or the grays'), or to the nearest of the 16. The painted themes
  need 256 colours or more: below that every choice draws `ansi16`, so nothing depends on how a
  terminal's theme maps the 16.
- `fx` reads `tui.theme` from the host's resolution (host config, `--variant tui.theme=light`, a
  trusted project's config); `fx attach` uses the default (the serving host's resolution is not its
  own).

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
| `Ctrl+P` | opens the command palette (`Esc` or `Ctrl+P` closes it) |
| `/` at the start of the prompt | the commands it may name show above the prompt; `Tab` completes the first, `Enter` runs it |

Steering by default matches spec §5 (a prompt reaches the agent at its next step boundary; with no
turn running it starts one); queueing is the deliberate second key.

## Commands (spec §19.5)

Commands are values (`TuiCommand`: a name, what it does, the keys that also run it, what it takes
after its name, and its action, `Commands.all`), so later screens add their own. They run from the
palette (`Ctrl+P`: a fuzzy search over names and titles, `↑` `↓`, `Enter`) or as a prompt that starts
with `/` (`//` sends a prompt that starts with `/`); a `/` prompt never reaches the agent, and one
that names no command says so.

| Command | Does |
|---|---|
| `/new` | leaves the session shown for a new one in the workspace (`session/create`) |
| `/sessions` | picks another session of the host (`session/list`, newest first) and shows it |
| `/model provider/model` | sets `model.role.main` for the session from its next turn on (`session/overrides`) |
| `/detail` | cycles the detail (as `Ctrl+O`) |
| `/theme [auto\|dark\|light\|ansi16]` | changes the colours for this run (without a name: a picker) |
| `/exit` | leaves; the session stays |

Switching sessions ends the subscriptions of the one shown before the next starts (`TuiClient.run`),
and notices of any session but the one shown are dropped (`Following`): the model is the next
session's alone. Grants stay (they are the host's).

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
| Question (`agent.question`) | that the agent waits for an answer (the question itself only when the transcript does not show it) | the prompt's text and `Enter` |

`←` `→` (or `Tab`, `Shift+Tab`) select an option and `Enter` answers with it. An answer being sent
shows as sending and is not sent twice; one the host refuses can be given again.

## The client protocol

The TUI uses `session/create` (or `session/list` to resume), `env/get`, `session/subscribe`,
`session/context`, `session/prompt`, `session/interrupt`, `session/answer`, `grants/pending`,
`grants/subscribe`, `grants/answer`, and `session/overrides` for `/model` (host.md). T130 added:

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

- `Fx.Tui.Tests`: the start screen (the wordmark, the first prompt to the transcript, golden
  renders with and without the palette); commands (parsing, the palette's search, `Ctrl+P`, `/model`, `/theme`, `/new`
  and `/sessions`, completion, golden renders of the palette and of `/` suggestions); usage (a turn's speed, the footer's usage, the sidebar at 130 columns); Markdown (inlines, blocks, an unclosed fence, no row wider than asked at any
  width, highlighting, golden renders of a sample at 80, 40 and 24 columns and its styles in the dark
  theme); colour depths from variables, lowering, SGR codes, the theme a choice gives, and
  golden styles of a screen at every depth (`styles-*.txt`); text widths (CJK, emoji, combining marks), wrapping, rows; key decoding (sequences
  cut between reads, pastes); the editor over grapheme clusters; frames on a `VtScreen` and the frame
  difference; golden renders (`golden/`, `UPDATE_GOLDEN=1`) of scripted sessions (made by the session
  machine itself) at 80×24, 40×20 and 24×14 (starting, a finished turn, a running turn with steering
  and a queued prompt, the agent's question after a draft, the loop guard's question, permissions, a
  decision, wide characters and a failure, a compacted turn, verbose); questions shown once and
  drafts; keys and cards through `update`; streaming; snapshots; resizes; the loop on a `FakeTty`.
- `Fx.Tui.Live.Tests`: the TUI on an embedded host through the client protocol on a `FakeTty` (a
  session started, a prompt to its result card, a permission given on the card, `/new` and `/sessions` back to it, Ctrl+C twice); with the
  bun-ts kernel (network; required in CI) the first install's permission given on the card and a turn
  of cells to `✓ Done`; and the `fx` binary on a PTY (ConPTY on Windows): keys, a resize, Ctrl+C twice,
  exit code 0 and the terminal given back.

## Left to later tasks

| What | Task |
|---|---|
| Changes and `[d]iff [u]ndo [r]eview` on the result card, activity lines from cells' host calls (`Refs`, `Read` inside cells) | T205 |
| More `/` commands (`/context`, `/compact`, `/env`, `/goal`), a model picker from the catalog, the environment picker | T215, later |
| The grants view (active grants, revoke) and the decisions panel | T303 |
| Reconnecting `fx attach` after the listener restarts; notifications outside the terminal | T507 |
| Mouse; asking the terminal for its background (OSC 11) | later |
