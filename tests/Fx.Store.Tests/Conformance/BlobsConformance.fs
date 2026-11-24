/// The blob-store conformance suite: what every `Blobs` interpreter must do. `InMemoryBlobs` passes it
/// in Fx.Store.Tests and `FileBlobStore` in Fx.Store.Live.Tests, on Linux, macOS and Windows.
module Fx.Store.Tests.Conformance.BlobsConformance

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
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
let private bytes (text: string) = Encoding.UTF8.GetBytes text

let private content (seed: int) (length: int) : byte array =
    Array.init length (fun i -> byte ((i * 31 + seed * 7 + i / 251) % 256))

let private ok (what: string) (result: Result<'T, BlobError>) : 'T =
    match result with
    | Ok value -> value
    | Error error -> failtest $"{what}: {error}"

let private cancelled () =
    let source = new CancellationTokenSource()
    source.Cancel()
    source.Token

/// Tests that the stores made by `create` conform. `create` is called once per test and returns an
/// empty store.
let tests (name: string) (create: unit -> Blobs) : Test =
    testList
        $"Blobs conformance ({name})"
        [
            testCase "a blob is named by the SHA-256 of its content" (fun () ->
                let blobs = create ()
                let blob = wait (blobs.Put (bytes "abc") none)

                Expect.equal
                    (string blob.Hash)
                    "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                    "hash"

                Expect.equal blob.Size 3L "size"
                Expect.equal (wait (blobs.Get blob.Hash none) |> ok "get") (bytes "abc") "content"
                Expect.equal (wait (blobs.Stat blob.Hash none)) (Some blob) "stat")

            testCase "the empty blob" (fun () ->
                let blobs = create ()
                let blob = wait (blobs.Put [||] none)

                Expect.equal blob.Hash.Hex "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "hash"

                Expect.equal blob.Size 0L "size"
                Expect.equal (wait (blobs.Get blob.Hash none) |> ok "get") [||] "content"
                Expect.equal (wait (blobs.Read blob.Hash 0L 10 none) |> ok "read") [||] "read")

            testCase "storing content again gives the same reference" (fun () ->
                let blobs = create ()
                let data = content 1 10_000
                let first = wait (blobs.Put data none)
                let second = wait (blobs.Put (Array.copy data) none)
                Expect.equal second first "same reference"
                Expect.equal (wait (blobs.Get first.Hash none) |> ok "get") data "content")

            testCase "a blob the store does not have is not found" (fun () ->
                let blobs = create ()
                let missing = BlobHash.ofBytes (bytes "never stored")
                Expect.equal (wait (blobs.Get missing none)) (Error(BlobError.NotFound missing)) "get"
                Expect.equal (wait (blobs.Read missing 0L 1 none)) (Error(BlobError.NotFound missing)) "read"
                Expect.equal (wait (blobs.Stat missing none)) None "stat")

            testCase "ranges read what is there, and nothing past the end" (fun () ->
                let blobs = create ()
                let data = content 2 1000
                let blob = wait (blobs.Put data none)

                let range offset count =
                    wait (blobs.Read blob.Hash offset count none) |> ok $"read {offset} {count}"

                Expect.equal (range 0L 10) data[0..9] "start"
                Expect.equal (range 500L 100) data[500..599] "middle"
                Expect.equal (range 990L 100) data[990..] "cut at the end"
                Expect.equal (range 1000L 10) [||] "at the end"
                Expect.equal (range 5000L 10) [||] "past the end"
                Expect.equal (range 10L 0) [||] "nothing"
                Expect.equal (range 0L 1000) data "everything"

                Expect.throwsT<ArgumentException>
                    (fun () -> wait (blobs.Read blob.Hash -1L 1 none) |> ignore)
                    "negative offset"

                Expect.throwsT<ArgumentException>
                    (fun () -> wait (blobs.Read blob.Hash 0L -1 none) |> ignore)
                    "negative count")

            testCase "a blob written in chunks equals the blob of its content" (fun () ->
                let blobs = create ()
                let data = content 3 (3 * 65536 + 17)
                let writer = wait (blobs.Create none)

                for chunk in Array.chunkBySize 65536 data do
                    wait (writer.Write chunk none)

                wait (writer.Write [||] none)
                let blob = wait (writer.Commit none)
                Expect.equal blob.Hash (BlobHash.ofBytes data) "hash"
                Expect.equal blob.Size data.LongLength "size"
                Expect.equal (wait (blobs.Get blob.Hash none) |> ok "get") data "content"
                Expect.equal (wait (blobs.Put data none)) blob "the same as putting it"
                wait (writer.Abort())

                Expect.throwsT<ObjectDisposedException>
                    (fun () -> wait (writer.Write [| 1uy |] none))
                    "finished after commit")

            testCase "an aborted writer stores nothing" (fun () ->
                let blobs = create ()
                let data = content 4 5000
                let writer = wait (blobs.Create none)
                wait (writer.Write data none)
                wait (writer.Abort())
                wait (writer.Abort())
                Expect.equal (wait (blobs.Stat (BlobHash.ofBytes data) none)) None "not stored"

                Expect.throwsT<ObjectDisposedException>
                    (fun () -> wait (writer.Commit none) |> ignore)
                    "finished after abort")

            testCase "a large blob reads back in ranges" (fun () ->
                let blobs = create ()
                let data = content 5 (5 * 1024 * 1024 + 3)
                let writer = wait (blobs.Create none)

                for chunk in Array.chunkBySize 100_000 data do
                    wait (writer.Write chunk none)

                let blob = wait (writer.Commit none)
                let chunkSize = 1 <<< 20

                let read =
                    [|
                        for offset in 0L .. int64 chunkSize .. blob.Size - 1L do
                            yield! wait (blobs.Read blob.Hash offset chunkSize none) |> ok "read"
                    |]

                Expect.equal read.Length data.Length "length"
                Expect.isTrue (read = data) "content")

            testCase "concurrent puts of the same content store it once, intact" (fun () ->
                let blobs = create ()
                let data = content 6 200_000

                let refs =
                    Array.init 8 (fun _ -> Task.Run(fun () -> wait (blobs.Put (Array.copy data) none)))
                    |> Task.WhenAll
                    |> wait

                Expect.equal (Array.distinct refs).Length 1 "one reference"
                Expect.equal (wait (blobs.Get refs[0].Hash none) |> ok "get") data "content")

            testCase "a cancelled call fails with a cancellation" (fun () ->
                let blobs = create ()
                let token = cancelled ()

                throwsCancellation (fun () -> wait (blobs.Put [| 1uy |] token) |> ignore) "put"

                throwsCancellation (fun () -> wait (blobs.Get (BlobHash.ofBytes [| 1uy |]) token) |> ignore) "get"

                Expect.equal (wait (blobs.Stat (BlobHash.ofBytes [| 1uy |]) none)) None "nothing stored")
        ]
