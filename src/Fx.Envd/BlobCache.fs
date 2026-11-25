namespace Fx.Envd

open Fx.Store

/// The host's cache of an environment's blobs (spec §6.6: values live in envd, content-addressed,
/// lazily fetched and cached by the host). Content is named by its hash, so a cached copy is never
/// stale: reads go to the local store first and fetch a missing blob once.
module BlobCache =
    /// `remote` (e.g. `EnvClient.Blobs`) with `local` (e.g. the host's `FileBlobStore`) as a cache.
    /// Writes go to the environment (and are cached); whole reads of a missing blob are fetched,
    /// checked by the remote store, and kept.
    let over (local: Blobs) (remote: Blobs) : Blobs =
        {
            Put =
                fun content cancellation ->
                    task {
                        let! blob = remote.Put content cancellation
                        let! _ = local.Put content cancellation
                        return blob
                    }
            Get =
                fun hash cancellation ->
                    task {
                        match! local.Get hash cancellation with
                        | Ok content -> return Ok content
                        | Error _ ->
                            match! remote.Get hash cancellation with
                            | Error e -> return Error e
                            | Ok content ->
                                let! _ = local.Put content cancellation
                                return Ok content
                    }
            Read =
                fun hash offset count cancellation ->
                    Blobs.checkRange offset count

                    task {
                        match! local.Stat hash cancellation with
                        | Some _ -> return! local.Read hash offset count cancellation
                        | None -> return! remote.Read hash offset count cancellation
                    }
            Stat =
                fun hash cancellation ->
                    task {
                        match! local.Stat hash cancellation with
                        | Some blob -> return Some blob
                        | None -> return! remote.Stat hash cancellation
                    }
            Create = remote.Create
        }
