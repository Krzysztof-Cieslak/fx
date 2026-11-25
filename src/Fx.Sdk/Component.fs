namespace Fx.Sdk

open System
open Fx.Contracts

/// An operating system a component can be installed on.
[<RequireQualifiedAccess>]
type Os =
    | Linux
    | MacOs
    | Windows

    override this.ToString() =
        match this with
        | Linux -> "linux"
        | MacOs -> "macos"
        | Windows -> "windows"

/// A processor architecture.
[<RequireQualifiedAccess>]
type Arch =
    | X64
    | Arm64

    override this.ToString() =
        match this with
        | X64 -> "x64"
        | Arm64 -> "arm64"

/// The C library of a Linux system; binaries built for one do not run on the other.
[<RequireQualifiedAccess>]
type Libc =
    | Glibc
    | Musl

    override this.ToString() =
        match this with
        | Glibc -> "glibc"
        | Musl -> "musl"

/// What a binary must be built for to run on a machine: written `linux-x64-glibc`,
/// `linux-arm64-musl`, `macos-arm64`, `windows-x64`. Linux names its C library; the others have
/// none.
[<CustomComparison; CustomEquality>]
type TargetTriple =
    {
        Os: Os
        Arch: Arch
        Libc: Libc option
    }

    override this.ToString() =
        match this.Libc with
        | Some libc -> $"{this.Os}-{this.Arch}-{libc}"
        | None -> $"{this.Os}-{this.Arch}"

    override this.Equals(other: obj) =
        match other with
        | :? TargetTriple as other -> string this = string other
        | _ -> false

    override this.GetHashCode() = (string this).GetHashCode()

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? TargetTriple as other -> String.CompareOrdinal(string this, string other)
            | _ -> invalidArg (nameof other) "not a target triple"

[<RequireQualifiedAccess>]
module TargetTriple =
    let create (os: Os) (arch: Arch) (libc: Libc option) : TargetTriple = { Os = os; Arch = arch; Libc = libc }

    /// Every target the product is built for (spec §20): Linux with glibc and musl, macOS and
    /// Windows, on x64 and arm64.
    let all: TargetTriple list =
        [
            for arch in [ Arch.X64; Arch.Arm64 ] do
                create Os.Linux arch (Some Libc.Glibc)
                create Os.Linux arch (Some Libc.Musl)
                create Os.MacOs arch None
                create Os.Windows arch None
        ]
        |> List.sort

    let tryParse (text: string) : TargetTriple option =
        all |> List.tryFind (fun t -> string t = text)

    let parse (text: string) : Result<TargetTriple, string> =
        match tryParse text with
        | Some target -> Ok target
        | None ->
            let known = all |> List.map string |> Issue.quoteAll
            Error $"expected a target such as \"linux-x64-glibc\" (one of {known}), got {Json.quote text}"

    /// The suffix of executables on the target: `.exe` on Windows, empty elsewhere.
    let exeSuffix (target: TargetTriple) : string =
        if target.Os = Os.Windows then ".exe" else ""

    let codec: Codec<TargetTriple> =
        Codec.string
        |> Codec.tryMap parse string
        |> Codec.describe "A target: <os>-<arch>[-<libc>], e.g. \"linux-x64-glibc\", \"macos-arm64\", \"windows-x64\"."

/// A content digest, written `sha256:<64 lowercase hex digits>`.
type Digest =
    | Sha256 of hex: string

    override this.ToString() =
        match this with
        | Sha256 hex -> "sha256:" + hex

[<RequireQualifiedAccess>]
module Digest =
    [<Literal>]
    let Pattern = "^sha256:[0-9a-f]{64}$"

    let parse (text: string) : Result<Digest, string> =
        let hex =
            if text.StartsWith("sha256:", StringComparison.Ordinal) then
                text.Substring 7
            else
                ""

        if
            hex.Length = 64
            && hex |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f'))
        then
            Ok(Sha256 hex)
        else
            Error $"expected sha256:<64 lowercase hex digits>, got {Json.quote text}"

    let codec: Codec<Digest> =
        Codec.string
        |> Codec.pattern Pattern
        |> Codec.tryMap parse string
        |> Codec.describe "A SHA-256 digest: \"sha256:\" and 64 lowercase hex digits."

