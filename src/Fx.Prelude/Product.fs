/// The product name and every name derived from it (spec §20 "Naming").
///
/// The name itself is the build-time constant `FxProductName` in Directory.Build.props.
/// Never hardcode the name, or anything derived from it, outside this module.
[<RequireQualifiedAccess>]
module Fx.Product

/// The product name. Also the binary name, the kernel global and the protocol namespace.
[<Literal>]
let Name = ProductConstants.Name

[<Literal>]
let Version = ProductConstants.Version

/// The name with a leading capital, for .NET-facing names such as NuGet package ids.
let pascalName = string (System.Char.ToUpperInvariant Name[0]) + Name.Substring 1

/// File name of the executable, without the platform's executable suffix.
let binaryName = Name

/// Name of the global object exposed to kernels and the TypeScript SDK.
let kernelGlobal = Name

/// Namespace for protocol extension methods and `_meta` keys.
let protocolNamespace = Name

/// Project-local directory at the repository root (`.fx/`).
let projectDirName = "." + Name

/// Per-user directory under the home directory on Unix (`~/.fx`).
let homeDirName = "." + Name

/// Per-user directory under `%LOCALAPPDATA%` on Windows.
let windowsDataDirName = Name

/// Prefix of every environment variable the product reads (`FX_`).
let envPrefix = Name.ToUpperInvariant() + "_"

/// An environment variable name, e.g. `envVar "HOME"` is `FX_HOME`.
let envVar (suffix: string) = envPrefix + suffix

/// The npm scope of the TypeScript packages (`@fx`).
let npmScope = "@" + Name

/// An npm package in the product scope, e.g. `npmPackage "sdk"` is `@fx/sdk`.
let npmPackage (name: string) = npmScope + "/" + name

/// A NuGet package id, e.g. `nugetPackage "Sdk"` is `Fx.Sdk`.
let nugetPackage (name: string) = pascalName + "." + name

/// A bundled extension id, e.g. `extensionId "env.local"` is `fx.env.local`.
let extensionId (localId: string) = Name + "." + localId

/// File name of the component lockfile (`fx.components.lock`).
let componentsLockFileName = Name + ".components.lock"
