// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

/** Something a cell did, reported by a kernel-side wrapper. */
export interface Activity {
  /** Non-empty. */
  kind: string;
  title: string;
  detail?: unknown;
}

/** How cells see host calls: awaited, or as synchronous calls. */
export type AsyncHostCalls = "await" | "transparent";

export interface BlobRef {
  /**
   * The SHA-256 of a blob's content.
   * Matches /^sha256:[0-9a-fA-F]{64}$/.
   */
  hash: string;
  /** Integer, at least 0. */
  size: number;
}

/** Text kept within a bound: its head and tail, and how much there was. */
export interface BoundedText {
  text: string;
  truncated: boolean;
  /** Integer, at least 0. */
  totalChars: number;
}

export interface Cancelled {
  wasRunning: boolean;
}

export interface Capabilities {
  /** Integer, at least 1024. */
  maxMessageSize: number;
  /** Integer, at least 1. */
  maxPendingRequests?: number;
  channels?: ChannelSettings;
  /** @default [] */
  methods?: string[];
}

export interface CellActivity {
  cellId?: string;
  activity: Activity;
  /** @default false */
  late?: boolean;
}

/** Type-checks a cell without running it. */
export interface CellCheck {
  /**
   * A cell id: 1 to 64 of A-Z a-z 0-9 _ -, unique per kernel.
   * Matches /^[A-Za-z0-9_-]{1,64}$/.
   */
  cellId: string;
  code: string;
}

/** A 1-based position in a cell. */
export interface CellLocation {
  cellId: string;
  /** Integer, at least 1. */
  line: number;
  /** Integer, at least 0. */
  column: number;
}

/** How a cell ended. */
export type CellOutcome =
  | {
      /** It ran to the end; the value of its last expression, if any. */
      kind: "completed";
      value?: ValueInfo;
    }
  | {
      kind: "syntaxError";
      message: string;
      /** Integer, at least 0. */
      line?: number;
      /** Integer, at least 0. */
      column?: number;
      lineText?: string;
    }
  | { kind: "typeError"; diagnostics: Diagnostic[] }
  | {
      kind: "runtimeError";
      name: string;
      message: string;
      code?: string;
      location?: CellLocation;
      stack: string;
    }
  | {
      /** It ran longer than its timeoutMs. */
      kind: "timedOut";
      /** Integer, at least 0. */
      afterMs: number;
    }
  | {
      /** cell/cancel stopped it. */
      kind: "cancelled";
    };

/** Output of a cell, as it happens; late output came after the cell's result. */
export interface CellOutput {
  cellId?: string;
  kind: OutputKind;
  text: string;
  /** @default false */
  late?: boolean;
}

export interface CellRef {
  cellId: string;
}

/** How a cell ran. */
export interface CellResult {
  cellId: string;
  outcome: CellOutcome;
  output: BoundedText;
  /** @default [] */
  declared?: string[];
  /** Declared names that hold no value because the cell stopped first (an error, a timeout, a cancellation); absent when none. */
  unassigned?: string[];
  /** @default false */
  finished?: boolean;
  /**
   * Integer, at least 0.
   *
   * @default 0
   */
  notified?: number;
  /**
   * Integer, at least 0.
   *
   * @default 0
   */
  durationMs?: number;
}

/** Runs a cell. */
export interface CellRun {
  /**
   * A cell id: 1 to 64 of A-Z a-z 0-9 _ -, unique per kernel.
   * Matches /^[A-Za-z0-9_-]{1,64}$/.
   */
  cellId: string;
  code: string;
  intent?: string;
  /**
   * The cell's own time limit; time spent waiting for the host to answer its host calls does not count.
   * Integer, at least 1.
   */
  timeoutMs?: number;
  /** @default false */
  check?: boolean;
}

export interface ChannelSettings {
  /** Integer, at least 1. */
  initialWindow: number;
  /** Integer, from 1 to 16777216. */
  maxFramePayload: number;
  /** Integer, at least 0. */
  maxChannels: number;
}

export interface Checked {
  diagnostics: Diagnostic[];
}

/** Binds a value to a top-level variable. */
export interface DecodeParams {
  /** Non-empty. */
  name: string;
  value: Value;
  type?: string;
}

