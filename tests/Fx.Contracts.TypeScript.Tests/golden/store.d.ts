// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

/** Who sent a command (the principal) and what delivered it. */
export interface Attribution {
  principal: string;
  via: string;
}

/** A stored blob: the hash of its content and its size in bytes. */
export interface BlobRef {
  /**
   * The SHA-256 of a blob's content.
   * Matches /^sha256:[0-9a-fA-F]{64}$/.
   */
  hash: string;
  /** Integer, at least 0. */
  size: number;
}

/** The position of an event: its stream and sequence number. */
export interface EventRef {
  /**
   * An event stream, `kind/id`.
   * Non-empty.
   */
  stream: string;
  /** Integer, at least 1. */
  seq: number;
}

/** An event as stored: its JSON and the schema version that wrote it. */
export interface Payload {
  /** Integer, at least 1. */
  version: number;
  data: unknown;
}

/** An event in a log: its position, the event, and when, who and why. */
export interface RecordedEvent {
  /**
   * An event stream, `kind/id`.
   * Non-empty.
   */
  stream: string;
  /** Integer, at least 1. */
  seq: number;
  event: unknown;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  at: number;
  by: Attribution;
  /**
   * The idempotency key of a command.
   * Non-empty.
   */
  key: string;
  cause?: EventRef;
}
