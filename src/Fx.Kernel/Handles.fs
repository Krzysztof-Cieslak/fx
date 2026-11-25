namespace Fx.Kernel

open System.Collections.Concurrent
open Fx.Contracts
open Fx.Effects

/// A live object the host handed out by reference.
type HandleEntry<'T> =
    {
        Id: HandleId
        Type: TypeRef
        Preview: Preview
        Target: 'T
    }

    override this.ToString() = $"{this.Id} ({this.Type})"

/// Why a handle does not resolve.
[<RequireQualifiedAccess>]
type HandleError =
    /// This session never issued the id (forged, from another session, or released).
    | Unknown of HandleId
    /// The id names an object of another type than the one expected.
    | WrongType of handle: HandleId * expected: TypeRef * actual: TypeRef

    override this.ToString() =
        match this with
        | Unknown id -> $"unknown handle {id}"
        | WrongType(id, expected, actual) -> $"handle {id} is a {actual}, not a {expected}"

/// The handles of one session (spec §6.6, §18): host-resident objects that kernels hold by id.
/// Ids are 128 random bits from the `IIds` capability (cryptographically strong in the live
/// interpreter), registered here; only ids this table issued resolve, so handles are unforgeable and
/// session-scoped: give each session its own table. Thread-safe.
type HandleTable<'T>() =
    let entries = ConcurrentDictionary<HandleId, HandleEntry<'T>>()

    /// How many handles are live.
    member _.Count = entries.Count

    member internal _.Add(entry: HandleEntry<'T>) =
        if not (entries.TryAdd(entry.Id, entry)) then
            invalidOp "A handle id was issued twice."

    member _.TryFind(id: HandleId) : HandleEntry<'T> option =
        match entries.TryGetValue id with
        | true, entry -> Some entry
        | _ -> None

    member _.Remove(id: HandleId) : bool = entries.TryRemove(id) |> fst

    override _.ToString() = $"HandleTable({entries.Count} handles)"

module HandleTable =
    /// Random bytes per id: 128 bits, so ids cannot be guessed.
    [<Literal>]
    let IdBytes = 16

    /// Registers an object and returns the value that refers to it.
    let issue<'R, 'T when 'R :> IIds>
        (table: HandleTable<'T>)
        (typeRef: TypeRef)
        (preview: Preview)
        (target: 'T)
        : Eff<'R, Value> =
        eff {
            let! bytes = Ids.randomBytes IdBytes
            let id = HandleId.ofRandom bytes

            table.Add
                {
                    Id = id
                    Type = typeRef
                    Preview = preview
                    Target = target
                }

            return Value.Handle(id, typeRef, preview)
        }

    /// The object an id names, if this table issued it.
    let resolve (table: HandleTable<'T>) (id: HandleId) : Result<HandleEntry<'T>, HandleError> =
        match table.TryFind id with
        | Some entry -> Ok entry
        | None -> Error(HandleError.Unknown id)

    /// The object an id names, if this table issued it for an object of the expected type.
    let resolveAs (table: HandleTable<'T>) (expected: TypeRef) (id: HandleId) : Result<'T, HandleError> =
        match resolve table id with
        | Ok entry when entry.Type = expected -> Ok entry.Target
        | Ok entry -> Error(HandleError.WrongType(id, expected, entry.Type))
        | Error e -> Error e

    /// Forgets a handle; later uses of its id fail as unknown.
    let release (table: HandleTable<'T>) (id: HandleId) : bool = table.Remove id
