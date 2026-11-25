namespace Fx.Grants

open Fx.Contracts

/// `grants/list` params: the grants in force, or every grant ever made.
type ListParams =
    {
        /// Also grants revoked, expired or spent.
        All: bool
    }

    override this.ToString() =
        if this.All then "all grants" else "active grants"

/// The grants view's list: grants with their status, as of a sequence number of the log.
type GrantList =
    {
        Seq: int64
        Grants: GrantEntry list
    }

    override this.ToString() =
        string this.Grants.Length + " grants at " + string this.Seq

/// The pending requests, oldest first, as of a sequence number of the log.
type PendingList =
    {
        Seq: int64
        Requests: GrantRequest list
    }

    override this.ToString() =
        string this.Requests.Length + " pending at " + string this.Seq

/// `grants/answer` params.
type AnswerParams =
    {
        Request: RequestId
        Answer: GrantAnswer
    }

    override this.ToString() =
        this.Request.Value + ": " + string this.Answer

/// `grants/grant` params: a grant a person makes directly in the grants view. The host fills in its
/// id, the person, the time and the client.
type GrantDraft =
    {
        Action: ActionName
        Resource: string
        Effect: GrantEffect
        /// Session, repository or host (once answers a request).
        Scope: GrantScope
        Expires: int64 option
        Reason: string option
    }

    override this.ToString() =
        string this.Effect
        + " "
        + this.Action.Value
        + " "
        + this.Resource
        + " ("
        + string this.Scope
        + ")"

/// `grants/revoke` params.
type RevokeParams =
    {
        Grant: GrantId
        Reason: string option
    }

    override this.ToString() = "revoke " + this.Grant.Value

/// `grants/subscribe` params: the events after this sequence number, then live ones.
type SubscribeParams =
    {
        After: int64
    }

    override this.ToString() = "after " + string this.After

/// `grants/event`: an event of the grants log and its sequence number. Clients fold them with
/// `GrantLog.evolve` onto a state they got with `grants/state` (spec §5: snapshot + tail).
type GrantNotice =
    {
        Seq: int64
        Event: GrantEvent
    }

    override this.ToString() =
        string this.Seq + ": " + string this.Event

/// The JSON of the `grants/*` client protocol (spec §17).
[<RequireQualifiedAccess>]
module ProtocolCodecs =
    let private seq: Codec<int64> =
        Codec.int64
        |> Codec.atLeast 0L
        |> Codec.describe "A sequence number of the grants log (0 before the first event)."

    let listParams: Codec<ListParams> =
        Codec.record {
            let! all = Codec.defaultField "all" (fun (p: ListParams) -> p.All) false Codec.bool
            return { All = all }
        }
        |> Codec.describe "Which grants to list: those in force, or all."
        |> Codec.named "GrantListParams"

    let grantList: Codec<GrantList> =
        Codec.record {
            let! s = Codec.field "seq" (fun (l: GrantList) -> l.Seq) seq
            and! grants = Codec.field "grants" (fun l -> l.Grants) (Codec.list Codecs.entry)
            return { Seq = s; Grants = grants }
        }
        |> Codec.describe "Grants with their status, as of a sequence number."
        |> Codec.named "GrantList"

    let pendingList: Codec<PendingList> =
        Codec.record {
            let! s = Codec.field "seq" (fun (l: PendingList) -> l.Seq) seq

            and! requests =
                Codec.field "requests" (fun l -> l.Requests) (Codec.list Codecs.request)

            return { Seq = s; Requests = requests }
        }
        |> Codec.describe "The pending requests, oldest first, as of a sequence number."
        |> Codec.named "PendingGrantRequests"

    let answerParams: Codec<AnswerParams> =
        Codec.record {
            let! request =
                Codec.field "request" (fun (p: AnswerParams) -> p.Request) Codecs.requestId

            and! answer = Codec.field "answer" (fun p -> p.Answer) Codecs.answer
            return { Request = request; Answer = answer }
        }
        |> Codec.describe "An answer to a pending request."
        |> Codec.named "GrantAnswerParams"

    let draft: Codec<GrantDraft> =
        Codec.record {
            let! action =
                Codec.field "action" (fun (d: GrantDraft) -> d.Action) Codecs.actionName

            and! resource =
                Codec.field "resource" (fun d -> d.Resource) (Codec.string |> Codec.nonEmpty)

            and! effect = Codec.field "effect" (fun d -> d.Effect) Codecs.effect
            and! scope = Codec.field "scope" (fun d -> d.Scope) Codecs.grantScope
            and! expires = Codec.optionalField "expires" (fun d -> d.Expires) Codec.int64
            and! reason = Codec.optionalField "reason" (fun d -> d.Reason) Codec.string

            return
                {
                    Action = action
                    Resource = resource
                    Effect = effect
                    Scope = scope
                    Expires = expires
                    Reason = reason
                }
        }
        |> Codec.describe "A grant made directly in the grants view (session, repo or host scope)."
        |> Codec.named "GrantDraft"

    let revokeParams: Codec<RevokeParams> =
        Codec.record {
            let! grant = Codec.field "grant" (fun (p: RevokeParams) -> p.Grant) Codecs.grantId
            and! reason = Codec.optionalField "reason" (fun p -> p.Reason) Codec.string
            return { Grant = grant; Reason = reason }
        }
        |> Codec.describe "The grant to revoke, and why."
        |> Codec.named "GrantRevokeParams"

    let subscribeParams: Codec<SubscribeParams> =
        Codec.record {
            let! after = Codec.defaultField "after" (fun (p: SubscribeParams) -> p.After) 0L seq
            return { After = after }
        }
        |> Codec.describe "Follow the grants log after a sequence number."
        |> Codec.named "GrantSubscribeParams"

    let notice: Codec<GrantNotice> =
        Codec.record {
            let! s =
                Codec.field "seq" (fun (n: GrantNotice) -> n.Seq) (Codec.int64 |> Codec.atLeast 1L)

            and! event = Codec.field "event" (fun n -> n.Event) Codecs.event
            return { Seq = s; Event = event }
        }
        |> Codec.describe "An event of the grants log and its sequence number."
        |> Codec.named "GrantNotice"
