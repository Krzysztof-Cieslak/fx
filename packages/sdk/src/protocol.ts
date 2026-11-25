// The extension protocol's messages (spec §17, docs/modules/extensions.md), as runtime codecs that
// mirror Fx.Sdk.Process's `ExtensionProtocol.Codecs` (their schemas are compared with the F# ones in
// test/fixtures/protocol.json), and the contracts extension code exchanges with the host: issues,
// decisions, transform settings and variant slot declarations.

import { Codec, createCodec, expected, type Issue, renderIssues, Validation } from "./codec.ts";
import type {
  ActivateParams,
  Activated,
  CallParams,
  Decision,
  ExtensionConstants,
  ExtensionProtocolError,
  ExtensionRef,
  InvokeParams,
  Output,
  SlotDecl,
} from "./generated/contracts.d.ts";
import { protocolVersion, sdkVersion } from "./generated/product.ts";
import { Manifest } from "./manifest.ts";

export type {
  ActivateParams,
  Activated,
  CallParams,
  ExtensionProtocolError,
  ExtensionRef,
  InvokeParams,
  Output,
  SlotDecl,
} from "./generated/contracts.d.ts";

/** A key as a string, an index as a non-negative integer (`Codecs.pathSegment`). */
const pathSegment = createCodec<string | number>(
  {
    kind: "anyOf",
    schemas: [{ kind: "string" }, { kind: "integer", minimum: { kind: "inclusive", value: 0 } }],
  },
  (segment) => segment,
  (json) =>
    typeof json === "string" || (typeof json === "number" && Number.isInteger(json) && json >= 0)
      ? Validation.ok(json)
      : Validation.fail([expected("string or non-negative integer", json)]),
);

/** `{"path": ["items", 2], "message": "…"}` (`Codecs.issue`). */
export const issueCodec: Codec<Issue> = Codec.record({ path: Codec.array(pathSegment), message: Codec.string })
  .named("Issue")
  .describe("A problem in a value: where it is (keys and indexes from the root) and what is wrong.");

/** What a gate decides (`Decision.codec`). */
export const decisionCodec: Codec<Decision> = Codec.union("decision", {
  allow: {},
  ask: { reason: Codec.string },
  deny: { reason: Codec.string },
})
  .describe("What a gate decides: allow, ask a human, or deny.")
  .named("Decision");

/** `{"priority": n}`: a transform's JSON form (`Transform.settings`). */
export const transformSettings: Codec<{ priority: number }> = Codec.record({ priority: Codec.withDefault(Codec.int, 0) }).describe(
  "Lower priorities run first.",
);

/** A variant slot an extension declares (`SlotInfo.declCodec`): enumerated, with its choices. */
export const slotDeclCodec: Codec<SlotDecl> = Codec.record({
  id: Codec.string.pattern("^[a-z][A-Za-z0-9_-]*(\\.[a-z][A-Za-z0-9_-]*)+$"),
  description: Codec.string,
  default: Codec.json,
  choices: Codec.optional(Codec.array(Codec.json)),
  privileged: Codec.withDefault(Codec.bool, false),
})
  .describe("A variant slot: its id, what it changes, its default and the values it may take.")
  .named("SlotDecl") as Codec<SlotDecl>;

const issues = Codec.array(issueCodec);

/** The protocol's typed errors, by `kind` (`ExtensionProtocol.Codecs.protocolError`). */
export const protocolErrorCodec: Codec<ExtensionProtocolError> = Codec.union("kind", {
  unknownExtension: { extension: Codec.string },
  notActive: { extension: Codec.string },
  activationFailed: { extension: Codec.string, reason: Codec.string },
  unknownContribution: { address: Codec.string },
  inProcessOnly: { address: Codec.string },
  unknownMethod: { method: Codec.string },
  invalid: { issues },
  methodFailed: { message: Codec.string, data: Codec.json },
}).named("ExtensionProtocolError") as Codec<ExtensionProtocolError>;

/** The message of a typed error, as the host reports it (`ProtocolError.ToString`). */
export function protocolErrorMessage(error: ExtensionProtocolError): string {
  switch (error.kind) {
    case "unknownExtension":
      return "unknown extension " + error.extension;
    case "notActive":
      return error.extension + " is not active";
    case "activationFailed":
      return error.reason;
    case "unknownContribution":
      return "unknown contribution " + error.address;
    case "inProcessOnly":
      return error.address + " runs only in its process";
    case "unknownMethod":
      return "unknown method " + error.method;
    case "invalid":
      return "invalid:\n" + renderIssues(error.issues, 10);
    case "methodFailed":
      return error.message;
  }
}

export const activateParamsCodec: Codec<ActivateParams> = Codec.record({
  extension: Codec.string,
  manifest: Codec.optional(Manifest.codec),
  root: Codec.optional(Codec.string),
}).named("ActivateParams");

export const activatedCodec: Codec<Activated> = Codec.record({
  manifest: Manifest.codec,
  contributions: Codec.array(Manifest.contributionDecl),
  methods: Codec.array(Codec.string),
}).named("Activated");

export const invokeParamsCodec: Codec<InvokeParams> = Codec.record({
  extension: Codec.string,
  type: Codec.string,
  id: Codec.string,
  input: Codec.json,
}).named("InvokeParams") as Codec<InvokeParams>;

export const outputCodec: Codec<Output> = Codec.record({ output: Codec.json }) as Codec<Output>;

export const callParamsCodec: Codec<CallParams> = Codec.record({
  extension: Codec.string,
  method: Codec.string,
  params: Codec.json,
}).named("CallParams") as Codec<CallParams>;

export const extensionRefCodec: Codec<ExtensionRef> = Codec.record({ extension: Codec.string }).named("ExtensionRef");

/**
 * Constants of the protocol, checked against the generated literal types: a change on the F# side
 * that this file does not follow is a type error.
 */
export const extensionConstants: ExtensionConstants = {
  rpcVersion: "1",
  protocolVersion,
  sdkVersion,
  versionMember: "extensionProtocol",
  servesMember: "serves",
};

/** The protocol's methods, its versions and the `initialize` members (`ExtensionProtocol`). */
export const ExtensionProtocol = {
  /** The extension protocol versions this SDK speaks. */
  versions: [extensionConstants.protocolVersion] as readonly string[],
  /** The `initialize` member that carries the host's extension protocol version. */
  versionMember: extensionConstants.versionMember,
  /** The `initialize` result member listing the extensions a process serves. */
  servesMember: extensionConstants.servesMember,
  methods: {
    activate: "extension/activate",
    invoke: "extension/invoke",
    call: "extension/call",
    deactivate: "extension/deactivate",
  },
} as const;
