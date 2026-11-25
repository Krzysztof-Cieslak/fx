// Generated from Fx.Product, Fx.Sdk.Versions and Fx.Sdk.InjectionVectors by tests/Fx.Sdk.Process.Tests.
// Do not edit: change the constants and run the tests with UPDATE_GOLDEN=1.

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

/**
 * The runtime injection vectors (spec §18): variable names, or prefixes ending in `*`, that make a
 * runtime load code before the program does. The host removes them from every extension process;
 * a manifest may not set them.
 */
export const injectionVectors: readonly string[] = [
  "BUN_OPTIONS",
  "BUN_INSPECT*",
  "BUN_CONFIG_*",
  "BUN_JSC_*",
  "BUN_FEATURE_FLAG_*",
  "BUN_DEBUG*",
  "NODE_OPTIONS",
  "NODE_INSPECT*",
  "NODE_PATH",
  "NODE_CHANNEL_*",
  "NODE_REPL_EXTERNAL_MODULE",
  "NODE_TLS_REJECT_UNAUTHORIZED",
  "NPM_CONFIG_*",
  "DOTNET_STARTUP_HOOKS",
  "DOTNET_ADDITIONAL_DEPS",
  "DOTNET_SHARED_STORE",
  "DOTNET_ENABLE_PROFILING*",
  "CORECLR_PROFILER*",
  "CORECLR_ENABLE_PROFILING",
  "COR_PROFILER*",
  "COR_ENABLE_PROFILING",
  "LD_PRELOAD",
  "LD_AUDIT",
  "LD_LIBRARY_PATH",
  "DYLD_*",
  "PYTHONSTARTUP",
  "PERL5OPT",
  "RUBYOPT",
  "JAVA_TOOL_OPTIONS",
  "_JAVA_OPTIONS",
];