/// How an archive is packed.
[<RequireQualifiedAccess>]
type ArchiveFormat =
    | Zip
    /// A tar file compressed with gzip (`.tar.gz`, `.tgz`).
    | TarGz
    /// An uncompressed tar file.
    | Tar

    override this.ToString() =
        match this with
        | Zip -> "zip"
        | TarGz -> "tar.gz"
        | Tar -> "tar"

/// Download an archive, check its digest and unpack it (spec §6.1 `Archive`).
type ArchiveSpec =
    {
        /// An `https://` URL; `${version}` stands for the component's version.
        Url: string
        Digest: Digest
        Format: ArchiveFormat
        /// How many leading path segments to drop from every entry (`bun-linux-x64/bun` with 1 is
        /// `bun`). Entries with no segments left are skipped.
        Strip: int
    }

    override this.ToString() =
        $"archive {this.Url} ({this.Format}, {this.Digest}, strip {this.Strip})"

/// Whose digests a release index is trusted for.
[<RequireQualifiedAccess>]
type IndexTrust =
    /// The digests the vendor's index publishes (the default, spec §6.1).
    | VendorIndex
    /// Only digests pinned in the lockfile.
    | Pinned

    override this.ToString() =
        match this with
        | VendorIndex -> "vendor-index"
        | Pinned -> "pinned"

/// Find an archive in a vendor's release index (spec §6.1 `ReleaseIndex`; T211).
type ReleaseIndexSpec =
    {
        Index: string
        /// Which release asset to take; its syntax belongs to T211.
        Select: string
        Trust: IndexTrust
    }

    override this.ToString() =
        $"release index {this.Index} ({this.Select}, {this.Trust})"

/// Download a script, check its digest and run it, sandboxed (spec §6.1 `Script`; T211).
type ScriptSpec =
    {
        Url: string
        Digest: Digest
        Runner: string
        Args: string list
    }

    override this.ToString() = $"script {this.Url} ({this.Runner})"

/// Install with a package manager that is itself a component (spec §6.1 `Package`; T211).
type PackageSpec =
    {
        Manager: ComponentRef
        Spec: string
    }

    override this.ToString() =
        $"package {this.Spec} with {this.Manager}"

/// Use what the system has, read-only, if its probe reports a version in the range (spec §6.1
/// `System`; T211).
type SystemSpec =
    {
        Probe: string list
        Constraint: string
    }

    override this.ToString() =
        $"""system {String.Join(" ", this.Probe)} ({this.Constraint})"""

/// Let an extension install it (spec §6.1 `Custom`; T211).
type CustomSpec =
    {
        Extension: string
    }

    override this.ToString() = $"custom {this.Extension}"

/// How a component gets onto a machine (spec §6.1). The installer implements `Archive`; the other
/// methods are typed so that components can declare them, and the planner and installer refuse
/// them with a clear error until T211 implements them.
type InstallMethod =
    | Archive of ArchiveSpec
    | ReleaseIndex of ReleaseIndexSpec
    | Script of ScriptSpec
    | Package of PackageSpec
    | System of SystemSpec
    | Custom of CustomSpec

    /// The method's name, as its JSON `kind`.
    member this.Kind =
        match this with
        | Archive _ -> "archive"
        | ReleaseIndex _ -> "release-index"
        | Script _ -> "script"
        | Package _ -> "package"
        | System _ -> "system"
        | Custom _ -> "custom"

    override this.ToString() =
        match this with
        | Archive spec -> string spec
        | ReleaseIndex spec -> string spec
        | Script spec -> string spec
        | Package spec -> string spec
        | System spec -> string spec
        | Custom spec -> string spec

/// A method and the targets it is for (empty: every target).
type MethodChoice =
    {
        Platforms: TargetTriple list
        Method: InstallMethod
    }

    override this.ToString() =
        let platforms =
            if this.Platforms.IsEmpty then
                "any target"
            else
                this.Platforms |> List.map string |> String.concat ", "

        $"{this.Method} for {platforms}"

