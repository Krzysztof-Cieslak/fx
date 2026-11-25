/// Runtimes (T114, docs/modules/runtimes.md): the injection vectors and env patterns, launch spec and
/// runtime spec validation with its issues, the Bun runtime's data (manifest hash, per-target
/// archives, codec round trip), and its launch resolved on every target, trusted and not, with a
/// hostile launcher environment. Pure: paths are made up in each target's form, nothing runs.
module Fx.Aot.Scenarios.Runtimes

open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Ext.RuntimeBun
open Fx.Sdk

let private hostile: (string * string) list =
    [
        "PATH", "/usr/bin"
        "Path", @"C:\Windows\system32"
        "LANG", "C.UTF-8"
        "lc_ctype", "C"
        "HOME", "/home/me"
        "SystemRoot", @"C:\Windows"
        "SECRET_TOKEN", "do-not-leak"
        "BUN_OPTIONS", "--preload=evil.ts"
        "bun_inspect", "127.0.0.1:9229"
        "NODE_OPTIONS", "--require=evil.js"
        "LD_PRELOAD", "/tmp/evil.so"
        "DYLD_INSERT_LIBRARIES", "/tmp/evil.dylib"
    ]

let private pathOn (target: TargetTriple) (segments: string list) =
    match target.Os with
    | Os.Windows -> @"C:\" + System.String.Join(@"\", segments)
    | _ -> "/" + System.String.Join("/", segments)

let private installedOn (target: TargetTriple) : InstalledComponent =
    let dir = pathOn target [ "rt"; "bun"; "1.4.5-0123456789ab" ]
    let separator = if target.Os = Os.Windows then @"\" else "/"

    {
        Id = "bun"
        Version = RuntimeBun.bunComponent.Version
        Dir = dir
        Bin = [ "bun", dir + separator + "bun" + TargetTriple.exeSuffix target ]
        Env = []
    }

let private sessionOn (target: TargetTriple) (trusted: bool) : LaunchSession =
    let under (name: string) = pathOn target [ "owned"; "s1"; name ]

    {
        Target = target
        Owned =
            {
                Root = pathOn target [ "owned"; "s1" ]
                Cwd = under "cwd"
                Home = under "home"
                Config = under "config"
                Cache = under "cache"
                Temp = under "tmp"
            }
        Workspace = Some(pathOn target [ "work" ])
        ProjectTrusted = trusted
        HostEnvironment = hostile
    }

let private issues (found: Issue list) : string list =
    found |> List.map (fun issue -> "  issue " + escape (string issue))

let vectors =
    Scenario.lines "runtimes-vectors" (fun () ->
        [
            for vector in InjectionVectors.all do
                $"vector {vector.Pattern} ({vector.Reader}): {escape vector.Effect}"

            for name in
                [
                    "BUN_OPTIONS"
                    "bun_inspect_connect_to"
                    "NODE_PATH"
                    "PATH"
                    "DO_NOT_TRACK"
                    "BUN_INSTALL"
                ] do
                $"isVector {name}: {InjectionVectors.isVector name}"

            for broad, narrow in
                [
                    "BUN_*", "BUN_INSPECT*"
                    "BUN_INSPECT*", "BUN_*"
                    "node_options", "NODE_OPTIONS"
                    "PATH", "PATHEXT"
                ] do
                $"covers {broad} {narrow}: {EnvPattern.covers broad narrow}"

            for ignoreCase in [ false; true ] do
                for pattern, name in [ "PATH", "Path"; "LC_*", "lc_all"; "LC_*", "LC_ALL" ] do
                    $"matches ignoreCase={ignoreCase} {pattern} {name}: {EnvPattern.matches ignoreCase pattern name}"

            for trusted in [ false; true ] do
                let args =
                    LaunchSpec.arguments trusted [ "--o=1" ] [ "--a"; "main.ts"; "--b" ]
                    |> String.concat " "

                $"arguments trusted={trusted}: {args}"
        ])

