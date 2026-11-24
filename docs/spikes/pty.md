# Spike: pseudo-terminals under Native AOT (T005)

Spec: §3 (envd: exec/PTY, ConPTY on Windows), §6.5 (PowerShell 7 default, Windows PowerShell fallback),
§9 (environments), §18 (security), §20 (OS integration). Feeds T111 (envd core: exec/PTY with
sequence-numbered, resumable output).

## Verdict

**Programs run on a pseudo-terminal under Native AOT on all eight targets**, from F#, with no
third-party package and no native helper library: a PTY from `/dev/ptmx` and `posix_spawn` on Linux
(glibc and musl) and macOS, and ConPTY on Windows (x64 and arm64). Spawn, input, output, resize (seen
by the program, with `SIGWINCH` on Linux and macOS), Ctrl+C, exit codes, end of output and kill behave
the same on the JIT and as a native binary. PowerShell 7 (`pwsh`), Windows PowerShell 5.1 and `cmd`
start on ConPTY. Evidence:

- `tests/Fx.Pty.Tests` on Linux x64, macOS arm64 and Windows x64 (CI `build` job): real terminals,
  including a controlling-terminal check, `SIGWINCH`, `SIGINT`, `SIGPIPE` and both PowerShells;
- seven `pty-*` scenarios in the JIT-vs-native diff on `linux-x64`, `linux-arm64`, `linux-musl-x64`,
  `linux-musl-arm64`, `osx-arm64`, `osx-x64`, `win-x64` and `win-arm64` (CI `aot` job). Each fact they
  print is also checked against its expected value, so they verify behaviour on every target, not only
  parity;
