namespace Fx.Sdk

open System
open System.Security.Cryptography
open System.Text
open Fx
open Fx.Contracts

/// Where an extension runs (spec §3): in the host (brain), in an environment's envd (hands), or in
/// a client.
[<RequireQualifiedAccess>]
type Placement =
    | Host
    | Environment
    | Client

    override this.ToString() =
        match this with
        | Host -> "host"
        | Environment -> "environment"
        | Client -> "client"

/// A component an extension needs installed before it starts (spec §6.1): an id and a version range.
/// The component installer (T110) resolves and installs it in the bootstrap's components phase.
type ComponentRef =
    {
        Id: string
        Version: string
    }

    override this.ToString() = this.Id + " " + this.Version

/// The working directory of an extension process.
[<RequireQualifiedAccess>]
type LaunchCwd =
    /// The extension's own package directory.
    | Owned
    /// The workspace the host serves.
    | Workspace

    override this.ToString() =
        match this with
        | Owned -> "owned"
        | Workspace -> "workspace"

/// How to start a process (spec §6.2 `LaunchSpec`), as data: an extension process (`process`
/// runtime, in a manifest's `launch`) or a runtime (`RuntimeSpec.Launch`, docs/modules/runtimes.md).
///
/// - `Exe`, `Args`, `EnvSet` values and `UntrustedOverrides` are templates. A manifest's launch may
///   use `${root}` (the package directory) and `${exeSuffix}` (`.exe` on Windows, empty elsewhere);
///   a runtime's has more (`RuntimeSpec`).
/// - The process gets exactly the host variables named in `EnvAllowlist` and the ones in `EnvSet`;
///   then every variable matching `EnvDeny` or the host's own deny list (runtime injection vectors
///   such as `NODE_OPTIONS`, spec §18) is removed. A trailing `*` matches any suffix.
/// - While the project is not trusted, `UntrustedOverrides` are inserted before the first argument
///   that is not a flag (the program the runtime runs), or at the end (`LaunchSpec.arguments`).
type LaunchSpec =
    {
        Exe: string
        Args: string list
        EnvAllowlist: string list
        EnvSet: (string * string) list
        EnvDeny: string list
        Cwd: LaunchCwd
        /// Runtime flags, written `--name=value`, added while the project is not trusted, e.g. Bun's
        /// `--tsconfig-override` so that the project's `tsconfig.json` is not read.
        UntrustedOverrides: string list
    }

    override this.ToString() =
        String.concat " " (this.Exe :: this.Args) + $" (cwd {this.Cwd})"

/// One contribution a manifest declares: its contribution type and its id, unique for the type.
/// Declarative extensions also give the value; native and process extensions provide it when they
/// activate, and the host checks that they provide exactly what they declared.
type ContributionDecl =
    {
        Type: string
        Id: string
        Value: Json option
    }

    override this.ToString() = this.Type + "/" + this.Id

/// A method of an extension's RPC contract. Clients call it as `ext/<extension id>/<name>`.
/// `Params` and `Result` are JSON Schemas, for documentation and generated clients.
type MethodDecl =
    {
        Name: string
        Description: string option
        Params: Json option
        Result: Json option
    }

    override this.ToString() = this.Name

/// The RPC contract of an extension: the methods it adds to the client protocol.
type RpcContract =
    {
        Methods: MethodDecl list
    }

    override this.ToString() =
        String.concat ", " (this.Methods |> List.map (fun m -> m.Name))

