namespace Fx.Sdk

open System
open System.IO
open Fx.Contracts
open Fx.Effects

/// A file the launcher writes into a launch's owned config directory before every launch
/// (`RuntimeSpec.prepare`), e.g. Bun's `bunfig.toml`. Templates name it `${file.<name>}`.
type RuntimeFile =
    {
        /// A plain file name: letters, digits, `.`, `_` and `-`.
        Name: string
        Content: string
    }

    override this.ToString() = this.Name

/// A runtime (spec §6.2): the component that provides it and how to launch it, hardened, as data.
/// Extensions contribute them under `RuntimeSpecs.contributionType` (privileged); a runtime's id is
/// its component's. Module spec: docs/modules/runtimes.md.
type RuntimeSpec =
    {
        Component: Component
        /// How to start it. Its templates may use the placeholders `RuntimeSpec.placeholders` lists;
        /// its `Args` are runtime flags only: the program to run comes from the launch.
        Launch: LaunchSpec
        Files: RuntimeFile list
    }

    override this.ToString() =
        $"runtime {this.Component.Id} {this.Component.Version}"

/// The directories one launch owns (spec §6.3): the launcher creates them, outside the workspace,
/// and nothing project-local is in them.
type OwnedDirs =
    {
        Root: string
        /// The working directory with `cwd = owned`.
        Cwd: string
        Home: string
        /// Where the runtime's files (`RuntimeSpec.Files`) are written.
        Config: string
        Cache: string
        Temp: string
    }

    override this.ToString() = $"owned {this.Root}"

/// What a launch needs from the world, read once by the caller (envd, the host).
type LaunchSession =
    {
        /// The machine the process runs on: its executable suffix, its absolute paths, and how
        /// variable names compare (without case on Windows).
        Target: TargetTriple
        Owned: OwnedDirs
        /// The workspace, for `cwd = workspace`.
        Workspace: string option
        /// Until the project is trusted, the launch spec's `UntrustedOverrides` are added.
        ProjectTrusted: bool
        /// The launcher's own variables. Only the allowlisted ones are passed on.
        HostEnvironment: (string * string) list
    }

    override this.ToString() =
        $"launch on {this.Target} in {this.Owned.Root} (trusted project: {this.ProjectTrusted})"

