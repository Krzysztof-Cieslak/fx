// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

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

/** A line that matched a search. */
export interface Match {
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
export type Priority = "low" | "medium" | "high";

/** Every schema construct. */
export interface Shapes {
  any: unknown;
  unit: null;
  flag?: boolean;
  pair: [string, number];
  triple: [boolean, string | null, number[]];
  version: "2.0";
  answer: 42.5;
  negative: -1;
  nothing: null;
  fixed: { on: true; "content-type": "json"; list: [1, "x"] };
  empty: { [key: string]: never };
  either: string | number | boolean;
  neither?: never;
  noChoice?: never;
  noCase?: never;
  attachment:
    | { type: "text"; text: string }
    | {
        type: "image";
        /** Integer. */
        bytes: number;
      }
    | null;
  /** From 1 to 3 items. */
  attachments: (
    | { type: "text"; text: string }
    | {
        type: "image";
        /** Integer. */
        bytes: number;
      }
    | null
  )[];
  grid: (number | null)[][];
  byName: { [key: string]: (string | null)[] };
  byKey: { [key: string]: Span };
  points: [number, number][];
  inline: {
    /** Integer. */
    x: number;
    label?: string;
  };
  nested: { [key: string]: {} };
  /** From 3 to 63 characters. Matches /^[a-z]+(\.[a-z]+)*\/x*\/$/. Format: hostname. */
  host: string;
  /** Number, from 0 to 1. */
  ratio: number;
  /** Number, greater than 0 and less than 1. */
  exclusive: number;
  /** Exactly 2 items. */
  few: string[];
  /** Items: A label. Non-empty. */
  described: string[];
  /** Values: Integer, from 100 to 599. */
  codes?: { [key: string]: number };
  "content-type": string;
  "2fa": boolean;
  default: string;
  $ref: string;
  __proto__?: string;
  ""?: string;
  "é"?: string;
  /** @default "*\/fast" */
  mode?: "fast" | "*/fast";
  /**
   * Bounds for previews of this value.
   *
   * @default {"maxChars":1000,"maxItems":10,"maxDepth":3,"maxStringChars":120}
   */
  limits?: Limits;
  /**
   * Text that ends a comment: *\/ here.
   * A second line.
   *
   *   Indented after a blank line.
   */
  comment: string;
  /** Nullable twice. */
  tree?: Tree | null;
}

/** A range of lines, end exclusive. */
export interface Span {
  /** Integer, at least 1. */
  start: number;
  /** Integer, at least 1. */
  end: number;
}

export type Tree =
  | {
      kind: "leaf";
      /** Integer. */
      value: number;
    }
  | { kind: "node"; label: string; children: Tree[] };

/** How a turn ends. */
export type TurnResult =
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
