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

export interface ChannelSettings {
  /** Integer, at least 1. */
  initialWindow: number;
  /** Integer, from 1 to 16777216. */
  maxFramePayload: number;
  /** Integer, at least 0. */
  maxChannels: number;
}

/** A component to install before the extension starts: its id and a version range. */
export interface ComponentRef {
  /** Non-empty. */
  id: string;
  /** Non-empty. */
  version: string;
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

/** What a gate decides: allow, ask a human, or deny. */
export type Decision =
  | { decision: "allow" }
  | { decision: "ask"; reason: string }
  | { decision: "deny"; reason: string };

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

/** How the process runtime starts the extension. exe and args may use ${root} (the package directory) and ${exeSuffix}. */
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

/** What extension/invoke and extension/call answer: the output, wrapped so that null and scalars travel as results too. */
export interface Output {
  output: unknown;
}

export interface PeerInfo {
  name: string;
  version: string;
}

export interface RpcContract {
  /** @default [] */
  methods?: MethodDecl[];
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

/** A transform's settings: its priority. */
export interface TransformSettings {
  /**
   * Integer.
   *
   * @default 0
   */
  priority?: number;
}
