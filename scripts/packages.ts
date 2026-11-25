#!/usr/bin/env bun
// Packs the public SDKs and proves that an extension can be built from the packages alone
// (docs/modules/sdk.md). Run it after a Release build:
//
//   dotnet build Fx.slnx -c Release && bun scripts/packages.ts
//
// 1. `dotnet pack` every packable project (IsPackable) into artifacts/packages. Each package must be
//    named from the naming constant (<FxPascalName>.<project name without "Fx.">) and depend only on
//    other SDK packages and FSharp.Core.
// 2. `bun pm pack` the TypeScript SDK (packages/sdk) into artifacts/packages; its name comes from the
//    constant too (a test checks package.json).
// 3. Build consumers outside the solution (artifacts/package-consumer), restored from artifacts/packages
//    and nuget.org with nothing inherited from the repository's build:
//    - every package under extensions/ from its sources, referencing only <FxPascalName>.Sdk: built-ins
//      need nothing but the public SDK;
//    - an F# extension process made of the sample extension's source and a main that serves it with
//      ExtensionProcess.run, referencing only <FxPascalName>.Sdk.Process. tests/Fx.Extensions.Tests runs
//      it through the parity and server conformance suites (required with PACKAGE_TESTS=1, as in CI).

import { inflateRawSync } from "node:zlib";
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join, relative } from "node:path";

const ROOT = join(import.meta.dir, "..");
const OUT = join(ROOT, "artifacts", "packages");
const CONSUMER = join(ROOT, "artifacts", "package-consumer");

function fail(message: string): never {
  console.error(message);
  process.exit(1);
}

function run(command: string[], cwd = ROOT): string {
  console.log(`$ ${command.join(" ")}`);
  const result = Bun.spawnSync(command, { cwd, stdout: "pipe", stderr: "pipe", env: { ...process.env, DOTNET_NOLOGO: "1" } });
  const output = result.stdout.toString() + result.stderr.toString();
  if (result.exitCode !== 0) fail(output + `\n${command[0]} failed with exit code ${result.exitCode}`);
  return output;
}

function property(file: string, name: string): string {
  const match = new RegExp(`<${name}>([^<]*)</${name}>`).exec(readFileSync(join(ROOT, file), "utf8"));
  if (!match) fail(`${file} has no ${name}`);
  return match[1]!;
}

const productName = property("Directory.Build.props", "FxProductName");
const version = property("Directory.Build.props", "Version");
const pascal = productName.charAt(0).toUpperCase() + productName.slice(1);
const fsharpCore = /<PackageVersion Include="FSharp.Core" Version="([^"]+)"/.exec(readFileSync(join(ROOT, "Directory.Packages.props"), "utf8"))?.[1];
if (!fsharpCore) fail("Directory.Packages.props pins no FSharp.Core");

// ---- 1. The F# packages ------------------------------------------------------------------------

const projectFile = (name: string) => join(ROOT, "src", name, `${name}.fsproj`);
const projects = readdirSync(join(ROOT, "src"))
  .filter((name) => existsSync(projectFile(name)) && readFileSync(projectFile(name), "utf8").includes("<IsPackable>true</IsPackable>"))
  .sort();

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });
for (const name of projects) run(["dotnet", "pack", projectFile(name), "--configuration", "Release", "--no-build", "--output", OUT]);

/** One file from a zip archive (a .nupkg), read with the central directory. */
function unzip(archive: Buffer, predicate: (name: string) => boolean): Buffer {
  const end = archive.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  if (end < 0) fail("not a zip archive");
  const entries = archive.readUInt16LE(end + 10);
  let offset = archive.readUInt32LE(end + 16);
  for (let i = 0; i < entries; i++) {
    const method = archive.readUInt16LE(offset + 10);
    const size = archive.readUInt32LE(offset + 20);
    const nameLength = archive.readUInt16LE(offset + 28);
    const extraLength = archive.readUInt16LE(offset + 30);
    const commentLength = archive.readUInt16LE(offset + 32);
    const local = archive.readUInt32LE(offset + 42);
    const name = archive.toString("utf8", offset + 46, offset + 46 + nameLength);
    if (predicate(name)) {
      const start = local + 30 + archive.readUInt16LE(local + 26) + archive.readUInt16LE(local + 28);
      const data = archive.subarray(start, start + size);
      return method === 0 ? Buffer.from(data) : inflateRawSync(data);
    }
    offset += 46 + nameLength + extraLength + commentLength;
  }
  fail("no such entry");
}

