/// The host-local store on the durable event store (SQLite): the store conformance suite, and a
/// grants log that survives the host stopping (spec §5: durable pending requests).
module Fx.Grants.Host.Tests.SqliteTests

open System
open System.Collections.Concurrent
open System.IO
open Expecto
open Fx.Effects
open Fx.Store
open Fx.Store.Live
open Fx.Sdk
open Fx.Grants
open Fx.Grants.Host
open Fx.Grants.Host.Tests.Support

let private root =
    Path.Combine(Path.GetTempPath(), "grants-tests-" + Guid.NewGuid().ToString("N"))

let private opened = ConcurrentStack<IDisposable>()

do
    AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
        let mutable store = Unchecked.defaultof<IDisposable>

        while opened.TryPop(&store) do
            try
                store.Dispose()
            with _ ->
                ()

        try
            Directory.Delete(root, true)
        with _ ->
            ())

let private openStore (path: string) : SqliteEventStore =
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore

    match SqliteEventStore.openFile path EventStoreOptions.defaults with
    | Ok store -> store
    | Error e -> failtest (string e)

let private newPath () =
    Path.Combine(root, Guid.NewGuid().ToString("N") + ".db")

let private fresh () : GrantStore =
    let store = openStore (newPath ())
    opened.Push store
    GrantStoreLog.hostLocal store.Log

[<Tests>]
let tests =
    testList
        "Grants on SQLite"
        [
            GrantStoreConformance.tests "host-local on SQLite" fresh

            test "grants and pending requests survive the host stopping" {
                let path = newPath ()
                let fakes = fakes ()
                let first = openStore path

                let service =
                    runIn fakes.Env (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal first.Log)))

                let request (a: Fx.Grants.GrantAsk) =
                    match runIn fakes.Env (GrantsService.request service a) with
                    | Ok(AskOutcome.Pending id) -> id
                    | other -> failtest (string other)

                let answered = request (ask "call-1" shellExec "git status")

                ok (runIn fakes.Env (GrantsService.answer service alice answered forSession))
                |> ignore

                let waiting = request (ask "call-2" fsWrite "src/a.fs")
                let before = GrantsService.snapshot service
                runIn fakes.Env (GrantsService.stop service)
                first.Dispose()

                let second = openStore path
                opened.Push second

                let restarted =
                    runIn fakes.Env (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal second.Log)))

                Expect.equal (GrantsService.snapshot restarted) before "the same state"
                Expect.equal ((GrantsService.pending restarted).Requests |> List.map _.Id) [ waiting ] "still pending"

                match runIn fakes.Env (GrantsService.check restarted (query shellExec "git status")) with
                | Ok(GrantCheck.Allowed _) -> ()
                | other -> failtest (string other)

                let awaiting = Eff.run fakes.Env (GrantsService.await restarted waiting)
                ok (runIn fakes.Env (GrantsService.answer restarted bob waiting once)) |> ignore

                match wait awaiting with
                | Ok(Resolution.Allowed _) -> ()
                | other -> failtest (string other)
            }
        ]
