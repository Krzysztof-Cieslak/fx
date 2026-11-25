namespace Fx.Identity.Host

open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Variants
open Fx.Rpc
open Fx.Identity

/// One client connection's sign-in, set when its `initialize` succeeds. The host server (T129) makes
/// one per connection, hands its accept function to the connection's service, and asks it who is on
/// the other end when a request comes (`IdentityServer.caller`). Thread-safe.
[<Sealed>]
type ClientConnection() =
    let gate = obj ()
    let mutable session: ClientSession option = None

    /// The client's session, once `initialize` has succeeded.
    member _.Session: ClientSession option = lock gate (fun () -> session)

    member internal _.SignedIn(signedIn: ClientSession) =
        lock gate (fun () -> session <- Some signedIn)

    override this.ToString() =
        match this.Session with
        | Some s -> "ClientConnection(" + string s + ")"
        | None -> "ClientConnection(not signed in)"

/// The identity side of the client protocol's `initialize` (spec §17: `initialize` carries identity
/// and capability negotiation, then a session token): the client's `identity` member says how it
/// signs in (`Codecs.signIn`), the result's `session` member tells it who it is and, when issued, its
/// token (`Codecs.sessionInfo`). A refusal is an `ApplicationError` whose data is the
/// `IdentityFailure`; the client may retry until the handshake times out.
[<RequireQualifiedAccess>]
module IdentityServer =
    /// The refusal of an `initialize`: `ApplicationError` with the failure as data.
    let refusal (failure: IdentityFailure) : RpcError =
        RpcError.create Protocol.ErrorCode.ApplicationError ("identity: " + string failure)
        |> RpcError.withData (Codec.encode Codecs.identityFailure failure)

    /// How the client signs in, from its `initialize` params: `None` if it said nothing.
    let signInOf (members: Json) : Result<SignIn option, IdentityFailure> =
        match Json.tryField Members.Identity members with
        | None
        | Some Json.Null -> Ok None
        | Some json ->
            match Codecs.signIn.Decode json with
            | Ok signIn -> Ok(Some signIn)
            | Error issues ->
                Error(IdentityFailure.Invalid(issues |> List.map (Issue.under (PathSegment.Key Members.Identity))))

    /// The `Service.Accept` of one connection: signs the client in (`Authenticator.signIn`), remembers
    /// the session in `connection`, and adds the `session` member to the result.
    let accept<'R when 'R :> IClock and 'R :> IIds and 'R :> IVariants and 'R :> IScopeWith<'R>>
        (auth: Authenticator)
        (facts: ConnectionFacts)
        (connection: ClientConnection)
        : Remote -> Eff<'R, Result<(string * Json) list, RpcError>> =
        fun remote ->
            eff {
                match signInOf remote.Members with
                | Error failure -> return Error(refusal failure)
                | Ok request ->
                    match! Authenticator.signIn auth facts remote.Peer.Name request with
                    | Error failure -> return Error(refusal failure)
                    | Ok signedIn ->
                        connection.SignedIn signedIn.Session

                        return
                            Ok
                                [
                                    Members.Session,
                                    Codec.encode Codecs.sessionInfo (ClientSession.info signedIn.Token signedIn.Session)
                                ]
            }

    /// A service that signs in each client of a connection before anything else.
    let withIdentity
        (auth: Authenticator)
        (facts: ConnectionFacts)
        (connection: ClientConnection)
        (service: Service<'R>)
        : Service<'R> =
        Service.withAccept (accept auth facts connection) service

    /// Who is on a connection now: its session, unless its token has been revoked since.
    let session (auth: Authenticator) (connection: ClientConnection) : Result<ClientSession, IdentityFailure> =
        match connection.Session with
        | None -> Error IdentityFailure.Missing
        | Some session -> Authenticator.current auth session

    /// The attribution of a connection's inputs, if its client may do what needs `least`; the
    /// failure otherwise (`NoRole` when signed in without the role).
    let caller
        (auth: Authenticator)
        (least: Role)
        (connection: ClientConnection)
        : Result<Attribution, IdentityFailure> =
        session auth connection
        |> Result.bind (fun s ->
            if Role.allows least s.Roles then
                Ok(ClientSession.attribution s)
            else
                Error(IdentityFailure.NoRole s.Principal.Id.Value))

/// The client side of identity in `initialize`.
[<RequireQualifiedAccess>]
module IdentityClient =
    /// The members to add to `initialize` (`Rpc.connect service members transport`).
    let members (signIn: SignIn) : (string * Json) list =
        [ Members.Identity, Codec.encode Codecs.signIn signIn ]

    /// The session the server returned in the `initialize` result.
    let session (peer: Peer) : Result<SessionInfo, Issue list> =
        match Json.tryField Members.Session peer.Remote.Members with
        | None -> Error [ Issue.create "the initialize result has no session" ]
        | Some json ->
            Codecs.sessionInfo.Decode json
            |> Result.mapError (List.map (Issue.under (PathSegment.Key Members.Session)))

    /// Why the server refused the client's identity, if that is why a connection failed.
    let failure (error: ConnectError) : IdentityFailure option =
        match error with
        | ConnectError.Refused e when e.Code = Protocol.ErrorCode.ApplicationError ->
            e.Data
            |> Option.bind (fun data -> Codecs.identityFailure.Decode data |> Result.toOption)
        | _ -> None
