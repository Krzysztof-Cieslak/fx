module Fx.Sdk.Tests.ManifestTests

open Expecto
open Fx
open Fx.Contracts
open Fx.Sdk
open Fx.Sdk.Tests.Support

let private gate = Hook.gate "test.gate" "A test gate." Codec.string

let private words = Hook.contribute "test.word" "Words." Codec.string

/// A manifest with something in every field.
let full: Manifest =
    { Manifest.create "acme.tools" "1.2.3-beta.1" Runtimes.Process with
        Description = Some "Tools."
        Placement = Placement.Environment
        Launch =
            Some
                {
                    Exe = "${root}/bin/tools${exeSuffix}"
                    Args = [ "--stdio" ]
                    EnvAllowlist = [ "PATH"; "LANG*" ]
                    EnvSet = [ "TOOLS_MODE", "fast"; "A", "b" ]
                    EnvDeny = [ "SECRET_*" ]
                    Cwd = LaunchCwd.Workspace
                    UntrustedOverrides = []
                }
        Requires = [ { Id = "bun"; Version = ">=1.4 <2" } ]
        Capabilities = [ "clock"; "fs.read" ]
        Contributions = [ Declare.contribution gate "strict"; Declare.contribution words "hello" ]
        Rpc =
            {
                Methods =
                    [
                        {
                            Name = "tools/list"
                            Description = Some "Lists tools."
                            Params = None
                            Result = Some(Json.Object [ "type", Json.String "array" ])
                        }
                    ]
            }
        Replaces = [ "acme.old-tools" ]
    }

let private issues (text: string) : string list =
    match Manifest.parse text with
    | Ok _ -> []
    | Error issues -> issues |> List.map string

