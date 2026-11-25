/// Built-in extensions use only the public SDK (spec §1 principle 9, §16.4): every package under
/// extensions/ references src/Fx.Sdk and nothing else, in its project file and in its compiled
/// assembly; the SDK exposes only what it is meant to; the core references no extension.
module Fx.Extensions.Tests.ArchitectureTests

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Xml.Linq
open Expecto

/// The repository root: the directory with Fx.slnx above the test binaries in artifacts/.
let private root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
        | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
        | dir -> up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// The public SDK: Fx.Sdk and the projects it exposes. Widening it widens what every extension may
/// depend on: change docs/modules/sdk.md with it.
let sdkSurface =
    set [ "Fx.Sdk"; "Fx.Prelude"; "Fx.Contracts"; "Fx.Effects"; "FSharp.Core" ]

/// The SDK of extension processes: the public SDK, the extension protocol's server (Fx.Sdk.Process),
/// the JSON-RPC engine it runs on and the real interpreters a process's composition root uses.
let processSdkSurface =
    sdkSurface + set [ "Fx.Sdk.Process"; "Fx.Rpc"; "Fx.Effects.Live" ]

/// The base class library, which every .NET assembly references. The analyzers keep I/O in it out of
/// extensions (the EFF rules apply to extensions/).
let private isFramework (name: string) =
    name = "netstandard"
    || name = "mscorlib"
    || name = "System"
    || name.StartsWith("System.", StringComparison.Ordinal)
    || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)

/// The assemblies an assembly references, from its metadata.
let assemblyReferences (path: string) : string list =
    use stream = File.OpenRead path
    use pe = new PEReader(stream)
    let reader = pe.GetMetadataReader()

    [
        for handle in reader.AssemblyReferences do
            reader.GetString(reader.GetAssemblyReference(handle).Name)
    ]
    |> List.sort

/// What an assembly references beyond a surface and the base class library.
let violationsOf (surface: Set<string>) (assembly: string) : string list =
    assemblyReferences assembly
    |> List.filter (fun name -> not (surface.Contains name || isFramework name))

/// What an extension assembly references beyond the SDK surface and the base class library.
let violations (assembly: string) : string list = violationsOf sdkSurface assembly

/// A project file's project and package references (paths relative to the repository root).
let private projectReferences (project: string) : string list * string list =
    let document = XDocument.Load project

    let directory =
        Path.GetDirectoryName project |> Option.ofObj |> Option.defaultValue root

    let includes (element: string) =
        [
            for node in document.Descendants(XName.Get element) do
                match node.Attribute(XName.Get "Include") with
                | null -> ()
                | attribute -> attribute.Value
        ]

    let projects =
        includes "ProjectReference"
        |> List.map (fun path ->
            Path
                .GetRelativePath(root, Path.GetFullPath(Path.Combine(directory, path.Replace('\\', '/'))))
                .Replace('\\', '/'))

    projects, includes "PackageReference"

/// Every extension package: extensions/<Name>/<Name>.fsproj.
let private extensionProjects () : (string * string) list =
    [
        for dir in Directory.GetDirectories(Path.Combine(root, "extensions")) |> Array.sort do
            let name = Path.GetFileName dir
            let project = Path.Combine(dir, name + ".fsproj")

            if File.Exists project then
                name, project
    ]

[<Tests>]
let tests =
    testList
        "Architecture"
        [
            test "there are extension packages to check" { Expect.isNonEmpty (extensionProjects ()) "extensions/" }

            test "extension packages reference only the public SDK" {
                for name, project in extensionProjects () do
                    let projects, packages = projectReferences project
                    Expect.equal projects [ "src/Fx.Sdk/Fx.Sdk.fsproj" ] $"{name}: project references"

                    Expect.equal
                        packages
                        []
                        $"{name}: package references (FSharp.Core comes from Directory.Build.props)"
            }

            test "extension assemblies use only the SDK surface and the base class library" {
                for name, _ in extensionProjects () do
                    let assembly = Path.Combine(AppContext.BaseDirectory, name + ".dll")

                    Expect.isTrue
                        (File.Exists assembly)
                        $"{name}.dll is not next to the tests: reference extensions/{name} from Fx.Extensions.Tests"

                    Expect.equal (violations assembly) [] $"{name} references the core"
            }

            test "the check fails on an assembly that uses the core" {
                let core = Path.Combine(AppContext.BaseDirectory, "Fx.Aot.Scenarios.dll")
                let found = violations core
                Expect.contains found "Fx.Extensions" "the extension system"
                Expect.contains found "Fx.Rpc" "the RPC engine"
            }

            test "the SDK exposes only its surface" {
                let projects, packages =
                    projectReferences (Path.Combine(root, "src", "Fx.Sdk", "Fx.Sdk.fsproj"))

                Expect.equal
                    (projects |> List.map (fun p -> Path.GetFileNameWithoutExtension p) |> List.sort)
                    [ "Fx.Contracts"; "Fx.Effects"; "Fx.Prelude" ]
                    "project references"

                Expect.equal packages [] "package references"

                Expect.equal
                    (violations (Path.Combine(AppContext.BaseDirectory, "Fx.Sdk.dll")))
                    []
                    "assembly references"
            }

            test "the process SDK adds only the protocol's server and what it runs on" {
                let projects, packages =
                    projectReferences (Path.Combine(root, "src", "Fx.Sdk.Process", "Fx.Sdk.Process.fsproj"))

                Expect.equal
                    (projects |> List.map (fun p -> Path.GetFileNameWithoutExtension p) |> List.sort)
                    [ "Fx.Effects.Live"; "Fx.Rpc"; "Fx.Sdk" ]
                    "project references"

                Expect.equal packages [] "package references"

                for assembly in [ "Fx.Sdk.Process"; "Fx.Rpc"; "Fx.Effects.Live" ] do
                    Expect.equal
                        (violationsOf processSdkSurface (Path.Combine(AppContext.BaseDirectory, assembly + ".dll")))
                        []
                        $"{assembly}: assembly references"
            }

            test "the core references no extension package" {
                let core =
                    assemblyReferences (Path.Combine(AppContext.BaseDirectory, "Fx.Extensions.dll"))

                let packages = extensionProjects () |> List.map fst
                Expect.isEmpty (core |> List.filter (fun name -> List.contains name packages)) "extensions"
            }
        ]
