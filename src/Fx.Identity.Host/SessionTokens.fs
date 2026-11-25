namespace Fx.Identity.Host

open System
open System.Buffers.Text
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Variants
open Fx.Identity

/// A client's session on the host: what a session token stands for (spec §3, §17). It holds no
/// secret: the token itself is only ever in the client's hands, and the host keeps its hash.
type ClientSession =
    {
        /// The token's public id: names it in lists and revocations, never authenticates.
        Id: string
        Principal: Principal
        /// What the client may do, from the provider when the token was issued.
        Roles: Role list
        /// The client, as events attribute its inputs (`Attribution.Via`): the same after reconnects.
        Client: string
        /// Microseconds since the Unix epoch, UTC.
        Issued: int64
        Expires: int64
    }

    override this.ToString() =
        let roles = this.Roles |> List.map string |> String.concat ", "
        $"{this.Id}: {this.Principal} via {this.Client} [{roles}]"

module ClientSession =
    /// The attribution of the client's inputs (spec §5: serialized, attributed inputs).
    let attribution (session: ClientSession) : Attribution =
        Attribution.create session.Principal.Id.Value session.Client

    /// What the `initialize` result tells the client, with the token if it was just issued.
    let info (token: string option) (session: ClientSession) : SessionInfo =
        {
            Id = session.Id
            Token = token
            Principal = session.Principal
            Roles = session.Roles
            Client = session.Client
            Expires = session.Expires
        }

/// Why a session token was not accepted.
[<RequireQualifiedAccess>]
type TokenError =
    /// No live token has this value or id: never issued, or forgotten after it expired.
    | Unknown
    | Expired
    | Revoked

    override this.ToString() =
        match this with
        | Unknown -> "unknown"
        | Expired -> "expired"
        | Revoked -> "revoked"

type TokenEntry =
    {
        Session: ClientSession
        Revoked: bool
    }

    override this.ToString() =
        string this.Session + (if this.Revoked then " (revoked)" else "")

/// The host's session tokens: entries by the SHA-256 of their token (hex), and the hash by id. Pure.
/// Revoked entries stay until they expire, so a revoked token reads `Revoked` rather than `Unknown`.
type TokenTable =
    {
        ByHash: Map<string, TokenEntry>
        HashOf: Map<string, string>
    }

    override this.ToString() = $"{this.ByHash.Count} tokens"

module TokenTable =
    let empty: TokenTable =
        {
            ByHash = Map.empty
            HashOf = Map.empty
        }

    /// What the host keeps instead of a token: its SHA-256, in lowercase hex. A token is 256 random
    /// bits, so a fast hash is enough: there is nothing to guess from it.
    let hash (token: string) : string =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes token))

    let add (tokenHash: string) (session: ClientSession) (table: TokenTable) : TokenTable =
        {
            ByHash = table.ByHash.Add(tokenHash, { Session = session; Revoked = false })
            HashOf = table.HashOf.Add(session.Id, tokenHash)
        }

    let private live (now: int64) (entry: TokenEntry) : Result<ClientSession, TokenError> =
        if entry.Revoked then
            Error TokenError.Revoked
        elif now >= entry.Session.Expires then
            Error TokenError.Expired
        else
            Ok entry.Session

    /// The session of a token's hash, if it is live at `now`.
    let find (tokenHash: string) (now: int64) (table: TokenTable) : Result<ClientSession, TokenError> =
        match table.ByHash.TryFind tokenHash with
        | None -> Error TokenError.Unknown
        | Some entry -> live now entry

    /// The session of a token's id, if it is live at `now`.
    let byId (id: string) (now: int64) (table: TokenTable) : Result<ClientSession, TokenError> =
        match table.HashOf.TryFind id with
        | None -> Error TokenError.Unknown
        | Some tokenHash -> find tokenHash now table

    /// Revokes a token by id; false if it is unknown or revoked already.
    let revoke (id: string) (table: TokenTable) : TokenTable * bool =
        match table.HashOf.TryFind id |> Option.bind table.ByHash.TryFind with
        | Some entry when not entry.Revoked ->
            { table with
                ByHash = table.ByHash.Add(table.HashOf[id], { entry with Revoked = true })
            },
            true
        | _ -> table, false

    /// The ids of a principal's tokens that are not revoked.
    let idsOf (principal: PrincipalId) (table: TokenTable) : string list =
        table.ByHash
        |> Map.toList
        |> List.choose (fun (_, entry) ->
            if entry.Session.Principal.Id = principal && not entry.Revoked then
                Some entry.Session.Id
            else
                None)
        |> List.sort

    /// Forgets tokens that expired before `now`.
    let prune (now: int64) (table: TokenTable) : TokenTable =
        let expired =
            table.ByHash |> Map.filter (fun _ entry -> now >= entry.Session.Expires)

        if expired.IsEmpty then
            table
        else
            {
                ByHash = table.ByHash |> Map.filter (fun hash _ -> not (expired.ContainsKey hash))
                HashOf =
                    expired
                    |> Map.fold (fun (ids: Map<string, string>) _ entry -> ids.Remove entry.Session.Id) table.HashOf
            }

    /// The live sessions at `now`, by id.
    let active (now: int64) (table: TokenTable) : ClientSession list =
        table.ByHash
        |> Map.toList
        |> List.choose (fun (_, entry) ->
            match live now entry with
            | Ok session -> Some session
            | Error _ -> None)
        |> List.sortBy _.Id

