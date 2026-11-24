/// Temporary stores for the tests, the environment effects run in, and helpers.
module Fx.Store.Live.Tests.Support

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Live

let none = CancellationToken.None
let wait (task: Task<'T>) = task.GetAwaiter().GetResult()

/// Temporary directories under one root per test run, deleted when the process exits, with the stores
/// opened in them (closed first: Windows cannot delete open files). The conformance suites create
/// stores without a way to close them.
module Scratch =
    let root =
        Path.Combine(Path.GetTempPath(), "store-tests-" + Guid.NewGuid().ToString("N"))

    let private opened = ConcurrentStack<IDisposable>()

    let private cleanUp () =
        let mutable store = Unchecked.defaultof<IDisposable>

        while opened.TryPop(&store) do
            try
                store.Dispose()
            with _ ->
                ()

        try
            Directory.Delete(root, true)
        with _ ->
            ()

    do AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> cleanUp ())

    /// A new empty directory.
    let directory () =
        let dir = Path.Combine(root, Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        dir

    /// Closes the value when the test run ends.
    let track (value: 'T :> IDisposable) : 'T =
        opened.Push value
        value

let private value (what: string) (result: Result<'T, OpenError>) : 'T =
    match result with
    | Ok value -> value
    | Error error -> failtest $"{what}: {error}"

/// Opens (or creates) an event store in a file.
let openStore (path: string) : SqliteEventStore =
    SqliteEventStore.openFile path EventStoreOptions.defaults
    |> value $"open {path}"

/// A new event store in a new directory, closed at the end of the run.
let newStoreWith (options: EventStoreOptions) : SqliteEventStore =
    SqliteEventStore.openFile (Path.Combine(Scratch.directory (), "events.db")) options
    |> value "open a new store"
    |> Scratch.track

/// A new event store with the default options.
let newStore () : SqliteEventStore = newStoreWith EventStoreOptions.defaults

let private ids = SystemIds.create SystemClock.clock

/// Opens (or creates) a blob store in a directory.
let openBlobs (root: string) : FileBlobStore =
    FileBlobStore.openAt root SystemClock.clock ids Durability.Full
    |> value $"open {root}"

/// A new blob store in a new directory, closed at the end of the run.
let newBlobs () : FileBlobStore =
    Path.Combine(Scratch.directory (), "blobs") |> openBlobs |> Scratch.track

let strings = EventSchema.create Codec.string

let meta (key: string) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 1_764_000_000_000_000L
        By = Attribution.create "user:alice" "test"
        Key = IdempotencyKey key
        Cause = None
    }

/// Appends a commit that must succeed and returns the new head.
let appendOk (log: EventLog<'Evt>) (stream: StreamId) (expected: int64) (key: string) (events: 'Evt list) : int64 =
    match wait (log.Append stream expected (meta key) events none) with
    | Ok head -> head
    | Error error -> failtest $"append {key}: {error}"

/// Every event of a stream.
let readAll (log: EventLog<'Evt>) (stream: StreamId) : Recorded<'Evt> list =
    let all = ResizeArray()
    let mutable position = 0L
    let mutable reading = true

    while reading do
        match wait (log.Read stream position 500 none) with
        | [] -> reading <- false
        | page ->
            all.AddRange page
            position <- (List.last page).Seq

    List.ofSeq all

type Env =
    {
        Clock: Clock
        Blobs: Blobs
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IBlobs with
        member env.Blobs = env.Blobs

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

let env (blobs: Blobs) : Env =
    {
        Clock = SystemClock.clock
        Blobs = blobs
        Scope = Scope.root
    }

let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()