/// How an installed component's files are laid out. Paths are relative to the install directory,
/// with `/` separators, and may use `${exeSuffix}` (`.exe` on Windows).
type InstallLayout =
    {
        /// Files that must be executable (on Linux and macOS), whatever the archive says.
        Executables: string list
    }

    override this.ToString() =
        $"""layout (executables: {String.Join(", ", this.Executables)})"""

/// What a component offers its users. Paths as in `InstallLayout`.
type Exports =
    {
        /// Commands, by name: the path of each executable.
        Bin: (string * string) list
        /// Environment variables a runtime sets for its processes; values may use `${dir}` (the
        /// install directory).
        Env: (string * string) list
    }

    override this.ToString() =
        let bins = this.Bin |> List.map fst |> String.concat ", "
        $"exports (bin: {bins})"

/// How the installer checks that an install works (spec §6.1 `Probe`): it runs `Argv` in the
/// install directory, and the program must exit with 0 and, if `Expect` is set, print exactly that
/// (surrounding whitespace aside). `Argv[0]` is a path in the install directory (as in
/// `InstallLayout`); the arguments are passed as they are. `Expect` may use `${version}`.
type Probe =
    {
        Argv: string list
        Expect: string option
    }

    override this.ToString() =
        let expect =
            match this.Expect with
            | Some text -> $" expecting {Json.quote text}"
            | None -> ""

        String.Join(" ", this.Argv) + expect

/// How big a component is, in bytes, as its author measured it. For prompts and progress; the
/// installer's limits do not depend on it.
type SizeHint =
    {
        Download: int64 option
        Installed: int64 option
    }

    override this.ToString() =
        let show (value: int64 option) =
            value |> Option.map string |> Option.defaultValue "?"

        $"size (download {show this.Download}, installed {show this.Installed})"

/// A component (spec §6.1): one version of a program or runtime that fx installs and manages
/// itself, such as the pinned Bun, never touching `PATH` or user-global installs. Extensions
/// contribute definitions (`Components.contributionType`); the planner picks a version of each
/// component that the requirements need, a method for each target, and writes the lockfile.
type Component =
    {
        /// Dotted lowercase, e.g. `bun`.
        Id: string
        /// The exact version this definition installs. A component with several versions has one
        /// definition per version.
        Version: SemVer
        /// The ways to install it, in order of preference; the planner takes the first one for the
        /// target that the host supports.
        Methods: MethodChoice list
        /// Components that must be installed first.
        DependsOn: ComponentRef list
        Layout: InstallLayout
        Exports: Exports
        Probe: Probe option
        /// The targets it supports; empty: every target one of its methods is for.
        Platforms: TargetTriple list
        Size: SizeHint
    }

    override this.ToString() = $"{this.Id} {this.Version}"

/// A case of a tagged union whose payload is a record codec, with the payload's type erased.
[<NoEquality; NoComparison>]
type internal TaggedCase<'U> =
    {
        Tag: string
        Description: string
        Payload: ObjectSchema
        Encode: 'U -> (string * Json) list option
        /// The preview of a value of this case, or `None` for other cases.
        Show: 'U * Limits -> Preview option
        Decode: Json -> Validation<'U, Issue>
    }

