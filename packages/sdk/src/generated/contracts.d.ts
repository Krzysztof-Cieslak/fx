// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

export interface ActivateParams {
  extension: string;
  manifest?: Manifest;
  root?: string;
}

export interface Activated {
  manifest: Manifest;
  contributions: ContributionDecl[];
  methods: string[];
}

export interface AuthMethodRef {
  /** Non-empty. */
  kind: string;
  /** @default {} */
  settings?: unknown;
}

export interface CacheHints {
  key?: string;
  tools?: CacheTtl;
}

export type CacheTtl = "short" | "long";

export interface CachingCaps {
  /** @default "none" */
  mode?: "none" | "automatic" | "breakpoints";
  /**
   * Integer.
   *
   * @default 0
   */
  maxBreakpoints?: number;
  /** @default false */
  key?: boolean;
  /** @default false */
  longTtl?: boolean;
}

export interface CallParams {
  extension: string;
  method: string;
  params: unknown;
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

export interface Catalog {
  /** @default [] */
  models?: ModelInfo[];
  discover?: string;
}

export interface ChannelSettings {
  /** Integer, at least 1. */
  initialWindow: number;
  /** Integer, from 1 to 16777216. */
  maxFramePayload: number;
  /** Integer, at least 0. */
  maxChannels: number;
}

/** Why a model call failed. */
export type ChatError =
  | {
      kind: "rateLimited";
      /** Integer. */
      retryAfterMs?: number;
      /** @default "" */
      message?: string;
    }
  | {
      kind: "quotaExhausted";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "contextOverflow";
      /** Integer. */
      tokens?: number;
      /** Integer. */
      limit?: number;
      /** @default "" */
      message?: string;
    }
  | {
      kind: "refused";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "authFailed";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "permissionDenied";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "notFound";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "invalidRequest";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "overloaded";
      /** Integer. */
      retryAfterMs?: number;
      /** @default "" */
      message?: string;
    }
  | {
      kind: "serverError";
      /** Integer. */
      status: number;
      /** @default "" */
      message?: string;
    }
  | { kind: "transport"; error: HttpError }
  | {
      kind: "cancelled";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "invalidResponse";
      /** @default "" */
      message?: string;
    }
  | {
      kind: "egressBlocked";
      ask: boolean;
      /** @default [] */
      reasons?: string[];
    }
  | {
      kind: "unavailable";
      /** @default "" */
      message?: string;
    };

/** One event of a streamed model response. */
export type ChatEvent =
  | { kind: "started"; id?: string; model?: string }
  | {
      kind: "partStarted";
      /** Integer, at least 0. */
      index: number;
      part: PartStart;
    }
  | {
      kind: "delta";
      /** Integer, at least 0. */
      index: number;
      text: string;
    }
  | {
      kind: "partState";
      /** Integer, at least 0. */
      index: number;
      state: ProviderState;
    }
  | {
      kind: "partEnded";
      /** Integer, at least 0. */
      index: number;
    }
  | { kind: "usage"; usage: Usage }
  | { kind: "finished"; stop: StopReason }
  | { kind: "failed"; error: ChatError };

/** A failed model response and what arrived before the failure. */
export interface ChatFailure {
  error: ChatError;
  id?: string;
  model?: string;
  /** @default [] */
  parts?: Part[];
  /** @default {"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"reasoning":0} */
  usage?: Usage;
}

/** A request to a chat model, in no provider's terms. */
export interface ChatRequest {
  model: ModelRef;
  /** @default [] */
  system?: Part[];
  messages: Message[];
  /** @default [] */
  tools?: ToolSpec[];
  /** @default {"kind":"auto"} */
  toolChoice?: ToolChoice;
  parallelToolCalls?: boolean;
  /** @default {"stop":[]} */
  sampling?: Sampling;
  reasoning?: ReasoningRequest;
  /** Integer. */
  maxOutputTokens?: number;
  /** @default {"kind":"text"} */
  output?: OutputFormat;
  /** @default {} */
  cache?: CacheHints;
  /** @default {} */
  metadata?: { [key: string]: string };
}

/** A complete model response. */
export interface ChatResponse {
  id?: string;
  model?: string;
  parts: Part[];
  stop: StopReason;
  /** @default {"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"reasoning":0} */
  usage?: Usage;
}

/** A component to install before the extension starts: its id and a version range. */
export interface ComponentRef {
  /** Non-empty. */
  id: string;
  /** Non-empty. */
  version: string;
}

/** A file whose content a part quotes, relative to the workspace (with /) when inside it. */
export interface ContentSource {
  /** Non-empty. */
  path: string;
  lines?: LineRange;
}

/** A contribution: its type and id. Declarative extensions give its value; other runtimes provide it when they activate. */
export interface ContributionDecl {
  /**
   * A contribution type, e.g. "extension-runtime".
   * Matches /^[a-z][a-z0-9-]*([.:\/][a-z0-9][a-z0-9-]*)*$/.
   */
  type: string;
  /**
   * The contribution's id, unique for its type.
   * Matches /^[a-z0-9][a-z0-9._-]*$/.
   */
  id: string;
  value?: unknown;
}

/** Prices in US dollars per million tokens. */
export interface Cost {
  input: number;
  output: number;
  cacheRead?: number;
  cacheWrite?: number;
}

/** What a gate decides: allow, ask a human, or deny. */
export type Decision =
  | { decision: "allow" }
  | { decision: "ask"; reason: string }
  | { decision: "deny"; reason: string };

/** How hard a model reasons. */
export type Effort = "off" | "minimal" | "low" | "medium" | "high" | "max";

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

/** Constants of the extension protocol and the SDK. */
export interface ExtensionConstants {
  rpcVersion: "1";
  protocolVersion: "1";
  sdkVersion: "0.1";
  versionMember: "extensionProtocol";
  servesMember: "serves";
}

export type ExtensionProtocolError =
  | { kind: "unknownExtension"; extension: string }
  | { kind: "notActive"; extension: string }
  | { kind: "activationFailed"; extension: string; reason: string }
  | { kind: "unknownContribution"; address: string }
  | { kind: "inProcessOnly"; address: string }
  | { kind: "unknownMethod"; method: string }
  | { kind: "invalid"; issues: Issue[] }
  | { kind: "methodFailed"; message: string; data: unknown };

export interface ExtensionRef {
  extension: string;
}

/** The requests a host sends to an extension process: params, result, and the typed error carried as the data of an ApplicationError (-32000). */
export interface ExtensionRequests {
  /** Activates an extension the process serves, or loads a package with a runtime it contributes. */
  "extension/activate": { params: ActivateParams; result: Activated; error: ExtensionProtocolError };
  /** Runs a contribution's code: a gate's check, a transform, an observer. */
  "extension/invoke": {
    params: InvokeParams;
    result: { output: unknown };
    error: ExtensionProtocolError;
  };
  /** Calls a method of an extension's RPC contract. */
  "extension/call": {
    params: CallParams;
    result: { output: unknown };
    error: ExtensionProtocolError;
  };
  /** Deactivates an extension. */
  "extension/deactivate": { params: ExtensionRef; result: null; error: ExtensionProtocolError };
}

export type HttpError =
  | { kind: "invalidRequest"; reason: string }
  | { kind: "refused"; reason: string }
  | { kind: "connectFailed"; reason: string }
  | { kind: "reset"; reason: string }
  | { kind: "timedOut"; phase: "response" | "body" };

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

export interface InvokeParams {
  extension: string;
  type: string;
  id: string;
  input: unknown;
}

/** A problem in a value: where it is (keys and indexes from the root) and what is wrong. */
export interface Issue {
  path: (string | number)[];
  message: string;
}

/** How to start a process: an extension process, or a runtime. exe and args are templates, e.g. ${root} (the package directory) and ${exeSuffix}. */
export interface LaunchSpec {
  /** Non-empty. */
  exe: string;
  /** @default [] */
  args?: string[];
  /**
   * Items: An environment variable name; in deny lists, a trailing * matches any suffix. Matches /^[A-Za-z_][A-Za-z0-9_]*\*?$/.
   *
   * @default []
   */
  envAllowlist?: string[];
  /** @default {} */
  envSet?: { [key: string]: string };
  /**
   * Items: An environment variable name; in deny lists, a trailing * matches any suffix. Matches /^[A-Za-z_][A-Za-z0-9_]*\*?$/.
   *
   * @default []
   */
  envDeny?: string[];
  /** @default "owned" */
  cwd?: "owned" | "workspace";
  /** Runtime flags (--name=value) added before the program while the project is not trusted. */
  untrustedOverrides?: string[];
}

/** Lines of a file, 1-based and inclusive. */
export interface LineRange {
  /** Integer, at least 1. */
  first: number;
  /** Integer, at least 1. */
  last: number;
}

/** An extension package's manifest. */
export interface Manifest {
  /**
   * An extension id: dotted lowercase segments, e.g. "acme.tools".
   * Matches /^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)*$/.
   */
  id: string;
  /**
   * A semantic version, e.g. "1.2.0".
   * Matches /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$/.
   */
  version: string;
  description?: string;
  /** The SDK API version the extension is built against, e.g. "0.1". */
  sdk: string;
  /**
   * The extension protocol version it speaks, e.g. "1".
   * Non-empty.
   */
  protocol: string;
  /**
   * Where the extension runs: in the host, in an environment (envd) or in a client.
   *
   * @default "host"
   */
  placement?: "host" | "environment" | "client";
  /**
   * The extension runtime: declarative, native, process, or a contributed one.
   * Matches /^[a-z][a-z0-9-]*$/.
   */
  runtime: string;
  launch?: LaunchSpec;
  /** @default [] */
  requires?: ComponentRef[];
  /**
   * Items: A capability name, e.g. "fs.read". Matches /^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$/.
   *
   * @default []
   */
  capabilities?: string[];
  /** @default [] */
  contributions?: ContributionDecl[];
  /** @default {"methods":[]} */
  rpc?: RpcContract;
  /**
   * Items: An extension id: dotted lowercase segments, e.g. "acme.tools". Matches /^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)*$/.
   *
   * @default []
   */
  replaces?: string[];
  hash?: string;
}

export type MediaKind = "image" | "pdf" | "audio" | "video";

export type MediaSource =
  | { kind: "base64"; data: string }
  | { kind: "url"; url: string }
  | { kind: "blob"; digest: string };

export interface Message {
  role: Role;
  parts: Part[];
}

/** A method of the extension's RPC contract; params and result are JSON Schemas. */
export interface MethodDecl {
  /**
   * The method's name; clients call ext/<extension id>/<name>.
   * Matches /^[a-z][A-Za-z0-9]*(\/[a-z][A-Za-z0-9]*)*$/.
   */
  name: string;
  description?: string;
  params?: unknown;
  result?: unknown;
}

/** What a model can do. */
export interface ModelCaps {
  reasoning?: ReasoningCaps;
  /** @default {"calls":false,"parallel":false,"forced":false,"strict":false} */
  tools?: ToolCaps;
  /** @default false */
  structuredOutput?: boolean;
  /** @default false */
  logprobs?: boolean;
  /** @default {"mode":"none","maxBreakpoints":0,"key":false,"longTtl":false} */
  caching?: CachingCaps;
  /** @default [] */
  media?: MediaKind[];
  /** @default {"temperature":false,"topP":false,"topK":false,"seed":false,"stop":false} */
  sampling?: SamplingCaps;
  /** @default ["sse"] */
  transports?: ("sse" | "websocket" | "http")[];
}

/** A model in a catalog: its limits, capabilities and prices. */
export interface ModelInfo {
  ref: ModelRef;
  /** @default "chat" */
  kind?: ModelKind;
  family?: string;
  name?: string;
  limits: ModelLimits;
  /** @default {"tools":{"calls":false,"parallel":false,"forced":false,"strict":false},"structuredOutput":false,"logprobs":false,"caching":{"mode":"none","maxBreakpoints":0,"key":false,"longTtl":false},"media":[],"sampling":{"temperature":false,"topP":false,"topK":false,"seed":false,"stop":false},"transports":["sse"]} */
  caps?: ModelCaps;
  cost?: Cost;
  /** @default false */
  planBacked?: boolean;
}

export type ModelKind = "chat" | "decision" | "embedding";

/** Token limits: the context window and the most output tokens per response. */
export interface ModelLimits {
  /** Integer, at least 1. */
  context: number;
  /** Integer, at least 1. */
  output: number;
}

/** A model: `provider/model`. */
export type ModelRef = string;

/** What extension/invoke and extension/call answer: the output, wrapped so that null and scalars travel as results too. */
export interface Output {
  output: unknown;
}

export type OutputFormat =
  | { kind: "text" }
  | {
      kind: "json";
      name: string;
      schema: unknown;
      /** @default false */
      strict?: boolean;
    };

/** A typed part of a message. */
export type Part =
  | {
      kind: "text";
      text: string;
      state?: ProviderState;
      /** The files its content quotes (provenance for content exclusions; never sent). */
      sources?: ContentSource[];
    }
  | {
      kind: "media";
      media: MediaKind;
      mediaType: string;
      source: MediaSource;
      name?: string;
      /** The files its content quotes (provenance for content exclusions; never sent). */
      sources?: ContentSource[];
    }
  | { kind: "reasoning"; text: string; state?: ProviderState }
  | {
      kind: "toolCall";
      id: string;
      name: string;
      arguments: string;
      state?: ProviderState;
    }
  | {
      kind: "toolResult";
      callId: string;
      name: string;
      content: ToolContent[];
      /** @default false */
      isError?: boolean;
      /** The files its content quotes (provenance for content exclusions; never sent). */
      sources?: ContentSource[];
    }
  | { kind: "refusal"; text: string }
  | {
      kind: "cacheBreakpoint";
      /** @default "short" */
      ttl?: CacheTtl;
    };

export type PartStart =
  | { kind: "text" }
  | { kind: "reasoning" }
  | { kind: "toolCall"; id: string; name: string }
  | { kind: "refusal" };

export interface PeerInfo {
  name: string;
  version: string;
}

/** A model provider: endpoints per wire family, catalog, auth methods and quirks. */
export interface ProviderDef {
  /** Matches /^[a-z0-9][a-z0-9.-]*$/. */
  id: string;
  name: string;
  endpoints: { [key: string]: string };
  /** @default {"models":[]} */
  catalog?: Catalog;
  /** @default [] */
  auth?: AuthMethodRef[];
  /** @default {} */
  quirks?: { [key: string]: unknown };
  transform?: RequestTransform;
}

/** Opaque provider state, replayed byte for byte. */
export interface ProviderState {
  family: string;
  model: ModelRef;
  kind: string;
  data: string;
}

export interface ReasoningCaps {
  /** @default [] */
  efforts?: Effort[];
  budget?: TokenRange;
  /** @default false */
  visible?: boolean;
  /** @default false */
  replay?: boolean;
}

export interface ReasoningRequest {
  effort?: Effort;
  /** Integer. */
  budgetTokens?: number;
  /** @default false */
  visible?: boolean;
}

export interface RequestTransform {
  /** @default {} */
  setHeaders?: { [key: string]: string };
  /** @default [] */
  removeHeaders?: string[];
  /** @default {} */
  setQuery?: { [key: string]: string };
  /** @default [] */
  body?: (
    | {
        kind: "set";
        /** Non-empty. */
        path: string[];
        value: unknown;
      }
    | {
        kind: "remove";
        /** Non-empty. */
        path: string[];
      }
  )[];
}

export type Role = "user" | "assistant";

export interface RpcContract {
  /** @default [] */
  methods?: MethodDecl[];
}

export interface Sampling {
  temperature?: number;
  topP?: number;
  /** Integer. */
  topK?: number;
  /** Integer. */
  seed?: number;
  /** @default [] */
  stop?: string[];
}

export interface SamplingCaps {
  /** @default false */
  temperature?: boolean;
  /** @default false */
  topP?: boolean;
  /** @default false */
  topK?: boolean;
  /** @default false */
  seed?: boolean;
  /** @default false */
  stop?: boolean;
}

/** The JSON shape of a value (a codec's schema), tagged by kind; every node may carry a description. */
export type Schema =
  | { kind: "any"; description?: string }
  | { kind: "null"; description?: string }
  | { kind: "boolean"; description?: string }
  | {
      kind: "integer";
      minimum?: SchemaBound;
      maximum?: SchemaBound;
      description?: string;
    }
  | {
      kind: "number";
      minimum?: SchemaBound;
      maximum?: SchemaBound;
      description?: string;
    }
  | {
      kind: "string";
      /** Integer, at least 0. */
      minLength?: number;
      /** Integer, at least 0. */
      maxLength?: number;
      pattern?: string;
      format?: string;
      description?: string;
    }
  | { kind: "const"; value: unknown; description?: string }
  | { kind: "enum"; values: string[]; description?: string }
  | {
      kind: "array";
      items: Schema;
      /** Integer, at least 0. */
      minItems?: number;
      /** Integer, at least 0. */
      maxItems?: number;
      description?: string;
    }
  | { kind: "tuple"; items: Schema[]; description?: string }
  | { kind: "map"; values: Schema; description?: string }
  | {
      kind: "object";
      fields: SchemaField[];
      /**
       * What a decoder does with members that are not fields: ignore them, or reject each one.
       *
       * @default "ignore"
       */
      unknownFields?: "ignore" | "reject";
      description?: string;
    }
  | {
      kind: "union";
      discriminator: string;
      cases: SchemaCase[];
      description?: string;
    }
  | { kind: "anyOf"; schemas: Schema[]; description?: string }
  | { kind: "nullable"; schema: Schema; description?: string }
  | {
      kind: "ref";
      /**
       * A type name: an identifier.
       * Matches /^[A-Za-z_][A-Za-z0-9_]*$/.
       */
      name: string;
      description?: string;
    }
  | {
      kind: "named";
      /**
       * A type name: an identifier.
       * Matches /^[A-Za-z_][A-Za-z0-9_]*$/.
       */
      name: string;
      body: Schema;
      description?: string;
    };

/** A bound on a number: inclusive (minimum, maximum) or exclusive. */
export type SchemaBound =
  | { kind: "inclusive"; value: number }
  | { kind: "exclusive"; value: number };

/** A case of a union: its tag and the fields next to the tag. */
export interface SchemaCase {
  tag: string;
  description?: string;
  fields: SchemaField[];
  /**
   * What a decoder does with members that are not fields: ignore them, or reject each one.
   *
   * @default "ignore"
   */
  unknownFields?: "ignore" | "reject";
}

/** A field of an object or of a union case. */
export interface SchemaField {
  name: string;
  schema: Schema;
  presence: SchemaPresence;
}

/** Whether a field must be present: required; optional (absent means no value); or defaulted (absent means this value, and it is always written). */
export type SchemaPresence =
  | { kind: "required" }
  | { kind: "optional" }
  | { kind: "defaulted"; value: unknown };

/** A variant slot: its id, what it changes, its default and the values it may take. */
export interface SlotDecl {
  /** Matches /^[a-z][A-Za-z0-9_-]*(\.[a-z][A-Za-z0-9_-]*)+$/. */
  id: string;
  description: string;
  default: unknown;
  choices?: unknown[];
  /** @default false */
  privileged?: boolean;
}

/** Why a response ended normally. */
export type StopReason =
  | { kind: "endTurn" }
  | { kind: "toolUse" }
  | { kind: "maxTokens" }
  | { kind: "stopSequence"; sequence?: string }
  | { kind: "refusal" }
  | { kind: "contentFilter" }
  | { kind: "contextWindow" }
  | { kind: "pause" }
  | { kind: "other"; reason: string };

export interface TokenRange {
  /** Integer. */
  min: number;
  /** Integer. */
  max: number;
}

export interface ToolCaps {
  /** @default false */
  calls?: boolean;
  /** @default false */
  parallel?: boolean;
  /** @default false */
  forced?: boolean;
  /** @default false */
  strict?: boolean;
}

export type ToolChoice =
  | { kind: "auto" }
  | { kind: "none" }
  | { kind: "required" }
  | { kind: "tool"; name: string };

export type ToolContent =
  | { kind: "text"; text: string }
  | {
      kind: "media";
      media: MediaKind;
      mediaType: string;
      source: MediaSource;
      name?: string;
      /** The files its content quotes (provenance for content exclusions; never sent). */
      sources?: ContentSource[];
    };

/** A tool the model may call, with the JSON Schema of its input. */
export interface ToolSpec {
  /** Matches /^[A-Za-z0-9_-]{1,64}$/. */
  name: string;
  /** @default "" */
  description?: string;
  parameters: unknown;
  /** @default false */
  strict?: boolean;
}

/** A transform's settings: its priority. */
export interface TransformSettings {
  /**
   * Integer.
   *
   * @default 0
   */
  priority?: number;
}

/** Tokens used: input (cached included), output (reasoning included), and their parts. */
export interface Usage {
  /**
   * Integer.
   *
   * @default 0
   */
  input?: number;
  /**
   * Integer.
   *
   * @default 0
   */
  output?: number;
  /**
   * Integer.
   *
   * @default 0
   */
  cacheRead?: number;
  /**
   * Integer.
   *
   * @default 0
   */
  cacheWrite?: number;
  /**
   * Integer.
   *
   * @default 0
   */
  reasoning?: number;
}
