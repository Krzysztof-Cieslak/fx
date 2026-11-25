/// Environment specs from config, capability negotiation, the layout and the host's variables.
module Fx.Environments.Tests.SpecTests

open Expecto
open Fx.Contracts
open Fx.Environments
open Fx.Sdk

let private parse (text: string) =
    match EnvSpecs.parse text with
    | Ok specs -> specs
    | Error issues -> failtest (Issue.render issues)

let private issues (text: string) =
    match EnvSpecs.parse text with
    | Ok specs -> failtest $"expected issues, got {specs}"
    | Error issues -> issues |> List.map (fun i -> JsonPath.render i.Path + ": " + i.Message)

let private provider: EnvProvider = Fx.Ext.EnvLocal.EnvLocal.provider

[<Tests>]
let tests =
    testList
        "environment specs"
        [
            test "reads the specs of a config file, by name, with defaults" {
                let specs =
                    parse
                        """{ "variants": {"edit.guard": "warn"},
                             "environments": {
                               "dev": { "provider": "local" },
                               "box": { "provider": "ssh", "config": {"host": "devbox.internal"}, "sandbox": "bubblewrap",
                                        "workspace": {"source": "existing", "path": "~/src/app"} },
                               "cloud": { "provider": "e2b", "config": {"template": "fx-node22", "apiKey": "secret:e2b"},
                                          "workspace": {"source": "git", "ref": "HEAD", "export": "branch"},
                                          "components": {"placement": "prebaked", "fallback": "host-push"},
                                          "setup": {"run": ["bun install"], "network": "open", "secrets": ["npm"]},
                                          "policy": {"network": "allowlist:dev", "idle": "suspend:15m", "ttl": "8h", "budget": "$5/day"} } } }"""

                Expect.equal (specs |> List.map (fun s -> s.Name)) [ "dev"; "box"; "cloud" ] "in the file's order"
                Expect.equal specs[0] (EnvSpec.create "dev" "local") "defaults"
                Expect.equal specs[1].Workspace.Path (Some "~/src/app") "workspace"
                Expect.equal specs[1].Sandbox (Some "bubblewrap") "sandbox"
                Expect.equal specs[2].Workspace.Source MaterializeKind.Git "git"
                Expect.equal specs[2].Workspace.Export (Some ExportKind.Branch) "export"
                Expect.equal specs[2].Components.Placement ComponentPlacement.Prebaked "placement"
                Expect.equal specs[2].Components.Fallback (Some ComponentPlacement.HostPush) "fallback"
                Expect.equal specs[2].Setup.Secrets [ "npm" ] "setup"
                Expect.equal specs[2].Policy.Budget (Some "$5/day") "policy"

                for spec in specs do
                    Expect.equal (Codec.decode EnvSpec.codec (EnvSpec.codec.Encode spec)) (Ok spec) "round trip"

                Expect.equal (parse "{}") [] "no environments"
            }

            test "issues have paths into the file" {
                Expect.equal
                    (issues
                        """{"environments": {"dev": {"provider": "local", "sandbx": "none", "workspace": {"source": "nfs"}},
                                             "Bad": {"provider": "local"}, "x": {"name": "x", "provider": "local"}, "y": 3}}""")
                    [
                        "$.environments.dev.workspace.source: expected one of \"existing\", \"mount\", \"git\", \"upload\", \"snapshot\", got string \"nfs\""
                        "$.environments.dev.sandbx: unknown field; known fields are \"name\", \"provider\", \"config\", \"transport\", \"sandbox\", \"network\", \"workspace\", \"components\", \"setup\", \"policy\""
                        "$.environments.Bad.name: expected a string matching /^[a-z][a-z0-9_-]*$/, got string \"Bad\""
                        "$.environments.x.name: the environment's name is its key: leave \"name\" out"
                        "$.environments.y: expected an object (an environment spec), got number 3"
                    ]
                    "issues"

                Expect.equal
                    (issues """{"environments": []}""")
                    [
                        "$.environments: expected an object of environment specs by name, got empty array"
                    ]
                    "not an object"
            }

            test "later layers replace specs of the same name" {
                let host =
                    parse """{"environments": {"dev": {"provider": "local"}, "box": {"provider": "ssh"}}}"""

                let project =
                    parse """{"environments": {"box": {"provider": "local"}, "new": {"provider": "local"}}}"""

                let merged = EnvSpecs.merge [ host; project ]

                Expect.equal
                    (merged |> List.map (fun s -> s.Name, s.Provider))
                    [ "dev", "local"; "box", "local"; "new", "local" ]
                    "merged"

                Expect.equal (EnvSpecs.tryFind "box" merged |> Option.map (fun s -> s.Provider)) (Some "local") "found"
            }

            test "capability negotiation names what the provider cannot do" {
                let spec = EnvSpec.create "dev" "local" |> EnvSpec.withWorkspace "/src/app"
                Expect.isEmpty (EnvSpecs.check provider spec) "the local subset"
                Expect.equal (EnvSpecs.transport provider spec) TransportKind.InProcess "the default transport"

                let paths (spec: EnvSpec) =
                    EnvSpecs.check provider spec |> List.map (fun i -> JsonPath.render i.Path)

                Expect.equal
                    (paths
                        { spec with
                            Transport = Some TransportKind.Direct
                        })
                    [ "$.transport"; "$.transport" ]
                    "direct"

                Expect.equal
                    (paths
                        { spec with
                            Sandbox = Some "bubblewrap"
                        })
                    []
                    "a driver's id"

                Expect.equal (paths { spec with Sandbox = Some "none" }) [] "none"
                Expect.equal (paths { spec with Sandbox = Some "auto" }) [ "$.sandbox" ] "auto is the host's"

                Expect.equal
                    (paths
                        { spec with
                            Sandbox = Some "Bubble Wrap"
                        })
                    [ "$.sandbox" ]
                    "not an id"

                Expect.equal
                    (EnvSpecs.checkSandbox [ "none"; "bubblewrap" ] { spec with Sandbox = Some "nsjail" }
                     |> List.map (fun i -> JsonPath.render i.Path))
                    [ "$.sandbox" ]
                    "a driver that is not in effect"

                Expect.isEmpty
                    (EnvSpecs.checkSandbox
                        [ "none"; "bubblewrap" ]
                        { spec with
                            Sandbox = Some "bubblewrap"
                        })
                    "a driver in effect"

                Expect.equal
                    (paths
                        { spec with
                            Network = Some "allowlist:dev"
                        })
                    [ "$.network" ]
                    "network allowlists need the egress proxy"

                Expect.equal (paths { spec with Network = Some "none" }) [] "no network"
                Expect.equal (paths { spec with Network = Some "open" }) [] "the network"

                Expect.equal
                    (paths
                        { spec with
                            Config = Json.Object [ "x", Json.Null ]
                        })
                    [ "$.config" ]
                    "config"

                Expect.equal (paths { spec with Provider = "ssh" }) [ "$.provider" ] "another provider"

                Expect.equal
                    (paths
                        { spec with
                            Workspace =
                                { spec.Workspace with
                                    Source = MaterializeKind.Git
                                    Export = Some ExportKind.Pr
                                }
                            Setup = { spec.Setup with Secrets = [ "npm" ] }
                            Components =
                                { spec.Components with
                                    Placement = ComponentPlacement.HostPush
                                }
                        })
                    [
                        "$.workspace.source"
                        "$.workspace.export"
                        "$.setup.secrets"
                        "$.components.placement"
                    ]
                    "the rest"
            }

            test "providers' caps must match what they provide" {
                Expect.isEmpty (EnvProviders.validate provider) "local"

                let broken =
                    { provider with
                        Kind = "Local"
                        Caps =
                            { provider.Caps with
                                Transports = [ TransportKind.Direct; TransportKind.Direct ]
                                Suspend = true
                            }
                    }

                Expect.equal
                    (EnvProviders.validate broken |> List.map (fun i -> JsonPath.render i.Path))
                    [ "$.kind"; "$.caps"; "$.connect"; "$.suspend" ]
                    "issues"
            }

            test "environments on the host's machine get the host's variables without credentials" {
                let vars =
                    [
                        "PATH", "/usr/bin"
                        "HOME", "/home/u"
                        "OPENAI_API_KEY", "x"
                        "anthropic_api_key", "x"
                        "GITHUB_TOKEN", "x"
                        "GH_TOKEN", "x"
                        "AWS_SECRET_ACCESS_KEY", "x"
                        "AWS_ACCESS_KEY_ID", "x"
                        "DB_PASSWORD", "x"
                        "CLIENT_SECRET", "x"
                        "GOOGLE_APPLICATION_CREDENTIALS", "x"
                        "SSH_PRIVATE_KEY", "x"
                        "TOKEN", "x"
                        "BUN_OPTIONS", "x"
                        "NODE_OPTIONS", "x"
                        "DOTNET_STARTUP_HOOKS", "x"
                        "LD_PRELOAD", "x"
                        "TOKENIZERS_PARALLELISM", "false"
                        "KEYBOARD", "us"
                        "LANG", "C.UTF-8"
                    ]

                Expect.equal
                    (HostEnvironment.scrub vars |> List.map fst)
                    [ "PATH"; "HOME"; "TOKENIZERS_PARALLELISM"; "KEYBOARD"; "LANG" ]
                    "scrubbed"

                Expect.isTrue (HostEnvironment.matches "*_TOKEN_*" "CI_TOKEN_X") "middle"
                Expect.isFalse (HostEnvironment.matches "*_TOKEN_*" "CI_TOKEN") "needs the suffix"
                Expect.isTrue (HostEnvironment.matches "A*B*C" "AxxBxxC") "parts in order"
                Expect.isFalse (HostEnvironment.matches "A*B*C" "AxxCxxB") "not out of order"
                Expect.isFalse (HostEnvironment.matches "AB*BA" "ABA") "no overlap"
            }

            test "the layout keeps what the host puts there out of the workspace" {
                let lease =
                    {
                        Id = "e1"
                        Provider = "local"
                        Workspace = "C:\\src\\app"
                        Home = "C:\\Users\\u\\AppData\\Local\\fx\\environments\\e1"
                        Data = Map.empty
                    }

                let machine =
                    {
                        Target = TargetTriple.create Os.Windows Arch.X64 None
                        DataDir = "C:\\Users\\u\\AppData\\Local\\fx"
                        SameHost = false
                    }

                let layout = EnvLayout.create machine lease
                Expect.equal layout.EnvdState (lease.Home + "\\envd") "envd's state"
                Expect.equal layout.Assets (lease.Home + "\\assets") "assets"
                Expect.equal layout.Components "C:\\Users\\u\\AppData\\Local\\fx\\runtimes" "components"
                let owned = EnvLayout.owned layout "kernel-1"
                Expect.equal owned.Home (lease.Home + "\\owned\\kernel-1\\home") "owned, in the environment's syntax"
            }
        ]