/** A type-checking diagnostic; line 0 is code carried over from earlier cells. */
export interface Diagnostic {
  /** Integer, at least 0. */
  line: number;
  /** Integer, at least 0. */
  column: number;
  code: string;
  message: string;
}

/** Encodes a value the kernel holds. */
export interface EncodeParams {
  ref: ValueRef;
  /** Integer, at least 1. */
  maxBytes?: number;
}

/** The JSON-RPC error codes (docs/modules/rpc.md). */
export interface ErrorCodes {
  parseError: -32700;
  invalidRequest: -32600;
  methodNotFound: -32601;
  invalidParams: -32602;
  internalError: -32603;
  applicationError: -32000;
  requestCancelled: -32001;
  notInitialized: -32002;
  overloaded: -32003;
  unsupportedVersion: -32004;
  responseTooLarge: -32005;
  shuttingDown: -32006;
}

/** A cell ends the turn with a result for the host to validate. */
export interface Finish {
  cellId?: string;
  result: Value;
}

/** The result does not decode, or fails a check; the issues say why. */
export interface FinishRejected {
  issues: Issue[];
}

/** A cell calls a binding of the host bridge. */
export interface HostCall {
  cellId?: string;
  /** Non-empty. */
  binding: string;
  /** @default [] */
  args?: Value[];
}

/** The typed errors of host/call; the cell gets an error whose code is the kind. */
export type HostCallError =
  | { kind: "unknownBinding"; binding: string }
  | { kind: "invalidArguments"; issues: Issue[] }
  | {
      kind: "unknownHandle";
      /**
       * An opaque handle id, issued by the host.
       * From 1 to 128 characters. Matches /^[!-~]+$/.
       */
      handle: string;
    }
  | { kind: "denied"; reason: string }
  | { kind: "failed"; code: string; message: string; data?: unknown };

/** The requests a kernel sends to its host: params, result, and the typed error carried as the data of an ApplicationError (-32000). */
export interface HostRequests {
  /** A cell calls a binding of the host bridge. */
  "host/call": { params: HostCall; result: Value; error: HostCallError };
  /** A cell ends the turn; the host validates the result. */
  finish: { params: Finish; result: null; error: FinishRejected };
}

export interface InitializeParams {
  /** Non-empty. */
  protocolVersions: string[];
  peer: PeerInfo;
  capabilities: Capabilities;
}

export interface InitializeResult {
  protocolVersion: string;
  peer: PeerInfo;
  capabilities: Capabilities;
}

/** What the kernel can do (spec §6.2 InterpreterCaps). */
export interface InterpreterCaps {
  snapshot: boolean;
  asyncHostCalls: AsyncHostCalls;
  packages?: PackageSupport;
  deterministic: boolean;
}

export interface Issue {
  path: (string | number)[];
  message: string;
}

/** Constants of the kernel protocol. */
export interface KernelConstants {
  rpcVersion: "1";
  kernelVersion: "1";
  initializeMember: "kernel";
  cellIdPattern: "^[A-Za-z0-9_-]{1,64}$";
}

/** The typed errors of the methods the host calls on a kernel. */
export type KernelError =
  | { kind: "busy"; running: string }
  | { kind: "duplicateCell"; cellId: string }
  | { kind: "invalidCellId"; cellId: string }
  | { kind: "unknownCell"; cellId: string }
  | { kind: "notFound"; ref: ValueRef }
  | { kind: "notEncodable"; type: string; reason: string }
  | {
      kind: "tooLarge";
      /** Integer. */
      bytes: number;
      /** Integer. */
      limit: number;
    }
  | { kind: "invalidName"; name: string }
  | { kind: "invalidValue"; reason: string };

/** The kernel's `kernel` member of the `initialize` result. */
export interface KernelInfo {
  version: string;
  /** Non-empty. */
  language: string;
  caps: InterpreterCaps;
  details?: unknown;
}

/** Bounds of a kernel's output and previews. */
export interface KernelLimits {
  /** Integer, at least 1. */
  outputChars: number;
  /** Bounds for a preview. */
  preview: Limits;
  /** Integer, at least 1. */
  inlineBytes: number;
  /**
   * The largest JSON (UTF-8 bytes) of one host call's argument; inlineBytes when absent.
   * Integer, at least 1.
   */
  argumentBytes?: number;
}

