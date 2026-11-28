/// The extension protocol's server side and the host's `RemotePeer` over it, beyond the server
/// conformance suite (Parity/ServerConformance.fs): several extensions in one process, interpreter
/// middleware, and a runtime contributed by an extension process (as `fx.exthost.bun` does).
module Fx.Extensions.Tests.ServerTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Extensions
open Fx.Rpc
open Fx.Sdk
open Fx.Sdk.Process
open Fx.Ext.Sample
open Fx.Aot.Scenarios.Extensions
open Fx.Aot.Scenarios.Extensions.SampleHost
open Fx.Extensions.Tests.BootstrapTests

let private inProcess = Hook.interpret "test.fn" "Middleware."

/// An extension with a middleware: code that cannot leave its process.
let private local: Extension =
    {
        Manifest =
            { Manifest.create "test.local" "1.0.0" Runtimes.Native with
                Contributions = [ Declare.contribution inProcess "m" ]
            }
        Activate =
            Eff.ret (
                Activation.create
                    [
                        Contribution.create inProcess "m" ({ Priority = 0; Wrap = id }: Middleware<int -> int>)
                    ]
                    []
            )
    }

/// A connection to a server of these extensions in this process, and the host's `RemotePeer`.
let private connect
    (members: (string * Json) list)
    (extensions: Extension list)
    : Task<Result<RemotePeer * Peer, ConnectError>> =
    let client, server =
        InProcess.pair
            (256 * 1024)
            (ConnectionLimits.frames ConnectionLimits.defaults)
            (ConnectionLimits.frames ConnectionLimits.defaults)

    let env = hostEnv SampleHost.memoryShell

    Eff.run env (ExtensionServer.serve info extensions (BasicExtensionEnv.create SystemClock.clock) server)
    |> ignore

    task {
        let attached = RpcRemote.Attached()

        let! connected =
            within (Eff.run env (Rpc.connect (Service.create info (RpcRemote.hostHandlers attached)) members client))

        return connected |> Result.map (fun peer -> RpcRemote.ofPeer attached peer, peer)
    }

let private remote (extensions: Extension list) : RemotePeer =
    match (connect RpcRemote.initializeMembers extensions).GetAwaiter().GetResult() with
    | Ok(remote, _) -> remote
    | Error e -> failtest (string e)

let private wait (work: Task<'T>) : 'T = (within work).GetAwaiter().GetResult()

let private activate (peer: RemotePeer) (id: string) =
    wait (
        peer.Activate
            {
                Extension = id
                Manifest = None
                Root = None
            }
            CancellationToken.None
    )

[<Tests>]
let tests =
    testList
        "Extension protocol"
        [
            test "the process serves several extensions; code that cannot leave its process is not servable" {
                match (connect RpcRemote.initializeMembers [ Sample.extension; local ]).GetAwaiter().GetResult() with
                | Ok(_, peer) ->
                    Expect.equal
                        (Json.tryField ExtensionProtocol.ServesMember peer.Remote.Members)
                        (Some(Json.Array [ Json.String Sample.id; Json.String "test.local" ]))
                        "serves"
                | Error e -> failtest (string e)

                let peer = remote [ Sample.extension; local ]
                activate peer "test.local" |> ignore

                match
                    wait (
                        peer.Invoke
                            {
                                Extension = "test.local"
                                Type = inProcess.Name
                                Id = "m"
                            }
                            Json.Null
                            CancellationToken.None
                    )
                with
                | Error(RemoteFailure.Failed reason) ->
                    Expect.stringContains reason "runs only in its process" "not servable"
                | other -> failtest (string other)
            }

            test "the host answers host/* only for extensions attached to the connection, with their capabilities" {
                let client, server =
                    InProcess.pair
                        (256 * 1024)
                        (ConnectionLimits.frames ConnectionLimits.defaults)
                        (ConnectionLimits.frames ConnectionLimits.defaults)

                let env = hostEnv SampleHost.memoryShell
                let attached = RpcRemote.Attached()

                let current () =
                    ExtensionHost.empty (Catalog.create Sample.Points.all)

                attached.Attach(
                    Sample.id,
                    BasicExtensionEnv.attenuated SystemClock.clock (SampleHost.services current) Sample.manifest
                )

                attached.Attach(
                    "acme.bare",
                    BasicExtensionEnv.attenuated
                        SystemClock.clock
                        (SampleHost.services current)
                        (Manifest.create "acme.bare" "1.0.0" Runtimes.Process)
                )

                // The process's side: a plain client of the host's service.
                let hostSide =
                    Eff.run env (Rpc.accept (Service.create info (RpcRemote.hostHandlers attached)) server)

                let processPeer =
                    wait (Eff.run env (Rpc.connect (Service.create info []) RpcRemote.initializeMembers client))
                    |> Result.defaultWith (fun e -> failtest (string e))

                wait hostSide |> ignore

                let variants (id: string) =
                    wait (Eff.run env (Rpc.call processPeer ExtensionProtocol.hostVariants { Extension = id }))

                Expect.equal (variants Sample.id) (Ok { Slots = [ SampleHost.styleValue ] }) "the sample's slots"

                Expect.equal
                    (variants "acme.bare")
                    (Error(CallError.Failed(ExtensionProtocol.HostError.NotGranted("acme.bare", Capability.Variants))))
                    "not listed in its manifest"

                Expect.equal
                    (variants "acme.nope")
                    (Error(CallError.Failed(ExtensionProtocol.HostError.UnknownExtension "acme.nope")))
                    "not on this connection"

                attached.Detach Sample.id

                Expect.equal
                    (variants Sample.id)
                    (Error(CallError.Failed(ExtensionProtocol.HostError.UnknownExtension Sample.id)))
                    "detached"
            }

            test "a runtime contributed by an extension process loads packages through that process" {
                let log = Log()

                let exthost =
                    { (echoRuntime log).Manifest with
                        Runtime = Runtimes.Process
                        Launch = (SampleHost.processManifest ()).Launch
                    }

                let env = hostEnv (SampleHost.memoryShellOf [ echoRuntime log ])

                let host =
                    wait (
                        Eff.run
                            env
                            (ExtensionHost.bootstrap
                                defaultSettings
                                [
                                    fromManifest Source.HostConfig (hosted "h" [ "h1"; "h2" ])
                                    fromManifest Source.Builtin exthost
                                ])
                    )

                Expect.equal (report host) [ "test.echo [process] activated"; "h [hosted] activated" ] "order"
                Expect.equal (wordsOf host) [ "h/h1"; "h/h2" ] "loaded through the process"
                Expect.equal log.Entries [ "echo loads h" ] "the runtime ran in the process"
                wait (Eff.run env (ExtensionHost.stop host))
            }
        ]
