module Fx.Store.Live.Tests.BlobStoreTests

open System
open System.IO
open System.Text
open Expecto
open Fx.Effects.Live
open Fx.Store
open Fx.Store.Live
open Fx.Store.Live.Tests.Support
open Fx.Store.Tests.Conformance

let private bytes (text: string) = Encoding.UTF8.GetBytes text

let private fileOf (store: FileBlobStore) (hash: BlobHash) =
    Path.Combine(store.Root, "sha256", hash.Hex.Substring(0, 2), hash.Hex.Substring 2)

let private temporaryFiles (store: FileBlobStore) =
    Directory.GetFiles(Path.Combine(store.Root, "tmp")) |> List.ofArray

[<Tests>]
let tests =
    testList
        "FileBlobStore"
        [
            BlobsConformance.tests "files" (fun () -> (newBlobs ()).Blobs)

            test "a blob is a file named by its hash, and the index lists it" {
                use store = newBlobs ()
                let blob = wait (store.Blobs.Put (bytes "abc") none)
                let file = fileOf store blob.Hash
                Expect.equal (Path.GetFileName file) (blob.Hash.Hex.Substring 2) "name"
                Expect.equal (File.ReadAllBytes file) (bytes "abc") "content"
                Expect.equal (store.Index()) [ blob ] "indexed"
                wait (store.Blobs.Put (bytes "abc") none) |> ignore
                Expect.equal (store.Index()) [ blob ] "once"
                Expect.isEmpty (temporaryFiles store) "no temporary files left"
            }

            test "an aborted writer leaves nothing; reopening removes what a crashed one left, and keeps the blobs" {
                let root = Path.Combine(Scratch.directory (), "blobs")
                let first = openBlobs root
                let kept = wait (first.Blobs.Put (bytes "kept") none)
                let writer = wait (first.Blobs.Create none)
                wait (writer.Write (bytes "unfinished") none)
                Expect.equal (temporaryFiles first).Length 1 "a temporary file while writing"
                wait (writer.Abort())
                Expect.isEmpty (temporaryFiles first) "removed by abort"
                // What a writer leaves when its process dies before it commits.
                File.WriteAllBytes(Path.Combine(root, "tmp", "0123456789abcdef.part"), bytes "left behind")
                first.Dispose()

                use second = openBlobs root
                Expect.isEmpty (temporaryFiles second) "removed on open"
                Expect.equal (wait (second.Blobs.Get kept.Hash none)) (Ok(bytes "kept")) "kept"
            }

            test "a damaged file is reported, and putting the content again repairs it" {
                use store = newBlobs ()
                let blob = wait (store.Blobs.Put (bytes "precious") none)
                let file = fileOf store blob.Hash
                File.WriteAllBytes(file, bytes "PRECIOUS")
                Expect.equal (wait (store.Blobs.Get blob.Hash none)) (Error(BlobError.Corrupt blob.Hash)) "same size"
                File.WriteAllBytes(file, bytes "trunc")
                Expect.equal (wait (store.Blobs.Get blob.Hash none)) (Error(BlobError.Corrupt blob.Hash)) "truncated"
                Expect.equal (wait (store.Blobs.Put (bytes "precious") none)) blob "put again"
                Expect.equal (wait (store.Blobs.Get blob.Hash none)) (Ok(bytes "precious")) "repaired"
            }

            test "a file that went missing is not found, and putting the content again restores it" {
                use store = newBlobs ()
                let blob = wait (store.Blobs.Put (bytes "lost") none)
                File.Delete(fileOf store blob.Hash)
                Expect.equal (wait (store.Blobs.Get blob.Hash none)) (Error(BlobError.NotFound blob.Hash)) "not found"
                Expect.equal (wait (store.Blobs.Stat blob.Hash none)) None "no stat"
                wait (store.Blobs.Put (bytes "lost") none) |> ignore
                Expect.equal (wait (store.Blobs.Get blob.Hash none)) (Ok(bytes "lost")) "restored"
                Expect.equal (store.Index()) [ blob ] "indexed once"
            }

            test "blob paths longer than Windows' 260 characters work" {
                // Between 200 and 240 characters: the index's path stays under 260, which SQLite needs on
                // Windows, while blob paths (75 more) go over it.
                let mutable root = Scratch.directory ()

                while root.Length < 200 do
                    root <- Path.Combine(root, String('d', 40))

                use store = openBlobs root
                let data = Array.init 100_000 byte
                let blob = wait (store.Blobs.Put data none)
                Expect.isGreaterThan (fileOf store blob.Hash).Length 260 "a long path"
                Expect.equal (wait (store.Blobs.Get blob.Hash none)) (Ok data) "get"
                Expect.equal (wait (store.Blobs.Read blob.Hash 99_990L 100 none)) (Ok data[99_990..]) "read"
                Expect.equal (wait (store.Blobs.Stat blob.Hash none)) (Some blob) "stat"
            }

            test "a relative root is refused" {
                match
                    FileBlobStore.openAt "blobs" SystemClock.clock (SystemIds.create SystemClock.clock) Durability.Full
                with
                | Error(OpenError.InvalidPath("blobs", "not absolute")) -> ()
                | Error other -> failtest (string other)
                | Ok store ->
                    store.Dispose()
                    failtest "opened"
            }
        ]
