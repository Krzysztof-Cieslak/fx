module Fx.Sdk.Tests.ComponentTests

open Expecto
open FsCheck
open Fx.Contracts
open Fx.Sdk
open Fx.Sdk.Tests.Support

let private version (text: string) =
    match SemVer.parse text with
    | Ok v -> v
    | Error e -> failtest e

let private range (text: string) =
    match VersionRange.parse text with
    | Ok r -> r
    | Error e -> failtest e

let private digest = Sha256(String.replicate 64 "a")

let private bun: Component =
    { Component.create
          "bun"
          (version "1.4.5")
          [
              {
                  Platforms = [ TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc) ]
                  Method =
                      Archive
                          {
                              Url = "https://github.com/oven-sh/bun/releases/download/bun-v${version}/bun-linux-x64.zip"
                              Digest = digest
                              Format = ArchiveFormat.Zip
                              Strip = 1
                          }
              }
              {
                  Platforms = []
                  Method =
                      System
                          {
                              Probe = [ "bun"; "--version" ]
                              Constraint = ">=1.4"
                          }
              }
          ] with
        DependsOn = [ { Id = "unzip"; Version = "^6" } ]
        Layout = { Executables = [ "bun${exeSuffix}" ] }
        Exports =
            {
                Bin = [ "bun", "bun${exeSuffix}" ]
                Env = [ "BUN_INSTALL", "${dir}" ]
            }
        Probe =
            Some
                {
                    Argv = [ "bun${exeSuffix}"; "--revision" ]
                    Expect = Some "${version}+a7733435c"
                }
        Size =
            {
                Download = Some 36_000_000L
                Installed = None
            }
    }