/// The identity's variant slots (spec §15). Core slots defined next to the code that reads them: the
/// host registers `IdentitySlots.all` with `Slots.all`.
[<RequireQualifiedAccess>]
module IdentitySlots =
    /// `identity.sessionTokenTtl`: minutes a session token lets a client connect (privileged: a
    /// project must not lengthen it).
    let sessionTokenTtl: Slot<int> =
        Slot.create
            "identity.sessionTokenTtl"
            "Minutes a session token lets a client reconnect, from when the host issued it (1 to 43200)."
            (Codec.int |> Codec.between 1 43200)
            720
        |> Slot.privileged

    /// The identity's slots, for the host's registry.
    let all: SlotInfo list = [ Slot.info sessionTokenTtl ]

/// The host's session tokens (spec §3: principals and session tokens are core): issued after an
/// identity provider vouched for a client, presented on reconnects (in `initialize`, or as the local
/// listener's bearer token), revoked by id or principal. A token is 256 random bits from `IIds`,
/// base64url; the host keeps its SHA-256 only. Tokens live as long as the host process: a restarted
/// host has none, and clients sign in again. Thread-safe.
[<Sealed>]
type SessionTokens() =
    let gate = obj ()
    let mutable table = TokenTable.empty
    // Cancelled when the session's token is revoked: live connections of the session end with it.
    let revocations =
        Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal)

    member internal _.Update(change: TokenTable -> TokenTable * 'T) : 'T =
        lock gate (fun () ->
            let next, result = change table
            table <- next
            result)

    /// The table now: hashes and sessions, never tokens.
    member _.Table: TokenTable = lock gate (fun () -> table)

    member internal _.Issued(id: string) : unit =
        lock gate (fun () -> revocations[id] <- new CancellationTokenSource())

    member internal _.Cancel(ids: string list) : unit =
        let sources =
            lock gate (fun () ->
                [
                    for id in ids do
                        match revocations.TryGetValue id with
                        | true, source -> source
                        | _ -> ()
                ])

        for source in sources do
            source.Cancel()

    member internal _.Forget(ids: string list) : unit =
        let sources =
            lock gate (fun () ->
                [
                    for id in ids do
                        match revocations.TryGetValue id with
                        | true, source ->
                            revocations.Remove id |> ignore
                            source
                        | _ -> ()
                ])

        for source in sources do
            source.Dispose()

    /// Cancelled when the session's token is revoked (never for an unknown id).
    member _.Revocation(id: string) : CancellationToken =
        lock gate (fun () ->
            match revocations.TryGetValue id with
            | true, source -> source.Token
            | _ -> CancellationToken.None)

    override this.ToString() = $"SessionTokens({this.Table})"

module SessionTokens =
    /// Random bytes in a token: 256 bits.
    [<Literal>]
    let TokenBytes = 32

    let private now (clock: Clock) = clock.Now().UnixMicroseconds

    /// Issues a session token for a principal's client: returns the token (give it to the client
    /// once; the host keeps only its hash) and the session. The lifetime is
    /// `IdentitySlots.sessionTokenTtl`. Expired tokens are forgotten on the way.
    let issue<'R when 'R :> IClock and 'R :> IIds and 'R :> IVariants>
        (tokens: SessionTokens)
        (principal: Principal)
        (roles: Role list)
        (client: string)
        : Eff<'R, string * ClientSession> =
        eff {
            let! ttl = Variants.get IdentitySlots.sessionTokenTtl
            let! at = Clock.now
            let! id = Ids.newId
            let! secret = Ids.randomBytes TokenBytes
            let token = Base64Url.EncodeToString(ReadOnlySpan secret)
            let issued = at.UnixMicroseconds

            let session =
                {
                    Id = string id
                    Principal = principal
                    Roles = roles
                    Client = client
                    Issued = issued
                    Expires = issued + int64 ttl * 60L * 1_000_000L
                }

            let forgotten =
                tokens.Update(fun table ->
                    let pruned = TokenTable.prune issued table

                    let gone =
                        table.HashOf
                        |> Map.toList
                        |> List.filter (fun (_, hash) -> not (pruned.ByHash.ContainsKey hash))
                        |> List.map fst

                    TokenTable.add (TokenTable.hash token) session pruned, gone)

            tokens.Forget forgotten
            tokens.Issued session.Id
            return token, session
        }

    /// The session of a token a client presents, if it is live now.
    let verify<'R when 'R :> IClock>
        (tokens: SessionTokens)
        (token: string)
        : Eff<'R, Result<ClientSession, TokenError>> =
        eff {
            let! at = Clock.now
            return TokenTable.find (TokenTable.hash token) at.UnixMicroseconds tokens.Table
        }

    /// The session of a token by its id (e.g. one the local listener verified), if it is live now.
    let find<'R when 'R :> IClock> (tokens: SessionTokens) (id: string) : Eff<'R, Result<ClientSession, TokenError>> =
        eff {
            let! at = Clock.now
            return TokenTable.byId id at.UnixMicroseconds tokens.Table
        }

    /// Whether a session's token has been revoked. Live connections check it: unlike expiry, which only
    /// stops new connections, revoking a token cuts off the connections it opened.
    let isRevoked (tokens: SessionTokens) (id: string) : bool =
        let table = tokens.Table

        match table.HashOf.TryFind id |> Option.bind table.ByHash.TryFind with
        | Some entry -> entry.Revoked
        | None -> false

    /// Revokes a token by id; false if it is unknown or revoked already. Connections that use it end
    /// (`SessionTokens.Revocation`) and are refused from then on.
    let revoke<'R> (tokens: SessionTokens) (id: string) : Eff<'R, bool> =
        fun _ ->
            let revoked = tokens.Update(TokenTable.revoke id)

            if revoked then
                tokens.Cancel [ id ]

            Threading.Tasks.Task.FromResult revoked

    /// Revokes every token of a principal ("sign out everywhere"); returns their ids.
    let revokePrincipal<'R> (tokens: SessionTokens) (principal: PrincipalId) : Eff<'R, string list> =
        fun _ ->
            let ids =
                tokens.Update(fun table ->
                    let ids = TokenTable.idsOf principal table

                    let revoked = ids |> List.fold (fun t id -> fst (TokenTable.revoke id t)) table

                    revoked, ids)

            tokens.Cancel ids
            Threading.Tasks.Task.FromResult ids

    /// The live sessions now, by id (for a list of connected clients).
    let active<'R when 'R :> IClock> (tokens: SessionTokens) : Eff<'R, ClientSession list> =
        eff {
            let! at = Clock.now
            return TokenTable.active at.UnixMicroseconds tokens.Table
        }

    /// The check the local listener makes on a bearer token (`LocalAccess.AccessPolicy.Sessions`):
    /// the id of the live session it is a token of. It reads the clock it is given.
    let lookup (tokens: SessionTokens) (clock: Clock) : string -> string option =
        fun token ->
            match TokenTable.find (TokenTable.hash token) (now clock) tokens.Table with
            | Ok session -> Some session.Id
            | Error _ -> None
