// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

export interface AnswerSession {
  /** From 1 to 128 characters. */
  session: string;
  /** From 1 to 256 characters. */
  key: string;
  request: string;
  answer: unknown;
}

export interface Attribution {
  principal: string;
  via: string;
}

/** Why an identity provider did not vouch for a client. */
export type AuthError =
  | { kind: "noProof"; reason: string }
  | { kind: "rejected"; reason: string }
  | { kind: "expired" }
  | { kind: "unavailable"; reason: string };

/** Why the host refused a request of the client protocol. */
export type ClientError =
  | { kind: "forbidden"; failure: IdentityFailure }
  | { kind: "unknownSession"; session: string }
  | { kind: "unknownEnvironment"; id: string }
  | { kind: "rejected"; code: string; message: string }
  | { kind: "cannotAnswer"; request: string }
  | { kind: "invalid"; reasons: string[] }
  | { kind: "unavailable"; reason: string };

/** What an accepted command appended: the head before it and how many events. */
export interface Committed {
  /** Integer. */
  after: number;
  /** Integer. */
  count: number;
}

/** A session in the host's workspace; with an id the client chose, creating again is harmless. */
export interface CreateSession {
  /** From 1 to 128 characters. */
  session?: string;
  title?: string;
}

/** An environment: where sessions' hands are. */
export interface EnvSummary {
  id: string;
  name: string;
  provider: string;
  phase: string;
  transport?: string;
  workspace?: string;
  reason?: string;
  warnings: string[];
}

export interface EventRef {
  /**
   * An event stream, `kind/id`.
   * Non-empty.
   */
  stream: string;
  /** Integer, at least 1. */
  seq: number;
}

/** Evidence for a result: what was checked and what it showed. */
export interface Evidence {
  /** Non-empty. */
  kind: string;
  summary: string;
  /** @default null */
  detail?: unknown;
}

/** A request's deadline and what happens then. */
export interface Expiry {
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
  then: ExpiryPolicy;
}

/** What happens when a request's time runs out. */
export type ExpiryPolicy =
  | { kind: "cancel" }
  | { kind: "answer"; response: unknown };

/** Why an extension's method failed. */
export type ExtCallError =
  | { kind: "forbidden"; failure: IdentityFailure }
  | {
      kind: "failed";
      failure:
        | { kind: "unknownMethod"; name: string }
        | { kind: "invalidParams"; issues: Issue[] }
        | { kind: "failed"; message: string; data: unknown }
        | { kind: "crashed"; detail: string }
        | { kind: "unreachable"; reason: string };
    };

/** A fork: a session whose history is this one's up to an event. */
export interface Fork {
  /** Non-empty. */
  child: string;
  /**
   * A sequence number in the session's stream.
   * Integer, at least 1.
   */
  upTo: number;
  /**
   * A sequence number in the session's stream.
   * Integer, at least 1.
   */
  seq: number;
  profile: string;
  /**
   * Variant overrides, by slot id (docs/modules/variants.md).
   *
   * @default {}
   */
  overrides?: { [key: string]: unknown };
  status: ForkStatus;
}

export interface ForkSession {
  /** From 1 to 128 characters. */
  session: string;
  /** From 1 to 256 characters. */
  key: string;
  /** From 1 to 128 characters. */
  child?: string;
  /** Integer. */
  upTo?: number;
}

export type ForkStatus =
  | { kind: "starting" }
  | { kind: "ready" }
  | { kind: "failed"; reason: string };

export interface IdRef {
  /** Non-empty. */
  id: string;
}

/** Why the host refused a client's identity in initialize. */
export type IdentityFailure =
  | { kind: "missing" }
  | { kind: "invalid"; issues: Issue[] }
  | {
      kind: "unknownProvider";
      /** Non-empty. */
      provider: string;
    }
  | {
      kind: "refused";
      /** Non-empty. */
      provider: string;
      error: AuthError;
    }
  | {
      kind: "invalidPrincipal";
      /** Non-empty. */
      provider: string;
      reason: string;
    }
  | {
      kind: "noRole";
      /** Non-empty. */
      principal: string;
    }
  | { kind: "tokenRejected" };

/** An input for the agent; its id is the sequence number of the event that received it. */
export interface Input {
  /**
   * A sequence number in the session's stream.
   * Integer, at least 1.
   */
  id: number;
  /** Who sent a command (the principal) and what delivered it. */
  by: Attribution;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
  content: InputContent;
}