[<RequireQualifiedAccess>]
module OwnedDirs =
    /// The usual layout under one directory: `cwd`, `home`, `config`, `cache` and `tmp`.
    let under (root: string) : OwnedDirs =
        {
            Root = root
            Cwd = Path.Combine(root, "cwd")
            Home = Path.Combine(root, "home")
            Config = Path.Combine(root, "config")
            Cache = Path.Combine(root, "cache")
            Temp = Path.Combine(root, "tmp")
        }

    /// Every directory, the root first.
    let all (owned: OwnedDirs) : string list =
        [ owned.Root; owned.Cwd; owned.Home; owned.Config; owned.Cache; owned.Temp ]

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module LaunchSpec =
    let private key (name: string) = PathSegment.Key name
    let private index (i: int) = PathSegment.Index i

    let private sameName (ignoreCase: bool) (a: string) (b: string) =
        String.Equals(
            a,
            b,
            if ignoreCase then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal
        )

    /// The arguments with the untrusted overrides inserted before the first argument that is not a
    /// flag (the program a runtime runs, or its subcommand), or at the end; unchanged in a trusted
    /// project.
    let arguments (projectTrusted: bool) (overrides: string list) (args: string list) : string list =
        if projectTrusted || overrides.IsEmpty then
            args
        else
            let flags = args |> List.takeWhile (fun arg -> arg.StartsWith '-')
            flags @ overrides @ List.skip flags.Length args

    /// Whether a name is denied: by the spec's deny list or as a known injection vector, compared
    /// without case on every platform.
    let isDenied (spec: LaunchSpec) (name: string) : bool =
        InjectionVectors.isVector name
        || spec.EnvDeny
           |> List.exists (fun pattern -> EnvPattern.matches true pattern name)

    /// The process's variables: the launcher's that the allowlist names (without case on Windows),
    /// then `added` in order (exports, then `EnvSet`), each replacing a variable of the same name;
    /// every denied name removed. Sorted by name, so the same inputs give the same list.
    let environment
        (ignoreCase: bool)
        (spec: LaunchSpec)
        (host: (string * string) list)
        (added: (string * string) list)
        : (string * string) list =
        let allowed (name: string) =
            spec.EnvAllowlist
            |> List.exists (fun pattern -> EnvPattern.matches ignoreCase pattern name)

        let inherited = host |> List.filter (fun (name, _) -> allowed name)

        (inherited @ added)
        |> List.fold
            (fun vars (name, value) ->
                (vars |> List.filter (fun (other, _) -> not (sameName ignoreCase name other)))
                @ [ name, value ])
            []
        |> List.filter (fun (name, _) -> not (isDenied spec name))
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    /// What every runtime's launch spec must hold to (spec §18): it denies every injection vector
    /// (`InjectionVectors.all`, or broader patterns), sets and allowlists none of them, allowlists
    /// names rather than the whole environment, and its overrides are flags.
    let validate (spec: LaunchSpec) : Issue list =
        [
            let missing =
                InjectionVectors.patterns
                |> List.filter (fun required ->
                    not (spec.EnvDeny |> List.exists (fun denied -> EnvPattern.covers denied required)))

            if not missing.IsEmpty then
                Issue.at
                    [ key "envDeny" ]
                    $"does not deny {Issue.quoteAll missing}: a launch spec must deny every runtime injection vector"

            for i, name in List.indexed spec.EnvAllowlist do
                if name = "*" then
                    Issue.at
                        [ key "envAllowlist"; index i ]
                        "* passes the launcher's whole environment on; name the variables the process needs"
                elif spec.EnvDeny |> List.exists (fun denied -> EnvPattern.covers denied name) then
                    Issue.at [ key "envAllowlist"; index i ] $"allows {name}, which is denied"

            for i, (name, _) in List.indexed spec.EnvSet do
                if name.Contains '*' then
                    Issue.at [ key "envSet"; index i ] "a variable name cannot contain *"
                elif isDenied spec name then
                    Issue.at
                        [ key "envSet"; index i ]
                        $"sets {name}, which is denied: it can inject code into the runtime"

            let names = spec.EnvSet |> List.map (fun (name, _) -> name.ToUpperInvariant())

            for dup in names |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst do
                Issue.at [ key "envSet" ] $"{dup} is set twice (names are compared without case, as on Windows)"

            for i, flag in List.indexed spec.UntrustedOverrides do
                if not (flag.StartsWith '-') then
                    Issue.at [ key "untrustedOverrides"; index i ] "an override is a runtime flag, written --name=value"
        ]

