/// The bootstrap (spec §16.1): phases in order, trust before any code runs, the declarative and native
/// runtimes, contributed runtimes, components, failures, and changes after it.
module Fx.Extensions.Tests.BootstrapTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Extensions
open Fx.Sdk
open Fx.Ext.Sample
open Fx.Aot.Scenarios.Extensions
open Fx.Aot.Scenarios.Extensions.SampleHost

let words = Hook.contribute "test.word" "Words." Codec.string

/// What test extensions did, in order.
type Log() =
    let entries = Collections.Concurrent.ConcurrentQueue<string>()
    member _.Add(entry: string) = entries.Enqueue entry
    member _.Entries = List.ofSeq entries

/// A native extension that contributes words (data) and logs its activation and deactivation.
let native (log: Log) (id: string) (ws: string list) : Extension =
    {
        Manifest =
            { Manifest.create id "1.0.0" Runtimes.Native with
                Contributions = ws |> List.map (Declare.contribution words)
            }
        Activate =
            eff {
                log.Add("activate " + id)

                return
                    { Activation.create (ws |> List.map (fun w -> Contribution.create words w w)) [] with
                        Deactivate =
                            eff {
                                log.Add("deactivate " + id)
                                return ()
                            }
                    }
            }
    }

/// A native extension that contributes the extension runtime `echo`: it loads a package by providing
/// each declared word as its own value.
let echoRuntime (log: Log) : Extension =
    let runtime: ExtensionRuntime =
        ExtensionRuntimes.create (fun request ->
            eff {
                log.Add("echo loads " + request.Manifest.Id)

                let contributions =
                    request.Manifest.Contributions
                    |> List.map (fun decl -> Contribution.create words decl.Id decl.Id)

                return Ok(Activation.create contributions [])
            })

    {
        Manifest =
            { Manifest.create "test.echo" "1.0.0" Runtimes.Native with
                Contributions = [ Declare.contribution ExtensionRuntimes.contributionType "echo" ]
            }
        Activate =
            Eff.ret (Activation.create [ Contribution.create ExtensionRuntimes.contributionType "echo" runtime ] [])
    }

let hosted (id: string) (ws: string list) : Manifest =
    { Manifest.create id "1.0.0" "echo" with
        Contributions = ws |> List.map (Declare.contribution words)
    }

let declarative (id: string) (ws: string list) : Manifest =
    { Manifest.create id "1.0.0" Runtimes.Declarative with
        Contributions = ws |> List.map (fun w -> Declare.value words w w)
    }

let settingsWith (install: Manifest -> Eff<HostEnv, Result<InstalledComponent list, string>>) : HostSettings<HostEnv> =
    { SampleHost.settings with
        Catalog = Catalog.create (ContributionType.info words :: Sample.Points.all)
        InstallComponents = install
    }

let defaultSettings = settingsWith SampleHost.settings.InstallComponents

let bootstrapWith (settings: HostSettings<HostEnv>) (candidates: Candidate list) : Host =
    let env = hostEnv SampleHost.memoryShell
    (within (Eff.run env (ExtensionHost.bootstrap settings candidates))).GetAwaiter().GetResult()

let bootstrap candidates =
    bootstrapWith defaultSettings candidates

let builtin (extension: Extension) =
    Candidate.create Source.Builtin (Package.Native extension)

let fromManifest (source: Source) (manifest: Manifest) =
    Candidate.create source (Package.Manifest(manifest, Some AppContext.BaseDirectory))

let report (host: Host) : string list = host.Report |> List.map string

let wordsOf (host: Host) : string list =
    ExtensionHost.contributions words host |> List.map (fun c -> c.By)

