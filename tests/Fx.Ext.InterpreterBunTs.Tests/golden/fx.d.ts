// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

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
