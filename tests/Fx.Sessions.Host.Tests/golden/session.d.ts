// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

export interface Attribution {
  principal: string;
  via: string;
}

/** A choice among options. */
export interface Choice {
  /** Non-empty. */
  question: string;
  /** Non-empty. */
  options: string[];
}

/** A yes-or-no question. */
export interface Confirmation {
  /** Non-empty. */
  message: string;
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

export type ForkStatus =
  | { kind: "starting" }
  | { kind: "ready" }
  | { kind: "failed"; reason: string };

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

export interface ProfileSelection {
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
  basedOn: string;
}

/** steer: into the running turn at its next step; queue: a turn of its own after it. */
export type PromptMode = "steer" | "queue";

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

/** What clients and the host ask a session to do (spec §5). */
export type SessionCommand =
  | { kind: "start"; session: SessionStart }
  | {
      kind: "prompt";
      text: string;
      /** @default "steer" */
      mode?: PromptMode;
    }
  | {
      kind: "withdraw";
      /**
       * A sequence number in the session's stream.
       * Integer, at least 1.
       */
      input: number;
    }
  | { kind: "interrupt" }
  | { kind: "openRequest"; request: RequestSpec }
  | {
      kind: "answer";
      /** Non-empty. */
      request: string;
      response: unknown;
    }
  | {
      kind: "cancelRequest";
      /** Non-empty. */
      request: string;
      reason: string;
    }
  | {
      kind: "expireRequest";
      /** Non-empty. */
      request: string;
    }
  | {
      kind: "notifyJob";
      /** Non-empty. */
      job: string;
      note: string;
    }
  | { kind: "selectProfile"; selection: ProfileSelection }
  | {
      kind: "fork";
      /** Non-empty. */
      child: string;
      /**
       * A sequence number in the session's stream.
       * Integer, at least 1.
       */
      upTo?: number;
    }
  | { kind: "close" }
  | { kind: "reopen" }
  | { kind: "archive" }
  | {
      kind: "takeInputs";
      /** Integer, at least 1. */
      turn: number;
      /** Integer, at least 1. */
      step: number;
    }
  | {
      kind: "endTurn";
      /** Integer, at least 1. */
      turn: number;
      result: TurnResult;
    }
  | {
      kind: "failTurn";
      /** Integer, at least 1. */
      turn: number;
      reason: string;
    }
  | {
      kind: "abandonTurn";
      /** Integer, at least 1. */
      turn: number;
      reason: string;
    }
  | {
      kind: "childStarted";
      /** Non-empty. */
      child: string;
    }
  | {
      kind: "childFailed";
      /** Non-empty. */
      child: string;
      reason: string;
    };

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
