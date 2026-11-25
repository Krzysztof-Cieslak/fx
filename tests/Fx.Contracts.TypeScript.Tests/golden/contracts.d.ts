// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

/** A problem in a value: where it is (keys and indexes from the root) and what is wrong. */
export interface Issue {
  path: (string | number)[];
  message: string;
}

/** Bounds for a preview. */
export interface Limits {
  /** Integer, at least 0. */
  maxChars: number;
  /** Integer, at least 0. */
  maxItems: number;
  /** Integer, at least 0. */
  maxDepth: number;
  /** Integer, at least 0. */
  maxStringChars: number;
}

/** A bounded rendering of a value; truncated when something was left out. */
export interface Preview {
  text: string;
  truncated: boolean;
}