/// Union codecs built from one record codec per case: `{"kind": "<tag>", …the payload's fields}`.
/// `Codec.union` takes its match function curried over one encoder per case, which Native AOT
/// refuses beyond four cases (IL3054, AGENTS.md); this builds the same JSON and schema without it.
module internal TaggedUnion =
    let case
        (tag: string)
        (description: string)
        (payload: Codec<'P>)
        (make: 'P -> 'U)
        (pick: 'U -> 'P option)
        : TaggedCase<'U> =
        let fields =
            match payload.Schema.Kind with
            | SchemaKind.Object o -> o
            | SchemaKind.Named {
                                   Body = { Kind = SchemaKind.Object o }
                               } -> o
            | _ -> invalidArg (nameof payload) $"The payload of case {Json.quote tag} must be an object codec."

        let members (value: 'P) =
            match payload.Encode value with
            | Json.Object members -> members
            | _ -> invalidOp $"The payload of case {Json.quote tag} did not encode to an object."

        {
            Tag = tag
            Description = description
            Payload = fields
            Encode = fun value -> pick value |> Option.map members
            Show =
                fun (value, limits) ->
                    pick value
                    |> Option.map (fun p -> Preview.prefixed tag limits (fun limits -> payload.Preview limits p))
            Decode = fun json -> payload.Decode json |> Result.map make
        }

    let codec (discriminator: string) (cases: TaggedCase<'U> list) : Codec<'U> =
        let tags = Issue.quoteAll (cases |> List.map (fun c -> c.Tag))

        let tagIssue (message: string) =
            Error [ Issue.at [ PathSegment.Key discriminator ] message ]

        let schema =
            Schema.ofKind (
                SchemaKind.Union
                    {
                        Discriminator = discriminator
                        Cases =
                            cases
                            |> List.map (fun c ->
                                ({
                                    Tag = c.Tag
                                    Description = Some c.Description
                                    Payload = c.Payload
                                }
                                : UnionCase))
                    }
            )

        let encode (value: 'U) =
            cases
            |> List.pick (fun c -> c.Encode value |> Option.map (fun members -> c.Tag, members))
            |> fun (tag, members) -> Json.Object((discriminator, Json.String tag) :: members)

        let decode (json: Json) =
            match json with
            | Json.Object members ->
                let found =
                    members
                    |> List.tryFind (fun (key, _) -> String.Equals(key, discriminator, StringComparison.Ordinal))
                    |> Option.map snd

                match found with
                | None -> tagIssue $"missing required field; expected one of {tags}"
                | Some(Json.String tag as found) ->
                    match cases |> List.tryFind (fun c -> c.Tag = tag) with
                    | Some case ->
                        members
                        |> List.filter (fun (key, _) ->
                            not (String.Equals(key, discriminator, StringComparison.Ordinal)))
                        |> Json.Object
                        |> case.Decode
                    | None -> tagIssue $"expected one of {tags}, got {Json.describe found}"
                | Some other -> tagIssue $"expected one of {tags}, got {Json.describe other}"
            | other -> Error [ Issue.expected "object" other ]

        Codec.create schema encode decode
        |> Codec.withPreview (fun limits value ->
            match cases |> List.tryPick (fun c -> c.Show(value, limits)) with
            | Some preview -> preview
            | None -> Preview.atom limits "?")

[<RequireQualifiedAccess>]
module Component =

    /// Component ids: dotted lowercase segments, like extension ids.
    [<Literal>]
    let IdPattern = "^[a-z][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)*$"

    /// Whether a path is relative to an install directory: `/`-separated, with no empty, `.` or `..`
    /// segments, no `\` and no `:` (so it cannot name a drive or climb out).
    let isRelativePath (path: string) : bool =
        path.Length > 0
        && path.Split '/'
           |> Array.forall (fun segment ->
               segment <> ""
               && segment <> "."
               && segment <> ".."
               && not (segment.Contains '\\')
               && not (segment.Contains ':')
               && not (segment.Contains '\000'))

    /// Replaces `${name}` placeholders. A placeholder without a value is an error.
    let expand (values: (string * string) list) (template: string) : Result<string, string> =
        let builder = Text.StringBuilder()
        let mutable i = 0
        let mutable error = None

        while error.IsNone && i < template.Length do
            if template[i] = '$' && i + 1 < template.Length && template[i + 1] = '{' then
                match template.IndexOf('}', i + 2) with
                | -1 -> error <- Some $"unterminated placeholder in {Json.quote template}"
                | close ->
                    let name = template.Substring(i + 2, close - i - 2)

                    match values |> List.tryFind (fun (key, _) -> key = name) with
                    | Some(_, value) ->
                        builder.Append value |> ignore
                        i <- close + 1
                    | None ->
                        let known = values |> List.map (fun (key, _) -> "${" + key + "}") |> Issue.quoteAll
                        error <- Some $"unknown placeholder ${{{name}}} in {Json.quote template}; known: {known}"
            else
                builder.Append template[i] |> ignore
                i <- i + 1

        match error with
        | Some error -> Error error
        | None -> Ok(builder.ToString())

    let private checkRelative (path: string) : Result<unit, string> =
        match expand [ "exeSuffix", "" ] path with
        | Ok expanded when isRelativePath expanded -> Ok()
        | Ok _ -> Error $"expected a path inside the install directory, with / separators, got {Json.quote path}"
        | Error e -> Error e

    /// A path template (`${exeSuffix}` allowed) relative to the install directory.
    let private relativePath (description: string) : Codec<string> =
        Codec.string |> Codec.refine checkRelative |> Codec.describe description

    /// Whether a name can be a command's: letters, digits, `.`, `_` and `-`, not starting with a
    /// punctuation mark.
    let isCommandName (name: string) : bool =
        name.Length > 0
        && Char.IsAsciiLetterOrDigit name[0]
        && name
           |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-')

    let private httpsUrl (description: string) : Codec<string> =
        Codec.string
        |> Codec.refine (fun url ->
            if url.StartsWith("https://", StringComparison.Ordinal) && url.Length > 8 then
                Ok()
            else
                Error $"expected an https:// URL, got {Json.quote url}")
        |> Codec.describe description

    let archiveFormat: Codec<ArchiveFormat> =
        Codec.enum
            [
                ArchiveFormat.Zip, "zip"
                ArchiveFormat.TarGz, "tar.gz"
                ArchiveFormat.Tar, "tar"
            ]
        |> Codec.describe "How the archive is packed."

    let archiveSpec: Codec<ArchiveSpec> =
        Codec.record {
            let! url =
                Codec.field
                    "url"
                    (fun (a: ArchiveSpec) -> a.Url)
                    (httpsUrl "The archive's https:// URL; ${version} is the component's version.")

            and! digest = Codec.field "digest" (fun a -> a.Digest) Digest.codec
            and! format = Codec.field "format" (fun a -> a.Format) archiveFormat

            and! strip =
                Codec.defaultField
                    "strip"
                    (fun a -> a.Strip)
                    0
                    (Codec.int
                     |> Codec.between 0 16
                     |> Codec.describe "Leading path segments to drop from every entry.")

            return
                {
                    Url = url
                    Digest = digest
                    Format = format
                    Strip = strip
                }
        }
        |> Codec.strict
        |> Codec.named "ArchiveMethod"

    let indexTrust: Codec<IndexTrust> =
        Codec.enum [ IndexTrust.VendorIndex, "vendor-index"; IndexTrust.Pinned, "pinned" ]

    let releaseIndexSpec: Codec<ReleaseIndexSpec> =
        Codec.record {
            let! index =
                Codec.field "index" (fun (r: ReleaseIndexSpec) -> r.Index) (httpsUrl "The index's URL.")

            and! select =
                Codec.field "select" (fun r -> r.Select) (Codec.string |> Codec.nonEmpty)

            and! trust =
                Codec.defaultField "trust" (fun r -> r.Trust) IndexTrust.VendorIndex indexTrust

            return
                {
                    Index = index
                    Select = select
                    Trust = trust
                }
        }
        |> Codec.strict
        |> Codec.named "ReleaseIndexMethod"

    let scriptSpec: Codec<ScriptSpec> =
        Codec.record {
            let! url =
                Codec.field "url" (fun (s: ScriptSpec) -> s.Url) (httpsUrl "The script's URL.")

            and! digest = Codec.field "digest" (fun s -> s.Digest) Digest.codec

            and! runner =
                Codec.field "runner" (fun s -> s.Runner) (Codec.string |> Codec.nonEmpty)

            and! args = Codec.defaultField "args" (fun s -> s.Args) [] (Codec.list Codec.string)

            return
                {
                    Url = url
                    Digest = digest
                    Runner = runner
                    Args = args
                }
        }
        |> Codec.strict
        |> Codec.named "ScriptMethod"

    let packageSpec: Codec<PackageSpec> =
        Codec.record {
            let! manager =
                Codec.field "manager" (fun (p: PackageSpec) -> p.Manager) Manifest.componentRef

            and! spec = Codec.field "spec" (fun p -> p.Spec) (Codec.string |> Codec.nonEmpty)
            return { Manager = manager; Spec = spec }
        }
        |> Codec.strict
        |> Codec.named "PackageMethod"

    let systemSpec: Codec<SystemSpec> =
        Codec.record {
            let! probe =
                Codec.field "probe" (fun (s: SystemSpec) -> s.Probe) (Codec.list Codec.string |> Codec.minItems 1)

            and! constraint' =
                Codec.field
                    "constraint"
                    (fun s -> s.Constraint)
                    (Codec.string
                     |> Codec.refine (fun text -> VersionRange.parse text |> Result.map ignore)
                     |> Codec.describe "The versions to accept, as a version range.")

            return
                {
                    Probe = probe
                    Constraint = constraint'
                }
        }
        |> Codec.strict
        |> Codec.named "SystemMethod"

    let customSpec: Codec<CustomSpec> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (c: CustomSpec) -> c.Extension) (Codec.string |> Codec.nonEmpty)

            return { Extension = extension }
        }
        |> Codec.strict
        |> Codec.named "CustomMethod"

    let installMethod: Codec<InstallMethod> =
        TaggedUnion.codec
            "kind"
            [
                TaggedUnion.case
                    "archive"
                    "Download an archive, check its digest and unpack it."
                    archiveSpec
                    Archive
                    (function
                     | Archive spec -> Some spec
                     | _ -> None)
                TaggedUnion.case
                    "release-index"
                    "Find an archive in the vendor's release index."
                    releaseIndexSpec
                    ReleaseIndex
                    (function
                     | ReleaseIndex spec -> Some spec
                     | _ -> None)
                TaggedUnion.case
                    "script"
                    "Download a script, check its digest and run it, sandboxed."
                    scriptSpec
                    Script
                    (function
                     | Script spec -> Some spec
                     | _ -> None)
                TaggedUnion.case
                    "package"
                    "Install with a package manager that is itself a component."
                    packageSpec
                    Package
                    (function
                     | Package spec -> Some spec
                     | _ -> None)
                TaggedUnion.case
                    "system"
                    "Use what the system has, read-only, if its version is in the range."
                    systemSpec
                    System
                    (function
                     | System spec -> Some spec
                     | _ -> None)
                TaggedUnion.case "custom" "Let an extension install it." customSpec Custom (function
                    | Custom spec -> Some spec
                    | _ -> None)
            ]
        |> Codec.describe "How the component gets onto a machine."
        |> Codec.named "InstallMethod"

    let methodChoice: Codec<MethodChoice> =
        Codec.record {
            let! platforms =
                Codec.defaultField
                    "platforms"
                    (fun (m: MethodChoice) -> m.Platforms)
                    []
                    (Codec.list TargetTriple.codec
                     |> Codec.describe "The targets the method is for; empty: every target.")

            and! method = Codec.field "method" (fun m -> m.Method) installMethod

            return
                {
                    Platforms = platforms
                    Method = method
                }
        }
        |> Codec.strict
        |> Codec.named "MethodChoice"

    let installLayout: Codec<InstallLayout> =
        Codec.record {
            let! executables =
                Codec.defaultField
                    "executables"
                    (fun (l: InstallLayout) -> l.Executables)
                    []
                    (Codec.list (relativePath "A file that must be executable."))

            return { Executables = executables }
        }
        |> Codec.strict
        |> Codec.describe "How the installed files are laid out; paths are relative to the install directory."
        |> Codec.named "InstallLayout"

    let exports: Codec<Exports> =
        Codec.record {
            let! bin =
                Codec.defaultField
                    "bin"
                    (fun (e: Exports) -> e.Bin)
                    []
                    (Codec.keyValues (relativePath "The executable's path in the install directory."))

            and! env =
                Codec.defaultField "env" (fun e -> e.Env) [] (Codec.keyValues Codec.string)

            return { Bin = bin; Env = env }
        }
        |> Codec.strict
        |> Codec.refine (fun e ->
            match e.Bin |> List.tryFind (fun (name, _) -> not (isCommandName name)) with
            | Some(name, _) -> Error $"{Json.quote name} is not a command name"
            | None -> Ok())
        |> Codec.describe
            "What the component offers: commands by name, and environment variables (values may use ${dir})."
        |> Codec.named "Exports"

    let probe: Codec<Probe> =
        Codec.record {
            let! argv =
                Codec.field
                    "argv"
                    (fun (p: Probe) -> p.Argv)
                    (Codec.list Codec.string
                     |> Codec.minItems 1
                     |> Codec.refine (fun argv ->
                         match checkRelative (List.head argv) with
                         | Ok() -> Ok()
                         | Error _ ->
                             Error
                                 $"the program must be a path in the install directory, got {Json.quote (List.head argv)}")
                     |> Codec.describe "The program (a path in the install directory) and its arguments.")

            and! expect =
                Codec.optionalField
                    "expect"
                    (fun p -> p.Expect)
                    (Codec.string
                     |> Codec.describe "What the program must print, surrounding whitespace aside; may use ${version}.")

            return { Argv = argv; Expect = expect }
        }
        |> Codec.strict
        |> Codec.describe "How to check that an install works: run argv in the install directory; it must exit with 0."
        |> Codec.named "Probe"

    let sizeHint: Codec<SizeHint> =
        Codec.record {
            let! download =
                Codec.optionalField "download" (fun (s: SizeHint) -> s.Download) (Codec.int64 |> Codec.atLeast 0L)

            and! installed =
                Codec.optionalField "installed" (fun s -> s.Installed) (Codec.int64 |> Codec.atLeast 0L)

            return
                {
                    Download = download
                    Installed = installed
                }
        }
        |> Codec.strict
        |> Codec.named "SizeHint"

    let private noSize: SizeHint = { Download = None; Installed = None }
    let private noExports: Exports = { Bin = []; Env = [] }
    let private noLayout: InstallLayout = { Executables = [] }

    let codec: Codec<Component> =
        Codec.record {
            let! id =
                Codec.field
                    "id"
                    (fun (c: Component) -> c.Id)
                    (Codec.string
                     |> Codec.pattern IdPattern
                     |> Codec.describe "The component's id: dotted lowercase segments, e.g. \"bun\".")

            and! version = Codec.field "version" (fun c -> c.Version) SemVer.codec

            and! methods =
                Codec.field "methods" (fun c -> c.Methods) (Codec.list methodChoice |> Codec.minItems 1)

            and! dependsOn =
                Codec.defaultField "dependsOn" (fun c -> c.DependsOn) [] (Codec.list Manifest.componentRef)

            and! layout = Codec.defaultField "layout" (fun c -> c.Layout) noLayout installLayout
            and! exports = Codec.defaultField "exports" (fun c -> c.Exports) noExports exports
            and! probe = Codec.optionalField "probe" (fun c -> c.Probe) probe

            and! platforms =
                Codec.defaultField "platforms" (fun c -> c.Platforms) [] (Codec.list TargetTriple.codec)

            and! size = Codec.defaultField "size" (fun c -> c.Size) noSize sizeHint

            return
                {
                    Id = id
                    Version = version
                    Methods = methods
                    DependsOn = dependsOn
                    Layout = layout
                    Exports = exports
                    Probe = probe
                    Platforms = platforms
                    Size = size
                }
        }
        |> Codec.strict
        |> Codec.describe
            "A component: one version of a program or runtime that the product installs and manages itself."
        |> Codec.named "Component"

    /// A definition with these methods and nothing else (no dependencies, layout, exports or probe).
    let create (id: string) (version: SemVer) (methods: MethodChoice list) : Component =
        {
            Id = id
            Version = version
            Methods = methods
            DependsOn = []
            Layout = noLayout
            Exports = noExports
            Probe = None
            Platforms = []
            Size = noSize
        }

/// The `component` contribution type.
[<RequireQualifiedAccess>]
module ComponentDefinitions =
    /// Component definitions (spec §16.2 "Installation and execution"). Privileged (spec §16.5):
    /// only extensions from host config, policy or the binary may contribute them. The contribution
    /// id is free (e.g. `bun-1.4.5`); the planner reads the component's id and version from the value.
    let contributionType: ContributionType<Component> =
        ContributionType.data "component" "A component the installer can install." Component.codec
        |> ContributionType.privileged
