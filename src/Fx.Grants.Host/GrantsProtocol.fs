namespace Fx.Grants.Host

open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Rpc
open Fx.Grants

/// The grants view's client protocol (spec §17 `grants/*`, §19.4): each method defined once with its
/// codecs, for the host server (T129) to serve and clients to call. Answering, granting and revoking
/// are a person's: the server says who is on the other end of a connection (`Caller`).
[<RequireQualifiedAccess>]
module GrantsProtocol =
    /// What the effective rules say about a query (a client's "would this be allowed?").
    let check: Method<GrantQuery, GrantCheck, GrantError> =
        Method.define "grants/check" Codecs.query Codecs.check
        |> Method.withErrors Codecs.error

    /// Grants with their status: those in force, or all of them.
    let list =
        Method.define "grants/list" ProtocolCodecs.listParams ProtocolCodecs.grantList

    /// The pending requests, oldest first: the prompts to show.
    let pending = Method.define "grants/pending" Codec.unit ProtocolCodecs.pendingList

    /// The whole state, to fold `grants/event` notices onto (spec §5: snapshot + tail).
    let state = Method.define "grants/state" Codec.unit Codecs.state

    /// A person answers a pending request: once, session, repo or host (spec §19.4).
    let answer: Method<AnswerParams, Resolution, GrantError> =
        Method.define "grants/answer" ProtocolCodecs.answerParams Codecs.resolution
        |> Method.withErrors Codecs.error

    /// A person grants directly.
    let grant: Method<GrantDraft, Grant, GrantError> =
        Method.define "grants/grant" ProtocolCodecs.draft Codecs.grant
        |> Method.withErrors Codecs.error

    /// A person revokes a grant.
    let revoke: Method<RevokeParams, unit, GrantError> =
        Method.define "grants/revoke" ProtocolCodecs.revokeParams Codec.unit
        |> Method.withErrors Codecs.error

    /// Follows the log after a sequence number: `grants/event` notices, in order, until the request is
    /// cancelled or the connection ends. Its result is the last sequence number sent.
    let subscribe =
        Method.define "grants/subscribe" ProtocolCodecs.subscribeParams (Codec.int64 |> Codec.atLeast 0L)

    /// One event of the grants log, sent while a `grants/subscribe` request runs.
    let event = Notification.define "grants/event" ProtocolCodecs.notice

    /// Who is on the other end of a connection, as the host server knows it (T122's principals): the
    /// attribution of what they do, or why they cannot answer, grant or revoke.
    type Caller = RequestContext -> Result<Attribution, GrantError>

    /// The handlers of every method, over a grants service. Effects run in the service's environment
    /// with the request's cancellation.
    let handlers<'R, 'S when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R> and 'S :> IScope>
        (service: GrantsService<'R>)
        (caller: Caller)
        : Handler<'S> list =
        let person
            (context: RequestContext)
            (act: Attribution -> Eff<'R, Result<'T, GrantError>>)
            : Eff<'S, Result<'T, GrantError>> =
            match caller context with
            | Error e -> Eff.ret (Error e)
            | Ok by -> GrantsService.within service (act by)

        [
            Handler.request check (fun _ query -> GrantsService.within service (GrantsService.check service query))
            Handler.request list (fun _ p -> Eff.ret (Ok(GrantsService.list service p.All)))
            Handler.request pending (fun _ () -> Eff.ret (Ok(GrantsService.pending service)))
            Handler.request state (fun _ () -> Eff.ret (Ok (GrantsService.snapshot service).State))
            Handler.request answer (fun context p ->
                person context (fun by -> GrantsService.answer service by p.Request p.Answer))
            Handler.request grant (fun context draft -> person context (fun by -> GrantsService.grant service by draft))
            Handler.request revoke (fun context p ->
                person context (fun by -> GrantsService.revoke service by p.Grant p.Reason))
            Handler.request subscribe (fun context p ->
                GrantsService.within
                    service
                    (GrantsService.subscribe service p.After (fun notice ->
                        Rpc.notify context.Peer event notice |> Eff.map Result.isOk)
                     |> Eff.map Ok))
        ]
