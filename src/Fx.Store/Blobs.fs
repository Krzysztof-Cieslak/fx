namespace Fx.Store

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// The SHA-256 of a blob's content: its name in every blob store, so equal content is stored once
/// and a reference can be checked against what it names. Written `sha256:<64 lowercase hex digits>`.
[<Struct>]
type BlobHash =
    private
    | BlobHash of hex: string

    /// The digest as 64 lowercase hex digits.
    member this.Hex =
        let (BlobHash hex) = this
        hex

    override this.ToString() = "sha256:" + this.Hex

module BlobHash =
    [<Literal>]
    let private Prefix = "sha256:"

    /// The hash of a SHA-256 digest (32 bytes), e.g. from an incremental hash.
    let ofDigest (digest: byte array) : BlobHash =
        if digest.Length <> 32 then
            invalidArg (nameof digest) $"A SHA-256 digest has 32 bytes, not {digest.Length}."

        BlobHash(Convert.ToHexStringLower digest)

    /// The hash of some content.
    let ofBytes (content: byte array) : BlobHash = ofDigest (SHA256.HashData content)

    let private isHex (c: char) =
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

    /// Reads `sha256:<hex>` (hex digits in either case).
    let tryParse (text: string) : BlobHash option =
        if
            text.Length = Prefix.Length + 64
            && text.StartsWith(Prefix, StringComparison.Ordinal)
        then
            let hex = text.Substring(Prefix.Length).ToLowerInvariant()

            if hex |> Seq.forall isHex then Some(BlobHash hex) else None
        else
            None

/// A stored blob: its hash and its size in bytes. Values that are too large to copy travel as
/// references (spec §6.6), and event payloads refer to blobs by them (spec §5).
type BlobRef =
    {
        Hash: BlobHash
        Size: int64
    }

    override this.ToString() =
        string this.Hash + " (" + string this.Size + " bytes)"

/// An expected failure of a blob store. Disk errors are defects (exceptions).
[<RequireQualifiedAccess>]
type BlobError =
    /// The store has no blob with this hash.
    | NotFound of BlobHash
    /// The stored content no longer matches its hash: it was damaged after it was written.
    | Corrupt of BlobHash

    override this.ToString() =
        match this with
        | NotFound hash -> "no blob " + string hash
        | Corrupt hash -> "blob " + string hash + " is damaged: its content does not match its hash"

/// A blob being written in chunks, for content too large to hold in memory at once. Like a
/// process (`Proc`), a record of functions: each call is data in and data out.
type BlobWriter =
    {
        /// Appends a chunk.
        Write: byte array -> CancellationToken -> Task<unit>
        /// Stores what was written (unless the store has that content already) and returns its
        /// reference. The writer is finished afterwards.
        Commit: CancellationToken -> Task<BlobRef>
        /// Discards what was written. Does nothing once the writer is finished.
        Abort: unit -> Task<unit>
    }

    override _.ToString() = "BlobWriter"

/// A content-addressed blob store (spec §3, §5, §6.6): immutable content named by its SHA-256.
/// Storing content twice stores it once.
///
/// Interpreters: `InMemoryBlobs` (Fx.Store.Fakes) and `FileBlobStore` (Fx.Store.Live, files plus a
/// SQLite index). Both pass `BlobsConformance` (tests/Fx.Store.Tests).
type Blobs =
    {
        /// Stores content and returns its reference.
        Put: byte array -> CancellationToken -> Task<BlobRef>
        /// The whole content, checked against its hash. Use `Read` for blobs too large for one array.
        Get: BlobHash -> CancellationToken -> Task<Result<byte array, BlobError>>
        /// At most `count` bytes from an offset: fewer at the end, none past it. Not checked against
        /// the hash (that needs the whole content).
        Read: BlobHash -> int64 -> int -> CancellationToken -> Task<Result<byte array, BlobError>>
        /// The blob's reference, if the store has it.
        Stat: BlobHash -> CancellationToken -> Task<BlobRef option>
        /// Starts writing a blob in chunks.
        Create: CancellationToken -> Task<BlobWriter>
    }

    override _.ToString() = "Blobs"

/// An environment with a blob store.
type IBlobs =
    abstract Blobs: Blobs

module Blobs =
    let put<'R when 'R :> IBlobs and 'R :> IScope> (content: byte array) : Eff<'R, BlobRef> =
        fun env -> env.Blobs.Put content env.Scope.Cancellation

    let get<'R when 'R :> IBlobs and 'R :> IScope> (hash: BlobHash) : Eff<'R, Result<byte array, BlobError>> =
        fun env -> env.Blobs.Get hash env.Scope.Cancellation

    let read<'R when 'R :> IBlobs and 'R :> IScope>
        (hash: BlobHash)
        (offset: int64)
        (count: int)
        : Eff<'R, Result<byte array, BlobError>> =
        fun env -> env.Blobs.Read hash offset count env.Scope.Cancellation

    let stat<'R when 'R :> IBlobs and 'R :> IScope> (hash: BlobHash) : Eff<'R, BlobRef option> =
        fun env -> env.Blobs.Stat hash env.Scope.Cancellation

    /// Whether the store has the blob.
    let exists (hash: BlobHash) : Eff<'R, bool> = stat hash |> Eff.map Option.isSome

    let create<'R when 'R :> IBlobs and 'R :> IScope> : Eff<'R, BlobWriter> =
        fun env -> env.Blobs.Create env.Scope.Cancellation

    let write<'R when 'R :> IScope> (writer: BlobWriter) (chunk: byte array) : Eff<'R, unit> =
        fun env -> writer.Write chunk env.Scope.Cancellation

    let commit<'R when 'R :> IScope> (writer: BlobWriter) : Eff<'R, BlobRef> =
        fun env -> writer.Commit env.Scope.Cancellation

    let abort<'R> (writer: BlobWriter) : Eff<'R, unit> = fun _ -> writer.Abort()

    /// Stores content written in chunks: aborts the writer if a chunk fails.
    let putChunks<'R when 'R :> IBlobs and 'R :> IScopeWith<'R>> (chunks: byte array seq) : Eff<'R, BlobRef> =
        Eff.bracket create abort (fun writer ->
            eff {
                for chunk in chunks do
                    do! write writer chunk

                return! commit writer
            })

    /// Checks the arguments of a ranged read; interpreters call it first.
    let checkRange (offset: int64) (count: int) =
        if offset < 0L then
            invalidArg (nameof offset) "The offset must not be negative."

        if count < 0 then
            invalidArg (nameof count) "The count must not be negative."
