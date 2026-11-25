/// Runtimes (spec §6.2): injection vectors, launch spec rules, templates, and the process a launch
/// becomes. The Bun runtime's own tests are in tests/Fx.Ext.RuntimeBun.Tests.
module Fx.Sdk.Tests.RuntimeTests

open System.IO
open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Sdk
open Fx.Sdk.Tests.Support

let private semver (text: string) =
    match SemVer.parse text with
    | Ok v -> v
    | Error e -> failtest e

let private linux = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)
let private windows = TargetTriple.create Os.Windows Arch.X64 None

let private tool: Component =
    { Component.create
          "tool"
          (semver "2.0.0")
          [
              {
                  Platforms = []
                  Method =
                      Archive
                          {
                              Url = "https://example.com/tool-${version}.zip"
                              Digest = Sha256(String.replicate 64 "b")
                              Format = ArchiveFormat.Zip
                              Strip = 1
                          }
              }
          ] with
        Exports =
            {
                Bin = [ "tool", "bin/tool${exeSuffix}" ]
                Env = []
            }
    }

let private runtime: RuntimeSpec =
    {
        Component = tool
        Launch =
            {
                Exe = "${bin.tool}"
                Args = [ "--config=${file.tool.conf}"; "--quiet" ]
                EnvAllowlist = [ "PATH"; "LANG"; "LC_*" ]
                EnvSet = [ "HOME", "${owned.home}"; "TMPDIR", "${owned.temp}"; "TOOL_TELEMETRY", "0" ]
                EnvDeny = InjectionVectors.patterns @ [ "TOOL_PLUGINS" ]
                Cwd = LaunchCwd.Owned
                UntrustedOverrides = [ "--project-config=${file.empty.json}" ]
            }
        Files =
            [
                {
                    Name = "tool.conf"
                    Content = "telemetry = off\n"
                }
                {
                    Name = "empty.json"
                    Content = "{}\n"
                }
            ]
    }

let private installedOn (dir: string) (exe: string) : InstalledComponent =
    {
        Id = "tool"
        Version = semver "2.0.0"
        Dir = dir
        Bin = [ "tool", exe ]
        Env = [ "TOOL_HOME", dir ]
    }

let private owned = OwnedDirs.under "/owned/s1"

let private hostile: (string * string) list =
    [
        "PATH", "/usr/bin"
        "LANG", "C.UTF-8"
        "LC_ALL", "C"
        "HOME", "/home/me"
        "SECRET_TOKEN", "do-not-leak"
        "BUN_OPTIONS", "--preload=evil.ts"
        "LD_PRELOAD", "/tmp/evil.so"
        "TOOL_PLUGINS", "/tmp/evil"
    ]

let private session: LaunchSession =
    {
        Target = linux
        Owned = owned
        Workspace = Some "/work/project"
        ProjectTrusted = false
        HostEnvironment = hostile
    }

let private launch (spec: RuntimeSpec) (session: LaunchSession) (program: string list) : ProcSpec =
    match RuntimeSpec.launch spec (installedOn "/opt/tool/2.0.0" "/opt/tool/2.0.0/bin/tool") session program with
    | Ok proc -> proc
    | Error issues -> failtest (Issue.render issues)