const expected = projects.map((name) => pascal + name.slice(2));
const ids = new Set(expected);
for (const id of expected) {
  const file = join(OUT, `${id}.${version}.nupkg`);
  if (!existsSync(file)) fail(`missing package ${relative(ROOT, file)}: is PackageId derived from FxPascalName?`);
  const nuspec = unzip(readFileSync(file), (name) => name.endsWith(".nuspec")).toString("utf8");
  const dependencies = [...nuspec.matchAll(/<dependency id="([^"]+)" version="([^"]+)"/g)].map((m) => [m[1]!, m[2]!] as const);
  for (const [dependency, dependencyVersion] of dependencies) {
    const allowed = ids.has(dependency) ? dependencyVersion === version : dependency === "FSharp.Core" && dependencyVersion === fsharpCore;
    if (!allowed) fail(`${id} depends on ${dependency} ${dependencyVersion}: SDK packages depend only on each other and FSharp.Core ${fsharpCore}`);
  }
  if (!/<license type="expression">MIT<\/license>/.test(nuspec)) fail(`${id} has no MIT license expression`);
  console.log(`${id} ${version}: ${dependencies.map(([d]) => d).join(", ")}`);
}

// ---- 2. The TypeScript SDK -----------------------------------------------------------------------

run([process.execPath, "pm", "pack", "--destination", OUT], join(ROOT, "packages", "sdk"));
const tarball = `${productName}-sdk-${version}.tgz`;
if (!existsSync(join(OUT, tarball))) fail(`missing ${tarball} in ${relative(ROOT, OUT)}: ${readdirSync(OUT).join(", ")}`);
console.log(`${tarball}: packed`);

// ---- 3. An extension process built from the packages alone ---------------------------------------

rmSync(CONSUMER, { recursive: true, force: true });
mkdirSync(CONSUMER, { recursive: true });
const write = (name: string, text: string) => writeFileSync(join(CONSUMER, name), text);

// Nothing of the repository's build: no shared props, no central package versions, no lockfiles.
write("Directory.Build.props", "<Project />\n");
write("Directory.Build.targets", "<Project />\n");
write("Directory.Packages.props", "<Project />\n");
write(
  "nuget.config",
  `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="sdk" value="${OUT}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="sdk">
      <package pattern="${pascal}.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
`,
);
write(
  "PackagedSample.fsproj",
  `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- FSharp.Core comes from the SDK packages' dependencies. -->
    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>
    <DisableImplicitLibraryPacksFolder>true</DisableImplicitLibraryPacksFolder>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="${join(ROOT, "extensions", "Fx.Ext.Sample", "Sample.fs")}" Link="Sample.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="${pascal}.Sdk.Process" Version="${version}" />
  </ItemGroup>
</Project>
`,
);
write(
  "Program.fs",
  `module Program

[<EntryPoint>]
let main _ =
    Fx.Sdk.Process.ExtensionProcess.run "packaged-sample" "1.0.0" [ Fx.Ext.Sample.Sample.extension ]
`,
);

const build = (project: string, output: string) => {
  run(["dotnet", "restore", project, "--configfile", join(CONSUMER, "nuget.config"), "--packages", join(CONSUMER, "nuget-packages")]);
  run(["dotnet", "build", project, "--no-restore", "--configuration", "Release", "--output", output]);
};

// Every extension package, from its own sources and the public SDK's package alone.
for (const name of readdirSync(join(ROOT, "extensions")).sort()) {
  const source = join(ROOT, "extensions", name, `${name}.fsproj`);
  if (!existsSync(source)) continue;
  const compiled = [...readFileSync(source, "utf8").matchAll(/<Compile Include="([^"]+)"/g)].map((m) => join(ROOT, "extensions", name, m[1]!.replaceAll("\\", "/")));
  const directory = join(CONSUMER, "extensions", name);
  mkdirSync(directory, { recursive: true });
  const project = join(directory, `${name}.fsproj`);
  writeFileSync(
    project,
    `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>
    <DisableImplicitLibraryPacksFolder>true</DisableImplicitLibraryPacksFolder>
  </PropertyGroup>
  <ItemGroup>
${compiled.map((file) => `    <Compile Include="${file}" />`).join("\n")}
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="${pascal}.Sdk" Version="${version}" />
  </ItemGroup>
</Project>
`,
  );
  build(project, join(directory, "out"));
  console.log(`extensions/${name}: built from the SDK package alone`);
}

build(join(CONSUMER, "PackagedSample.fsproj"), join(CONSUMER, "out"));
console.log(`${relative(ROOT, join(CONSUMER, "out"))}: the sample extension as a process, built from the SDK packages`);
