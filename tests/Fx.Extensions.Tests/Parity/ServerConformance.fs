/// The extension protocol's server conformance suite: a function from a way to reach a process that
/// serves the sample extension (`Fx.Ext.Sample`, or a sample in another language implementing the same
/// contract) to tests of the protocol's rules, as the host sees them through its `RemotePeer`: the
/// handshake, activation, the typed errors of each method, and deactivation. F#'s `ExtensionServer`
/// and `@fx/sdk`'s server both pass it (spec §16.1: other languages implement the same protocol).
module Fx.Extensions.Tests.ServerConformance

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
open Fx.Aot.Scenarios.Extensions.SampleHost

/// A connection to a server, and how to end it.
[<NoEquality; NoComparison>]
type Connection =
    {
        Remote: RemotePeer
        Peer: Peer
        Close: unit -> Task
    }

    override this.ToString() = "Connection"

/// Connects with these `initialize` members.
type Connect = (string * Json) list -> Task<Result<Connection, ConnectError>>

/// A server of the sample in this process, over the in-process transport.
let inProcess: Connect =
    fun members ->
        let client, server =
            InProcess.pair
                (256 * 1024)
                (ConnectionLimits.frames ConnectionLimits.defaults)
                (ConnectionLimits.frames ConnectionLimits.defaults)

        let env = hostEnv memoryShell

        Eff.run
            env
            (ExtensionServer.serve info [ Sample.extension ] (BasicExtensionEnv.forManifest SystemClock.clock) server)
        |> ignore

        task {
            let! connected =
                within (Eff.run env (Rpc.connect (Service.create info []) members client))

            return
                connected
                |> Result.map (fun peer ->
                    {
                        Remote = RpcRemote.ofPeer peer
                        Peer = peer
                        Close = fun () -> within (Eff.run env (Rpc.close peer)) :> Task
                    })
        }

/// A server in a child process, started from a launch spec with the real shell.
let child (root: string) (launch: LaunchSpec) : Connect =
    fun members ->
        let env =
            hostEnv (LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory)

        let context =
            {
                Root = root
                Workspace = None
                HostEnvironment = processSettings.HostEnvironment
                IsWindows = OperatingSystem.IsWindows()
            }

        task {
            match Launch.procSpec context launch with
            | Error reason -> return failtest reason
            | Ok spec ->
                match! within (Eff.run env (Shell.spawn spec)) with
                | Error e -> return failtest (string e)
                | Ok proc ->
                    let transport =
                        ProcTransport.create (ConnectionLimits.frames ConnectionLimits.defaults) proc ignore

                    let stop () =
                        task {
                            let! exited = Eff.run env (Eff.timeout (TimeSpan.FromSeconds 10.0) (Shell.wait proc))

                            if exited.IsNone then
                                do! Eff.run env (Shell.kill proc)
                        }

                    match! within (Eff.run env (Rpc.connect (Service.create info []) members transport)) with
                    | Error e ->
                        do! Eff.run env (Shell.kill proc)
                        return Error e
                    | Ok peer ->
                        return
                            Ok
                                {
                                    Remote = RpcRemote.ofPeer peer
                                    Peer = peer
                                    Close =
                                        fun () ->
                                            within (
                                                task {
                                                    let! _ = Eff.run env (Rpc.close peer)
                                                    do! stop ()
                                                }
                                            )
                                            :> Task
                                }
        }