/// An extension package's manifest (spec §16.1): everything the host reviews, pins and checks before
/// it runs any of the extension's code.
type Manifest =
    {
        /// Dotted lowercase, e.g. `fx.env.local` (built-ins: `Product.extensionId`) or `acme.tools`.
        Id: string
        /// Semantic version.
        Version: string
        Description: string option
        /// The SDK API version it is built against (`Versions.Sdk`).
        Sdk: string
        /// The extension protocol version it speaks (`Versions.Protocol`).
        Protocol: string
        Placement: Placement
        /// The extension runtime: `declarative`, `native`, `process`, or one an extension contributes
        /// (e.g. `bun`).
        Runtime: string
        /// How the `process` runtime starts it; only for that runtime.
        Launch: LaunchSpec option
        /// Components to install before it starts.
        Requires: ComponentRef list
        /// The capabilities its code may use (e.g. `clock`, `fs.read`); its environment is attenuated
        /// to them.
        Capabilities: string list
        Contributions: ContributionDecl list
        Rpc: RpcContract
        /// Extensions this one replaces (a fork or an override): they are disabled when it is admitted.
        Replaces: string list
        /// The manifest's own hash (`Manifest.hash`), if the package records it. It must match.
        Hash: string option
    }

    override this.ToString() =
        $"{this.Id} {this.Version} ({this.Runtime}, {this.Placement})"

/// The extension runtimes of the core (spec §16.1). Others are contributed (`ExtensionRuntime`).
[<RequireQualifiedAccess>]
module Runtimes =
    /// Data only: the manifest's contributions are the extension.
    [<Literal>]
    let Declarative = "declarative"

    /// F# compiled into the binary, registered from a static list of values (no assembly loading).
    [<Literal>]
    let Native = "native"

    /// A child process speaking the extension protocol over JSON-RPC on stdio, in any language.
    [<Literal>]
    let Process = "process"

    /// Whether the core runs this runtime itself.
    let isCore (runtime: string) : bool =
        runtime = Declarative || runtime = Native || runtime = Process

