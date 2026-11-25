// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

/** What became of an ask: settled by the rules, or a pending request. */
export type AskOutcome =
  | { kind: "settled"; resolution: GrantResolution }
  | {
      kind: "pending";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
    };

/** Why a pending request closed without an answer. */
export type CloseReason =
  | {
      kind: "withdrawn";
      /** Non-empty. */
      reason: string;
    }
  | {
      kind: "covered";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
    }
  | { kind: "refused"; reasons: string[] }
  | { kind: "timedOut" }
  | { kind: "sessionEnded" };

/** A permission (spec §13.2): an action on the resources a pattern matches, allowed or denied in a scope by a person. */
export interface Grant {
  /**
   * A grant's id.
   * From 1 to 512 characters.
   */
  id: string;
  /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
  action: string;
  /** Non-empty. */
  resource: string;
  effect: GrantEffect;
  scope: GrantScope;
  /** Non-empty. */
  by: string;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  expires?: number;
  reason?: string;
  origin: GrantOrigin;
}

/** An action grants are about: its name, the kind of its resources, the scopes answers may choose. */
export interface GrantActionDef {
  /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
  name: string;
  resource: ResourceKind;
  /** Non-empty. */
  description: string;
  scopes: ScopeKind[];
}

/** A person's answer to a request: allow or deny, once or in a scope, for the resource or a pattern. */
export interface GrantAnswer {
  effect: GrantEffect;
  scope: ScopeChoice;
  /** Non-empty. */
  resource?: string;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  expires?: number;
  reason?: string;
}

/** An answer to a pending request. */
export interface GrantAnswerParams {
  /**
   * A grant request's id.
   * From 1 to 512 characters.
   */
  request: string;
  answer: GrantAnswer;
}

/** What a requester asks for; the key makes asking again the same request. */
export interface GrantAsk {
  /** Non-empty. */
  key: string;
  /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
  action: string;
  /** Non-empty. */
  resource: string;
  /** @default {} */
  context?: GrantContext;
  requester: Requester;
  /** Non-empty. */
  summary: string;
  /** @default [] */
  evidence?: GrantEvidence[];
  /**
   * Items: Non-empty.
   *
   * @default []
   */
  suggestions?: string[];
  /** @default [] */
  scopes?: ScopeKind[];
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  expires?: number;
}

/** What the effective rules say now: allowed by a grant, ask a person, or denied. */
export type GrantCheck =
  | {
      kind: "allowed";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
    }
  | { kind: "ask"; reasons: string[] }
  | { kind: "denied"; reasons: string[] };

/** A command to the grants log, with its sender and time. */
export interface GrantCommand {
  command: GrantOp;
  /** Non-empty. */
  by: string;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
}

/** The context of a check or request, which the scopes of grants are compared with. */
export interface GrantContext {
  /** Non-empty. */
  session?: string;
  /** Non-empty. */
  run?: string;
  repo?: RepoPlace;
}

/** A grant made directly in the grants view (session, repo or host scope). */
export interface GrantDraft {
  /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
  action: string;
  /** Non-empty. */
  resource: string;
  effect: GrantEffect;
  scope: GrantScope;
  /** Integer. */
  expires?: number;
  reason?: string;
}

/** Whether a grant permits or forbids. */
export type GrantEffect = "allow" | "deny";

/** A grant and where it stands. */
export interface GrantEntry {
  grant: Grant;
  status: GrantStatus;
}

/** Why the grants log refused a command, or a check or request could not be made. */
export type GrantError =
  | { kind: "unknownAction"; action: string }
  | { kind: "invalidResource"; resource: string; reason: string }
  | { kind: "invalidPattern"; pattern: string; reason: string }
  | { kind: "invalidRequest"; reason: string }
  | { kind: "invalidAnswer"; reason: string }
  | { kind: "invalidGrant"; reason: string }
  | {
      kind: "unknownRequest";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
    }
  | {
      kind: "notPending";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
    }
  | {
      kind: "unknownGrant";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
    }
  | {
      kind: "notActive";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
    }
  | { kind: "notAPerson"; principal: string }
  | { kind: "notDue"; what: string }
  | { kind: "duplicateId"; id: string }
  | { kind: "conflict"; code: string; message: string }
  | { kind: "unavailable"; reason: string };