/// A runtime's launch: templates, validation, and the process it becomes. Pure, except `prepare`.
[<RequireQualifiedAccess>]
module RuntimeSpec =
    let private key (name: string) = PathSegment.Key name
    let private index (i: int) = PathSegment.Index i

    /// The owned directories' placeholders and their values.
    let private ownedVariables (owned: OwnedDirs) : (string * string) list =
        [
            "owned.root", owned.Root
            "owned.cwd", owned.Cwd
            "owned.home", owned.Home
            "owned.config", owned.Config
            "owned.cache", owned.Cache
            "owned.temp", owned.Temp
        ]

    /// The placeholders a runtime's templates may use:
    ///
    /// - `${root}`: the component's install directory; `${bin.<name>}`: a command it exports;
    /// - `${exeSuffix}`: `.exe` on Windows, empty elsewhere;
    /// - `${owned.root}`, `${owned.cwd}`, `${owned.home}`, `${owned.config}`, `${owned.cache}`,
    ///   `${owned.temp}`: the launch's owned directories;
    /// - `${file.<name>}`: one of the runtime's files, written in `${owned.config}`.
    ///
    /// Nothing else: no host variables, no workspace paths.
    let placeholders (spec: RuntimeSpec) : string list =
        [
            "root"
            "exeSuffix"
            yield! ownedVariables (OwnedDirs.under "") |> List.map fst
            for name, _ in spec.Component.Exports.Bin do
                "bin." + name
            for file in spec.Files do
                "file." + file.Name
        ]

    /// The value of every placeholder for a launch.
    let variables
        (spec: RuntimeSpec)
        (installed: InstalledComponent)
        (session: LaunchSession)
        : (string * string) list =
        [
            "root", installed.Dir
            "exeSuffix", TargetTriple.exeSuffix session.Target
            yield! ownedVariables session.Owned
            for name, path in installed.Bin do
                "bin." + name, path
            for file in spec.Files do
                "file." + file.Name, Path.Combine(session.Owned.Config, file.Name)
        ]

    /// Whether a path is absolute on the target: `/…` on Linux and macOS; `C:\…` (or `C:/…`) or
    /// `\\server\…` on Windows. Judged by the target's rules, not the machine's that evaluates it.
    let isAbsoluteOn (os: Os) (path: string) : bool =
        match os with
        | Os.Windows ->
            (path.Length >= 3
             && Char.IsAsciiLetter path[0]
             && path[1] = ':'
             && (path[2] = '\\' || path[2] = '/'))
            || path.StartsWith(@"\\", StringComparison.Ordinal)
        | Os.Linux
        | Os.MacOs -> path.StartsWith '/'

    /// Everything wrong with a runtime spec, each issue with its path: the launch spec's rules
    /// (`LaunchSpec.validate`), placeholders that do not exist, a program that is not in the install
    /// directory, arguments that are not flags, and file names.
    let validate (spec: RuntimeSpec) : Issue list =
        let known = placeholders spec |> List.map (fun name -> name, name)

        let template (path: PathSegment list) (text: string) =
            match Component.expand known text with
            | Ok _ -> []
            | Error message -> [ Issue.at path message ]

        let launch = spec.Launch

        [
            yield! LaunchSpec.validate launch |> List.map (Issue.under (key "launch"))

            if
                not (
                    launch.Exe.StartsWith("${bin.", StringComparison.Ordinal)
                    || launch.Exe.StartsWith("${root}", StringComparison.Ordinal)
                )
            then
                Issue.at
                    [ key "launch"; key "exe" ]
                    "a runtime runs from its install directory: start with ${bin.<command>} or ${root}, never a name looked up on PATH"

            yield! template [ key "launch"; key "exe" ] launch.Exe

            for i, arg in List.indexed launch.Args do
                if not (arg.StartsWith '-') then
                    Issue.at
                        [ key "launch"; key "args"; index i ]
                        "a runtime's arguments are flags, written --name=value; the program and its arguments come from the launch"

                yield! template [ key "launch"; key "args"; index i ] arg

            for i, flag in List.indexed launch.UntrustedOverrides do
                yield! template [ key "launch"; key "untrustedOverrides"; index i ] flag

            for i, (_, value) in List.indexed launch.EnvSet do
                yield! template [ key "launch"; key "envSet"; index i ] value

            for i, file in List.indexed spec.Files do
                if not (Component.isCommandName file.Name) then
                    Issue.at
                        [ key "files"; index i; key "name" ]
                        $"{Json.quote file.Name} is not a file name: letters, digits, '.', '_' and '-'"

            let names = spec.Files |> List.map (fun f -> f.Name.ToUpperInvariant())

            for dup in names |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst do
                Issue.at [ key "files" ] $"{dup} is listed twice (names are compared without case, as on Windows)"
        ]

    let fileCodec: Codec<RuntimeFile> =
        Codec.record {
            let! name =
                Codec.field
                    "name"
                    (fun (f: RuntimeFile) -> f.Name)
                    (Codec.string
                     |> Codec.nonEmpty
                     |> Codec.describe
                         "The file's name in the owned config directory; templates name it ${file.<name>}.")

            and! content = Codec.field "content" (fun f -> f.Content) Codec.string
            return { Name = name; Content = content }
        }
        |> Codec.strict
        |> Codec.describe "A file the launcher writes before every launch."
        |> Codec.named "RuntimeFile"

    let private shape: Codec<RuntimeSpec> =
        Codec.record {
            let! componentDefinition =
                Codec.field "component" (fun (r: RuntimeSpec) -> r.Component) Component.codec

            and! launch = Codec.field "launch" (fun r -> r.Launch) Manifest.launchSpec
            and! files = Codec.defaultField "files" (fun r -> r.Files) [] (Codec.list fileCodec)

            return
                {
                    Component = componentDefinition
                    Launch = launch
                    Files = files
                }
        }
        |> Codec.strict

    /// The runtime codec. Decoding also validates (`validate`), so a manifest whose runtime does not
    /// deny the injection vectors is refused when the extension is admitted.
    let codec: Codec<RuntimeSpec> =
        { shape with
            Decode =
                fun json ->
                    shape.Decode json
                    |> Result.bind (fun spec ->
                        match validate spec with
                        | [] -> Ok spec
                        | issues -> Error issues)
        }
        |> Codec.describe "A runtime: the component that provides it and how to launch it, hardened."
        |> Codec.named "RuntimeSpec"

    /// The process that runs `program` (the program and its arguments, as they are) on the runtime:
    /// templates expanded; untrusted overrides added if the project is not trusted; the working
    /// directory the policy says; a fresh environment (`LaunchSpec.environment`, with the component's
    /// exported variables before `EnvSet`), never the launcher's. Refused if the spec is invalid, if
    /// `installed` is another component, or if the program would not be an absolute path.
    let launch
        (spec: RuntimeSpec)
        (installed: InstalledComponent)
        (session: LaunchSession)
        (program: string list)
        : Validation<ProcSpec, Issue> =
        match validate spec with
        | _ :: _ as issues -> Error issues
        | [] when
            installed.Id <> spec.Component.Id
            || SemVer.compare installed.Version spec.Component.Version <> 0
            ->
            Error
                [
                    Issue.create $"the runtime is {spec.Component}, but {installed.Id} {installed.Version} was given"
                ]
        | [] ->
            let values = variables spec installed session
            let launch = spec.Launch
            let issues = ResizeArray<Issue>()

            let expand (path: PathSegment list) (text: string) =
                match Component.expand values text with
                | Ok expanded -> expanded
                | Error message ->
                    issues.Add(Issue.at path message)
                    text

            let exe = expand [ key "launch"; key "exe" ] launch.Exe

            let args =
                launch.Args
                |> List.mapi (fun i arg -> expand [ key "launch"; key "args"; index i ] arg)

            let overrides =
                launch.UntrustedOverrides
                |> List.mapi (fun i flag -> expand [ key "launch"; key "untrustedOverrides"; index i ] flag)

            let set =
                launch.EnvSet
                |> List.mapi (fun i (name, value) -> name, expand [ key "launch"; key "envSet"; index i ] value)

            if not (isAbsoluteOn session.Target.Os exe) then
                issues.Add(Issue.at [ key "launch"; key "exe" ] $"{Json.quote exe} is not an absolute path")

            let cwd =
                match launch.Cwd, session.Workspace with
                | LaunchCwd.Owned, _ -> Some session.Owned.Cwd
                | LaunchCwd.Workspace, Some workspace -> Some workspace
                | LaunchCwd.Workspace, None ->
                    issues.Add(
                        Issue.at [ key "launch"; key "cwd" ] "the runtime runs in the workspace, and there is none"
                    )

                    None

            if issues.Count > 0 then
                Error(List.ofSeq issues)
            else
                Ok
                    {
                        Program = Exec(exe, LaunchSpec.arguments session.ProjectTrusted overrides (args @ program))
                        Cwd = cwd
                        Env =
                            Exactly(
                                LaunchSpec.environment
                                    (session.Target.Os = Os.Windows)
                                    launch
                                    session.HostEnvironment
                                    (installed.Env @ set)
                            )
                    }

    /// Creates the owned directories and writes the runtime's files into `Config`. Run it before
    /// every launch: the files are rewritten, so a process cannot change what the next one reads.
    let prepare<'R when 'R :> IFs and 'R :> IScope>
        (spec: RuntimeSpec)
        (owned: OwnedDirs)
        : Eff<'R, Result<unit, FsError>> =
        effResult {
            for dir in OwnedDirs.all owned do
                do! Fs.createDirectory dir

            for file in spec.Files do
                do! Fs.writeText (Path.Combine(owned.Config, file.Name)) file.Content
        }

/// The `runtime` contribution type.
[<RequireQualifiedAccess>]
module RuntimeSpecs =
    /// Runtimes (spec §6.2, §16.2 "Installation and execution"), by id (the contribution id is the
    /// component's id). Privileged (spec §16.5): only extensions from host config, policy or the
    /// binary may contribute them. Values are validated when they are loaded (`RuntimeSpec.codec`).
    let contributionType: ContributionType<RuntimeSpec> =
        ContributionType.data "runtime" "A runtime: its component and its hardened launch." RuntimeSpec.codec
        |> ContributionType.privileged