/** The notifications a kernel sends to its host. */
export interface KernelNotifications {
  /** Output of a cell, as it happens. */
  "cell/output": CellOutput;
  /** Activity of a cell, from kernel-side wrappers. */
  "cell/activity": CellActivity;
}

/** The requests a host sends to a kernel: params, result, and the typed error carried as the data of an ApplicationError (-32000). */
export interface KernelRequests {
  /** Runs a cell. $/cancelRequest on it interrupts the cell. */
  "cell/run": { params: CellRun; result: CellResult; error: KernelError };
  /** Interrupts a running cell and stops what cells left running. */
  "cell/cancel": { params: CellRef; result: Cancelled; error: KernelError };
  /** Optional: type-checks a cell without running it. */
  "cell/check": { params: CellCheck; result: Checked; error: KernelError };
  /** A bounded preview of a value the kernel holds. */
  "value/preview": { params: PreviewParams; result: ValueInfo; error: KernelError };
  /** A value the kernel holds, encoded. */
  "value/encode": { params: EncodeParams; result: Value; error: KernelError };
  /** Binds a value to a top-level variable. */
  "value/decode": { params: DecodeParams; result: ValueInfo; error: KernelError };
  /** Restores the completion value of a cell (Out[cellId]). */
  recall: { params: RecallParams; result: ValueInfo; error: KernelError };
  /** Optional (caps.snapshot): the kernel's state. */
  snapshot: { params: null; result: KernelState; error: KernelError };
  /** Optional (caps.snapshot): restores a state. */
  restore: { params: KernelState; result: null; error: KernelError };
}

/** The host's `kernel` member of the `initialize` params. */
export interface KernelSetup {
  /** Non-empty. */
  versions: string[];
  /** Non-empty. */
  global: string;
  bindings: string[];
  moduleBase?: string;
  limits: KernelLimits;
}

/** A kernel's state, in its own format. */
export interface KernelState {
  state: Value;
}

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

export type OutputKind = "text" | "error";

/** How the kernel installs packages for a session. */
export interface PackageSupport {
  /** Non-empty. */
  manager: string;
}

export interface PeerInfo {
  name: string;
  version: string;
}

export interface Preview {
  text: string;
  truncated: boolean;
}

/** Previews a value the kernel holds. */
export interface PreviewParams {
  ref: ValueRef;
  /** Bounds for a preview. */
  limits?: Limits;
}

/** Restores the completion value of a cell (Out[cellId]). */
export interface RecallParams {
  /**
   * A cell id: 1 to 64 of A-Z a-z 0-9 _ -, unique per kernel.
   * Matches /^[A-Za-z0-9_-]{1,64}$/.
   */
  cellId: string;
  value: Value;
}

/** A value crossing a process boundary: copied when small, referenced otherwise. */
export type Value =
  | {
      /** Small: copied. */
      kind: "inline";
      /** The value itself, as JSON. */
      value: unknown;
    }
  | {
      /** Large: content-addressed in the environment, read lazily. */
      kind: "blob";
      /** A stored blob: the hash of its content and its size in bytes. */
      blob: BlobRef;
      /** A bounded rendering of a value; truncated when something was left out. */
      preview: Preview;
    }
  | {
      /** A live object that stays on the host; pass it back as it is. */
      kind: "handle";
      /**
       * An opaque handle id, issued by the host.
       * From 1 to 128 characters. Matches /^[!-~]+$/.
       */
      handle: string;
      /**
       * The name of a declared type.
       * Non-empty.
       */
      type: string;
      /** A bounded rendering of a value; truncated when something was left out. */
      preview: Preview;
    };

/** What the model sees of a kernel value: its type and a bounded preview. */
export interface ValueInfo {
  type: string;
  /** A bounded rendering of a value; truncated when something was left out. */
  preview: Preview;
}

/** Where the kernel keeps a value. */
export type ValueRef =
  | {
      /** A top-level variable. */
      kind: "variable";
      /** Non-empty. */
      name: string;
    }
  | {
      /** The completion value of a cell (`Out[cellId]`). */
      kind: "output";
      /** Non-empty. */
      cellId: string;
    };
