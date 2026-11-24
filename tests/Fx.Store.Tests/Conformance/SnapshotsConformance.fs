/// The snapshot-store conformance suite: what every `SnapshotStore` interpreter must do.
/// `InMemorySnapshots` passes it in Fx.Store.Tests and the SQLite store in Fx.Store.Live.Tests.
module Fx.Store.Tests.Conformance.SnapshotsConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Store

let private wait (task: Task<'T>) = task.GetAwaiter().GetResult()

/// Expects `f` to fail with a cancellation (of any subtype, e.g. `TaskCanceledException`).
let private throwsCancellation (f: unit -> unit) (message: string) =
    let cancelled =
        try
            f ()
            false
        with :? OperationCanceledException ->
            true

    Expect.isTrue cancelled message

let private none = CancellationToken.None
let private stream = StreamId "conformance/a"
let private other = StreamId "conformance/b"

let private state (text: string) =
    match Json.parse text with
    | Ok json -> Json.canonicalize json
    | Error error -> failtest (string error)

let private snapshot (seq: int64) (version: int) (text: string) : StoredSnapshot =
    {
        Seq = seq
        Version = version
        State = state text
    }

/// Tests that the stores made by `create` conform. `create` is called once per test and returns an
/// empty store.
let tests (name: string) (create: unit -> SnapshotStore) : Test =
    testList
        $"SnapshotStore conformance ({name})"
        [
            testCase "nothing is kept at first" (fun () ->
                let store = create ()
                Expect.equal (wait (store.Load stream "view" none)) None "none")

            testCase "a saved snapshot loads as it was saved" (fun () ->
                let store = create ()

                let saved =
                    snapshot 7L 2 """{"items":[1,2.5,"three",null,true],"name":"naïve 🎉","nested":{"a":{}}}"""

                wait (store.Save stream "view" saved none)
                Expect.equal (wait (store.Load stream "view" none)) (Some saved) "loaded")

            testCase
                "a later snapshot replaces an earlier one; an earlier one of the same version is ignored"
                (fun () ->
                    let store = create ()
                    wait (store.Save stream "view" (snapshot 5L 1 "[5]") none)
                    wait (store.Save stream "view" (snapshot 9L 1 "[9]") none)
                    Expect.equal (wait (store.Load stream "view" none)) (Some(snapshot 9L 1 "[9]")) "later"
                    wait (store.Save stream "view" (snapshot 6L 1 "[6]") none)
                    Expect.equal (wait (store.Load stream "view" none)) (Some(snapshot 9L 1 "[9]")) "not earlier"
                    wait (store.Save stream "view" (snapshot 9L 1 "[99]") none)
                    Expect.equal (wait (store.Load stream "view" none)) (Some(snapshot 9L 1 "[99]")) "same position")

            testCase "a snapshot of another version replaces the one kept, wherever it is" (fun () ->
                let store = create ()
                wait (store.Save stream "view" (snapshot 9L 1 "[1]") none)
                wait (store.Save stream "view" (snapshot 3L 2 "[2]") none)
                Expect.equal (wait (store.Load stream "view" none)) (Some(snapshot 3L 2 "[2]")) "new version"
                wait (store.Save stream "view" (snapshot 4L 1 "[1]") none)
                Expect.equal (wait (store.Load stream "view" none)) (Some(snapshot 4L 1 "[1]")) "and back")

            testCase "streams and names keep their snapshots apart" (fun () ->
                let store = create ()
                wait (store.Save stream "view" (snapshot 1L 1 "\"a view\"") none)
                wait (store.Save stream "state" (snapshot 2L 1 "\"a state\"") none)
                wait (store.Save other "view" (snapshot 3L 1 "\"b view\"") none)
                Expect.equal (wait (store.Load stream "view" none)) (Some(snapshot 1L 1 "\"a view\"")) "a view"
                Expect.equal (wait (store.Load stream "state" none)) (Some(snapshot 2L 1 "\"a state\"")) "a state"
                Expect.equal (wait (store.Load other "view" none)) (Some(snapshot 3L 1 "\"b view\"")) "b view"
                Expect.equal (wait (store.Load other "state" none)) None "b state")

            testCase "a cancelled call fails with a cancellation" (fun () ->
                let store = create ()
                use source = new CancellationTokenSource()
                source.Cancel()

                throwsCancellation (fun () -> wait (store.Save stream "view" (snapshot 1L 1 "1") source.Token)) "save"

                throwsCancellation (fun () -> wait (store.Load stream "view" source.Token) |> ignore) "load"

                Expect.equal (wait (store.Load stream "view" none)) None "nothing saved")
        ]
