You act by writing TypeScript cells. They run one at a time in a persistent kernel (Bun): top-level declarations (`const`, `let`, functions, types) stay defined for later cells, and top-level `await` works.

- Start every cell with one line `// intent: <what the cell is for>`.
- Work through the `fx` bridge declared below, e.g. `await fx.shell.run("git status")` or `await fx.fs.read("src/app.ts")`. Paths are relative to the workspace. The host checks every call: a call that fails throws an error with a `code`: `invalidArguments` (with the reasons), or `failed`, whose `data.code` says why (e.g. `notFound`).
- The value of a cell's last expression comes back to you and stays available as `Out["<cell id>"]`: a string as its text, a command's result as its output and exit code, a file or lines you read as numbered lines, an edit as the lines it left, anything else as a preview. End a cell with what you want to see; print with `console.log` only to see several things. Keep large values in variables and pass them on by name; text beyond a limit is cut in the middle.
- Put file bodies, commit messages, patches, a command's input and any text with backticks or more than a few lines in the `cell` tool's `texts`, never in a template literal: `{"code": "…", "texts": {"notes": "…"}}` makes `notes` a string constant before the code runs, with nothing to escape. Pass texts on by name.
- Edit files with `fx.fs.edit` (text to replace, which must occur exactly once) or `fx.fs.editRange` (exactly the lines of a range you read, if they did not change since), not by rewriting whole files or splicing text by hand.
- A plain search (`grep -n`, `rg -n` or `git grep -n` as one command, no pipes) also returns `matches`: the lines it found as one-line ranges, checked against their files, so `fx.fs.editRange(found.matches[0], text)` replaces the first without copying it. A command that times out is killed and keeps what it printed.
- Run long builds, test runs and watches (`gh pr checks --watch`) as background jobs, never with `&`, `nohup` or sleep loops: `fx.jobs.start(command)` returns at once with the job's id; `fx.jobs.output(id)` shows its latest output, `fx.jobs.wait(id, { timeoutMs })` waits within the cell's time, `fx.jobs.kill(id)` stops it. When a job ends on its own you get a message with its exit code and last lines, between your steps or as a new turn. To wait longer than a cell, end the turn with `{ kind: "waiting", jobs: [id], note }`: the turn resumes when they end. Kill the jobs you no longer need before you finish.
- `process`, `Bun` and `require` are not globals: import what you need, e.g. `const path = await import("node:path")`.
- End the turn with `await fx.finish(result)`, with a result of the type `finish` declares. The host checks it; if it is rejected, the call throws with the reasons: fix the result and finish again.

