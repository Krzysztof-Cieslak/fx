namespace Fx.Sdk

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Grants

/// The grants capability (spec §4.2 `IGrants`, §13): how agents, strategies and extensions ask for
/// permissions. It checks and requests, and never grants: only people grant, through the host's
/// client protocol (`grants/*`), so an environment holding this capability cannot authorize itself
/// (spec §13.2: agents can only request). The host builds it from the grants log
/// (`GrantsService.grants` in Fx.Grants.Host). Data in and out, the cancellation token last.
type Grants =
    {
        /// What the effective rules say about a query now. Consumes nothing and records nothing.
        Check: GrantQuery -> CancellationToken -> Task<Result<GrantCheck, GrantError>>
        /// Settles an ask from the rules if they allow or deny; otherwise records a durable pending
        /// request (spec §5) that a person answers. Asking again with the same key is the same request.
        Request: GrantAsk -> CancellationToken -> Task<Result<AskOutcome, GrantError>>
        /// Waits until a pending request is answered, covered by a later grant, refused, withdrawn or
        /// timed out. Cancelling the wait leaves the request pending.
        Await: RequestId -> CancellationToken -> Task<Result<Resolution, GrantError>>
        /// Withdraws a pending request the requester no longer needs, with a reason.
        Withdraw: RequestId -> string -> CancellationToken -> Task<Result<unit, GrantError>>
    }

    override _.ToString() = "Grants"

/// An environment that can ask for permissions.
type IGrants =
    abstract Grants: Grants

/// A durable log of grant events (spec §13.2 `GrantStore`, §16.2 persistence): where the grants log
/// lives. The host runs the grants machine on it as the single writer; the built-in store is
/// host-local, on the host's event store (`GrantStores.ofEventLog` in Fx.Grants.Host), and a
/// repository-policy store can implement the same contract. It is the machine host's event log for
/// one stream, in the SDK's types; a store passes `GrantStoreConformance` (tests/Fx.Grants.Host.Tests).
type GrantStore =
    {
        /// Names the store, e.g. `host`.
        Id: string
        /// Appends one commit (the events of one command, never empty) atomically if the head is
        /// `expected`, and returns the new head. A commit whose key the store holds is `Duplicate`
        /// (checked first); otherwise a wrong head is `Conflict`. A read-only store answers `ReadOnly`.
        Append: int64 -> GrantEventMeta -> GrantEvent list -> CancellationToken -> Task<Result<int64, GrantAppendError>>
        /// At most `max` events after a sequence number, in order (sequence numbers start at 1).
        Read: int64 -> int -> CancellationToken -> Task<StoredGrantEvent list>
        /// The last sequence number, 0 when empty.
        Head: CancellationToken -> Task<int64>
        /// Completes, with the head, once the head is past a sequence number.
        WaitPast: int64 -> CancellationToken -> Task<int64>
    }

    override this.ToString() = "GrantStore(" + this.Id + ")"

/// What a store keeps with each event of a commit: when the command was handled, who sent it and
/// through what, its idempotency key, and the event it reacted to (`stream#seq`), if any.
and GrantEventMeta =
    {
        /// Microseconds since the Unix epoch, UTC.
        At: int64
        Principal: string
        Via: string
        Key: string
        Cause: string option
    }

    override this.ToString() =
        this.Key + " by " + this.Principal + " via " + this.Via

/// An event in a grant store, with its position and metadata.
and StoredGrantEvent =
    {
        Seq: int64
        Event: GrantEvent
        Meta: GrantEventMeta
    }

    override this.ToString() =
        string this.Seq + " " + string this.Event + " (" + string this.Meta + ")"

/// Why a grant store did not append a commit.
and [<RequireQualifiedAccess>] GrantAppendError =
    /// The head was not the expected one: another writer appended first.
    | Conflict of expected: int64 * actual: int64
    /// The store holds a commit with this key: these sequence numbers. Nothing was appended.
    | Duplicate of key: string * after: int64 * count: int
    /// The store takes no appends (e.g. a policy read from a repository).
    | ReadOnly of reason: string

    override this.ToString() =
        match this with
        | Conflict(expected, actual) -> "conflict: expected head " + string expected + ", found " + string actual
        | Duplicate(key, after, count) -> "duplicate: " + key + " (" + string count + " after " + string after + ")"
        | ReadOnly reason -> "read-only: " + reason

