/// The Bun runtime's data: the pins, the component, the hardened launch and the package's manifest.
/// Offline; the network tests are in `LiveTests`.
module Fx.Ext.RuntimeBun.Tests.BunTests

open System
open System.IO
open Expecto
open Fx
open Fx.Contracts
open Fx.Ext.RuntimeBun
open Fx.Sdk

/// Golden files in `golden/` next to these tests; `UPDATE_GOLDEN=1` rewrites them.
module Golden =
    let private directory =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
            | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
            | dir -> up dir.Parent

        Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "tests", "Fx.Ext.RuntimeBun.Tests", "golden")

    let expect (name: string) (actual: string) =
        let path = Path.Combine(directory, name)

        if Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1" then
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(path, actual)
        else
            Expect.isTrue (File.Exists path) $"missing golden file {path}; run the tests with UPDATE_GOLDEN=1"
            Expect.equal actual (File.ReadAllText path) $"golden/{name} (rerun with UPDATE_GOLDEN=1 to update)"

let private isSha256 (hex: string) =
    hex.Length = 64
    && hex |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f'))

[<Tests>]
let tests =
    testList
        "Bun"
        [
            RuntimeConformance.tests "runtime conformance" RuntimeBun.runtime

            test "the pins: twelve archives, one per desktop or server build, with SHA-256 digests" {
                Expect.equal Pins.assets.Length 12 "archives"
                Expect.equal (Pins.assets |> List.distinctBy (fun a -> a.Name)).Length 12 "unique names"

                for asset in Pins.assets do
                    Expect.isTrue (isSha256 asset.Sha256) $"{asset}: archive digest"
                    Expect.isTrue (isSha256 asset.Executable) $"{asset}: executable digest"
                    Expect.isGreaterThan asset.Bytes 30_000_000L $"{asset}: size"
                    Expect.isGreaterThan asset.ExecutableBytes asset.Bytes $"{asset}: the executable is compressed"

                Expect.equal Pins.Revision (Pins.Version + "+a7733435c") "the revision starts with the version"
            }

            test
                "every product target installs a pinned archive; x64 takes the baseline build, which is the same executable" {
                let chosen = TargetTriple.all |> List.map (fun t -> string t, Pins.assetFor t)

                Expect.equal
                    chosen
                    [
                        "linux-arm64-glibc", "bun-linux-aarch64"
                        "linux-arm64-musl", "bun-linux-aarch64-musl"
                        "linux-x64-glibc", "bun-linux-x64-baseline"
                        "linux-x64-musl", "bun-linux-x64-musl-baseline"
                        "macos-arm64", "bun-darwin-aarch64"
                        "macos-x64", "bun-darwin-x64-baseline"
                        "windows-arm64", "bun-windows-aarch64"
                        "windows-x64", "bun-windows-x64-baseline"
                    ]
                    "archives"

                for target, name in chosen do
                    let asset = Pins.find name

                    if name.EndsWith("-baseline", StringComparison.Ordinal) then
                        let regular = Pins.find (name.Substring(0, name.Length - "-baseline".Length))

                        Expect.equal
                            asset.Executable
                            regular.Executable
                            $"{target}: the baseline build is the default build's executable, byte for byte"
            }

            test "the component: one archive method per target, strip 1, the executable, and an exact probe" {
                let c = RuntimeBun.bunComponent
                Expect.equal c.Id "bun" "id"
                Expect.equal (string c.Version) Pins.Version "version"
                Expect.equal c.Platforms TargetTriple.all "every product target"
                Expect.equal c.Methods.Length TargetTriple.all.Length "one method per target"

                for choice in c.Methods do
                    match choice.Platforms, choice.Method with
                    | [ target ], Archive archive ->
                        let asset = Pins.find (Pins.assetFor target)

                        Expect.equal
                            archive.Url
                            $"https://github.com/oven-sh/bun/releases/download/bun-v${{version}}/{asset.Name}.zip"
                            $"{target}: URL"

                        Expect.equal archive.Digest (Sha256 asset.Sha256) $"{target}: digest"
                        Expect.equal archive.Format ArchiveFormat.Zip $"{target}: zip"
                        Expect.equal archive.Strip 1 $"{target}: the archive's top directory is dropped"
                    | other -> failtest $"{other}"

                Expect.equal
                    c.Probe
                    (Some
                        {
                            Argv = [ "bun${exeSuffix}"; "--revision" ]
                            Expect = Some Pins.Revision
                        })
                    "probe"

                Expect.equal c.Exports.Bin [ "bun", "bun${exeSuffix}" ] "the bun command"
            }

            test "the hardened launch, as data" {
                let launch = RuntimeBun.launch
                Expect.equal launch.Exe "${bin.bun}" "the installed bun, never PATH's"

                Expect.equal
                    launch.Args
                    [
                        "--config=${file.bunfig.toml}"
                        "--no-env-file"
                        "--no-install"
                        "--no-orphans"
                        "--no-macros"
                    ]
                    "flags (spec §6.3)"

                Expect.equal launch.UntrustedOverrides [ "--tsconfig-override=${file.tsconfig.json}" ] "until trusted"
                Expect.equal launch.EnvDeny InjectionVectors.patterns "every vector"
                Expect.equal launch.Cwd LaunchCwd.Owned "owned cwd"

                let set = Map.ofList launch.EnvSet
                Expect.equal set["DO_NOT_TRACK"] "1" "no telemetry"
                Expect.equal set["BUN_ENABLE_CRASH_REPORTING"] "0" "no crash reports"

                for name in
                    [
                        "HOME"
                        "USERPROFILE"
                        "XDG_CONFIG_HOME"
                        "XDG_CACHE_HOME"
                        "TMPDIR"
                        "TEMP"
                        "TMP"
                    ] do
                    Expect.stringStarts set[name] "${owned." $"{name} is owned"

                Expect.stringContains RuntimeBun.bunfig.Content "env = false" "no .env from the config either"

                Expect.stringContains
                    RuntimeBun.bunfig.Content
                    "auto = \"disable\""
                    "no auto-install from the config either"

                Expect.isFalse (RuntimeBun.bunfig.Content.Contains Product.Name) "no product name in the bunfig"
            }

            test "the package: a declarative manifest with the component and the runtime, valid and hashed" {
                Expect.equal RuntimeBun.id (Product.extensionId "runtime.bun") "id"
                Expect.equal RuntimeBun.manifest.Runtime Runtimes.Declarative "declarative"
                Expect.equal (RuntimeBun.check ()) [] "no issues"

                match Manifest.parse RuntimeBun.manifestText with
                | Ok parsed ->
                    Expect.equal { parsed with Hash = None } RuntimeBun.manifest "the written manifest reads back"
                    Expect.equal parsed.Hash (Some(Manifest.hash RuntimeBun.manifest)) "with its hash"
                | Error issues -> failtest (Issue.render issues)

                let values = RuntimeBun.manifest.Contributions |> List.map (fun c -> c.Type, c.Id)

                Expect.equal values [ "component", "bun-1.4.5"; "runtime", "bun" ] "contributions"
            }

            test "the package's manifest (golden)" { Golden.expect "manifest.json" (RuntimeBun.manifestText + "\n") }
        ]
