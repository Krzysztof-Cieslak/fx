// Generated from Fx.Product and Fx.Sdk.Versions by tests/Fx.Sdk.Process.Tests. Do not edit: change the
// constants and run the tests with UPDATE_GOLDEN=1.

/** The product name: the build-time constant every user-visible name derives from. */
export const productName = "fx";

/** The product's version. */
export const productVersion = "0.0.0";

/** The extension API version this SDK implements (`Versions.Sdk`): a manifest's `sdk`. */
export const sdkVersion = "0.1";

/** The extension protocol version this SDK speaks (`Versions.Protocol`): a manifest's `protocol`. */
export const protocolVersion = "1";

/** The file name of a manifest in a package directory. */
export const manifestFileName = "fx.extension.json";

/** A bundled extension's id: `extensionId("sample")` is `fx.sample`. */
export function extensionId(localId: string): string {
  return "fx." + localId;
}
