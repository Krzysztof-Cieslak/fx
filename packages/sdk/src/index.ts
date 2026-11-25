// The public TypeScript SDK (spec §16.1): codecs that produce the same schemas as the F# ones, the
// contracts an extension sees (types generated from the F# codecs), the extension API, and the
// extension protocol's server. See docs/modules/sdk.md.

export * from "./codec.ts";
export * from "./extension.ts";
export * from "./manifest.ts";
export * from "./protocol.ts";
export * from "./rpc.ts";
export * from "./server.ts";
export * from "./stdio.ts";
export { canonicalize, describe as describeJson, equalJson, type Json, type PathSegment, quote, renderPath, serializeCanonical } from "./json.ts";
export { definitions, toJsonSchema, typeName, type Schema, type SchemaBound, type SchemaCase, type SchemaField, type SchemaPresence } from "./schema.ts";
export { extensionId, manifestFileName, productName, productVersion, protocolVersion, sdkVersion } from "./generated/product.ts";
export type {
  Capabilities,
  ChannelSettings,
  ErrorCodes,
  ExtensionConstants,
  ExtensionRequests,
  InitializeParams,
  InitializeResult,
  TransformSettings,
} from "./generated/contracts.d.ts";