[<Tests>]
let tests =
    testList
        "Manifest"
        [
            test "round-trips through JSON, field by field" {
                Expect.equal (Manifest.parse (Manifest.serialize full)) (Ok full) "full"

                let minimal = Manifest.create "a" "0.0.1" Runtimes.Native
                Expect.equal (Manifest.parse (Manifest.serialize minimal)) (Ok minimal) "minimal"
            }

            test "defaults: placement host, empty lists; required fields are reported" {
                Expect.equal
                    (Manifest.parse
                        """{"id": "a", "version": "1.0.0", "sdk": "0.1", "protocol": "1", "runtime": "native"}""")
                    (Ok(Manifest.create "a" "1.0.0" Runtimes.Native))
                    "defaults"

                Expect.equal
                    (issues "{}")
                    [
                        "$.id: missing required field"
                        "$.version: missing required field"
                        "$.sdk: missing required field"
                        "$.protocol: missing required field"
                        "$.runtime: missing required field"
                    ]
                    "missing"
            }

            test "unknown members are issues, at any depth" {
                let text =
                    """{"id": "a", "version": "1.0.0", "sdk": "0.1", "protocol": "1", "runtime": "process",
                        "launch": {"exe": "x", "cwdd": "owned"}, "contribution": []}"""

                Expect.equal
                    (issues text)
                    [
                        "$.launch.cwdd: unknown field; known fields are \"exe\", \"args\", \"envAllowlist\", \"envSet\", \"envDeny\", \"cwd\", \"untrustedOverrides\""
                        "$.contribution: unknown field; known fields are \"id\", \"version\", \"description\", \"sdk\", \"protocol\", \"placement\", \"runtime\", \"launch\", \"requires\", \"capabilities\", \"contributions\", \"rpc\", \"replaces\", \"hash\""
                    ]
                    "issues"
            }

            test "names follow their patterns" {
                let text =
                    """{"id": "Acme.Tools", "version": "1.0", "sdk": "0.1", "protocol": "1", "runtime": "Bun",
                        "capabilities": ["fs read"], "contributions": [{"type": "Gate", "id": "-x"}],
                        "rpc": {"methods": [{"name": "List"}]}, "replaces": ["ok.id"]}"""

                let found = issues text

                for path in
                    [
                        "$.id:"
                        "$.version:"
                        "$.runtime:"
                        "$.capabilities[0]:"
                        "$.contributions[0].type:"
                        "$.contributions[0].id:"
                        "$.rpc.methods[0].name:"
                    ] do
                    Expect.exists found (fun issue -> issue.StartsWith path) path

                Expect.equal found.Length 7 "one issue each"
            }

            test "checks across fields" {
                let check (manifest: Manifest) =
                    Manifest.check manifest |> List.map string

                Expect.equal
                    (check { full with Sdk = "1" })
                    [ "$.sdk: expected a version major.minor, e.g. \"0.1\"" ]
                    "sdk"

                Expect.equal
                    (check { full with Launch = None })
                    [ "$.launch: a process extension needs \"launch\": how to start it" ]
                    "process without launch"

                Expect.equal
                    (check { full with Runtime = Runtimes.Native })
                    [
                        "$.launch: a native extension is not started as a process; remove \"launch\""
                    ]
                    "native with launch"

                let declarative =
                    { Manifest.create "a" "1.0.0" Runtimes.Declarative with
                        Contributions = [ Declare.contribution words "x"; Declare.value words "y" "y" ]
                    }

                Expect.equal
                    (check declarative)
                    [
                        "$.contributions[0]: a declarative extension gives each contribution's value; add \"value\""
                    ]
                    "declarative without a value"

                Expect.equal
                    (check
                        { full with
                            Contributions = [ Declare.value words "y" "y" ]
                        })
                    [
                        "$.contributions[0].value: only declarative extensions give values in the manifest; a process extension provides them when it activates"
                    ]
                    "a value in a process manifest"

                Expect.equal
                    (check
                        { full with
                            Contributions = full.Contributions @ full.Contributions
                            Replaces = [ full.Id ]
                            Capabilities = [ "clock"; "clock" ]
                        })
                    [
                        "$.contributions: the contribution \"gate:test.gate/strict\" is declared twice"
                        "$.contributions: the contribution \"test.word/hello\" is declared twice"
                        "$.capabilities: the capability \"clock\" is listed twice"
                        "$.replaces: an extension cannot replace itself"
                    ]
                    "duplicates"

                Expect.equal
                    (check
                        { declarative with
                            Contributions = [ Declare.value words "y" "y" ]
                            Rpc = full.Rpc
                        })
                    [ "$.rpc.methods: a declarative extension has no code, so no methods" ]
                    "methods without code"
            }

            test "a manifest may not set runtime injection vectors, its own denied names, or patterns" {
                let launch = full.Launch.Value

                let withSet (set: (string * string) list) =
                    { full with
                        Launch = Some { launch with EnvSet = set }
                    }
                    |> Manifest.check
                    |> List.map string

                Expect.equal
                    (withSet
                        [
                            "NODE_OPTIONS", "--require=x"
                            "BUN_INSPECT_CONNECT_TO", "x"
                            "SECRET_KEY", "x"
                            "OK", "x"
                        ])
                    [
                        "$.launch.envSet[0]: NODE_OPTIONS is denied: it can inject code into the runtime"
                        "$.launch.envSet[1]: BUN_INSPECT_CONNECT_TO is denied: it can inject code into the runtime"
                        "$.launch.envSet[2]: SECRET_KEY is denied: it can inject code into the runtime"
                    ]
                    "denied"

                // Manifests are checked case-insensitively: they may run on Windows.
                Expect.equal (withSet [ "node_options", "x" ]).Length 1 "case"

                for name in
                    [
                        "DOTNET_STARTUP_HOOKS"
                        "LD_PRELOAD"
                        "DYLD_INSERT_LIBRARIES"
                        "NPM_CONFIG_PREFIX"
                    ] do
                    Expect.equal (withSet [ name, "x" ]).Length 1 name
            }

            test "env patterns: exact names, or prefixes ending in *" {
                Expect.isTrue (Manifest.matchesEnvPattern false "BUN_*" "BUN_X") "prefix"
                Expect.isTrue (Manifest.matchesEnvPattern false "BUN_*" "BUN_") "empty suffix"
                Expect.isFalse (Manifest.matchesEnvPattern false "BUN_*" "BUNX") "not a prefix"
                Expect.isFalse (Manifest.matchesEnvPattern false "PATH" "PATHEXT") "exact"
                Expect.isFalse (Manifest.matchesEnvPattern false "PATH" "Path") "case on Unix"
                Expect.isTrue (Manifest.matchesEnvPattern true "PATH" "Path") "no case on Windows"
            }

            test "the hash is over the canonical JSON without the hash, and must match if given" {
                let hash = Manifest.hash full
                Expect.stringStarts hash "sha256:" "prefix"
                Expect.equal hash.Length (7 + 64) "hex"
                Expect.equal (Manifest.hash { full with Hash = Some "anything" }) hash "ignores its own hash"
                Expect.notEqual (Manifest.hash { full with Version = "1.2.4" }) hash "content"

                // Member order and number notation do not matter: the hash is of the canonical form.
                let reordered =
                    match Json.parse (Manifest.serialize full) with
                    | Ok(Json.Object members) -> Json.serialize (Json.Object(List.rev members))
                    | other -> failtest (string other)

                match Manifest.parse reordered with
                | Ok manifest -> Expect.equal (Manifest.hash manifest) hash "reordered"
                | Error issues -> failtest (Issue.render issues)

                Expect.equal (Manifest.check { full with Hash = Some hash }) [] "matching"

                Expect.equal
                    (Manifest.check { full with Hash = Some "sha256:00" } |> List.map string)
                    [ $"$.hash: does not match the manifest's content; its hash is \"{hash}\"" ]
                    "mismatch"
            }

            test "the manifest file name comes from the product name" {
                Expect.equal Manifest.fileName (Product.Name + ".extension.json") "file name"
            }

            test "JSON Schema of the manifest (golden)" {
                Golden.expect
                    "manifest.schema.json"
                    (Json.serializeIndented (Schema.toJsonSchema Manifest.codec.Schema))
            }

            test "invalid JSON is one issue" {
                match Manifest.parse "{" with
                | Error [ issue ] -> Expect.stringStarts issue.Message "invalid JSON: " "message"
                | other -> failtest (string other)
            }
        ]