let private issues (codec: Codec<'T>) (text: string) : string list =
    match Codec.decodeText codec text with
    | Ok value -> failtest $"expected issues, decoded {value}"
    | Error issues -> issues |> List.map string

[<Tests>]
let tests =
    testList
        "Components"
        [
            test "semantic versions parse strictly and print as they were written" {
                for text in [ "0.0.0"; "1.4.5"; "1.4.5+a7733435c"; "2.0.0-beta.2"; "10.20.30-rc.1+build.5" ] do
                    Expect.equal (string (version text)) text text

                for text in
                    [
                        ""
                        "1"
                        "1.2"
                        "01.2.3"
                        "1.02.3"
                        "v1.2.3"
                        "1.2.3-"
                        "1.2.3-01"
                        "1.2.3+"
                        "1.2.3.4"
                        "a.b.c"
                    ] do
                    Expect.isNone (SemVer.tryParse text) text
            }

            test "precedence follows semver: pre-releases first, build metadata ignored" {
                let ordered =
                    [
                        "1.0.0-alpha"
                        "1.0.0-alpha.1"
                        "1.0.0-alpha.beta"
                        "1.0.0-beta"
                        "1.0.0-beta.2"
                        "1.0.0-beta.11"
                        "1.0.0-rc.1"
                        "1.0.0"
                        "1.0.1"
                        "1.2.0"
                        "2.0.0"
                    ]
                    |> List.map version

                for a, b in List.pairwise ordered do
                    Expect.equal (SemVer.compare a b) -1 $"{a} < {b}"
                    Expect.equal (SemVer.compare b a) 1 $"{b} > {a}"

                Expect.equal (SemVer.compare (version "1.4.5+a") (version "1.4.5+b")) 0 "build metadata"
            }

            test "ranges match as npm's do" {
                let cases =
                    [
                        "1.4.5", [ "1.4.5" ], [ "1.4.6"; "1.4.5-rc.1" ]
                        "=1.4.5", [ "1.4.5"; "1.4.5+b" ], [ "1.4.4" ]
                        "1", [ "1.0.0"; "1.99.0" ], [ "2.0.0"; "0.9.0"; "2.0.0-beta" ]
                        "1.x", [ "1.2.3" ], [ "2.0.0" ]
                        "1.4", [ "1.4.0"; "1.4.9" ], [ "1.5.0"; "1.3.9" ]
                        "*", [ "0.0.1"; "9.9.9" ], [ "1.0.0-beta" ]
                        ">=1.4 <2", [ "1.4.0"; "1.99.0" ], [ "1.3.9"; "2.0.0"; "2.0.0-alpha" ]
                        ">= 1.4 < 2", [ "1.5.0" ], [ "2.0.0" ]
                        "^1.4", [ "1.4.0"; "1.9.0" ], [ "2.0.0"; "1.3.0" ]
                        "^0.2.3", [ "0.2.3"; "0.2.9" ], [ "0.3.0" ]
                        "^0.0.3", [ "0.0.3" ], [ "0.0.4" ]
                        "~1.4.5", [ "1.4.5"; "1.4.9" ], [ "1.5.0"; "1.4.4" ]
                        ">1.2", [ "1.3.0" ], [ "1.2.9" ]
                        "<=1.2", [ "1.2.9" ], [ "1.3.0" ]
                        "<1.2.3", [ "1.2.2" ], [ "1.2.3" ]
                        "1.2.3 || >=2.1", [ "1.2.3"; "2.1.0" ], [ "2.0.0" ]
                        ">=1.0.0-beta <1.0.0", [ "1.0.0-beta.2" ], [ "1.0.0"; "0.9.0-beta" ]
                    ]

                for text, inside, outside in cases do
                    let r = range text

                    for v in inside do
                        Expect.isTrue (VersionRange.satisfies r (version v)) $"{v} in {text}"

                    for v in outside do
                        Expect.isFalse (VersionRange.satisfies r (version v)) $"{v} not in {text}"
            }

            test "invalid ranges are errors that say what is wrong" {
                for text in [ ""; ">="; "1.2.3.4"; "abc"; "1.x.3"; ">=1.2 foo" ] do
                    match VersionRange.parse text with
                    | Ok r -> failtest $"{text} parsed as {r.Sets}"
                    | Error message -> Expect.isNonEmpty message text
            }

            property "a version is in its own exact range, and in no range that excludes it"
            <| fun (major: byte) (minor: byte) (patch: byte) ->
                let v = SemVer.create (int major) (int minor) (int patch)

                VersionRange.satisfies (VersionRange.exactly v) v
                && VersionRange.satisfies VersionRange.any v
                && VersionRange.satisfies (range $"^{v}") v
                && not (VersionRange.satisfies (range $">{v}") v)
                && not (VersionRange.satisfies (range $"<{v}") v)

            test "targets cover every platform the product is built for, and print as they parse" {
                Expect.equal
                    (TargetTriple.all |> List.map string)
                    [
                        "linux-arm64-glibc"
                        "linux-arm64-musl"
                        "linux-x64-glibc"
                        "linux-x64-musl"
                        "macos-arm64"
                        "macos-x64"
                        "windows-arm64"
                        "windows-x64"
                    ]
                    "sorted by name"

                for t in TargetTriple.all do
                    Expect.equal (TargetTriple.parse (string t)) (Ok t) (string t)

                for text in [ "linux-x64"; "macos-arm64-musl"; "win-x64"; "" ] do
                    Expect.isError (TargetTriple.parse text) text

                Expect.equal (TargetTriple.exeSuffix (TargetTriple.create Os.Windows Arch.Arm64 None)) ".exe" "Windows"
                Expect.equal (TargetTriple.exeSuffix (TargetTriple.create Os.MacOs Arch.Arm64 None)) "" "macOS"
            }

            test "digests are sha256 with 64 lowercase hex digits" {
                Expect.equal (Digest.parse (string digest)) (Ok digest) "round trip"

                for text in
                    [
                        "sha256:" + String.replicate 64 "A"
                        "sha256:abc"
                        "md5:" + String.replicate 32 "a"
                        ""
                    ] do
                    Expect.isError (Digest.parse text) text
            }

            test "a component round-trips through its codec, every method included" {
                let all =
                    { bun with
                        Methods =
                            bun.Methods
                            @ [
                                {
                                    Platforms = []
                                    Method =
                                        ReleaseIndex
                                            {
                                                Index = "https://example.test/index.json"
                                                Select = "latest"
                                                Trust = IndexTrust.VendorIndex
                                            }
                                }
                                {
                                    Platforms = []
                                    Method =
                                        Script
                                            {
                                                Url = "https://example.test/install.sh"
                                                Digest = digest
                                                Runner = "sh"
                                                Args = [ "-s" ]
                                            }
                                }
                                {
                                    Platforms = []
                                    Method =
                                        Package
                                            {
                                                Manager = { Id = "bun"; Version = "1" }
                                                Spec = "typescript@5"
                                            }
                                }
                                {
                                    Platforms = []
                                    Method = Custom { Extension = "acme.installer" }
                                }
                            ]
                    }

                let json = Component.codec.Encode all
                Expect.equal (Component.codec.Decode json) (Ok all) "round trip"

                Expect.equal
                    (all.Methods |> List.map (fun m -> m.Method.Kind))
                    [ "archive"; "system"; "release-index"; "script"; "package"; "custom" ]
                    "kinds"
            }

            test "the component JSON (golden)" {
                Golden.expect "component.json" (Json.serializeIndented (Component.codec.Encode bun))
            }

            test "JSON Schema of a component (golden)" {
                Golden.expect
                    "component.schema.json"
                    (Json.serializeIndented (Schema.toJsonSchema Component.codec.Schema))
            }

            test "the codec refuses what would be unsafe or meaningless, with paths" {
                let valid = Json.serialize (Component.codec.Encode bun)

                let replace (oldValue: string) (newValue: string) =
                    let replaced = valid.Replace(oldValue, newValue)
                    Expect.notEqual replaced valid $"{oldValue} occurs"
                    replaced

                let cases =
                    [
                        replace "\"https://github.com" "\"http://github.com", "$.methods[0].method.url"
                        replace "\"strip\":1" "\"strip\":-1", "$.methods[0].method.strip"
                        replace "\"kind\":\"archive\"" "\"kind\":\"zipfile\"", "$.methods[0].method.kind"
                        replace "\"format\":\"zip\"" "\"format\":\"tar.xz\"", "$.methods[0].method.format"
                        replace "\"executables\":[\"bun${exeSuffix}\"]" "\"executables\":[\"../bun\"]",
                        "$.layout.executables[0]"
                        replace "\"argv\":[\"bun${exeSuffix}\"" "\"argv\":[\"/usr/bin/bun\"", "$.probe.argv"
                        replace "\"id\":\"bun\"" "\"id\":\"Bun\"", "$.id"
                        replace "\"version\":\"1.4.5\"" "\"version\":\"1.4\"", "$.version"
                        replace "\"linux-x64-glibc\"" "\"linux-x64\"", "$.methods[0].platforms[0]"
                        replace "\"constraint\":\">=1.4\"" "\"constraint\":\"latest\"", "$.methods[1].method.constraint"
                        replace "\"strip\":1" "\"strip\":1,\"stripp\":2", "$.methods[0].method.stripp"
                    ]

                for text, path in cases do
                    let found = issues Component.codec text
                    Expect.isTrue (found |> List.exists (fun i -> i.StartsWith(path + ":"))) $"{path} in {found}"
            }

            test "placeholders expand, and unknown ones are errors" {
                Expect.equal
                    (Component.expand [ "version", "1.4.5" ] "bun-v${version}.zip")
                    (Ok "bun-v1.4.5.zip")
                    "known"

                Expect.isError (Component.expand [ "version", "1" ] "${os}/bun") "unknown"
                Expect.isError (Component.expand [] "${version") "unterminated"
            }

            test "relative paths stay inside the install directory" {
                for path in [ "bun"; "bin/bun.exe"; "a/b/c" ] do
                    Expect.isTrue (Component.isRelativePath path) path

                for path in
                    [
                        ""
                        "/bin/bun"
                        "../bun"
                        "a/../b"
                        "./bun"
                        "a//b"
                        "C:/bun"
                        "a\\b"
                        "bin/"
                    ] do
                    Expect.isFalse (Component.isRelativePath path) path
            }

            test "components are a privileged data contribution" {
                let t = ComponentDefinitions.contributionType
                Expect.equal t.Name "component" "name"
                Expect.isTrue t.Privileged "privileged (spec §16.5)"
                Expect.equal t.Kind ContributionKind.Data "data"
                Expect.equal (t.OfJson (t.ToJson bun) None) (Ok bun) "round trip"
            }
        ]
