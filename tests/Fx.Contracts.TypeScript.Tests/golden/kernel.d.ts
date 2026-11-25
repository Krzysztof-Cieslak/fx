// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

/** The host bridge: what cells can call, and the types it uses. */
declare namespace fx {
  /** Specialized agents. */
  const agents: {
    /** Asks the reviewer agent for a review. */
    reviewer(input: { diff: string; intent: string }): Promise<Review>;
  };

  const context: {
    /** Pins a value into the prompt. */
    pin(name: string, value: unknown): Promise<null>;
  };

  /** Ends the turn with a result the host validates. */
  function finish(result: TurnResult): Promise<FinishResult>;

  /** The workspace's files. */
  const fs: {
    /** Deletes a file; the name is a reserved word, which is fine for a property. */
    delete(path: string): Promise<null>;
    /** Reads a file. */
    read(path: string): Promise<string>;
  };

  const ledger: {
    /**
     * Searches decisions; an optional parameter before a required one.
     *
     * @param limit Integer, from 1 to 100.
     */
    search(scope: string | undefined, text: string, limit?: number): Promise<string[]>;
  };

  /**
   * The host's clock, in microseconds since the Unix epoch.
   *
   * @returns Integer.
   */
  function now(): Promise<number>;

  const repo: {
    /**
     * Finds the references to a symbol.
     *
     * @param symbol A qualified name, e.g. `Http.retry`.
     */
    refs(symbol: string): Promise<Match[]>;
  };

  /** Commands in the workspace's shell. */
  const shell: {
    /**
     * Runs a command line in the workspace.
     *
     * @param command The command line.
     */
    run(command: string, options?: ShellOptions): Promise<ShellResult>;
  };

  const tools: {
    /**
     * A tool whose name needs quotes.
     *
     * @param class_2 Integer.
     */
    "my-tool"(class_: string, class_2: number): Promise<unknown>;
  };

  interface FinishResult {
    accepted: boolean;
    /** @default [] */
    issues?: Issue[];
  }

  interface Issue {
    path: (string | number)[];
    message: string;
  }

  /** A line that matched a search. */
  interface Match {
    /**
     * Workspace-relative.
     * Non-empty.
     */
    path: string;
    /** Integer, at least 1. */
    line: number;
    text: string;
    note?: string;
    /** @default [] */
    tags?: string[];
    score: number;
    /** Number, from 0 to 1. */
    weight: number;
    /** Integer. */
    sequence: number;
    span: Span | null;
    priority: Priority;
    /** Values: Integer, at least 0. */
    counts: { [key: string]: number };
  }

  /** How urgent a match is. */
  type Priority = "low" | "medium" | "high";

  interface Review {
    verdict: "approve" | "changes";
    comments: string[];
  }

  /** How to run a command. */
  interface ShellOptions {
    cwd?: string;
    /** Integer, at least 1. */
    timeoutMs?: number;
  }

  /** What a command did. */
  interface ShellResult {
    /** Integer. */
    exitCode: number;
    stdout: string;
    lines: string[];
  }

  /** A range of lines, end exclusive. */
  interface Span {
    /** Integer, at least 1. */
    start: number;
    /** Integer, at least 1. */
    end: number;
  }

  /** How a turn ends. */
  type TurnResult =
    | {
        /** The work is complete. */
        kind: "done";
        summary: string;
        /** @default [] */
        evidence?: Match[];
        /** A command that checks the work. */
        verify?: string;
      }
    | {
        kind: "needInput";
        /** Non-empty. */
        decisions: string[];
      }
    | { kind: "waiting"; on: string[]; note: string }
    | {
        /** A range of lines, end exclusive. */
        kind: "moved";
        /** Integer, at least 1. */
        start: number;
        /** Integer, at least 1. */
        end: number;
      }
    | {
        /** The user cancelled the turn. */
        kind: "cancelled";
      };
}
