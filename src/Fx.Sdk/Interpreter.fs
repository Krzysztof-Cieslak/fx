namespace Fx.Sdk

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Fx.Contracts

/// How a kernel's language exposes host calls (spec §6.2).
[<RequireQualifiedAccess>]
type AsyncHostCalls =
    /// Cells await host calls (`await fx.shell.run(…)`).
    | Await
    /// Host calls look synchronous to cells; the kernel suspends the cell while the host answers.
    | Transparent

    override this.ToString() =
        match this with
        | Await -> "await"
        | Transparent -> "transparent"

/// How a kernel installs packages for a session (spec §6.3), if it does.
type PackageSupport =
    {
        /// The package manager, e.g. `bun` or `nuget`.
        Manager: string
    }

    override this.ToString() = this.Manager

/// What an interpreter's kernels can do (spec §6.2 `InterpreterCaps`). The interpreter declares
/// them; its kernels announce them in `initialize`, and the conformance suite checks that they hold.
type InterpreterCaps =
    {
        /// The kernel serializes its state (`snapshot`) and restores it in a fresh process (`restore`).
        Snapshot: bool
        AsyncHostCalls: AsyncHostCalls
        Packages: PackageSupport option
        /// The same cells with the same host answers give the same results (no clock, randomness or
        /// I/O of its own).
        Deterministic: bool
    }

    override this.ToString() =
        let packages =
            match this.Packages with
            | Some p -> " packages " + p.Manager
            | None -> ""

        let yes (flag: bool) = if flag then "yes" else "no"
        $"snapshot {yes this.Snapshot}, host calls {this.AsyncHostCalls}, deterministic {yes this.Deterministic}{packages}"

/// A parameter of a host binding. Arguments are passed by position; the name documents it.
[<NoComparison>]
type BindingParam =
    {
        Name: string
        Description: string option
        Schema: Schema
        /// May be left out (trailing).
        Optional: bool
    }

    override this.ToString() =
        let optional = if this.Optional then "?" else ""
        $"{this.Name}{optional}: {Schema.render this.Schema}"

/// A host binding as interpreters see it (spec §6.5): a dotted path (`shell.run` is
/// `<global>.shell.run(…)` in TypeScript), what it does, its parameters and its result. Every call
/// is a `host/call` the host decodes with the parameters' codecs.
[<NoComparison>]
type BindingDecl =
    {
        Path: string
        Description: string option
        Params: BindingParam list
        Returns: Schema
    }

    override this.ToString() =
        let parameters = this.Params |> List.map string |> String.concat ", "
        $"{this.Path}({parameters}): {Schema.render this.Returns}"

/// What the host offers a session's kernels (spec §6.5): the bridge's global, its bindings with
/// their schemas, descriptions of the groups they form (`shell`, `fs`), and the type `finish` takes
/// (spec §4.5 `TurnResult`, or a goal's result type).
[<NoComparison>]
type BindingSurface =
    {
        /// The global in the kernel's language: `Fx.Product.kernelGlobal`.
        Global: string
        Description: string option
        /// Group paths and their descriptions, e.g. `"fs", "The workspace's files."`.
        Groups: (string * string) list
        Bindings: BindingDecl list
        /// The schema of `finish`'s result.
        Finish: Schema
    }

    override this.ToString() =
        $"surface {this.Global} ({this.Bindings.Length} bindings)"

/// What an interpreter makes of a surface (spec §6.2 `BindingFiles`, generated host-side): files its
/// kernel reads (written before it starts, e.g. `<global>.d.ts` for the type checker) and the
/// declarations a model reads in its prompt.
[<NoComparison>]
type BindingFiles =
    {
        Files: RuntimeFile list
        /// The bridge as the model sees it (e.g. the text of `<global>.d.ts`).
        Declarations: string
    }

    override this.ToString() =
        let names = this.Files |> List.map (fun f -> f.Name) |> String.concat ", "
        $"binding files [{names}]"

/// An example cell for the prompt.
type PromptExample =
    {
        /// What it shows, one line.
        Title: string
        Code: string
    }

    override this.ToString() = this.Title

/// How a model is taught to write cells for an interpreter (spec §6.2 `PromptPack`): instructions
/// and examples. The bridge's declarations come from `BindingFiles`; `PromptPack.render` puts them
/// together (the prompt render, T126, places the text).
type PromptPack =
    {
        /// The code fence's language, e.g. `ts`.
        Fence: string
        Instructions: string
        Examples: PromptExample list
    }

    override this.ToString() =
        $"prompt pack ({this.Examples.Length} examples)"

/// How an interpreter type-checks cells (spec §6.2 `CheckSpec`).
[<RequireQualifiedAccess>]
type CheckSpec =
    /// The kernel does: it handles `cell/check`, and `cell/run` with `check` runs nothing that has
    /// type errors.
    | Kernel

    override this.ToString() =
        match this with
        | Kernel -> "kernel"