let validation =
    Scenario.lines "runtimes-validation" (fun () ->
        let launch = RuntimeBun.launch
        let runtime = RuntimeBun.runtime

        [
            $"bun launch: {(LaunchSpec.validate launch).Length} issues"
            $"bun runtime: {(RuntimeSpec.validate runtime).Length} issues"
            "lacking deny list:"
            yield!
                issues (
                    LaunchSpec.validate
                        { launch with
                            EnvDeny = launch.EnvDeny |> List.filter (fun p -> p <> "BUN_OPTIONS" && p <> "DYLD_*")
                        }
                )
            "lax allowlist and set:"
            yield!
                issues (
                    LaunchSpec.validate
                        { launch with
                            EnvAllowlist = [ "*"; "NODE_PATH" ]
                            EnvSet = [ "Bun_Options", "x"; "TMP", "y" ]
                            UntrustedOverrides = [ "tsconfig.json" ]
                        }
                )
            "broken runtime:"
            yield!
                issues (
                    RuntimeSpec.validate
                        { runtime with
                            Launch =
                                { launch with
                                    Exe = "bun"
                                    Args = [ "--config=${file.missing}"; "main.ts" ]
                                }
                            Files = [ { Name = "a/b"; Content = "" } ]
                        }
                )
            "decoding a runtime without the deny list:"
            match
                Codec.decodeText
                    RuntimeSpec.codec
                    (Codec.encodeText
                        RuntimeSpec.codec
                        { runtime with
                            Launch = { launch with EnvDeny = [] }
                        })
            with
            | Ok _ -> "  decoded"
            | Error found -> yield! issues found
        ])

let bun =
    Scenario.lines "runtimes-bun" (fun () ->
        [
            $"extension {RuntimeBun.manifest.Id} {RuntimeBun.manifest.Version}"
            $"manifest hash {Manifest.hash RuntimeBun.manifest}"
            $"check: {(RuntimeBun.check ()).Length} issues"
            $"component {RuntimeBun.bunComponent} size {RuntimeBun.bunComponent.Size}"

            for choice in RuntimeBun.bunComponent.Methods do
                let platforms = choice.Platforms |> List.map string |> String.concat ","
                $"method {platforms}: {choice.Method}"

            for target in TargetTriple.all do
                $"asset {target}: {Pins.assetFor target}"

            let json = Codec.encodeText RuntimeSpec.codec RuntimeBun.runtime

            match Codec.decodeText RuntimeSpec.codec json with
            | Ok decoded -> $"round trip: {decoded = RuntimeBun.runtime}, {json.Length} chars"
            | Error found -> yield! issues found

            let contribution = RuntimeSpecs.contributionType

            match contribution.OfJson (contribution.ToJson RuntimeBun.runtime) None with
            | Ok decoded -> $"contribution {contribution.Name} (privileged {contribution.Privileged}): {decoded}"
            | Error found -> yield! issues found

            $"bunfig: {escape RuntimeBun.bunfig.Content}"
        ])

let launch =
    Scenario.lines "runtimes-launch" (fun () ->
        [
            for target in TargetTriple.all do
                for trusted in [ false; true ] do
                    $"{target} trusted={trusted}"

                    match
                        RuntimeSpec.launch
                            RuntimeBun.runtime
                            (installedOn target)
                            (sessionOn target trusted)
                            [ pathOn target [ "srv"; "kernel.js" ]; "--global=x" ]
                    with
                    | Error found -> yield! issues found
                    | Ok proc ->
                        match proc.Program with
                        | Exec(exe, args) ->
                            $"  exe {escape exe}"

                            for arg in args do
                                $"  arg {escape arg}"
                        | Program.Script script -> $"  script {escape script}"

                        let cwd = proc.Cwd |> Option.defaultValue "<none>"
                        $"  cwd {escape cwd}"

                        match proc.Env with
                        | Exactly vars ->
                            for name, value in vars do
                                $"  env {kv name value}"
                        | Inherit _ -> "  env inherited"

            let target = TargetTriple.all.Head

            match
                RuntimeSpec.launch
                    { RuntimeBun.runtime with
                        Launch =
                            { RuntimeBun.launch with
                                Cwd = LaunchCwd.Workspace
                            }
                    }
                    (installedOn target)
                    { sessionOn target false with
                        Workspace = None
                    }
                    []
            with
            | Ok _ -> "no workspace: launched"
            | Error found ->
                "no workspace:"
                yield! issues found
        ])

let scenarios = [ vectors; validation; bun; launch ]