- the probe (`docs/spikes/pty-probe`, workflow `pty-probe.yml`) records what a terminal looks like on
  every target: [measurements](#measurements).

Changes to the spec: §20 names `Fx.Pty` (posix_spawn + ConPTY) instead of Porta.Pty
([why](#options-considered)).

## What was built

| Path | What |
|---|---|
| `src/Fx.Pty/Terminal.fs` | The capability shape: `PtySpec`, `PtySize`, `Pty` (a running program: `Read`, `Write`, `Resize`, `Wait`, `Kill`, `Close`), `PtyError`, the `Terminal` record (`Spawn`), `ITerminal` and the `Terminal.*` effects (`withPty` closes the terminal after use) |
| `src/Fx.Pty/VtText.fs` | Pure: `VtText.strip`/`plain`/`lines` remove VT sequences; `Transcript` collects output and waits for text in it |
| `src/Fx.Pty/Native/Posix.fs`, `Native/Windows.fs` | The P/Invoke layer: blittable signatures, per-OS constants, structure offsets |
| `src/Fx.Pty/PtyStreams.fs` | Two dedicated threads per terminal for the blocking reads and writes; a bounded output channel |
| `src/Fx.Pty/UnixPty.fs`, `WindowsPty.fs` | How a terminal starts and stops on each OS (below) |
| `src/Fx.Pty/LocalTerminal.fs` | The interpreter: `LocalTerminal.create shell cwd : Terminal` |
| `tests/Fx.Pty.Tests` | VT stripping, and real terminals on Linux, macOS and Windows |
| `tests/aot/Fx.Aot.Scenarios/Scenarios/Pty.fs` | The `pty-*` scenarios |
| `docs/spikes/pty-probe/probe.fsx`, `.github/workflows/pty-probe.yml` | The probe |
| `tools/Fx.Analyzers` | `EFF008` (calls to P/Invoke functions) and the .NET 11 process APIs under `EFF006` |

```fsharp
let terminal = LocalTerminal.create (LocalTerminal.defaultShell ()) workspace
match! terminal.Spawn { PtySpec.exec "pwsh" [ "-NoLogo" ] with Size = { Cols = 120; Rows = 40 } } ct with
| Error e -> ...                                   // NotStarted (program missing) or InvalidSize
| Ok pty ->
    do! pty.Write "Get-ChildItem\r"B ct            // \r is Enter; \u0003 is Ctrl+C
    let! chunk = pty.Read ct                       // Some bytes (UTF-8 + VT), None at the end
    do! pty.Resize { Cols = 160; Rows = 50 }
    let! code = pty.Wait ct
    do! pty.Close ()                               // kills what still runs, discards output
```

| Scenario | Proves |
|---|---|
| `pty-basics` | The initial size reaches the program (`stty size`, `[Console]::WindowHeight`); typed input reaches it; exit code 3; the output ends |
| `pty-resize` | A resize reaches the program; on Linux and macOS its `WINCH` trap runs |
| `pty-interrupt` | Ctrl+C (`\u0003`) stops the foreground program; exit 130 (`SIGINT`) on Linux and macOS |
| `pty-kill` | `Kill` ends the program (137, or -1 on Windows) and the output ends |
| `pty-unix-session` | `/dev/tty` opens (a controlling terminal); `SIGPIPE` is at its default although the runtime ignores it; `Exactly` environments |
| `pty-windows-shells` | `pwsh` 7, `powershell` 5 and `cmd` on ConPTY |
| `pty-not-found` | A missing program is `NotStarted` |

## How a terminal starts

### Linux and macOS

1. Open the master: `open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC)`, `grantpt`, `unlockpt`,
   `ptsname_r`. This is `posix_openpt` plus `O_CLOEXEC`, a flag POSIX does not give `posix_openpt`.
   `openpty` would be simpler, but it opens both sides without `O_CLOEXEC`, so a process that .NET starts
   on another thread at that moment would inherit them, and the output would not end until that unrelated
   process exits.
2. Open the slave too, with `O_NOCTTY` (the parent must never become its controlling process), and set
   the size there with `TIOCSWINSZ`. macOS refuses terminal ioctls on a master whose slave was never opened
   (`TS_IOCTL_NOT_OK` in XNU's `ptyioctl`: `ENOTTY`) and clears the size when the slave is opened while
   nobody has it open, so the parent keeps it open until the child has it, then closes it.
3. Start the child with **.NET 11's `UnixProcessStartArguments.Start(startInfo, callback)`**. .NET resolves
   the program (same rules as `Process.Start`), builds `argv` and `envp` from the `ProcessStartInfo`, calls
   the callback inside its process-start lock, and registers the returned pid, so `Process` reaps it and
   reports its exit (`WaitForExitAsync`, `ExitCode`, `Kill(entireProcessTree)`). `StartDetached = true`
   makes .NET prepare the null device as the child's handles, which the callback ignores; without it .NET
   hands over its own console handles and, if they are a terminal, reconfigures that terminal while the
   child runs (`ConfigureTerminalForChildProcesses`), which would disturb the TUI.
4. The callback calls `posix_spawn` with:
   - `POSIX_SPAWN_SETSID`: a new session, so the terminal can become its controlling terminal and the
     child leads its own process group;
   - `POSIX_SPAWN_SETSIGDEF` with every signal and `POSIX_SPAWN_SETSIGMASK` with none: the runtime ignores
     `SIGPIPE` (and handles others), and `posix_spawn` keeps ignored signals ignored. A terminal session
     starts clean; `pty-unix-session` checks that `SIGPIPE` kills the shell;
   - on macOS also `POSIX_SPAWN_CLOEXEC_DEFAULT`, so no stray descriptor leaks into the child;
   - file actions: open the slave as descriptor 0 (no `O_NOCTTY`), duplicate it to 1 and 2, change the
     directory (`posix_spawn_file_actions_addchdir_np`).
5. **The controlling terminal.** A session leader without one acquires the first terminal it opens
   without `O_NOCTTY`. glibc and musl run `setsid` before the file actions, so the open in step 4 does it.
   XNU runs the file actions *before* `POSIX_SPAWN_SETSID` (`kern_exec.c`), when the child is not yet a
   session leader, so on macOS the child is `/bin/sh -c 'exec 0<>"$1" || exit 127; shift; exec "$@"'`,
   which reopens the slave as a session leader (XNU's `open1` then issues `TIOCSCTTY`) and `exec`s the
   program. Without a controlling terminal there is no `SIGWINCH`, no Ctrl+C, no job control, and
   `/dev/tty` fails (`ssh`, `sudo`, `git` credential prompts and `gpg` open it).

No managed code ever runs between `fork` and `exec`. `forkpty` followed by managed code in the child is
unsafe in any multithreaded runtime: only async-signal-safe functions may run there, and the child can
deadlock on a lock (allocator, GC, runtime) that another thread held at the moment of the fork; under
Native AOT the P/Invoke return path itself polls for a GC that will never come.

### Windows

1. Two anonymous pipes. `CreatePseudoConsole(size, inputRead, outputWrite, 0, &hpc)`, then close those two
   ends here: ConPTY has its own copies, and keeping ours would keep the output from ever ending.
2. A job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.
3. **.NET 11's `WindowsProcessStartArguments.Start(startInfo, callback)`** builds the command line and
   environment block; the callback calls `CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT |
   CREATE_UNICODE_ENVIRONMENT`, no inherited handles, an attribute list with
   `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` (the `HPCON` itself is the value) and
   `PROC_THREAD_ATTRIBUTE_JOB_LIST` (a pointer to the handle array, which must outlive the list), and
   `STARTF_USESTDHANDLES` with null handles: otherwise a child can inherit this process's redirected
   standard handles and write there instead of to the console (node-pty and WezTerm do the same).
   The job is assigned at creation, so there is no window in which the program runs outside it.
4. **The output does not end when the program exits**: the pseudo-console stays open. Once the program
   exits, `ClosePseudoConsole` makes conhost flush its last frame and exit, which ends the output pipe.
   `ClosePseudoConsole` can block until conhost exits, which needs the output drained (older builds), so
   the output thread keeps reading meanwhile. Then the job handle closes, which kills anything the program left
   behind (e.g. a background job of the shell), and the pipes close.

## Options considered

| Option | Verdict |
|---|---|
| `forkpty` P/Invoke + managed code in the child (Pty.Net, Porta.Pty 1.x) | **Unsafe**: managed code between `fork` and `exec` (above) |
| `fork`/`vfork` + `exec` in a "P/Invoke-only" path | **Impossible**: the code after the `fork` P/Invoke returns is managed |
| .NET 11 `Process.Start` with `StandardInputHandle` = slave and `StartDetached` | **No controlling terminal on any OS** (`dup2`, then `setsid`, never an open), and .NET reconfigures its own terminal while the child runs. Its `UnixProcessStartArguments` callback gives the useful half: resolution, `argv`/`envp`, reaping |
| `posix_spawn` + `POSIX_SPAWN_SETSID` + file actions **(chosen)** | Safe (no managed code in the child), in libc everywhere, no extra files. Linux acquires the controlling terminal directly; macOS needs the `/bin/sh` launcher |
| A tiny native helper (C shim, or `spawn-helper` like node-pty) | Safe, but a native build per target (8), files next to the single binary or static linking, and a separate build for the JIT side of tests |
| Re-executing the fx binary as the helper (`fx <hidden verb>` does `TIOCSCTTY` and `execve`) | Clean in production (envd is the fx binary, Native AOT starts in milliseconds), but tests and scenarios run on the JIT, where "the binary" is `dotnet` + an assembly. The fallback if the macOS launcher ever gets in the way |
| **Porta.Pty 2.2.4** (spec §20 named it) | AOT-ready interop (source-generated), but its fork happens in a native shim it ships for `linux-x64`, `linux-arm64`, `osx-*` only (**no musl**), next to the binary; on Windows it ships `conpty.dll` + `OpenConsole.exe` (2.2 MB) from the out-of-band ConPTY package. Breaks the single binary, adds prebuilt native code to the supply chain, and its stream/event API is not a data-in/data-out capability. **Rejected** |

Library placement differs per libc (`openpty`/`forkpty` in `libutil` on glibc before 2.34, in `libc`
after, in `libc` on musl, in `libSystem` on macOS); the functions used here (`open`, `grantpt`,
`unlockpt`, `ptsname_r`, `posix_spawn*`, `ioctl`, `kill`, `sigfillset`…) are in `libc` everywhere, and
`DllImport("libc")` resolves on glibc, musl (its loader maps `libc.so` to itself) and macOS.

## Platform notes

**Linux (glibc and musl)**

- Needs `posix_spawn_file_actions_addchdir_np`: glibc 2.29+ (2019), musl 1.1.24+.
- Reading the master fails with `EIO` once every descriptor of the slave is closed; data written before
  is delivered first. This is the end of the output.
- Default termios: `-iutf8` (canonical-mode backspace erases one byte of a multi-byte character),
  `TERM` is whatever the parent has (unset on the CI runners).
- Sandboxes (T113) must provide `/dev/ptmx` and `/dev/pts` (bubblewrap: `--dev /dev`, or a devpts mount).

**macOS**

- XNU orders file actions before `setsid` (hence the launcher), refuses ioctls on an unopened master,
  and zeroes the size on a first slave open (hence the parent's slave descriptor).
- Reading the master returns 0 or fails with `EIO` at the end; both are handled.
- The launcher is `/bin/sh` (bash 3.2 in POSIX mode by default). It adds `PWD` to the environment;
  bash's `exec` keeps `SHLVL` as it was. `/bin/sh` is SIP-protected, so `DYLD_*` variables do not reach
  the program. The program's `argv[0]` is its resolved path.
- `poll` and `kqueue` have a history of not working on terminal devices on macOS (libuv watches them
  from a `select` thread, tmux turns both off), one reason for blocking reads on dedicated threads.
- Spawn to first output is 13–24 ms (Linux: 1–2 ms); the launcher is part of that.

**Windows**

- ConPTY needs Windows 10 1809+. Both CI images (Windows Server 2025, Windows 11 arm64) use the in-box
  conhost. The out-of-band ConPTY (Windows Terminal's `conpty.dll` + `OpenConsole.exe`) fixes in-box bugs
  on older Windows 10 builds, at the cost of shipping a 2.2 MB executable; not needed so far.
- Every session starts with a VT preamble and ends with a postamble, even for `cmd /c echo hi`:
  `ESC[?9001h ESC[?1004h ESC[?25l ESC[2J ESC[m ESC[H` (win32-input-mode, focus events, hide cursor,
  clear, home), the window title as `OSC 0`, `ESC[?25h`, and finally `ESC[?9001l ESC[?1004l`. The
  output is a rendering of the console buffer, not a copy of what the program wrote: lines can be
  repainted (after a resize, text can appear twice), runs of spaces can become cursor moves, and lines
  end with `\r\n`.
- Without `PSEUDOCONSOLE_INHERIT_CURSOR` ConPTY sent no queries on either image. With the flag it sends
  `ESC[6n` and waits for the answer (its documentation), so the flag stays off.
- The console code page is the OEM one (437 on the runners): `pwsh` loses non-ASCII characters in typed
  input unless its console encodings are set to UTF-8 ([measurements](#measurements)).
- Enter is `\r`. Ctrl+C (`\u0003`) becomes `CTRL_C_EVENT`; PowerShell then stops and exits with 1.
- `Kill` is `TerminateJobObject(job, -1)`: exit code -1, like `Process.Kill`.
- Windows PowerShell 5.1 on Windows 11 arm64 took about 22 s per start on the runner (pwsh 7: under
  0.4 s). It runs on .NET Framework, which apparently has no native images there. The fallback works but
  is slow on arm64; prefer `pwsh` (spec §6.5).

## Process lifecycle

- **Reaping**: .NET's `Process` reaps the child, because the callback registers it (`SIGCHLD`, `waitpid`
  on its own pid only). Nothing else calls `waitpid`.
- **Exit code**: as `Process.ExitCode`: 128 + the signal when a signal ended the program on Linux and
  macOS (`SIGINT` 130, `SIGKILL` 137, `SIGPIPE` 141).
- **Kill** (Linux, macOS): `Process.Kill(entireProcessTree = true)` (it stops the whole tree, then kills it
  bottom-up), then `kill(-pid, SIGKILL)` for the session's process group, which also reaches processes
  whose parent already exited. Only while the program runs: once it is reaped its id can be reused.
  **Windows**: `TerminateJobObject`.
- **What survives**: on Linux and macOS, a process that left the session (`setsid`, daemons) or that
  an interactive shell put in another process group and that is no longer a descendant. When the session
  leader exits, the kernel sends `SIGHUP` to the foreground process group. On Windows the job holds every
  descendant unless it breaks away.
- **End of output**: on Linux and macOS when no process has the slave open, so a surviving background
  process keeps the output open, as with pipes. On Windows when the pseudo-console closes after the
  program's exit.
- **Close**: kills what runs, discards unread output and stops input. The threads end, and the handles
  close, when the terminal ends; a surviving process that holds the slave keeps one thread and the
  master open until it exits.

## Reading and writing

- `read`/`ReadFile` block, so each terminal has **two dedicated threads** (256 KiB stacks): one reads
  into a pinned 16 KiB buffer and writes chunks into a bounded channel (64 chunks), one writes queued
  input. `Pty.Read` awaits the channel, so no thread-pool thread ever blocks.
- Alternatives: `FileStream.ReadAsync` on a terminal descriptor is a blocking read on a thread-pool
  thread (non-seekable files); `poll`/`epoll` would serve all terminals from one thread on Linux, but not
  on macOS (above); overlapped named pipes would on Windows. One thread pair per terminal is fine for the
  handful an agent runs; a poller is an optimisation for later.
- **Backpressure**: when nobody reads, the output thread waits for room, the kernel buffer fills, and the
  program blocks on its next write, as on a real terminal. The environment protocol's credits (§17) map
  onto this.
- `Write` completes when the bytes were handed to the terminal; cancelling stops waiting, not the write.
- Throughput through a terminal: 25–90 MiB/s on Linux and macOS arm64 (including the producer), 3–10
  MiB/s on the macOS x64 runner, 2–3 MiB/s through ConPTY (which renders every frame).

## Native AOT and interop

- **P/Invoke with blittable signatures only**: `int`, `int16`, `nativeint`, `unativeint`; `BOOL` as
  `int`; `COORD` packed into an `int` (two 16-bit values passed by value fit one register on x64 and
  arm64); structures written into zeroed native memory at their 64-bit offsets; opaque C types
  (`posix_spawnattr_t`: 336 bytes on glibc and musl, a pointer on macOS) as 1 KiB buffers.
  `SetLastError = true` and `Marshal.GetLastPInvokeError` work under Native AOT.
- **Variadic functions**: `open` is called without its variadic `mode` (no `O_CREAT`). `ioctl`'s third
  argument is variadic, and Apple arm64 passes variadic arguments on the stack, not in registers: the
  P/Invoke for that target declares six padding arguments so that the real one becomes the first stack
  slot (`Posix.ioctl`). A fixed third parameter would put it in `x2`, where `ioctl` never looks.
- **Constants differ per OS** (`O_NOCTTY`, `O_CLOEXEC`, `POSIX_SPAWN_SETSID`, `TIOCSWINSZ`, `SIGSTOP`) and
  live in one table per OS (`Posix.linux`, `Posix.macOS`). macOS ioctl numbers must be passed zero-extended
  (`unativeint`): `TIOCSWINSZ` is `0x80087467`.
- **F# specifics**: a lambda taking a ref struct (`Func<UnixProcessStartArguments, int>`) compiles and
  runs under Native AOT; its pointer properties are `nativeptr<_>`. Extern names that are F# keywords
  (`open`) need `EntryPoint`. F# `fixed` makes the analyzers fail (the compiler service throws on it):
  pin with `GC.AllocateArray(n, pinned = true)` or a `GCHandle`. `#nowarn "9" "51"` only in the interop
  files.
- **Analyzers**: P/Invoke calls are invisible to the API bans, so `EFF008` now flags every call to a
  `[<DllImport>]` function, and `EFF006` now covers .NET 11's `SafeProcessHandle.Start`/`Kill`/`Signal`
  and the two process-start callbacks. `analyzers.json` allows them only in `UnixPty.fs`, `WindowsPty.fs`
  and `Native/Posix.fs`.

## Testing terminals deterministically

- Child programs print `@@key=value` markers; tests read the output to its end and parse facts from
  `VtText.plain` (VT sequences stripped, `\r\n` → `\n`). Never compare raw terminal output: echo, the
  ConPTY preamble and repaints, and line endings differ per OS.
- Take the *last* occurrence of a marker (ConPTY can repaint), and keep markers short and without
  spaces (ConPTY may turn spaces into cursor moves and wraps long lines).
- Wait for a "ready" marker before typing, resizing or killing; type `\r` for Enter.
- Bound every wait (`CancellationTokenSource(120 s)` in scenarios, 60 s in tests); a hang then fails one case.
- `Fx.Aot.Tests` runs the scenarios twice and in parallel: terminals are independent, so that holds.

## Measurements

From two runs of `pty-probe.yml` (JIT; latency is from `Spawn` to the first byte, median of 5; Windows
PowerShell 5.1 took 243–337 ms to the end on x64 and about 22 s on arm64):

| Target | `sh -c 'echo hi'` / `cmd /c echo hi` | `pwsh -Command` to end | Throughput (MiB/s) | Ctrl+C / Kill exit |
|---|---:|---:|---:|---|
| linux-x64 | 1.0–1.9 ms | — | 38–89 | 130 / 137 |
| linux-arm64 | 0.9–1.1 ms | — | 68–70 | 130 / 137 |
| linux-musl-x64 | 0.8–0.9 ms | — | 26–27 | 130 / 137 |
| linux-musl-arm64 | 1.1–2.3 ms | — | 33–41 | 130 / 137 |
| osx-arm64 | 13–19 ms | — | 25–36 | 130 / 137 |
| osx-x64 | 21–24 ms | — | 3.4–10 | 130 / 137 |
| win-x64 | 12–15 ms (first frame) | 295–395 ms | 2.0–3.0 | 1 / -1 |
| win-arm64 | 22–25 ms (first frame) | 370–386 ms | 2.1–2.3 | 1 / -1 |

Non-ASCII text (`héllo ✓ 世`) typed into the program and echoed back, and printed by it:

| Program | Typed in, echoed back | Printed |
|---|---|---|
| `sh` (Linux, musl, macOS) | `héllo ✓ 世` | `héllo ✓ 世` |
| `pwsh` (Windows x64 and arm64) | `héllo √ ?` | `héllo ✓ 世` |
| `pwsh` with `[Console]::InputEncoding` and `OutputEncoding` set to UTF-8 | `héllo ✓ 世` | `héllo ✓ 世` |
| `cmd` (`set /p`, `echo`) | `héllo ✓ 世` | `héllo ✓ 世` |

ConPTY's input is UTF-8 and its output is UTF-8, but a console starts with the OEM code page (437 on the
runners). `cmd` and PowerShell's host use the Unicode console API and are fine; .NET's `Console` streams
in `pwsh` decode typed input with the code page, so characters outside it become best-fit look-alikes
(`✓` → `√`) or `?`. Other programs that read or write bytes are affected the same way.

The probe also showed the ConPTY postamble arriving before the last `\r\n` in one run: the order of the
final frame is not fixed.

## Risks

- **macOS launcher**: depends on `/bin/sh` and on XNU's acquire-on-open; both are long-standing.
  Fallback: the self-exec helper (above).
- **Escaped processes** keep a terminal's output (and one thread) alive until they exit (Linux, macOS).
  Real containment is the sandbox (T113: cgroups, or a kill by session id scanning `/proc`).
- **In-box ConPTY differs across Windows builds** (preamble, repaints, close blocking). Tests parse facts,
  never raw output; the out-of-band ConPTY is the escape hatch.
- **.NET 11 APIs**: `UnixProcessStartArguments`/`WindowsProcessStartArguments` are new; the JIT-vs-native
  diff and the tests catch a behaviour change on an SDK bump.
- **glibc older than 2.29** lacks `posix_spawn_file_actions_addchdir_np`; the spawn would fail with
  `EntryPointNotFoundException`. .NET 11 itself needs a newer glibc on most distributions.

## Recommendations for T111

1. **Make `Terminal` a capability** like `Shell`: keep `Terminal`/`ITerminal`/`Terminal.*` (or move them
   to `Fx.Effects`), add a deterministic fake (`ScriptedTerminal`: canned output chunks, recorded input
   and resizes) and turn `LocalTerminalTests` into a conformance suite that the fake (for what it can
   honour) and the envd-backed interpreter also pass. Journal `Read` results like `Proc` output (T131).
2. **Sequenced, resumable output** belongs on top of `Pty.Read`: an append-only log per terminal with
   byte offsets as sequence numbers, a bounded in-memory window plus a blob spill, and `pty/read
   {from: offset}` for clients that reconnect. Keep reading while no client is attached, so programs
   do not block; the bounded channel here is the per-terminal backpressure point.
3. **Environment**: set `TERM=xterm-256color` (and `COLORTERM=truecolor` if the client renders it) instead
   of inheriting the host's (`dumb` or unset on CI), a UTF-8 locale on Linux and macOS, and consider
   `IUTF8` on Linux. Build security-sensitive environments with `Exactly` (spec §18).
4. **Windows text**: the console starts with the OEM code page, and programs that read typed input
   through it (PowerShell's `[Console]::ReadLine`, anything using byte streams) lose non-ASCII characters
   (measurements above). Start PowerShell with `[Console]::InputEncoding` and `OutputEncoding` set to UTF-8
   (or the console code page to 65001) in the session's startup.
5. **Shell selection** (§6.5, variant): `pwsh` if found on `PATH`, else `powershell`; on Windows arm64 the
   fallback is slow to start.
6. **Headless use**: an agent driving a terminal has no terminal emulator to answer queries (`ESC[6n`,
   DA). Programs that ask (some TUIs, readline in some modes) wait or time out. A small VT responder in
   envd, or `TERM=dumb` for non-interactive runs, decides this; the spec does not yet.
7. **Kill semantics**: Unix kills the tree and the process group; Windows the job. Make "what survives"
   explicit in the environment protocol and leave containment to sandbox drivers (T113).
8. **`LocalShell` inherits ignored signals**: children of `Process.Start` get `SIGPIPE` ignored
   (signal 13 in `SigIgn` of `/proc/<pid>/status`), so `yes | head` behaves differently there than in a terminal. Consider the
   same `UnixProcessStartArguments` path with `POSIX_SPAWN_SETSIGDEF` for `IShell` too.