/** What an input says: a prompt, a settled request, a finished job. */
export type InputContent =
  | { kind: "text"; text: string }
  | {
      kind: "resolved";
      /** Non-empty. */
      request: string;
      resolution: Resolution;
    }
  | {
      kind: "jobFinished";
      /** Non-empty. */
      job: string;
      note: string;
    };

export interface Issue {
  path: (string | number)[];
  message: string;
}

export interface KeyedSession {
  /** From 1 to 128 characters. */
  session: string;
  /** From 1 to 256 characters. */
  key: string;
}

export interface LastTurn {
  /** Integer, at least 1. */
  turn: number;
  end: TurnEnd;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
}

/** turn: cancelled when its turn ends; session: open until settled. */
export type Lifetime = "turn" | "session";

/** Variant overrides of a session: slots to set, and slots whose override to remove. */
export interface OverrideSession {
  /** From 1 to 128 characters. */
  session: string;
  /** From 1 to 256 characters. */
  key: string;
  /** @default {} */
  set?: { [key: string]: unknown };
  /** @default [] */
  unset?: string[];
}

/** An open request, the client capabilities that can answer it, and who opened it when. */
export interface PendingRequest {
  request: RequestSpec;
  capabilities: string[];
  /** Who sent a command (the principal) and what delivered it. */
  by: Attribution;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
  /**
   * A sequence number in the session's stream.
   * Integer, at least 1.
   */
  seq: number;
}

/** steer: into the running turn at its next step; queue: a turn of its own after it. */
export type PromptMode = "steer" | "queue";

export interface PromptSession {
  /** From 1 to 128 characters. */
  session: string;
  /** From 1 to 256 characters. */
  key: string;
  text: string;
  mode: PromptMode;
}

/** toOpener: the opener waits for it in the log; asInput: it becomes an input for the agent. */
export type Reply = "toOpener" | "asInput";

/** Who opened a request: a turn, the session, or a follower of the log (a run step). */
export type RequestOrigin =
  | {
      kind: "turn";
      /** Integer, at least 1. */
      turn: number;
    }
  | { kind: "session" }
  | {
      kind: "stream";
      /** The position of an event: its stream and sequence number. */
      event: EventRef;
    };

/** A pending request: a decision card, grant request, gate, form, auth flow or install. */
export interface RequestSpec {
  /** Non-empty. */
  id: string;
  /** Non-empty. */
  kind: string;
  title: string;
  payload: unknown;
  origin: RequestOrigin;
  reply: Reply;
  lifetime: Lifetime;
  expiry?: Expiry;
}

/** How a pending request was settled. */
export type Resolution =
  | { kind: "answered"; response: unknown }
  | { kind: "cancelled"; reason: string }
  | { kind: "expired" }
  | { kind: "expiredWithAnswer"; response: unknown };

/** The turn in progress and the inputs delivered to it. */
export interface RunningTurn {
  /** Integer, at least 1. */
  turn: number;
  profile: string;
  inputs: Input[];
  /** Integer, at least 0. */
  step: number;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
  /**
   * A sequence number in the session's stream.
   * Integer, at least 1.
   */
  seq: number;
}

/** A session the host created, and where it works. */
export interface SessionEntry {
  session: string;
  workspace: string;
  environment: string;
  title?: string;
}

/** What happened in a session (spec §5); kept forever. */
export type SessionEvent =
  | {
      kind: "started";
      session: SessionStart;
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
    }
  | {
      kind: "profileSelected";
      /** Non-empty. */
      profile: string;
      /**
       * Variant overrides, by slot id (docs/modules/variants.md).
       *
       * @default {}
       */
      overrides?: { [key: string]: unknown };
      /** @default [] */
      changed?: string[];
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
    }
  | { kind: "promptQueued"; input: Input }
  | { kind: "promptSteered"; input: Input }
  | {
      kind: "promptWithdrawn";
      /**
       * A sequence number in the session's stream.
       * Integer, at least 1.
       */
      input: number;
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
    }
  | {
      kind: "turnStarted";
      /** Integer, at least 1. */
      turn: number;
      /** Items: A sequence number in the session's stream. Integer, at least 1. */
      inputs: number[];
      profile: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
    }
  | {
      kind: "inputsDelivered";
      /** Integer, at least 1. */
      turn: number;
      /** Integer, at least 1. */
      step: number;
      /** Items: A sequence number in the session's stream. Integer, at least 1. */
      inputs: number[];
    }
  | {
      kind: "turnEnded";
      /** Integer, at least 1. */
      turn: number;
      end: TurnEnd;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
    }
  | {
      kind: "interrupted";
      /** Integer, at least 1. */
      turn?: number;
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
    }
  | { kind: "requestOpened"; pending: PendingRequest }
  | {
      /** A request settled; `input` is set when it became an input for the agent. */
      kind: "requestResolved";
      /** Non-empty. */
      request: string;
      resolution: Resolution;
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      /**
       * A sequence number in the session's stream.
       * Integer, at least 1.
       */
      input?: number;
    }
  | { kind: "jobNotified"; input: Input }
  | {
      kind: "forked";
      /** Non-empty. */
      child: string;
      /**
       * A sequence number in the session's stream.
       * Integer, at least 1.
       */
      upTo: number;
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
    }
  | {
      kind: "forkReady";
      /** Non-empty. */
      child: string;
    }
  | {
      kind: "forkFailed";
      /** Non-empty. */
      child: string;
      reason: string;
    }
  | {
      kind: "closed";
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
    }
  | {
      kind: "reopened";
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
    }
  | {
      kind: "archived";
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
    };