[<RequireQualifiedAccess>]
module Manifest =

    /// The file name of a manifest in a package directory (`fx.extension.json`).
    let fileName = Product.Name + ".extension.json"

    /// Patterns of the names in a manifest (ECMAScript, anchored).
    module Patterns =
        /// Extension ids: dotted lowercase segments.
        [<Literal>]
        let ExtensionId = "^[a-z][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)*$"

        /// Semantic versions.
        [<Literal>]
        let Version =
            "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\\+[0-9A-Za-z.-]+)?$"

        /// Runtime names.
        [<Literal>]
        let Runtime = "^[a-z][a-z0-9-]*$"

        /// Contribution type names, e.g. `extension-runtime`, `gate:model-egress`, `sample.word`.
        [<Literal>]
        let ContributionType = "^[a-z][a-z0-9-]*([.:/][a-z0-9][a-z0-9-]*)*$"

        /// Contribution ids, unique per type.
        [<Literal>]
        let ContributionId = "^[a-z0-9][a-z0-9._-]*$"

        /// Capability names, e.g. `fs.read`.
        [<Literal>]
        let Capability = "^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$"

        /// Method names: camelCase segments separated by `/`.
        [<Literal>]
        let MethodName = "^[a-z][A-Za-z0-9]*(/[a-z][A-Za-z0-9]*)*$"

        /// Environment variable names, with an optional trailing `*` in deny lists.
        [<Literal>]
        let EnvName = "^[A-Za-z_][A-Za-z0-9_]*\\*?$"

    let private named (pattern: string) (description: string) =
        Codec.string |> Codec.pattern pattern |> Codec.describe description

    let private extensionId =
        named Patterns.ExtensionId "An extension id: dotted lowercase segments, e.g. \"acme.tools\"."

    let private semver = named Patterns.Version "A semantic version, e.g. \"1.2.0\"."

    let placement: Codec<Placement> =
        Codec.enum
            [
                Placement.Host, "host"
                Placement.Environment, "environment"
                Placement.Client, "client"
            ]
        |> Codec.describe "Where the extension runs: in the host, in an environment (envd) or in a client."

    let componentRef: Codec<ComponentRef> =
        Codec.record {
            let! id =
                Codec.field "id" (fun (c: ComponentRef) -> c.Id) (Codec.string |> Codec.nonEmpty)

            and! version =
                Codec.field "version" (fun c -> c.Version) (Codec.string |> Codec.nonEmpty)

            return { Id = id; Version = version }
        }
        |> Codec.strict
        |> Codec.describe "A component to install before the extension starts: its id and a version range."
        |> Codec.named "ComponentRef"

    let launchCwd: Codec<LaunchCwd> =
        Codec.enum [ LaunchCwd.Owned, "owned"; LaunchCwd.Workspace, "workspace" ]

    let private envName =
        named Patterns.EnvName "An environment variable name; in deny lists, a trailing * matches any suffix."

    let launchSpec: Codec<LaunchSpec> =
        Codec.record {
            let! exe =
                Codec.field "exe" (fun (l: LaunchSpec) -> l.Exe) (Codec.string |> Codec.nonEmpty)

            and! args = Codec.defaultField "args" (fun l -> l.Args) [] (Codec.list Codec.string)

            and! allow =
                Codec.defaultField "envAllowlist" (fun l -> l.EnvAllowlist) [] (Codec.list envName)

            and! set =
                Codec.defaultField "envSet" (fun l -> l.EnvSet) [] (Codec.keyValues Codec.string)

            and! deny = Codec.defaultField "envDeny" (fun l -> l.EnvDeny) [] (Codec.list envName)
            and! cwd = Codec.defaultField "cwd" (fun l -> l.Cwd) LaunchCwd.Owned launchCwd

            // Written only when there are some, so that adding the field changed no manifest's hash.
            and! overrides =
                Codec.optionalField
                    "untrustedOverrides"
                    (fun l ->
                        if l.UntrustedOverrides.IsEmpty then
                            None
                        else
                            Some l.UntrustedOverrides)
                    (Codec.list Codec.string
                     |> Codec.describe
                         "Runtime flags (--name=value) added before the program while the project is not trusted.")

            return
                {
                    Exe = exe
                    Args = args
                    EnvAllowlist = allow
                    EnvSet = set
                    EnvDeny = deny
                    Cwd = cwd
                    UntrustedOverrides = overrides |> Option.defaultValue []
                }
        }
        |> Codec.strict
        |> Codec.describe
            "How to start a process: an extension process, or a runtime. exe and args are templates, e.g. ${root} (the package directory) and ${exeSuffix}."
        |> Codec.named "LaunchSpec"

    let contributionDecl: Codec<ContributionDecl> =
        Codec.record {
            let! kind =
                Codec.field
                    "type"
                    (fun (c: ContributionDecl) -> c.Type)
                    (named Patterns.ContributionType "A contribution type, e.g. \"extension-runtime\".")

            and! id =
                Codec.field
                    "id"
                    (fun c -> c.Id)
                    (named Patterns.ContributionId "The contribution's id, unique for its type.")

            and! value = Codec.optionalField "value" (fun c -> c.Value) Codec.json
            return { Type = kind; Id = id; Value = value }
        }
        |> Codec.strict
        |> Codec.describe
            "A contribution: its type and id. Declarative extensions give its value; other runtimes provide it when they activate."
        |> Codec.named "ContributionDecl"

    let methodDecl: Codec<MethodDecl> =
        Codec.record {
            let! name =
                Codec.field
                    "name"
                    (fun (m: MethodDecl) -> m.Name)
                    (named Patterns.MethodName "The method's name; clients call ext/<extension id>/<name>.")

            and! description =
                Codec.optionalField "description" (fun m -> m.Description) Codec.string

            and! parameters = Codec.optionalField "params" (fun m -> m.Params) Codec.json
            and! result = Codec.optionalField "result" (fun m -> m.Result) Codec.json

            return
                {
                    Name = name
                    Description = description
                    Params = parameters
                    Result = result
                }
        }
        |> Codec.strict
        |> Codec.describe "A method of the extension's RPC contract; params and result are JSON Schemas."
        |> Codec.named "MethodDecl"

    let rpcContract: Codec<RpcContract> =
        Codec.record {
            let! methods =
                Codec.defaultField "methods" (fun (r: RpcContract) -> r.Methods) [] (Codec.list methodDecl)

            return { Methods = methods }
        }
        |> Codec.strict
        |> Codec.named "RpcContract"

    let private noRpc: RpcContract = { Methods = [] }

    /// The manifest codec. Unknown members are issues (a misspelt field must not pass silently).
    /// Decode with `Manifest.decode`, which adds the checks across fields.
    let codec: Codec<Manifest> =
        Codec.record {
            let! id = Codec.field "id" (fun (m: Manifest) -> m.Id) extensionId
            and! version = Codec.field "version" (fun m -> m.Version) semver

            and! description =
                Codec.optionalField "description" (fun m -> m.Description) Codec.string

            and! sdk =
                Codec.field
                    "sdk"
                    (fun m -> m.Sdk)
                    (Codec.string
                     |> Codec.describe "The SDK API version the extension is built against, e.g. \"0.1\".")

            and! protocol =
                Codec.field
                    "protocol"
                    (fun m -> m.Protocol)
                    (Codec.string
                     |> Codec.nonEmpty
                     |> Codec.describe "The extension protocol version it speaks, e.g. \"1\".")

            and! placement =
                Codec.defaultField "placement" (fun m -> m.Placement) Placement.Host placement

            and! runtime =
                Codec.field
                    "runtime"
                    (fun m -> m.Runtime)
                    (named Patterns.Runtime "The extension runtime: declarative, native, process, or a contributed one.")

            and! launch = Codec.optionalField "launch" (fun m -> m.Launch) launchSpec

            and! requires =
                Codec.defaultField "requires" (fun m -> m.Requires) [] (Codec.list componentRef)

            and! capabilities =
                Codec.defaultField
                    "capabilities"
                    (fun m -> m.Capabilities)
                    []
                    (Codec.list (named Patterns.Capability "A capability name, e.g. \"fs.read\"."))

            and! contributions =
                Codec.defaultField "contributions" (fun m -> m.Contributions) [] (Codec.list contributionDecl)

            and! rpc = Codec.defaultField "rpc" (fun m -> m.Rpc) noRpc rpcContract

            and! replaces =
                Codec.defaultField "replaces" (fun m -> m.Replaces) [] (Codec.list extensionId)

            and! hash = Codec.optionalField "hash" (fun m -> m.Hash) Codec.string

            return
                {
                    Id = id
                    Version = version
                    Description = description
                    Sdk = sdk
                    Protocol = protocol
                    Placement = placement
                    Runtime = runtime
                    Launch = launch
                    Requires = requires
                    Capabilities = capabilities
                    Contributions = contributions
                    Rpc = rpc
                    Replaces = replaces
                    Hash = hash
                }
        }
        |> Codec.strict
        |> Codec.describe "An extension package's manifest."
        |> Codec.named "Manifest"

    /// A manifest with the required fields and nothing else: no contributions, methods or launch.
    let create (id: string) (version: string) (runtime: string) : Manifest =
        {
            Id = id
            Version = version
            Description = None
            Sdk = Versions.Sdk
            Protocol = Versions.Protocol
            Placement = Placement.Host
            Runtime = runtime
            Launch = None
            Requires = []
            Capabilities = []
            Contributions = []
            Rpc = noRpc
            Replaces = []
            Hash = None
        }

    /// The manifest's content hash: `sha256:<hex>` of the canonical JSON (RFC 8785 order) of the
    /// manifest without its `hash` member. Pins in host config name it (spec §16.5).
    let hash (manifest: Manifest) : string =
        let canonical = Json.serializeCanonical (codec.Encode { manifest with Hash = None })
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        "sha256:" + Convert.ToHexStringLower digest

    /// Runtime injection vectors (spec §18): variables that make a runtime load code or options
    /// before the program does (`InjectionVectors`). The process runtime always removes them,
    /// whatever the manifest says; a manifest may not set them.
    let deniedEnvironment = InjectionVectors.patterns

    /// Whether a variable name matches a deny pattern (exact, or a prefix ending in `*`).
    /// `ignoreCase`: Windows compares variable names case-insensitively.
    let matchesEnvPattern (ignoreCase: bool) (pattern: string) (name: string) : bool =
        EnvPattern.matches ignoreCase pattern name

    let private key (name: string) = PathSegment.Key name
    let private index (i: int) = PathSegment.Index i

    let private duplicates (names: string list) : string list =
        names |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

    /// Checks across fields that the codec cannot express; each issue has a path.
    let check (manifest: Manifest) : Issue list =
        [
            if not (Versions.isSdkVersion manifest.Sdk) then
                Issue.at [ key "sdk" ] $"expected a version major.minor, e.g. \"{Versions.Sdk}\""

            let declarative = manifest.Runtime = Runtimes.Declarative

            for i, c in List.indexed manifest.Contributions do
                match c.Value with
                | None when declarative ->
                    Issue.at
                        [ key "contributions"; index i ]
                        "a declarative extension gives each contribution's value; add \"value\""
                | Some _ when not declarative ->
                    Issue.at
                        [ key "contributions"; index i; key "value" ]
                        $"only declarative extensions give values in the manifest; a {manifest.Runtime} extension provides them when it activates"
                | _ -> ()

            for dup in duplicates (manifest.Contributions |> List.map string) do
                Issue.at [ key "contributions" ] $"the contribution {Json.quote dup} is declared twice"

            for dup in duplicates (manifest.Rpc.Methods |> List.map (fun m -> m.Name)) do
                Issue.at [ key "rpc"; key "methods" ] $"the method {Json.quote dup} is declared twice"

            if declarative && not manifest.Rpc.Methods.IsEmpty then
                Issue.at [ key "rpc"; key "methods" ] "a declarative extension has no code, so no methods"

            for dup in duplicates manifest.Capabilities do
                Issue.at [ key "capabilities" ] $"the capability {Json.quote dup} is listed twice"

            for dup in duplicates manifest.Replaces do
                Issue.at [ key "replaces" ] $"{Json.quote dup} is listed twice"

            if List.contains manifest.Id manifest.Replaces then
                Issue.at [ key "replaces" ] "an extension cannot replace itself"

            match manifest.Launch with
            | None when manifest.Runtime = Runtimes.Process ->
                Issue.at [ key "launch" ] "a process extension needs \"launch\": how to start it"
            | Some _ when declarative || manifest.Runtime = Runtimes.Native ->
                Issue.at
                    [ key "launch" ]
                    $"a {manifest.Runtime} extension is not started as a process; remove \"launch\""
            | Some launch ->
                for i, (name, _) in List.indexed launch.EnvSet do
                    let denied =
                        deniedEnvironment @ launch.EnvDeny
                        |> List.exists (fun pattern -> matchesEnvPattern true pattern name)

                    if denied then
                        Issue.at
                            [ key "launch"; key "envSet"; index i ]
                            $"{name} is denied: it can inject code into the runtime"

                    if name.Contains '*' then
                        Issue.at [ key "launch"; key "envSet"; index i ] "a variable name cannot contain *"

                for i, flag in List.indexed launch.UntrustedOverrides do
                    if not (flag.StartsWith '-') then
                        Issue.at
                            [ key "launch"; key "untrustedOverrides"; index i ]
                            "an override is a runtime flag, written --name=value"
            | None -> ()

            match manifest.Hash with
            | Some recorded when recorded <> hash manifest ->
                Issue.at
                    [ key "hash" ]
                    $"does not match the manifest's content; its hash is {Json.quote (hash manifest)}"
            | _ -> ()
        ]

    /// Decodes and checks a manifest: every issue, with its path, worded for a model to fix.
    let decode (json: Json) : Validation<Manifest, Issue> =
        codec.Decode json
        |> Result.bind (fun manifest ->
            match check manifest with
            | [] -> Ok manifest
            | issues -> Error issues)

    /// Parses and checks a manifest file's text.
    let parse (text: string) : Validation<Manifest, Issue> =
        match Json.parse text with
        | Ok json -> decode json
        | Error e -> Error [ Issue.create ("invalid JSON: " + string e) ]

    /// The manifest as indented JSON, as written to a package.
    let serialize (manifest: Manifest) : string =
        Json.serializeIndented (codec.Encode manifest)