let private issues (result: Validation<'T, Issue>) : string list =
    match result with
    | Ok value -> failtest $"expected issues, got {value}"
    | Error issues -> issues |> List.map string

/// An environment with a filesystem and a scope, for `prepare`.
type private FsEnv =
    {
        Files: InMemoryFs
        Scope: Scope
    }

    interface IFs with
        member env.FsRead = env.Files.FsRead
        member env.Fs = env.Files.Fs

    interface IScopeWith<FsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = "FsEnv"

[<Tests>]
let tests =
    testList
        "Runtimes"
        [
            test "injection vectors are unique patterns, and patterns cover narrower ones" {
                Expect.equal (List.distinct InjectionVectors.patterns) InjectionVectors.patterns "unique"
                Expect.equal Manifest.deniedEnvironment InjectionVectors.patterns "the process runtime denies the same"

                for name in
                    [
                        "BUN_OPTIONS"
                        "bun_inspect_connect_to"
                        "NODE_PATH"
                        "DYLD_INSERT_LIBRARIES"
                        "LD_AUDIT"
                    ] do
                    Expect.isTrue (InjectionVectors.isVector name) name

                for name in [ "PATH"; "BUN_RUNTIME_TRANSPILER_CACHE_PATH"; "DO_NOT_TRACK"; "NODE_ENV" ] do
                    Expect.isFalse (InjectionVectors.isVector name) name

                Expect.isTrue (EnvPattern.covers "BUN_*" "BUN_INSPECT*") "prefix covers a longer prefix"
                Expect.isTrue (EnvPattern.covers "BUN_*" "BUN_OPTIONS") "prefix covers a name"
                Expect.isTrue (EnvPattern.covers "node_options" "NODE_OPTIONS") "without case"
                Expect.isFalse (EnvPattern.covers "BUN_OPTIONS" "BUN_*") "a name does not cover a prefix"
                Expect.isFalse (EnvPattern.covers "BUN_INSPECT*" "BUN_*") "a longer prefix does not cover a shorter one"
                Expect.isFalse (EnvPattern.covers "NODE_PATH" "NODE_PATHS") "exact"
            }

            test "untrusted overrides go before the first argument that is not a flag" {
                let overrides = [ "--a=1"; "--b" ]

                Expect.equal
                    (LaunchSpec.arguments false overrides [ "--x"; "main.ts"; "--y" ])
                    [ "--x"; "--a=1"; "--b"; "main.ts"; "--y" ]
                    "before the program"

                Expect.equal
                    (LaunchSpec.arguments false overrides [ "--x"; "--version" ])
                    [ "--x"; "--version"; "--a=1"; "--b" ]
                    "at the end"

                Expect.equal
                    (LaunchSpec.arguments false overrides [ "check"; "-p" ])
                    [ "--a=1"; "--b"; "check"; "-p" ]
                    "before a subcommand"

                Expect.equal (LaunchSpec.arguments true overrides [ "--x"; "main.ts" ]) [ "--x"; "main.ts" ] "trusted"
            }

            test "a launch spec must deny every injection vector" {
                Expect.equal (LaunchSpec.validate runtime.Launch) [] "valid"

                let without =
                    { runtime.Launch with
                        EnvDeny =
                            InjectionVectors.patterns
                            |> List.filter (fun p -> p <> "BUN_OPTIONS" && p <> "NODE_PATH")
                    }

                Expect.equal
                    (LaunchSpec.validate without |> List.map string)
                    [
                        "$.envDeny: does not deny \"BUN_OPTIONS\", \"NODE_PATH\": a launch spec must deny every runtime injection vector"
                    ]
                    "missing"

                Expect.equal
                    (LaunchSpec.validate { runtime.Launch with EnvDeny = [] } |> List.length)
                    1
                    "an empty deny list is one issue"

                let broader =
                    { runtime.Launch with
                        EnvDeny =
                            [ "BUN_*"; "NODE_*" ]
                            @ (InjectionVectors.patterns
                               |> List.filter (fun p -> not (p.StartsWith "BUN_" || p.StartsWith "NODE_")))
                    }

                Expect.equal (LaunchSpec.validate broader) [] "broader patterns cover narrower ones"
            }

            test "a launch spec sets and allowlists no denied name, and names what it allows" {
                let spec =
                    { runtime.Launch with
                        EnvAllowlist = [ "PATH"; "*"; "NODE_OPTIONS"; "TOOL_PLUGINS" ]
                        EnvSet = [ "HOME", "x"; "bun_options", "--preload=x"; "A*", "x"; "home", "y" ]
                        UntrustedOverrides = [ "--ok"; "project.json" ]
                    }

                Expect.equal
                    (LaunchSpec.validate spec |> List.map string)
                    [
                        "$.envAllowlist[1]: * passes the launcher's whole environment on; name the variables the process needs"
                        "$.envAllowlist[2]: allows NODE_OPTIONS, which is denied"
                        "$.envAllowlist[3]: allows TOOL_PLUGINS, which is denied"
                        "$.envSet[1]: sets bun_options, which is denied: it can inject code into the runtime"
                        "$.envSet[2]: a variable name cannot contain *"
                        "$.envSet: HOME is set twice (names are compared without case, as on Windows)"
                        "$.untrustedOverrides[1]: an override is a runtime flag, written --name=value"
                    ]
                    "issues"
            }

            test "a runtime runs from its install directory, takes flags only, and uses known placeholders" {
                Expect.equal (RuntimeSpec.validate runtime) [] "valid"

                let broken =
                    { runtime with
                        Launch =
                            { runtime.Launch with
                                Exe = "tool"
                                Args = [ "--config=${file.missing.conf}"; "main.js" ]
                                EnvSet = [ "HOME", "${env.HOME}" ]
                            }
                        Files =
                            [
                                { Name = "../x"; Content = "" }
                                { Name = "A"; Content = "" }
                                { Name = "a"; Content = "" }
                            ]
                    }

                let found = RuntimeSpec.validate broken |> List.map string

                Expect.equal
                    found
                    [
                        "$.launch.exe: a runtime runs from its install directory: start with ${bin.<command>} or ${root}, never a name looked up on PATH"
                        "$.launch.args[0]: unknown placeholder ${file.missing.conf} in \"--config=${file.missing.conf}\"; known: \"${root}\", \"${exeSuffix}\", \"${owned.root}\", \"${owned.cwd}\", \"${owned.home}\", \"${owned.config}\", \"${owned.cache}\", \"${owned.temp}\", \"${bin.tool}\", \"${file.../x}\", \"${file.A}\", \"${file.a}\""
                        "$.launch.args[1]: a runtime's arguments are flags, written --name=value; the program and its arguments come from the launch"
                        "$.launch.untrustedOverrides[0]: unknown placeholder ${file.empty.json} in \"--project-config=${file.empty.json}\"; known: \"${root}\", \"${exeSuffix}\", \"${owned.root}\", \"${owned.cwd}\", \"${owned.home}\", \"${owned.config}\", \"${owned.cache}\", \"${owned.temp}\", \"${bin.tool}\", \"${file.../x}\", \"${file.A}\", \"${file.a}\""
                        "$.launch.envSet[0]: unknown placeholder ${env.HOME} in \"${env.HOME}\"; known: \"${root}\", \"${exeSuffix}\", \"${owned.root}\", \"${owned.cwd}\", \"${owned.home}\", \"${owned.config}\", \"${owned.cache}\", \"${owned.temp}\", \"${bin.tool}\", \"${file.../x}\", \"${file.A}\", \"${file.a}\""
                        "$.files[0].name: \"../x\" is not a file name: letters, digits, '.', '_' and '-'"
                        "$.files: A is listed twice (names are compared without case, as on Windows)"
                    ]
                    "issues"
            }

            test "the codec round-trips and refuses an invalid runtime with paths" {
                match Codec.decodeText RuntimeSpec.codec (Codec.encodeText RuntimeSpec.codec runtime) with
                | Ok decoded -> Expect.equal decoded runtime "round trip"
                | Error issues -> failtest (Issue.render issues)

                let noDeny =
                    Codec.encodeText
                        RuntimeSpec.codec
                        { runtime with
                            Launch =
                                { runtime.Launch with
                                    EnvDeny = [ "TOOL_PLUGINS" ]
                                }
                        }

                match Codec.decodeText RuntimeSpec.codec noDeny with
                | Ok _ -> failtest "a runtime without the deny list decoded"
                | Error issues ->
                    Expect.stringStarts
                        (string (List.head issues))
                        "$.launch.envDeny: does not deny \"BUN_OPTIONS\", \"BUN_INSPECT*\""
                        "issue"
            }

            test "the runtime contribution type is privileged data" {
                let t = RuntimeSpecs.contributionType
                Expect.equal t.Name "runtime" "name"
                Expect.isTrue t.Privileged "privileged (spec §16.5)"
                Expect.equal t.Kind ContributionKind.Data "data"

                match t.OfJson (t.ToJson runtime) None with
                | Ok value -> Expect.equal value runtime "through the manifest's JSON"
                | Error issues -> failtest (Issue.render issues)
            }

            test "JSON Schema of a runtime (golden)" {
                Golden.expect
                    "runtime.schema.json"
                    (Json.serializeIndented (Schema.toJsonSchema RuntimeSpec.codec.Schema))
            }

            test "a launch: flags, overrides before the program while untrusted, owned cwd" {
                let proc = launch runtime session [ "/srv/main.js"; "--port=1" ]

                Expect.equal
                    proc.Program
                    (Exec(
                        "/opt/tool/2.0.0/bin/tool",
                        [
                            "--config=" + owned.Config + "/tool.conf"
                            "--quiet"
                            "--project-config=" + owned.Config + "/empty.json"
                            "/srv/main.js"
                            "--port=1"
                        ]
                    ))
                    "argv"

                Expect.equal proc.Cwd (Some owned.Cwd) "owned"

                let trusted =
                    launch runtime { session with ProjectTrusted = true } [ "/srv/main.js" ]

                match trusted.Program with
                | Exec(_, args) ->
                    Expect.equal
                        args
                        [ "--config=" + owned.Config + "/tool.conf"; "--quiet"; "/srv/main.js" ]
                        "no overrides once trusted"
                | other -> failtest (string other)

                let inWorkspace =
                    launch
                        { runtime with
                            Launch =
                                { runtime.Launch with
                                    Cwd = LaunchCwd.Workspace
                                }
                        }
                        session
                        []

                Expect.equal inWorkspace.Cwd (Some "/work/project") "workspace"
            }

            test "a launch's environment is fresh: allowlisted names, exports, then EnvSet, never a denied name" {
                let proc = launch runtime session []

                Expect.equal
                    proc.Env
                    (Exactly
                        [
                            "HOME", owned.Home
                            "LANG", "C.UTF-8"
                            "LC_ALL", "C"
                            "PATH", "/usr/bin"
                            "TMPDIR", owned.Temp
                            "TOOL_HOME", "/opt/tool/2.0.0"
                            "TOOL_TELEMETRY", "0"
                        ])
                    "exactly these, sorted"

                // An export that is an injection vector is dropped too.
                let sneaky =
                    { installedOn "/opt/tool/2.0.0" "/opt/tool/2.0.0/bin/tool" with
                        Env = [ "NODE_OPTIONS", "--require=x"; "TOOL_PLUGINS", "/x" ]
                    }

                match RuntimeSpec.launch runtime sneaky session [] with
                | Ok { Env = Exactly vars } ->
                    Expect.isFalse
                        (vars
                         |> List.exists (fun (name, _) -> name = "NODE_OPTIONS" || name = "TOOL_PLUGINS"))
                        "denied"
                | other -> failtest (string other)
            }

            test "on Windows: names compare without case, programs end in .exe and paths are Windows paths" {
                let ownedWin =
                    {
                        Root = @"C:\owned"
                        Cwd = @"C:\owned\cwd"
                        Home = @"C:\owned\home"
                        Config = @"C:\owned\config"
                        Cache = @"C:\owned\cache"
                        Temp = @"C:\owned\tmp"
                    }

                let winSession =
                    { session with
                        Target = windows
                        Owned = ownedWin
                        HostEnvironment =
                            [
                                "Path", @"C:\Windows"
                                "lang", "x"
                                "Home", @"C:\Users\me"
                                "bun_options", "x"
                            ]
                    }

                match
                    RuntimeSpec.launch
                        runtime
                        (installedOn @"C:\rt\tool" @"C:\rt\tool\bin\tool.exe")
                        winSession
                        [ "--version" ]
                with
                | Ok proc ->
                    match proc.Env with
                    | Exactly vars ->
                        Expect.equal
                            (vars |> List.map fst)
                            [ "HOME"; "Path"; "TMPDIR"; "TOOL_HOME"; "TOOL_TELEMETRY"; "lang" ]
                            "Path and lang pass without case; Home and bun_options do not"
                    | other -> failtest (string other)
                | Error issues -> failtest (Issue.render issues)

                let relative = RuntimeSpec.launch runtime (installedOn "rt" "rt/tool") session []

                Expect.equal
                    (issues relative)
                    [ "$.launch.exe: \"rt/tool\" is not an absolute path" ]
                    "the program must be absolute on the target"

                Expect.isTrue (RuntimeSpec.isAbsoluteOn Os.Windows @"\\server\share\x") "UNC"
                Expect.isFalse (RuntimeSpec.isAbsoluteOn Os.Windows "/opt/x") "not on Windows"
                Expect.isFalse (RuntimeSpec.isAbsoluteOn Os.Linux @"C:\x") "not on Linux"
            }

            test "a launch is refused for another component, an invalid spec, or a missing workspace" {
                let other =
                    { installedOn "/opt/x" "/opt/x/tool" with
                        Version = semver "2.0.1"
                    }

                Expect.equal
                    (issues (RuntimeSpec.launch runtime other session []))
                    [ "$: the runtime is tool 2.0.0, but tool 2.0.1 was given" ]
                    "version"

                Expect.equal
                    (issues (
                        RuntimeSpec.launch
                            { runtime with
                                Launch =
                                    { runtime.Launch with
                                        Cwd = LaunchCwd.Workspace
                                    }
                            }
                            (installedOn "/opt/x" "/opt/x/tool")
                            { session with Workspace = None }
                            []
                    ))
                    [ "$.launch.cwd: the runtime runs in the workspace, and there is none" ]
                    "workspace"

                Expect.isNonEmpty
                    (issues (
                        RuntimeSpec.launch
                            { runtime with
                                Launch = { runtime.Launch with EnvDeny = [] }
                            }
                            (installedOn "/opt/x" "/opt/x/tool")
                            session
                            []
                    ))
                    "invalid"
            }

            test "prepare creates the owned directories and rewrites the runtime's files" {
                let files = InMemoryFs((FakeClock.Create()).Clock, false)
                let env = { Files = files; Scope = Scope.root }

                // A root in this machine's form: the in-memory filesystem uses its path rules.
                let owned =
                    OwnedDirs.under (
                        if System.OperatingSystem.IsWindows() then
                            @"C:\owned\s1"
                        else
                            "/owned/s1"
                    )

                let run (effect: Eff<FsEnv, 'T>) =
                    (Eff.run env effect).WaitAsync(System.TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()

                Expect.equal (run (RuntimeSpec.prepare runtime owned)) (Ok()) "prepared"

                for dir in OwnedDirs.all owned do
                    Expect.equal (run (Fs.exists dir)) (Ok true) dir

                let conf = Path.Combine(owned.Config, "tool.conf")
                Expect.equal (run (Fs.writeText conf "changed by the last process")) (Ok()) "changed"
                Expect.equal (run (RuntimeSpec.prepare runtime owned)) (Ok()) "prepared again"
                Expect.equal (run (Fs.readText conf)) (Ok "telemetry = off\n") "rewritten"
            }

            test "targets from .NET runtime identifiers" {
                Expect.equal (TargetTriple.ofRid "linux-x64") (Some linux) "glibc"

                Expect.equal
                    (TargetTriple.ofRid "linux-musl-arm64" |> Option.map string)
                    (Some "linux-arm64-musl")
                    "musl"

                Expect.equal (TargetTriple.ofRid "osx-arm64" |> Option.map string) (Some "macos-arm64") "macOS"
                Expect.equal (TargetTriple.ofRid "win-x64") (Some windows) "Windows"

                for rid in [ ""; "linux"; "linux-x86"; "freebsd-x64"; "win-x64-extra" ] do
                    Expect.isNone (TargetTriple.ofRid rid) rid
            }

            test "a manifest's launch writes untrustedOverrides only when there are some" {
                let launch =
                    { runtime.Launch with
                        UntrustedOverrides = []
                    }

                let text = Codec.encodeText Manifest.launchSpec launch
                Expect.isFalse (text.Contains "untrustedOverrides") "omitted, so earlier manifests keep their hash"

                match Codec.decodeText Manifest.launchSpec (Codec.encodeText Manifest.launchSpec runtime.Launch) with
                | Ok decoded -> Expect.equal decoded runtime.Launch "round trip"
                | Error issues -> failtest (Issue.render issues)
            }
        ]