/** What a session subscriber receives: the state at a sequence number, replacing what it had, or the next event. */
export type SessionFeed =
  | {
      kind: "reset";
      /** Integer, at least 1. */
      seq: number;
      state: SessionState;
    }
  | {
      /** A session event with its position, time, sender, key and cause. */
      kind: "event";
      /**
       * An event stream, `kind/id`.
       * Non-empty.
       */
      stream: string;
      /** Integer, at least 1. */
      seq: number;
      event: SessionEvent;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      /** Who sent a command (the principal) and what delivered it. */
      by: Attribution;
      /**
       * The idempotency key of a command.
       * Non-empty.
       */
      key: string;
      /** The position of an event: its stream and sequence number. */
      cause?: EventRef;
    };

export interface SessionNotice {
  session: string;
  item: SessionFeed;
}

export interface SessionRef {
  /** From 1 to 128 characters. */
  session: string;
}

/** A session's start: its id, profile, overrides and, for a fork, its parent. */
export interface SessionStart {
  /** Non-empty. */
  id: string;
  /** Non-empty. */
  profile: string;
  /**
   * Variant overrides, by slot id (docs/modules/variants.md).
   *
   * @default {}
   */
  overrides?: { [key: string]: unknown };
  /** The position of an event: its stream and sequence number. */
  parent?: EventRef;
  title?: string;
}

/** A session's state: the fold of its log, and every client's view of it. */
export interface SessionState {
  id: string;
  /** Integer, at least 0. */
  seq: number;
  status: SessionStatus;
  title?: string;
  profile: string;
  /**
   * Variant overrides, by slot id (docs/modules/variants.md).
   *
   * @default {}
   */
  overrides?: { [key: string]: unknown };
  /** The position of an event: its stream and sequence number. */
  parent?: EventRef;
  /** @default [] */
  forks?: Fork[];
  /** Integer, at least 0. */
  turns: number;
  running?: RunningTurn;
  last?: LastTurn;
  /** @default false */
  held?: boolean;
  /** @default [] */
  queue?: Input[];
  /** @default [] */
  inbox?: Input[];
  /** @default [] */
  awaitingRequests?: string[];
  /** @default [] */
  awaitingJobs?: string[];
  /** @default [] */
  requests?: PendingRequest[];
}

export type SessionStatus = "new" | "open" | "closed" | "archived";

export interface SessionView {
  /** Integer. */
  seq: number;
  state: SessionState;
}

export interface SubscribeSession {
  /** From 1 to 128 characters. */
  session: string;
  /**
   * Integer, at least 0.
   *
   * @default 0
   */
  after?: number;
}

/** How a turn ended. */
export type TurnEnd =
  | { kind: "completed"; result: TurnResult }
  | { kind: "interrupted" }
  | { kind: "failed"; reason: string }
  | { kind: "abandoned"; reason: string };

/** How a turn finishes its work (spec §4.5). */
export type TurnResult =
  | {
      kind: "done";
      summary: string;
      /** @default [] */
      evidence?: Evidence[];
      verify?: string;
    }
  | {
      kind: "needInput";
      /** Non-empty. Items: Non-empty. */
      requests: string[];
    }
  | {
      kind: "waiting";
      /** Non-empty. Items: Non-empty. */
      jobs: string[];
      note: string;
    };