/** An event of the grants log (spec §13.2). */
export type GrantEvent =
  | {
      /** A durable pending request for a permission: what the person deciding sees. */
      kind: "requested";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      id: string;
      /** Non-empty. */
      key: string;
      /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
      action: string;
      /** Non-empty. */
      resource: string;
      context: GrantContext;
      requester: Requester;
      /** Non-empty. */
      summary: string;
      /** @default [] */
      evidence?: GrantEvidence[];
      /**
       * Items: Non-empty.
       *
       * @default []
       */
      suggestions?: string[];
      /** Non-empty. */
      offer: ScopeKind[];
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      expires?: number;
    }
  | {
      /** A permission (spec §13.2): an action on the resources a pattern matches, allowed or denied in a scope by a person. */
      kind: "granted";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      id: string;
      /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
      action: string;
      /** Non-empty. */
      resource: string;
      effect: GrantEffect;
      scope: GrantScope;
      /** Non-empty. */
      by: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      expires?: number;
      reason?: string;
      origin: GrantOrigin;
    }
  | {
      /** A request answered "deny, just this once". */
      kind: "denied";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
      /** Non-empty. */
      by: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      reason?: string;
    }
  | {
      /** A grant revoked by a person. */
      kind: "revoked";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
      /** Non-empty. */
      by: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      reason?: string;
    }
  | {
      /** A grant that stopped applying: its expiry passed or its session ended. */
      kind: "expired";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
    }
  | {
      /** A pending request closed without an answer. */
      kind: "closed";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      why: CloseReason;
    };

/** Something the person deciding sees. */
export interface GrantEvidence {
  /** Non-empty. */
  label: string;
  text: string;
}

/** What the grants log asks the host to do. */
export type GrantIntent =
  | {
      kind: "expireAt";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
    }
  | {
      kind: "timeOutAt";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
    };

/** Grants with their status, as of a sequence number. */
export interface GrantList {
  /**
   * A sequence number of the grants log (0 before the first event).
   * Integer, at least 0.
   */
  seq: number;
  grants: GrantEntry[];
}

/** Which grants to list: those in force, or all. */
export interface GrantListParams {
  /** @default false */
  all?: boolean;
}

/** An event of the grants log and its sequence number. */
export interface GrantNotice {
  /** Integer, at least 1. */
  seq: number;
  event: GrantEvent;
}

/** What happens to the grants log. */
export type GrantOp =
  | {
      /** A durable pending request for a permission: what the person deciding sees. */
      kind: "request";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      id: string;
      /** Non-empty. */
      key: string;
      /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
      action: string;
      /** Non-empty. */
      resource: string;
      context: GrantContext;
      requester: Requester;
      /** Non-empty. */
      summary: string;
      /** @default [] */
      evidence?: GrantEvidence[];
      /**
       * Items: Non-empty.
       *
       * @default []
       */
      suggestions?: string[];
      /** Non-empty. */
      offer: ScopeKind[];
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      expires?: number;
    }
  | {
      kind: "answer";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
      answer: GrantAnswer;
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
    }
  | { kind: "grant"; grant: Grant }
  | {
      kind: "revoke";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
      reason?: string;
    }
  | {
      kind: "expire";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
    }
  | {
      kind: "close";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
      why: CloseReason;
    }
  | {
      kind: "endSession";
      /** Non-empty. */
      session: string;
    };

/** How a grant came about: an answer to a request, or made directly. */
export type GrantOrigin =
  | {
      kind: "answer";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
    }
  | {
      kind: "direct";
      /** Non-empty. */
      via: string;
    };

/** A policy rule: it asks or denies, and outranks every allow grant (policy only tightens). */
export interface GrantPolicyRule {
  /** Non-empty. */
  id: string;
  /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
  action: string;
  /** Non-empty. */
  resource: string;
  effect: "ask" | "deny";
  /** Non-empty. */
  reason: string;
}

/** May this action happen on this resource in this context? */
export interface GrantQuery {
  /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
  action: string;
  /** Non-empty. */
  resource: string;
  /** @default {} */
  context?: GrantContext;
}