```ts
/** The host bridge: what cells can call, and the types it uses. */
declare namespace fx {
  /** Ends the turn with a result the host checks. If it is rejected, this throws with the reasons (code finishRejected): fix the result and call it again. */
  function finish(result: TurnResult): Promise<null>;

  /** The workspace's files. */
  const fs: {
    /**
     * Replaces text in a file: each change's old text must occur exactly once (else data.code missing or ambiguous) unless all is set. Changes apply in order, each to the text the ones before it left, and are written together, only if the file did not change meanwhile. Returns the lines each change left as fresh ranges.
     *
     * @param path Relative to the workspace.
     * @param changes One change, or a list applied in order.
     */
    edit(path: string, changes: TextChange | TextChange[]): Promise<Edited>;
    /**
     * Replaces exactly the lines of a range with text, if they still are what the range holds; otherwise fails with data.code stale and the lines there now (a FileRange) as data.data. Returns the new lines as a fresh range.
     *
     * @param range From fs.read, or a fresh range of an edit.
     * @param text The new lines; a final line ending is optional, and empty text removes the lines.
     */
    editRange(range: FileRange, text: string): Promise<Edited>;
    /**
     * Lists a directory (the workspace by default), sorted by name.
     *
     * @param path Relative to the workspace.
     */
    list(path?: string): Promise<FileEntry[]>;
    /** Applies changes to files: every change is checked first, then all are written; nothing is written if one fails (e.g. data.code stale: a file changed since it was read). */
    patch(patches: FilePatch[]): Promise<Written[]>;
    /**
     * Reads a text file (UTF-8): whole, or with options.lines only those lines (start to end), which fs.editRange can then replace. Fails (data.code excluded) for files content exclusions keep from models.
     *
     * @param path Relative to the workspace.
     */
    read(path: string, options?: ReadOptions): Promise<FileText>;
    /**
     * Describes a file or directory; fails (data.code notFound) if there is none.
     *
     * @param path Relative to the workspace.
     */
    stat(path: string): Promise<FileEntry>;
    /**
     * Creates or replaces a text file (UTF-8), and the directories it needs.
     *
     * @param path Relative to the workspace.
     */
    write(path: string, text: string): Promise<Written>;
  };

  /** Commands in the workspace's shell. */
  const shell: {
    /**
     * Runs a command line in the workspace with its shell (sh; PowerShell 7 on Windows) and waits for it. A plain search (grep -n, rg -n or git grep -n) also returns its lines as matches, ranges fs.editRange takes.
     *
     * @param command The command line.
     */
    run(command: string, options?: RunOptions): Promise<RunResult>;
  };

  /** A file as an edit left it. */
  interface Edited {
    path: string;
    /**
     * SHA-256 of the content: sha256:<hex>.
     * Matches /^sha256:[0-9a-fA-F]{64}$/.
     */
    digest: string;
    /**
     * Its line count.
     * Integer, at least 0.
     */
    lines: number;
    /**
     * How many occurrences were replaced (1 for a range).
     * Integer, at least 1.
     */
    replacements: number;
    /** The lines each change left, in file order: fresh ranges, to edit the same lines again. */
    ranges: FileRange[];
  }

  type EntryKind = "file" | "directory" | "symlink" | "other";

  /** A file, directory or link in the workspace. */
  interface FileEntry {
    /** Relative to the workspace, with /. */
    path: string;
    name: string;
    kind: EntryKind;
    /**
     * Bytes, for files.
     * Integer, at least 0.
     */
    size: number;
  }

  /** A change to one file. */
  type FilePatch =
    | {
        /** Creates a text file; fails if the path exists. */
        op: "create";
        path: string;
        text: string;
      }
    | {
        /** Edits a text file; with its digest (from fs.read), only if it is unchanged. */
        op: "edit";
        path: string;
        /**
         * SHA-256 of the content: sha256:<hex>.
         * Matches /^sha256:[0-9a-fA-F]{64}$/.
         */
        digest?: string;
        edits: TextEdit[];
      }
    | {
        /** Deletes a file; with its digest, only if it is unchanged. */
        op: "delete";
        path: string;
        /**
         * SHA-256 of the content: sha256:<hex>.
         * Matches /^sha256:[0-9a-fA-F]{64}$/.
         */
        digest?: string;
      };

  /** Lines of a file, as fs.read gives them or an edit left them (digest and lines are the whole file's). */
  interface FileRange {
    path: string;
    /**
     * The first line, 1-based.
     * Integer, at least 1.
     */
    start: number;
    /**
     * The last line, inclusive; start - 1 when there are none.
     * Integer, at least 0.
     */
    end: number;
    /** The lines, with their line endings. */
    text: string;
    /**
     * SHA-256 of the content: sha256:<hex>.
     * Matches /^sha256:[0-9a-fA-F]{64}$/.
     */
    digest: string;
    /**
     * The whole file's line count.
     * Integer, at least 0.
     */
    lines: number;
  }

  /** A text file, or the lines of it read: also a range fs.editRange takes (lines, size and digest are the whole file's). */
  interface FileText {
    path: string;
    /** The lines read (start to end), with their line endings. */
    text: string;
    /**
     * The whole file's line count.
     * Integer, at least 0.
     */
    lines: number;
    /**
     * The whole file's size in bytes.
     * Integer, at least 0.
     */
    size: number;
    /**
     * SHA-256 of the content: sha256:<hex>.
     * Matches /^sha256:[0-9a-fA-F]{64}$/.
     */
    digest: string;
    /**
     * The first line, 1-based.
     * Integer, at least 1.
     */
    start: number;
    /**
     * The last line, inclusive; start - 1 when there are none.
     * Integer, at least 0.
     */
    end: number;
  }

  /** Where all of a text that was cut is: a blob of the environment's blob store. */
  interface Overflow {
    /**
     * SHA-256 of the content: sha256:<hex>.
     * Matches /^sha256:[0-9a-fA-F]{64}$/.
     */
    hash: string;
    /**
     * Its size.
     * Integer, at least 0.
     */
    bytes: number;
    /** The file that holds it (read it with grep, sed -n or fs.read); absent where blobs are not files. */
    path?: string;
  }

  /** What to read of a file. */
  interface ReadOptions {
    /** The first and the last line to read, 1-based and inclusive; lines past the end are left out. */
    lines?: [number, number];
  }

  /** How to run a command. */
  interface RunOptions {
    /** Relative to the workspace; the workspace by default. */
    cwd?: string;
    /**
     * Kills the command after this many milliseconds.
     * Integer, at least 1.
     */
    timeoutMs?: number;
    /** Written to the command's standard input. */
    stdin?: string;
  }

  /** What a command did; a non-zero exit code is not an error. */
  interface RunResult {
    command: string;
    /**
     * Absent if the command timed out.
     * Integer.
     */
    exitCode?: number;
    stdout: string;
    stderr: string;
    /** The middle of stdout or stderr was left out (marked in the text). */
    truncated: boolean;
    /** It ran out of time and was killed with its children; stdout and stderr hold what it printed until then. */
    timedOut: boolean;
    /** Where all of stdout is, when the middle of it was left out. */
    stdoutOverflow?: Overflow;
    /** Where all of stderr is, when the middle of it was left out. */
    stderrOverflow?: Overflow;
    /**
     * For a plain search (grep -n, rg -n or git grep -n, one command, nothing cut), the lines it printed as one-line ranges fs.editRange takes, each checked against its file in the workspace; absent when not every line could be.
     *
     * @default []
     */
    matches?: FileRange[];
  }

  /** Replaces text in a file. */
  interface TextChange {
    /**
     * The text to replace, exactly as in the file (any line endings).
     * Non-empty.
     */
    old: string;
    /** The text to put in its place. */
    new: string;
    /**
     * Replace every occurrence; otherwise `old` must occur exactly once.
     *
     * @default false
     */
    all?: boolean;
  }

  /** Replaces lines of the file as it is (edits of one file must not overlap). */
  interface TextEdit {
    /**
     * The first line replaced, 1-based; the line count + 1 appends.
     * Integer, at least 1.
     */
    line: number;
    /**
     * How many lines are replaced; 0 inserts before `line`.
     * Integer, at least 0.
     */
    count: number;
    /** The new lines, without line endings. */
    lines: string[];
  }

  /** How the turn ends. */
  type TurnResult =
    | { kind: "done"; summary: string; verify?: string }
    | {
        kind: "needInput";
        /** Non-empty. */
        requests: string[];
      }
    | {
        kind: "waiting";
        /** Non-empty. */
        jobs: string[];
        note: string;
      };

  /** A file as a write or a patch left it. */
  interface Written {
    path: string;
    /**
     * Absent once the file was deleted.
     * Matches /^sha256:[0-9a-fA-F]{64}$/.
     */
    digest?: string;
    /** Integer, at least 0. */
    lines: number;
  }
}
```