let stop (host: Host) =
    (within (Eff.run (hostEnv SampleHost.memoryShell) (ExtensionHost.stop host))).GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "Bootstrap"
        [
            test "phases: native, declarative, components, processes, then contributed runtimes" {
                let log = Log()
                let installed = ResizeArray<string>()

                let install (manifest: Manifest) =
                    eff {
                        installed.Add manifest.Id
                        log.Add("install " + manifest.Id)
                        return Ok []
                    }

                let candidates =
                    [
                        fromManifest
                            Source.HostConfig
                            { hosted "h" [ "h1" ] with
                                Requires = [ { Id = "bun"; Version = "1" } ]
                            }
                        fromManifest Source.Builtin (SampleHost.processManifest ())
                        fromManifest Source.HostConfig (declarative "d" [ "d1"; "d2" ])
                        builtin (native log "n" [ "n1" ])
                        builtin (echoRuntime log)
                    ]

                let host = bootstrapWith (settingsWith install) candidates

                Expect.equal
                    (report host)
                    [
                        "n [in-binary] activated"
                        "test.echo [in-binary] activated"
                        "d [in-binary] activated"
                        $"{Sample.id} [process] activated"
                        "h [hosted] activated"
                    ]
                    "order"

                Expect.equal log.Entries [ "activate n"; "install h"; "echo loads h" ] "components before activation"
                Expect.equal (List.ofSeq installed) [ "h" ] "only what requires components"
                Expect.equal (wordsOf host) [ "n/n1"; "d/d1"; "d/d2"; "h/h1" ] "contributions in admission order"
                Expect.equal (Registry.replay host.Events) host.State "replay"
                stop host
            }

            test "the same candidates give the same events" {
                let candidates () =
                    [
                        builtin (native (Log()) "n" [ "n1" ])
                        builtin (echoRuntime (Log()))
                        fromManifest Source.HostConfig (hosted "h" [ "h1" ])
                        fromManifest Source.Project (declarative "p" [ "p1" ])
                    ]

                let first = bootstrap (candidates ())
                let second = bootstrap (candidates ())
                Expect.equal (second.Events |> List.map string) (first.Events |> List.map string) "events"
                Expect.equal second.State first.State "state"
            }

            test "nothing project-local runs before the project is trusted" {
                let log = Log()

                let project =
                    Candidate.create Source.Project (Package.Native(native log "p" [ "p1" ]))

                let host = bootstrap [ project ]

                Expect.equal
                    (report host)
                    [
                        "p [in-binary] rejected: trust.untrusted-project: p comes from the project, which is not trusted: nothing project-local runs before trust"
                    ]
                    "rejected"

                Expect.isEmpty log.Entries "its code never ran"

                let trusting =
                    { defaultSettings with
                        Trust =
                            { defaultSettings.Trust with
                                ProjectTrusted = true
                            }
                    }

                let host = bootstrapWith trusting [ project ]
                Expect.equal (report host) [ "p [in-binary] activated" ] "trusted"
            }

            test "components are installed only for admitted extensions; a failed install stops activation" {
                let installed = ResizeArray<string>()

                let install (manifest: Manifest) =
                    eff {
                        installed.Add manifest.Id
                        return Error "no network"
                    }

                let needs id =
                    { hosted id [ id + "1" ] with
                        Requires = [ { Id = "bun"; Version = "1" } ]
                    }

                let host =
                    bootstrapWith
                        (settingsWith install)
                        [
                            builtin (echoRuntime (Log()))
                            fromManifest Source.Project (needs "untrusted")
                            fromManifest Source.HostConfig (needs "h")
                        ]

                Expect.equal (List.ofSeq installed) [ "h" ] "the untrusted one gets nothing installed"

                Expect.equal
                    (report host |> List.map (fun line -> line.Split(':')[0]))
                    [
                        "test.echo [in-binary] activated"
                        "untrusted [hosted] rejected"
                        "h [components] failed"
                    ]
                    "outcomes"

                Expect.isEmpty (wordsOf host) "nothing from h"
            }

            test "an extension process's launch names the commands of the components installed for it" {
                let spawned = ResizeArray<ProcSpec>()

                let shell: Shell =
                    {
                        Run = fun _ _ -> invalidOp "spawn only"
                        Spawn =
                            fun spec token ->
                                spawned.Add spec
                                SampleHost.memoryShell.Spawn spec token
                    }

                let peer = Path.Combine(Path.GetTempPath(), "runtimes", "peer", "bin", "peer")

                let installedPeer: InstalledComponent =
                    {
                        Id = "peer"
                        Version = SemVer.tryParse "1.0.0" |> Option.get
                        Dir = Path.Combine(Path.GetTempPath(), "runtimes", "peer")
                        Bin = [ "peer", peer ]
                        Env = []
                    }

                let manifest (exe: string) =
                    let manifest = SampleHost.processManifest ()

                    { manifest with
                        Requires = [ { Id = "peer"; Version = "1" } ]
                        Launch = manifest.Launch |> Option.map (fun launch -> { launch with Exe = exe })
                    }

                let load (exe: string) =
                    let env = hostEnv shell

                    (within (
                        Eff.run
                            env
                            (ExtensionHost.bootstrap
                                (settingsWith (fun _ -> EffResult.ok [ installedPeer ]))
                                [ fromManifest Source.Builtin (manifest exe) ])
                    ))
                        .GetAwaiter()
                        .GetResult()

                let host = load "${bin.peer}"
                Expect.equal (report host) [ $"{Sample.id} [process] activated" ] "activated"

                match List.ofSeq spawned with
                | [ { Program = Exec(exe, _) } ] -> Expect.equal exe peer "the installed command"
                | other -> failtest (string other)

                stop host

                let host = load "${bin.node}"

                Expect.equal
                    (report host)
                    [
                        $"{Sample.id} [process] failed: its launch names ${{bin.node}}, a command that none of the components it requires exports"
                    ]
                    "a command none exports"
            }

            test "without an installer, extensions that require components fail closed" {
                let host =
                    bootstrap
                        [
                            builtin (echoRuntime (Log()))
                            fromManifest
                                Source.HostConfig
                                { hosted "h" [ "h1" ] with
                                    Requires = [ { Id = "bun"; Version = "1" } ]
                                }
                        ]

                Expect.equal
                    (List.last (report host))
                    "h [components] failed: this host has no component installer yet"
                    "failed"
            }

            test "a runtime nobody contributes fails the extension" {
                let host = bootstrap [ fromManifest Source.HostConfig (hosted "h" [ "h1" ]) ]

                Expect.equal
                    (report host)
                    [ "h [hosted] failed: no active extension contributes the runtime echo" ]
                    "failed"
            }

            test "declarative contributions load through the catalog; code cannot be declared" {
                let host =
                    bootstrap
                        [
                            fromManifest Source.HostConfig (declarative "d" [ "w" ])
                            fromManifest
                                Source.HostConfig
                                { Manifest.create "code" "1.0.0" Runtimes.Declarative with
                                    Contributions =
                                        [
                                            { Declare.contribution Sample.Points.greeting "g" with
                                                Value = Some(Json.Object [])
                                            }
                                        ]
                                }
                            fromManifest
                                Source.HostConfig
                                { Manifest.create "bad" "1.0.0" Runtimes.Declarative with
                                    Contributions =
                                        [
                                            { Declare.value words "w2" "w2" with
                                                Value = Some(Json.int 1)
                                            }
                                        ]
                                }
                        ]

                Expect.equal (wordsOf host) [ "d/w" ] "data"

                Expect.equal
                    (report host |> List.skip 1)
                    [
                        "code [in-binary] failed: its contributions do not load:\n$.contributions[0].value: gate:sample.greeting is code: a native or process extension must provide it"
                        "bad [in-binary] failed: its contributions do not load:\n$.contributions[0].value: expected string, got number 1"
                    ]
                    "issues"
            }

            test "native extensions can define contribution types for those after them" {
                let colours = Hook.contribute "test.colour" "Colours." Codec.string

                let definer =
                    {
                        Manifest =
                            { Manifest.create "definer" "1.0.0" Runtimes.Native with
                                Contributions = [ Declare.contribution ContributionTypes.contributionType "colour" ]
                            }
                        Activate =
                            Eff.ret (
                                Activation.create
                                    [
                                        Contribution.create
                                            ContributionTypes.contributionType
                                            "colour"
                                            (ContributionType.info colours)
                                    ]
                                    []
                            )
                    }

                let user =
                    { Manifest.create "user" "1.0.0" Runtimes.Declarative with
                        Contributions = [ Declare.value colours "red" "red" ]
                    }

                let host = bootstrap [ fromManifest Source.HostConfig user; builtin definer ]
                Expect.equal (report host) [ "definer [in-binary] activated"; "user [in-binary] activated" ] "order"

                Expect.equal
                    (ExtensionHost.contributions colours host |> List.map (fun c -> c.Value))
                    [ "red" ]
                    "loaded"

                Expect.isSome (Catalog.tryFind "test.colour" host.Catalog) "in the catalog"
            }

            test "an activation that does not match its manifest fails and is deactivated" {
                let log = Log()
                let liar = native log "liar" [ "a" ]

                let host =
                    bootstrap
                        [
                            builtin
                                { liar with
                                    Manifest =
                                        { liar.Manifest with
                                            Contributions = [ Declare.contribution words "b" ]
                                        }
                                }
                        ]

                Expect.stringStarts
                    (List.head (report host))
                    "liar [in-binary] failed: liar does not provide what its manifest declares"
                    "failed"

                Expect.equal log.Entries [ "activate liar"; "deactivate liar" ] "deactivated"
                Expect.isEmpty host.State.Owners "released"
            }

            test "an activation that throws fails the extension, not the host" {
                let broken =
                    {
                        Manifest = Manifest.create "broken" "1.0.0" Runtimes.Native
                        Activate = fun _ -> invalidOp "broken"
                    }

                let host = bootstrap [ builtin broken; builtin (native (Log()) "ok" [ "w" ]) ]

                Expect.equal
                    (report host)
                    [
                        "broken [in-binary] failed: it failed with InvalidOperationException"
                        "ok [in-binary] activated"
                    ]
                    "outcomes"
            }

            test "an invalid manifest is rejected before anything runs" {
                let log = Log()
                let bad = native log "bad" [ "w" ]

                let host =
                    bootstrap
                        [
                            builtin
                                { bad with
                                    Manifest = { bad.Manifest with Sdk = "x" }
                                }
                        ]

                Expect.stringStarts
                    (List.head (report host))
                    "bad [in-binary] rejected: manifest.invalid: $.sdk:"
                    "rejected"

                Expect.isEmpty log.Entries "never ran"
            }

            test "placement and host config: skipped, but policy extensions cannot be disabled" {
                let envd = native (Log()) "envd" []

                let settings =
                    { defaultSettings with
                        Disabled = [ "off"; "pol" ]
                    }

                let host =
                    bootstrapWith
                        settings
                        [
                            builtin
                                { envd with
                                    Manifest =
                                        { envd.Manifest with
                                            Placement = Placement.Environment
                                        }
                                }
                            builtin (native (Log()) "off" [])
                            Candidate.create Source.Policy (Package.Native(native (Log()) "pol" []))
                        ]

                Expect.equal
                    (report host)
                    [
                        "envd [in-binary] skipped: it runs in the environment"
                        "off [in-binary] skipped: disabled in host config"
                        "pol [in-binary] activated"
                    ]
                    "outcomes"
            }

            test "replacing: a host-config fork takes over a built-in's contributions" {
                let log = Log()
                let original = native log "orig" [ "w" ]
                let fork = native log "fork" [ "w" ]

                let host =
                    bootstrap
                        [
                            builtin original
                            Candidate.create
                                Source.HostConfig
                                (Package.Native
                                    { fork with
                                        Manifest =
                                            { fork.Manifest with
                                                Replaces = [ "orig" ]
                                            }
                                    })
                        ]

                Expect.equal (wordsOf host) [ "fork/w" ] "the fork's"
                Expect.equal log.Entries [ "activate orig"; "deactivate orig"; "activate fork" ] "the original stopped"
            }

            test "disable and stop deactivate; policy extensions stay" {
                let log = Log()

                let host =
                    bootstrap
                        [
                            builtin (native log "a" [ "a1" ])
                            builtin (native log "b" [ "b1" ])
                            Candidate.create Source.Policy (Package.Native(native log "pol" [ "p1" ]))
                        ]

                let env = hostEnv SampleHost.memoryShell

                let disable id =
                    (Eff.run env (ExtensionHost.disable host id "test")).GetAwaiter().GetResult()

                match disable "pol" with
                | Error rejection -> Expect.equal rejection.Code "trust.policy" "policy"
                | Ok _ -> failtest "policy disabled"

                match disable "a" with
                | Error rejection -> failtest (string rejection)
                | Ok after ->
                    Expect.equal (wordsOf after) [ "b/b1"; "pol/p1" ] "a is gone"
                    Expect.equal (wordsOf host) [ "a/a1"; "b/b1"; "pol/p1" ] "the old snapshot is unchanged"
                    stop after

                Expect.equal
                    log.Entries
                    [
                        "activate a"
                        "activate b"
                        "activate pol"
                        "deactivate a"
                        "deactivate pol"
                        "deactivate b"
                    ]
                    "stopped last first"
            }

            test "fx.runtime.bun loads as data: its component and its runtime; a project cannot contribute runtimes" {
                let manifest = Fx.Ext.RuntimeBun.RuntimeBun.manifest
                let host = bootstrap [ fromManifest Source.Builtin manifest ]
                Expect.equal (report host) [ $"{manifest.Id} [in-binary] activated" ] "activated"

                let runtimes = ExtensionHost.contributions RuntimeSpecs.contributionType host

                let components =
                    ExtensionHost.contributions ComponentDefinitions.contributionType host

                Expect.equal
                    (runtimes |> List.map (fun c -> c.By, c.Value))
                    [ $"{manifest.Id}/bun", Fx.Ext.RuntimeBun.RuntimeBun.runtime ]
                    "the runtime"

                Expect.equal
                    (components |> List.map (fun c -> c.By, c.Value))
                    [ $"{manifest.Id}/bun-1.4.5", Fx.Ext.RuntimeBun.RuntimeBun.bunComponent ]
                    "the component"

                // Runtimes are privileged (spec §16.5): a trusted project still cannot contribute one.
                let fromProject =
                    bootstrapWith
                        { defaultSettings with
                            Trust =
                                { defaultSettings.Trust with
                                    ProjectTrusted = true
                                }
                        }
                        [
                            fromManifest
                                Source.Project
                                { manifest with
                                    Id = "acme.bun"
                                    Contributions = [ List.last manifest.Contributions ]
                                }
                        ]

                Expect.stringStarts
                    (List.head (report fromProject))
                    "acme.bun [in-binary] rejected: trust.privileged"
                    "privileged"

                // A runtime whose launch lets an injection vector through does not load.
                let lax =
                    { manifest with
                        Id = "acme.lax"
                        Contributions =
                            [
                                { List.last manifest.Contributions with
                                    Value =
                                        Some(
                                            RuntimeSpec.codec.Encode
                                                { Fx.Ext.RuntimeBun.RuntimeBun.runtime with
                                                    Launch =
                                                        { Fx.Ext.RuntimeBun.RuntimeBun.launch with
                                                            EnvDeny = [ "BUN_OPTIONS" ]
                                                        }
                                                }
                                        )
                                }
                            ]
                    }

                Expect.stringStarts
                    (List.head (report (bootstrap [ fromManifest Source.HostConfig lax ])))
                    "acme.lax [in-binary] failed: its contributions do not load:\n$.contributions[0].value.launch.envDeny: does not deny"
                    "refused with a clear issue"
            }

            test "fx.interpreter.bun-ts loads natively next to fx.runtime.bun; a project cannot contribute interpreters" {
                let interpreter = Fx.Ext.InterpreterBunTs.BunTs.extension
                let runtime = Fx.Ext.RuntimeBun.RuntimeBun.manifest

                let host = bootstrap [ builtin interpreter; fromManifest Source.Builtin runtime ]

                Expect.equal
                    (report host)
                    [
                        $"{interpreter.Manifest.Id} [in-binary] activated"
                        $"{runtime.Id} [in-binary] activated"
                    ]
                    "activated"

                match ExtensionHost.contributions InterpreterProviders.contributionType host with
                | [ contributed ] ->
                    Expect.equal contributed.By $"{interpreter.Manifest.Id}/bun-ts" "the interpreter"

                    let runtimes =
                        ExtensionHost.contributions RuntimeSpecs.contributionType host
                        |> List.map (fun c -> c.Id)

                    Expect.contains runtimes contributed.Value.Launch.Runtime "its runtime is there"
                | other -> failtest $"{other}"

                let fromProject =
                    bootstrapWith
                        { defaultSettings with
                            Trust =
                                { defaultSettings.Trust with
                                    ProjectTrusted = true
                                }
                        }
                        [
                            Candidate.create
                                Source.Project
                                (Package.Native
                                    { interpreter with
                                        Manifest =
                                            { interpreter.Manifest with
                                                Id = "acme.ts"
                                            }
                                    })
                        ]

                Expect.stringStarts
                    (List.head (report fromProject))
                    "acme.ts [in-binary] rejected: trust.privileged"
                    "privileged"
            }

            test "fx.env.local loads natively; a project cannot contribute environment providers" {
                let local = Fx.Ext.EnvLocal.EnvLocal.extension
                let host = bootstrap [ builtin local ]
                Expect.equal (report host) [ $"{local.Manifest.Id} [in-binary] activated" ] "activated"

                match ExtensionHost.contributions EnvProviders.contributionType host with
                | [ contributed ] ->
                    Expect.equal contributed.By $"{local.Manifest.Id}/local" "the provider, by kind"
                    Expect.equal contributed.Value.Kind "local" "its kind"
                | other -> failtest $"{other}"

                let fromProject =
                    bootstrapWith
                        { defaultSettings with
                            Trust =
                                { defaultSettings.Trust with
                                    ProjectTrusted = true
                                }
                        }
                        [
                            Candidate.create
                                Source.Project
                                (Package.Native
                                    { local with
                                        Manifest = { local.Manifest with Id = "acme.env" }
                                    })
                        ]

                Expect.stringStarts
                    (List.head (report fromProject))
                    "acme.env [in-binary] rejected: trust.privileged"
                    "privileged"
            }

            test "the sandbox drivers load natively; a project cannot contribute sandbox drivers" {
                let builtIns =
                    [
                        Fx.Ext.SandboxBubblewrap.Bubblewrap.extension
                        Fx.Ext.SandboxSeatbelt.Seatbelt.extension
                        Fx.Ext.SandboxNone.SandboxNone.extension
                    ]

                let host = bootstrap (builtIns |> List.map builtin)

                Expect.equal
                    (report host)
                    (builtIns |> List.map (fun e -> $"{e.Manifest.Id} [in-binary] activated"))
                    "activated"

                Expect.equal
                    (ExtensionHost.contributions SandboxDrivers.contributionType host
                     |> List.map (fun c -> c.Value.Id))
                    [ "bubblewrap"; "seatbelt"; "none" ]
                    "the drivers, by id"

                for contributed in ExtensionHost.contributions SandboxDrivers.contributionType host do
                    Expect.isEmpty (SandboxDrivers.validate contributed.Value) $"{contributed.Value.Id} is valid"

                let bwrap = Fx.Ext.SandboxBubblewrap.Bubblewrap.extension

                let fromProject =
                    bootstrapWith
                        { defaultSettings with
                            Trust =
                                { defaultSettings.Trust with
                                    ProjectTrusted = true
                                }
                        }
                        [
                            Candidate.create
                                Source.Project
                                (Package.Native
                                    { bwrap with
                                        Manifest =
                                            { bwrap.Manifest with
                                                Id = "acme.sandbox"
                                            }
                                    })
                        ]

                Expect.stringStarts
                    (List.head (report fromProject))
                    "acme.sandbox [in-binary] rejected: trust.privileged"
                    "privileged"
            }

            test "a process that cannot start fails its extension" {
                let manifest =
                    { SampleHost.processManifest () with
                        Launch =
                            Some
                                { (SampleHost.processManifest ()).Launch.Value with
                                    Exe = IO.Path.Combine(AppContext.BaseDirectory, "no-such-program")
                                }
                    }

                let env =
                    hostEnv (LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory)

                let host =
                    (within (
                        Eff.run env (ExtensionHost.bootstrap defaultSettings [ fromManifest Source.Builtin manifest ])
                    ))
                        .GetAwaiter()
                        .GetResult()

                Expect.stringStarts (List.head (report host)) $"{Sample.id} [process] failed: could not start" "failed"
            }
        ]