/// How the host starts an interpreter's kernel: on a runtime (a `runtime` contribution, e.g.
/// `fx.runtime.bun`'s `bun`), whose hardened launch comes first, then the program.
type InterpreterLaunch =
    {
        /// The runtime's id.
        Runtime: string
        /// The program and its arguments: templates with `${asset.<name>}` (an asset, extracted) and
        /// `${bindings}` (the directory with the binding files).
        Program: string list
    }

    override this.ToString() =
        let program = String.concat " " this.Program
        $"on {this.Runtime}: {program}"

/// An interpreter (spec §6.2 `InterpreterProvider`): a language for cells, the kernel that runs
/// them and how to start it, how its bindings and prompt are generated, and what it can do. A code
/// contribution of native extensions (built-ins), under `InterpreterProviders.contributionType`.
/// Module spec: docs/modules/interpreters.md.
[<NoEquality; NoComparison>]
type InterpreterProvider =
    {
        /// Unique among interpreters, e.g. `bun-ts`; the interpreter variant's value.
        Id: string
        /// The cells' language, as kernels announce it (`typescript`).
        Language: string
        Description: string
        /// Components the kernel needs in the environment (its runtime's among them).
        Requires: ComponentRef list
        Launch: InterpreterLaunch
        /// The kernel's own files, carried in the binary and extracted (with hash checks) into the
        /// environment before a launch (spec §16.4). Plain file names.
        Assets: RuntimeFile list
        /// The binding files for a surface; pure and deterministic.
        Bindings: BindingSurface -> BindingFiles
        PromptPack: PromptPack
        Check: CheckSpec option
        Caps: InterpreterCaps
    }

    override this.ToString() =
        $"interpreter {this.Id} ({this.Language}; {this.Caps})"

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AsyncHostCalls =
    let codec: Codec<AsyncHostCalls> =
        Codec.enum [ AsyncHostCalls.Await, "await"; AsyncHostCalls.Transparent, "transparent" ]
        |> Codec.describe "How cells see host calls: awaited, or as synchronous calls."
        |> Codec.named "AsyncHostCalls"

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PackageSupport =
    let codec: Codec<PackageSupport> =
        Codec.record {
            let! manager =
                Codec.field "manager" (fun (p: PackageSupport) -> p.Manager) (Codec.string |> Codec.nonEmpty)

            return { Manager = manager }
        }
        |> Codec.describe "How the kernel installs packages for a session."
        |> Codec.named "PackageSupport"

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InterpreterCaps =
    let codec: Codec<InterpreterCaps> =
        Codec.record {
            let! snapshot =
                Codec.field "snapshot" (fun (c: InterpreterCaps) -> c.Snapshot) Codec.bool

            and! hostCalls =
                Codec.field "asyncHostCalls" (fun c -> c.AsyncHostCalls) AsyncHostCalls.codec

            and! packages =
                Codec.optionalField "packages" (fun c -> c.Packages) PackageSupport.codec

            and! deterministic =
                Codec.field "deterministic" (fun c -> c.Deterministic) Codec.bool

            return
                {
                    Snapshot = snapshot
                    AsyncHostCalls = hostCalls
                    Packages = packages
                    Deterministic = deterministic
                }
        }
        |> Codec.describe "What the kernel can do (spec §6.2 InterpreterCaps)."
        |> Codec.named "InterpreterCaps"

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PromptPack =
    /// The prompt text for an interpreter: its instructions, the bridge's declarations in a code
    /// block, then the examples. Deterministic; `\n` line ends.
    let render (pack: PromptPack) (files: BindingFiles) : string =
        let fence (code: string) =
            "```" + pack.Fence + "\n" + code.TrimEnd('\n') + "\n```"

        let text = StringBuilder()
        text.Append(pack.Instructions.TrimEnd('\n')).Append("\n\n") |> ignore
        text.Append(fence files.Declarations).Append('\n') |> ignore

        for example in pack.Examples do
            text.Append('\n').Append(example.Title).Append('\n') |> ignore
            text.Append(fence example.Code).Append('\n') |> ignore

        text.ToString()

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InterpreterProvider =
    let private key (name: string) = PathSegment.Key name
    let private index (i: int) = PathSegment.Index i

    /// Interpreter ids: lowercase words joined by `-` or `.`, e.g. `bun-ts`.
    [<Literal>]
    let IdPattern = "^[a-z][a-z0-9]*([.-][a-z0-9]+)*$"

    let private idRegex = Regex(IdPattern, RegexOptions.CultureInvariant)

    /// The placeholders the program may use: `${bindings}` and `${asset.<name>}` for every asset.
    let placeholders (provider: InterpreterProvider) : string list =
        "bindings" :: (provider.Assets |> List.map (fun asset -> "asset." + asset.Name))

    /// `sha256:<hex>` of the assets: of the canonical JSON of their names and contents' SHA-256,
    /// sorted by name. Equal assets have equal digests in any order; it names their directory.
    let assetsDigest (provider: InterpreterProvider) : string =
        let hex (bytes: byte array) = Convert.ToHexStringLower bytes

        let listing =
            provider.Assets
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))
            |> List.map (fun asset ->
                Json.Object
                    [
                        "name", Json.String asset.Name
                        "sha256", Json.String(hex (SHA256.HashData(Encoding.UTF8.GetBytes asset.Content)))
                    ])
            |> Json.Array

        "sha256:"
        + hex (SHA256.HashData(Encoding.UTF8.GetBytes(Json.serializeCanonical listing)))

    /// A file in a directory, joined with the target's separator.
    let private fileIn (os: Os) (dir: string) (name: string) =
        let separator = if os = Os.Windows then '\\' else '/'

        if dir.EndsWith separator || dir.EndsWith '/' then
            dir + name
        else
            dir + string separator + name

    /// The program and its arguments for a launch: `${asset.<name>}` is the asset in `assetsDir`,
    /// `${bindings}` is `bindingsDir`, joined by the target's rules (pure).
    let program
        (provider: InterpreterProvider)
        (os: Os)
        (assetsDir: string)
        (bindingsDir: string)
        : Validation<string list, Issue> =
        let values =
            ("bindings", bindingsDir)
            :: (provider.Assets
                |> List.map (fun asset -> "asset." + asset.Name, fileIn os assetsDir asset.Name))

        let expanded =
            provider.Launch.Program
            |> List.mapi (fun i template ->
                Component.expand values template
                |> Result.mapError (fun message -> Issue.at [ key "launch"; key "program"; index i ] message))

        let issues =
            expanded
            |> List.collect (fun r ->
                match r with
                | Error issue -> [ issue ]
                | Ok _ -> [])

        if issues.IsEmpty then
            Ok(expanded |> List.choose Result.toOption)
        else
            Error issues

    /// An empty surface, to check that `Bindings` works when the interpreter is registered.
    let private probeSurface: BindingSurface =
        {
            Global = "probe"
            Description = None
            Groups = []
            Bindings = []
            Finish = Schema.ofKind SchemaKind.Any
        }

    /// Everything wrong with an interpreter, each issue with its path: its id, language and runtime,
    /// its assets' names, its program's templates, its requirements, and `Bindings` failing on an
    /// empty surface.
    let validate (provider: InterpreterProvider) : Issue list =
        [
            if not (idRegex.IsMatch provider.Id) then
                Issue.at
                    [ key "id" ]
                    $"{Json.quote provider.Id} is not an interpreter id: lowercase words joined by - or ."

            if String.IsNullOrWhiteSpace provider.Language then
                Issue.at [ key "language" ] "an interpreter names the language of its cells"

            if String.IsNullOrWhiteSpace provider.Launch.Runtime then
                Issue.at [ key "launch"; key "runtime" ] "an interpreter runs on a runtime: name it"

            if provider.Launch.Program.IsEmpty then
                Issue.at [ key "launch"; key "program" ] "an interpreter's launch names the program to run"

            for i, asset in List.indexed provider.Assets do
                if not (Component.isCommandName asset.Name) then
                    Issue.at
                        [ key "assets"; index i; key "name" ]
                        $"{Json.quote asset.Name} is not a file name: letters, digits, '.', '_' and '-'"

            let names = provider.Assets |> List.map (fun a -> a.Name.ToUpperInvariant())

            for dup in names |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst do
                Issue.at [ key "assets" ] $"{dup} is listed twice (names are compared without case, as on Windows)"

            match program provider Os.Linux "/assets" "/bindings" with
            | Ok _ -> ()
            | Error issues -> yield! issues

            for i, required in List.indexed provider.Requires do
                if String.IsNullOrWhiteSpace required.Id then
                    Issue.at [ key "requires"; index i; key "id" ] "a component id is required"

                match VersionRange.parse required.Version with
                | Ok _ -> ()
                | Error message -> Issue.at [ key "requires"; index i; key "version" ] message

            let rendered =
                try
                    Ok(provider.Bindings probeSurface)
                with e ->
                    Error(e.GetType().Name)

            match rendered with
            | Ok files ->
                for i, file in List.indexed files.Files do
                    if not (Component.isCommandName file.Name) then
                        Issue.at
                            [ key "bindings"; key "files"; index i; key "name" ]
                            $"{Json.quote file.Name} is not a file name"
            | Error kind ->
                Issue.at [ key "bindings" ] $"generating the binding files of an empty surface failed ({kind})"
        ]

[<RequireQualifiedAccess>]
module InterpreterProviders =
    /// Interpreters (spec §6.2, §16.2 "Installation and execution"), by id. Code (`Bindings` is a
    /// function), so only native extensions contribute them; privileged (spec §16.5). Validate a
    /// provider with `InterpreterProvider.validate` before using it.
    let contributionType: ContributionType<InterpreterProvider> =
        ContributionType.inProcess
            "interpreter"
            "An interpreter: the language of cells, its kernel and how to launch it, its bindings and prompt."
            ContributionKind.Code
        |> ContributionType.privileged
