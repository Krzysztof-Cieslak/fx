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
    set
        [
            "Fx.Sdk"
            "Fx.Prelude"
            "Fx.Contracts"
            "Fx.Contracts.TypeScript"
            "Fx.Effects"
            "Fx.Variants"
            "Fx.Models"
            "Fx.Grants"
            "FSharp.Core"
        ]

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

/// A method of an assembly: its name and the names of the types that declare it, outermost first.
type MethodSite =
    {
        Types: string list
        Name: string
    }

    override this.ToString() =
        String.Join("/", this.Types) + "::" + this.Name

/// What an assembly's IL says about some of its own methods: which methods call them (`call`,
/// `callvirt`, `newobj`) or take their address (`ldftn`, `ldvirtftn`), generic instantiations
/// included. The IL is scanned for the instructions' bytes, so it may report a caller too many,
/// never one too few.
let callersIn (assembly: string) (isTarget: MetadataReader -> MethodDefinition -> bool) : MethodSite list =
    use stream = File.OpenRead assembly
    use pe = new PEReader(stream)
    let reader = pe.GetMetadataReader()

    let rec typeNames (handle: TypeDefinitionHandle) : string list =
        let typeDef = reader.GetTypeDefinition handle
        let name = reader.GetString typeDef.Name
        let outer = typeDef.GetDeclaringType()
        if outer.IsNil then [ name ] else typeNames outer @ [ name ]

    // Rows of the method table are numbered from 1 in the order `MethodDefinitions` lists them.
    let targets =
        [
            for row, handle in Seq.indexed reader.MethodDefinitions do
                if isTarget reader (reader.GetMethodDefinition handle) then
                    row + 1
        ]

    let tokens =
        [
            for row in targets do
                0x06000000 ||| row

            for row in 1 .. Ecma335.MetadataReaderExtensions.GetTableRowCount(reader, Ecma335.TableIndex.MethodSpec) do
                let specified =
                    reader.GetMethodSpecification(Ecma335.MetadataTokens.MethodSpecificationHandle row)

                if
                    specified.Method.Kind = HandleKind.MethodDefinition
                    && List.contains (Ecma335.MetadataTokens.GetRowNumber specified.Method) targets
                then
                    0x2B000000 ||| row
        ]
        |> List.map (fun token -> BitConverter.GetBytes(token: int))

    let refers (il: byte array) =
        let at (i: int) (token: byte array) =
            i + 4 <= il.Length
            && il[i] = token[0]
            && il[i + 1] = token[1]
            && il[i + 2] = token[2]
            && il[i + 3] = token[3]

        seq {
            for i in 0 .. il.Length - 5 do
                match il[i] with
                | 0x28uy
                | 0x6Fuy
                | 0x73uy -> yield tokens |> List.exists (at (i + 1))
                | 0xFEuy when i + 6 <= il.Length && (il[i + 1] = 0x06uy || il[i + 1] = 0x07uy) ->
                    yield tokens |> List.exists (at (i + 2))
                | _ -> ()
        }
        |> Seq.exists id

    [
        for handle in reader.MethodDefinitions do
            let methodDef = reader.GetMethodDefinition handle

            if methodDef.RelativeVirtualAddress <> 0 then
                let il =
                    match pe.GetMethodBody(methodDef.RelativeVirtualAddress).GetILBytes() with
                    | null -> [||]
                    | il -> il

                if refers il then
                    {
                        Types = typeNames (methodDef.GetDeclaringType())
                        Name = reader.GetString methodDef.Name
                    }
    ]

/// Whether a method is a member of the type of this name (namespace and name).
let isMemberOf (ns: string) (typeName: string) (name: string) (reader: MetadataReader) (methodDef: MethodDefinition) =
    let declaring = reader.GetTypeDefinition(methodDef.GetDeclaringType())

    reader.GetString declaring.Namespace = ns
    && reader.GetString declaring.Name = typeName
    && reader.GetString methodDef.Name = name

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
                    [
                        "Fx.Contracts"
                        "Fx.Contracts.TypeScript"
                        "Fx.Effects"
                        "Fx.Grants"
                        "Fx.Models"
                        "Fx.Prelude"
                        "Fx.Variants"
                    ]
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

            test "only ModelAccess reaches a wire, and only through the model-egress hooks" {
                // Wires take only WireRequests (spec §18): the SDK must make them in ModelEgress.clear
                // alone, after the transforms and gates, and only ModelAccess.stream may call that.
                let sdk = Path.Combine(AppContext.BaseDirectory, "Fx.Sdk.dll")
                use stream = File.OpenRead sdk
                use pe = new PEReader(stream)
                let reader = pe.GetMetadataReader()

                let wireRequest =
                    reader.TypeDefinitions
                    |> Seq.map reader.GetTypeDefinition
                    |> Seq.find (fun t ->
                        reader.GetString t.Namespace = "Fx.Sdk"
                        && reader.GetString t.Name = "WireRequest")

                Expect.equal (int wireRequest.Attributes &&& 0x100) 0x100 "WireRequest is sealed"

                for handle in wireRequest.GetMethods() do
                    let methodDef = reader.GetMethodDefinition handle
                    let name = reader.GetString methodDef.Name

                    if name = ".ctor" then
                        // Member access 3 is `assembly`: internal.
                        Expect.equal (int methodDef.Attributes &&& 0x7) 3 "its constructor is internal"
                    else
                        // 0x10 is `static`: a static member could be a factory.
                        Expect.equal (int methodDef.Attributes &&& 0x10) 0 $"{name} is an instance member"

                let internalsVisibleTo =
                    reader.GetAssemblyDefinition().GetCustomAttributes()
                    |> Seq.map reader.GetCustomAttribute
                    |> Seq.filter (fun a ->
                        match a.Constructor.Kind with
                        | HandleKind.MemberReference ->
                            let ctor =
                                reader.GetMemberReference(MemberReferenceHandle.op_Explicit a.Constructor)

                            ctor.Parent.Kind = HandleKind.TypeReference
                            && reader.GetString(
                                reader.GetTypeReference(TypeReferenceHandle.op_Explicit ctor.Parent).Name
                            )
                                =
                                "InternalsVisibleToAttribute"
                        | _ -> false)
                    |> Seq.length

                Expect.equal internalsVisibleTo 0 "Fx.Sdk shows its internals to no one"

                // A positive control: the scan sees generic calls made inside state machines.
                Expect.exists
                    (callersIn sdk (isMemberOf "Fx.Sdk" "Hooks" "transform"))
                    (fun site -> List.head site.Types = "ModelEgress")
                    "clear's call of Hooks.transform"

                let makers = callersIn sdk (isMemberOf "Fx.Sdk" "WireRequest" ".ctor")
                Expect.isNonEmpty makers "the constructor is used"

                for site in makers do
                    Expect.equal (List.head site.Types) "ModelEgress" $"{site} makes a WireRequest"

                let clearing = callersIn sdk (isMemberOf "Fx.Sdk" "ModelEgress" "clear")
                Expect.isNonEmpty clearing "clear is used"

                for site in clearing do
                    Expect.stringStarts (List.head site.Types) "ModelAccess" $"{site} runs the egress hooks"
            }

            test "the core references no extension package" {
                let core =
                    assemblyReferences (Path.Combine(AppContext.BaseDirectory, "Fx.Extensions.dll"))

                let packages = extensionProjects () |> List.map fst
                Expect.isEmpty (core |> List.filter (fun name -> List.contains name packages)) "extensions"
            }
        ]