/// Effects over the grants capability.
[<RequireQualifiedAccess>]
module Grants =
    let check<'R when 'R :> IGrants and 'R :> IScope> (query: GrantQuery) : Eff<'R, Result<GrantCheck, GrantError>> =
        fun env -> env.Grants.Check query env.Scope.Cancellation

    let request<'R when 'R :> IGrants and 'R :> IScope> (ask: GrantAsk) : Eff<'R, Result<AskOutcome, GrantError>> =
        fun env -> env.Grants.Request ask env.Scope.Cancellation

    let await<'R when 'R :> IGrants and 'R :> IScope> (request: RequestId) : Eff<'R, Result<Resolution, GrantError>> =
        fun env -> env.Grants.Await request env.Scope.Cancellation

    let withdraw<'R when 'R :> IGrants and 'R :> IScope>
        (request: RequestId)
        (reason: string)
        : Eff<'R, Result<unit, GrantError>> =
        fun env -> env.Grants.Withdraw request reason env.Scope.Cancellation

    /// Asks, and waits for a person if the rules say ask: the common case for a tool call. If the wait
    /// is cancelled (the turn was interrupted), the request is withdrawn, shielded from the
    /// cancellation, and the cancellation goes on.
    let authorize<'R when 'R :> IGrants and 'R :> IScopeWith<'R>>
        (ask: GrantAsk)
        : Eff<'R, Result<Resolution, GrantError>> =
        eff {
            match! request ask with
            | Error e -> return Error e
            | Ok(AskOutcome.Settled resolution) -> return Ok resolution
            | Ok(AskOutcome.Pending id) ->
                let! outcome = Eff.catch (await id)

                match outcome with
                | Ok resolution -> return resolution
                | Error(:? OperationCanceledException as e) ->
                    let! _ = Eff.shield (withdraw id "the requester stopped waiting")
                    return raise e
                | Error e -> return raise e
        }

    /// A check as a gate's decision (spec §16.3): allowed, ask a person, or denied.
    let decision (check: GrantCheck) : Decision =
        match check with
        | GrantCheck.Allowed _ -> Decision.Allow
        | GrantCheck.MustAsk reasons -> Decision.Ask(String.concat "; " reasons)
        | GrantCheck.Denied reasons -> Decision.Deny(String.concat "; " reasons)

    /// A gate backed by a grants capability: an intent that maps to a query is decided by the
    /// effective rules; one that maps to none is allowed (not this gate's business). A failed check
    /// denies. Combined with other gates by `Hooks.decide` (Deny > Ask > Allow), so other gates can
    /// only tighten it.
    let gate (grants: Grants) (toQuery: 'I -> GrantQuery option) : Gate<'I> =
        {
            Check =
                fun intent ->
                    match toQuery intent with
                    | None -> Eff.ret Decision.Allow
                    | Some query ->
                        fun env ->
                            task {
                                match! grants.Check query env.Scope.Cancellation with
                                | Ok check -> return decision check
                                | Error e -> return Decision.Deny(string e)
                            }
        }

/// The contribution types of grants (spec §16.2, §16.5), for the host's catalog.
[<RequireQualifiedAccess>]
module GrantStores =
    /// Where the grants log lives (privileged: only extensions from host config or policy, spec §16.5).
    /// Code, so in process only (native extensions).
    let contributionType: ContributionType<GrantStore> =
        ContributionType.inProcess
            "grant-store"
            "Keeps the grants log's events (spec §13.2 GrantStore)."
            ContributionKind.Code
        |> ContributionType.privileged

/// Actions extensions add to the grants vocabulary (spec §13: tool approvals, …).
[<RequireQualifiedAccess>]
module GrantActions =
    /// Actions, as data. Their names must not clash with the core's or another extension's.
    let contributionType: ContributionType<ActionDef> =
        ContributionType.data
            "grant-action"
            "An action the grants log knows (name, resource kind, scopes)."
            Codecs.actionDef

/// Policy over grants (spec §13.2, §16.5: policy only tightens). Both are privileged.
[<RequireQualifiedAccess>]
module GrantPolicy =
    /// Rules that ask or deny, as data; they outrank every allow grant.
    let rules: ContributionType<PolicyRule> =
        ContributionType.data "grant-policy" "A rule that asks or denies, whatever grants allow." Codecs.policyRule
        |> ContributionType.privileged

    /// Gates over every check and request: Deny or Ask outranks what grants allow; Allow changes
    /// nothing. A gate that fails denies.
    let gates: ContributionType<Gate<GrantQuery>> =
        Hook.gate "grants" "Decides about every grants check and request; can only tighten." Codecs.query
        |> ContributionType.privileged

    /// The grants' contribution types, for the host's catalog (`Catalog.create`).
    let contributionTypes: ContributionTypeInfo list =
        [
            ContributionType.info GrantStores.contributionType
            ContributionType.info GrantActions.contributionType
            ContributionType.info rules
            ContributionType.info gates
        ]