/** A durable pending request for a permission: what the person deciding sees. */
export interface GrantRequest {
  /**
   * A grant request's id.
   * From 1 to 512 characters.
   */
  id: string;
  /** Non-empty. */
  key: string;
  /** An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits. */
  action: string;
  /** Non-empty. */
  resource: string;
  context: GrantContext;
  requester: Requester;
  /** Non-empty. */
  summary: string;
  /** @default [] */
  evidence?: GrantEvidence[];
  /**
   * Items: Non-empty.
   *
   * @default []
   */
  suggestions?: string[];
  /** Non-empty. */
  offer: ScopeKind[];
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  expires?: number;
}

/** How a request ended: allowed by a grant, denied, or cancelled unanswered. */
export type GrantResolution =
  | {
      kind: "allowed";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
    }
  | { kind: "denied"; reasons: string[] }
  | { kind: "cancelled"; reason: string };

/** The grant to revoke, and why. */
export interface GrantRevokeParams {
  /**
   * A grant's id.
   * From 1 to 512 characters.
   */
  grant: string;
  reason?: string;
}

/** Where a grant applies. */
export type GrantScope =
  | {
      kind: "once";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
    }
  | {
      kind: "session";
      /** Non-empty. */
      session: string;
    }
  | {
      kind: "repo";
      /** Non-empty. */
      repo: string;
      /** Non-empty. */
      path?: string;
    }
  | { kind: "host" };

/** Where a grant stands. */
export type GrantStatus =
  | { kind: "active" }
  | { kind: "spent" }
  | {
      /** A grant revoked by a person. */
      kind: "revoked";
      /**
       * A grant's id.
       * From 1 to 512 characters.
       */
      grant: string;
      /** Non-empty. */
      by: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      reason?: string;
    }
  | {
      kind: "expired";
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
    };

/** Follow the grants log after a sequence number. */
export interface GrantSubscribeParams {
  /**
   * A sequence number of the grants log (0 before the first event).
   * Integer, at least 0.
   *
   * @default 0
   */
  after?: number;
}

/** The grants log's state: the fold of its events. */
export interface GrantsState {
  grants: GrantEntry[];
  requests: RequestEntry[];
  /** Integer, at least 0. */
  seq: number;
}

/** The pending requests, oldest first, as of a sequence number. */
export interface PendingGrantRequests {
  /**
   * A sequence number of the grants log (0 before the first event).
   * Integer, at least 0.
   */
  seq: number;
  requests: GrantRequest[];
}

/** Where in a repository an action happens: a repo-relative, `/`-separated path ("" for the root). */
export interface RepoPlace {
  /** Non-empty. */
  repo: string;
  /** @default "" */
  path?: string;
}

/** A request and where it stands. */
export interface RequestEntry {
  request: GrantRequest;
  status: RequestStatus;
}

/** Where a request stands. */
export type RequestStatus =
  | { kind: "pending" }
  | { kind: "answered"; grant: Grant }
  | {
      /** A request answered "deny, just this once". */
      kind: "rejected";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
      /** Non-empty. */
      by: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      reason?: string;
    }
  | {
      /** A pending request closed without an answer. */
      kind: "closed";
      /**
       * A grant request's id.
       * From 1 to 512 characters.
       */
      request: string;
      /**
       * Microseconds since the Unix epoch, UTC.
       * Integer.
       */
      at: number;
      why: CloseReason;
    };

/** Who asks: on whose behalf, and the agent or extension that asks. */
export interface Requester {
  /** Non-empty. */
  principal: string;
  /** Non-empty. */
  agent?: string;
  /** Non-empty. */
  extension?: string;
}

/** How an action's resources look and how patterns match them. */
export type ResourceKind = "path" | "command" | "name";

/** The scope of an answer: the request's session or repository (or a path in it), or the host. */
export type ScopeChoice =
  | { kind: "once" }
  | { kind: "session" }
  | {
      kind: "repo";
      /** Non-empty. */
      path?: string;
    }
  | { kind: "host" };

/** A scope a person can choose: once, session, repo or host. */
export type ScopeKind = "once" | "session" | "repo" | "host";