let private using (connect: Connect) (members: (string * Json) list) (body: Connection -> Task<unit>) =
    task {
        match! connect members with
        | Error e -> failtest $"connect: {e}"
        | Ok connection ->
            try
                do! body connection
            finally
                (connection.Close()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
    }

let private activate (peer: RemotePeer) (id: string) =
    within (
        peer.Activate
            {
                Extension = id
                Manifest = None
                Root = None
            }
            CancellationToken.None
    )

let private invoke (peer: RemotePeer) (kind: string) (id: string) (input: Json) =
    within (
        peer.Invoke
            {
                Extension = Sample.id
                Type = kind
                Id = id
            }
            input
            CancellationToken.None
    )

let private greeting (name: string) (language: string) =
    Json.Object [ "name", Json.String name; "language", Json.String language ]

/// The manifest without what depends on how a server runs the sample (its runtime and launch).
let private declared (manifest: Manifest) : Manifest =
    { manifest with
        Runtime = Sample.manifest.Runtime
        Launch = None
    }

let tests (name: string) (connect: Connect) : Test =
    testList
        name
        [
            testTask "a host that does not speak the extension protocol is refused" {
                match! connect [] with
                | Error(ConnectError.Refused e) ->
                    Expect.equal e.Code Protocol.ErrorCode.InvalidRequest "code"
                    Expect.stringContains e.Message "extension protocol" "refused"
                | Error other -> failtest $"expected a refusal, got {other}"
                | Ok connection ->
                    do! connection.Close()
                    failtest "expected a refusal"
            }

            testTask "the process says which extensions it serves" {
                do!
                    using connect RpcRemote.initializeMembers (fun c ->
                        task {
                            Expect.equal
                                (Json.tryField ExtensionProtocol.ServesMember c.Peer.Remote.Members)
                                (Some(Json.Array [ Json.String Sample.id ]))
                                "serves"
                        })
            }

            testTask "activation: unknown extensions, the sample's declarations, and twice answers the same" {
                do!
                    using connect RpcRemote.initializeMembers (fun c ->
                        task {
                            match! activate c.Remote "nope" with
                            | Error(RemoteFailure.Unknown what) -> Expect.equal what "extension nope" "unknown"
                            | other -> failtest (string other)

                            let! first = activate c.Remote Sample.id
                            let! second = activate c.Remote Sample.id

                            match first, second with
                            | Ok first, Ok second ->
                                Expect.equal second first "idempotent"
                                Expect.equal (declared first.Manifest) Sample.manifest "the sample's manifest"

                                Expect.equal
                                    (first.Contributions |> List.map string)
                                    (Sample.manifest.Contributions |> List.map string)
                                    "what it provides"

                                Expect.equal
                                    (first.Contributions
                                     |> List.choose (fun c -> c.Value |> Option.map Json.serialize))
                                    [
                                        """{"priority":10}"""
                                        """{"priority":20}"""
                                        """{"priority":20}"""
                                        """{"priority":0}"""
                                        "\"alpha\""
                                        "\"beta\""
                                    ]
                                    "the JSON forms: transform priorities and data"

                                Expect.equal first.Methods [ "echo"; "divide"; "observed"; "fail" ] "methods"
                            | other -> failtest (string other)
                        })
            }

            testTask "invocation: not active, unknown, invalid input, data, decisions and defects" {
                do!
                    using connect RpcRemote.initializeMembers (fun c ->
                        task {
                            let gate = Sample.Points.greeting.Name

                            match! invoke c.Remote gate "polite" (greeting "Ada" "en") with
                            | Error(RemoteFailure.Failed reason) ->
                                Expect.stringContains reason "is not active" "not active"
                            | other -> failtest (string other)

                            let! _ = activate c.Remote Sample.id

                            match! invoke c.Remote gate "nope" (Json.Object []) with
                            | Error(RemoteFailure.Unknown what) ->
                                Expect.equal what $"contribution {Sample.id}/{gate}/nope" "unknown"
                            | other -> failtest (string other)

                            match! invoke c.Remote gate "polite" (Json.Object [ "name", Json.int 1 ]) with
                            | Error(RemoteFailure.InvalidInput issues) ->
                                Expect.equal
                                    (issues |> List.map string)
                                    [
                                        "$.name: expected string, got number 1"
                                        "$.language: missing required field"
                                    ]
                                    "issues"
                            | other -> failtest (string other)

                            match! invoke c.Remote Sample.Points.words.Name "alpha" Json.Null with
                            | Error(RemoteFailure.Failed reason) ->
                                Expect.stringContains reason "runs only in its process" "data is not code"
                            | other -> failtest (string other)

                            let! allowed = invoke c.Remote gate "polite" (greeting "Ada" "en")
                            Expect.equal allowed (Ok(Json.Object [ "decision", Json.String "allow" ])) "allowed"

                            let! asked = invoke c.Remote gate "polite" (greeting "Ada" "fr")

                            Expect.equal
                                asked
                                (Ok(
                                    Json.Object [ "decision", Json.String "ask"; "reason", Json.String "greet in fr?" ]
                                ))
                                "asked"

                            match! invoke c.Remote gate "fragile" (greeting "crash" "en") with
                            | Error(RemoteFailure.Failed reason) ->
                                Expect.stringContains reason "internal error" "a defect is an internal error"
                            | other -> failtest (string other)

                            let! shouted = invoke c.Remote Sample.Points.text.Name "shout" (Json.String "hi")
                            Expect.equal shouted (Ok(Json.String "HI!")) "transformed"

                            let! observed =
                                invoke
                                    c.Remote
                                    Sample.Points.notes.Name
                                    "recorder"
                                    (Json.Object [ "text", Json.String "n" ])

                            Expect.equal observed (Ok Json.Null) "observed"
                        })
            }

            testTask "calls: results, typed errors, invalid params, unknown methods and defects" {
                do!
                    using connect RpcRemote.initializeMembers (fun c ->
                        task {
                            let! _ = activate c.Remote Sample.id

                            let call name parameters =
                                within (c.Remote.Call Sample.id name parameters CancellationToken.None)

                            let division dividend divisor =
                                Json.Object [ "dividend", Json.int dividend; "divisor", Json.int divisor ]

                            let! quotient = call "divide" (division 7 2)
                            Expect.equal quotient (Ok(Json.int 3)) "result"

                            match! call "divide" (division 1 0) with
                            | Error(MethodFailure.Failed(message, data)) ->
                                Expect.equal message "division by zero" "the error's text"
                                Expect.equal data (Json.Object [ "kind", Json.String "divideByZero" ]) "its encoding"
                            | other -> failtest (string other)

                            match! call "divide" (Json.Object [ "dividend", Json.int 1 ]) with
                            | Error(MethodFailure.InvalidParams issues) ->
                                Expect.equal (Issue.render issues) "$.divisor: missing required field" "issues"
                            | other -> failtest (string other)

                            match! call "nope" Json.Null with
                            | Error(MethodFailure.UnknownMethod name) -> Expect.equal name "nope" "unknown"
                            | other -> failtest (string other)

                            match! call "fail" Json.Null with
                            | Error(MethodFailure.Crashed _) -> ()
                            | other -> failtest (string other)
                        })
            }

            testTask "deactivation ends the extension's activation in the process" {
                do!
                    using connect RpcRemote.initializeMembers (fun c ->
                        task {
                            let! _ = activate c.Remote Sample.id
                            let! deactivated = within (c.Remote.Deactivate Sample.id CancellationToken.None)
                            Expect.equal deactivated (Ok()) "deactivated"

                            match! invoke c.Remote Sample.Points.words.Name "alpha" Json.Null with
                            | Error(RemoteFailure.Failed reason) -> Expect.stringContains reason "is not active" "gone"
                            | other -> failtest (string other)

                            match! within (c.Remote.Deactivate Sample.id CancellationToken.None) with
                            | Error(RemoteFailure.Failed _) -> ()
                            | other -> failtest (string other)
                        })
            }
        ]
