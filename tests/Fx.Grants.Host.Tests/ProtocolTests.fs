/// The `grants/*` client protocol over a JSON-RPC connection (in process): what the TUI's grants view
/// does (spec §19.4).
module Fx.Grants.Host.Tests.ProtocolTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Rpc
open Fx.Grants
open Fx.Grants.Host
open Fx.Grants.Host.Tests.Support

type private Client =
    {
        Peer: Peer
        Notices: ConcurrentQueue<GrantNotice>
        Arrived: SemaphoreSlim
    }

/// A host serving the grants protocol to one client, who is alice, and the client.
let private connect (h: Harness) : Client =
    let limits = ConnectionLimits.frames ConnectionLimits.defaults
    let clientSide, serverSide = InProcess.pair 65536 limits limits

    let caller: GrantsProtocol.Caller = fun _ -> Ok alice

    let server =
        Service.create { Name = "host"; Version = "0" } (GrantsProtocol.handlers h.Service caller)

    let notices = ConcurrentQueue<GrantNotice>()
    let arrived = new SemaphoreSlim(0)

    let client =
        Service.create
            { Name = "tui"; Version = "0" }
            [
                Handler.notification GrantsProtocol.event (fun _ notice ->
                    fun _ ->
                        notices.Enqueue notice
                        arrived.Release() |> ignore
                        Task.FromResult())
            ]

    let accepting = Eff.run h.Env (Rpc.accept server serverSide)
    let peer = wait (Eff.run h.Env (Rpc.connect client [] clientSide))
    wait accepting |> ignore

    match peer with
    | Ok peer ->
        {
            Peer = peer
            Notices = notices
            Arrived = arrived
        }
    | Error e -> failtest (string e)

let private call (h: Harness) (client: Client) (m: Method<'P, 'R, 'E>) (p: 'P) : Result<'R, CallError<'E>> =
    wait (Eff.run h.Env (Rpc.call client.Peer m p))

let private value (result: Result<'R, CallError<'E>>) : 'R =
    match result with
    | Ok value -> value
    | Error e -> failtest (string e)

[<Tests>]
let tests =
    testList
        "Grants protocol"
        [
            test "the grants view: pending prompts, answers, the list, revoking" {
                let h = harness ()
                let client = connect h
                let id = pendingOf h (ask "call-1" shellExec "git status")
                let waiting = Eff.run h.Env (GrantsService.await h.Service id)

                let pending = value (call h client GrantsProtocol.pending ())
                Expect.equal (pending.Requests |> List.map _.Id) [ id ] "the prompt"

                let resolution =
                    value (
                        call
                            h
                            client
                            GrantsProtocol.answer
                            {
                                Request = id
                                Answer =
                                    { forSession with
                                        Resource = Some "git *"
                                    }
                            }
                    )

                Expect.equal (wait waiting) (Ok resolution) "the requester sees the answer"
                let listed = value (call h client GrantsProtocol.list { All = false })

                match listed.Grants with
                | [ entry ] ->
                    Expect.equal entry.Grant.By "user:alice" "by the connection's person"
                    Expect.equal entry.Grant.Scope (GrantScope.Session "s1") "the request's session"
                    Expect.equal entry.Grant.Resource "git *" "the pattern"

                    value (
                        call
                            h
                            client
                            GrantsProtocol.revoke
                            {
                                Grant = entry.Grant.Id
                                Reason = None
                            }
                    )
                | other -> failtest (string other)

                Expect.isEmpty (value (call h client GrantsProtocol.list { All = false })).Grants "revoked"
                Expect.equal (value (call h client GrantsProtocol.list { All = true })).Grants.Length 1 "kept"
            }

            test "typed errors reach the client" {
                let h = harness ()
                let client = connect h

                match
                    call
                        h
                        client
                        GrantsProtocol.answer
                        {
                            Request = RequestId "nope"
                            Answer = once
                        }
                with
                | Error(CallError.Failed(GrantError.UnknownRequest(RequestId "nope"))) -> ()
                | other -> failtest (string other)

                match call h client GrantsProtocol.check (query (ActionName.create "x.y") "a") with
                | Error(CallError.Failed(GrantError.UnknownAction "x.y")) -> ()
                | other -> failtest (string other)
            }

            test "a connection that is not a person cannot answer, grant or revoke" {
                let h = harness ()
                let limits = ConnectionLimits.frames ConnectionLimits.defaults
                let clientSide, serverSide = InProcess.pair 65536 limits limits

                let caller: GrantsProtocol.Caller =
                    fun _ -> Error(GrantError.NotAPerson "anonymous")

                let server =
                    Service.create { Name = "host"; Version = "0" } (GrantsProtocol.handlers h.Service caller)

                let accepting = Eff.run h.Env (Rpc.accept server serverSide)

                let peer =
                    match
                        wait (
                            Eff.run
                                h.Env
                                (Rpc.connect (Service.create { Name = "tui"; Version = "0" } []) [] clientSide)
                        )
                    with
                    | Ok peer -> peer
                    | Error e -> failtest (string e)

                wait accepting |> ignore
                let id = pendingOf h (ask "call-1" shellExec "ls")

                match wait (Eff.run h.Env (Rpc.call peer GrantsProtocol.answer { Request = id; Answer = once })) with
                | Error(CallError.Failed(GrantError.NotAPerson "anonymous")) -> ()
                | other -> failtest (string other)

                Expect.equal (GrantsService.pending h.Service).Requests.Length 1 "still pending"
            }

            test "a client folds the state and the events it subscribes to into the host's state" {
                let h = harness ()
                let client = connect h
                let first = pendingOf h (ask "call-1" shellExec "ls")
                let start = value (call h client GrantsProtocol.state ())
                Expect.equal start.Seq 1L "one event so far"

                use cancel = new CancellationTokenSource()

                let subscribing =
                    Eff.run
                        { h.Env with
                            Scope = Scope.ofCancellation cancel.Token
                        }
                        (Rpc.call client.Peer GrantsProtocol.subscribe { After = start.Seq })

                value (call h client GrantsProtocol.answer { Request = first; Answer = forSession })
                |> ignore

                let second = pendingOf h (ask "call-2" fsWrite "src/a.fs")

                value (
                    call
                        h
                        client
                        GrantsProtocol.answer
                        {
                            Request = second
                            Answer = { once with Effect = GrantEffect.Deny }
                        }
                )
                |> ignore

                for _ in 1..3 do
                    if not (client.Arrived.Wait(TimeSpan.FromSeconds 30.0)) then
                        failtest "no notice"

                let notices = List.ofSeq client.Notices
                Expect.equal (notices |> List.map _.Seq) [ 2L; 3L; 4L ] "in order, after the state"
                let folded = notices |> List.fold (fun s n -> GrantLog.evolve s n.Event) start
                Expect.equal folded (GrantsService.snapshot h.Service).State "the same state"
                cancel.Cancel()

                cancelled (fun () -> wait subscribing |> ignore) "a subscription ends when the client cancels it"
            }
        ]