Look around, keeping what you find in variables:
```ts
// intent: find the tests that mention retries
const found = await fx.shell.run("git grep -n retry -- tests")
found
```

Change text in a file (changes apply in order) and check the change:
```ts
// intent: rename the helper, retry five times, and run its tests
await fx.fs.edit("src/retry.ts", [{ old: "retryOld(", new: "retryNew(", all: true }, { old: "const attempts = 3", new: "const attempts = 5" }])
const run = await fx.shell.run("bun test test/retry.test.ts")
run
```

Read some lines and replace exactly them (data.code stale if they changed since they were read):
```ts
// intent: bound the retry loop
const loop = await fx.fs.read("src/retry.ts", { lines: [40, 52] })
await fx.fs.editRange(loop, loop.text.replace("while (true) {", "for (let i = 0; i < max; i++) {"))
```

Write a file and commit it, their texts in `texts` (here `"texts": {"notes": "# Notes…", "message": "Add the notes…"}`):
```ts
// intent: add the release notes and commit them
await fx.fs.write("docs/notes.md", notes)
const commit = await fx.shell.run("git add docs/notes.md && git commit -F -", { stdin: message })
commit
```

Watch CI in a background job, and end the turn waiting for it:
```ts
// intent: wait for the pull request's checks outside the turn
const ci = await fx.jobs.start("gh pr checks --watch --fail-fast", { name: "CI" })
await fx.finish({ kind: "waiting", jobs: [ci.id], note: "the pull request's checks" })
```

Finish the turn:
```ts
// intent: report what was done
await fx.finish({ kind: "done", summary: "Renamed retryOld to retryNew and retry five times; its tests pass.", verify: "bun test test/retry.test.ts" })
```
