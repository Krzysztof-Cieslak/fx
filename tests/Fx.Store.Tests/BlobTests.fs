module Fx.Store.Tests.BlobTests

open System.Text
open Expecto
open Fx.Effects
open Fx.Store
open Fx.Store.Fakes
open Fx.Store.Tests.Conformance
open Fx.Store.Tests.TestEnv

[<Tests>]
let tests =
    testList
        "Blobs"
        [
            BlobsConformance.tests "in memory" (fun () -> InMemoryBlobs().Blobs)

            test "effects reach the environment's store; chunks are written and committed" {
                let store = InMemoryBlobs()
                let env = { create () with Blobs = store.Blobs }
                let chunks = [ "a"; "bc"; ""; "def" ] |> List.map Encoding.UTF8.GetBytes
                let blob = runIn env (Blobs.putChunks chunks)
                Expect.equal blob.Hash (BlobHash.ofBytes (Encoding.UTF8.GetBytes "abcdef")) "hash"
                Expect.isTrue (runIn env (Blobs.exists blob.Hash)) "exists"
                Expect.equal (runIn env (Blobs.read blob.Hash 2L 2)) (Ok(Encoding.UTF8.GetBytes "cd")) "range"
                Expect.equal store.Count 1 "stored once"
            }

            test "a damaged blob is reported, not returned" {
                let store = InMemoryBlobs()

                let blob =
                    store.Blobs.Put [| 1uy; 2uy |] System.Threading.CancellationToken.None
                    |> _.Result

                store.Damage(blob.Hash, [| 9uy; 9uy |])

                Expect.equal
                    (store.Blobs.Get blob.Hash System.Threading.CancellationToken.None).Result
                    (Error(BlobError.Corrupt blob.Hash))
                    "corrupt"
            }
        ]
