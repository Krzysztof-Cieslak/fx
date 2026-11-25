namespace Fx.Grants

/// What a policy rule demands.
[<RequireQualifiedAccess>]
type PolicyEffect =
    /// A person must decide every time, even if a grant allows.
    | Ask
    /// Forbidden, whatever any grant says.
    | Deny

    override this.ToString() =
        match this with
        | Ask -> "ask"
        | Deny -> "deny"

/// A rule from host config or a policy extension (spec §13.2, §16.5: policy only tightens). Rules can
/// only ask or deny, and they outrank every allow grant, so adding one never permits more.
type PolicyRule =
    {
        /// Names the rule in reasons, e.g. `host/no-force-push`.
        Id: string
        Action: ActionName
        /// A pattern over the action's resources, matched ignoring case.
        Resource: string
        Effect: PolicyEffect
        Reason: string
    }

    override this.ToString() =
        this.Id
        + ": "
        + string this.Effect
        + " "
        + this.Action.Value
        + " "
        + this.Resource

/// Why the rules decided as they did.
[<RequireQualifiedAccess>]
type Because =
    /// No grant applies.
    | NoGrant
    | Grant of Grant
    | Policy of PolicyRule
    | UnknownAction of ActionName
    | InvalidResource of reason: string

    override this.ToString() =
        match this with
        | NoGrant -> "no grant applies"
        | Grant grant ->
            string grant.Effect
            + " by grant "
            + grant.Id.Value
            + " ("
            + grant.By
            + (grant.Reason |> Option.map (fun r -> ": " + r) |> Option.defaultValue "")
            + ")"
        | Policy rule -> string rule.Effect + " by policy " + rule.Id + ": " + rule.Reason
        | UnknownAction action -> "unknown action " + action.Value
        | InvalidResource reason -> "invalid resource: " + reason

/// What the effective rules decide about a query (docs/modules/grants.md, "Effective rules").
[<RequireQualifiedAccess>]
type RuleDecision =
    /// The most specific grant that allows it.
    | Allowed of Grant
    | MustAsk of Because list
    | Denied of Because list

    override this.ToString() =
        let show (reasons: Because list) =
            String.concat "; " (reasons |> List.map string)

        match this with
        | Allowed grant -> "allowed by " + grant.Id.Value
        | MustAsk reasons -> "ask (" + show reasons + ")"
        | Denied reasons -> "deny (" + show reasons + ")"

/// The effective rules (spec §13.2): a pure projection of the grants and the policy.
///
/// For a query at a time, among the grants that apply (active, not past their expiry, of the query's
/// action, their scope covering the query's context, their pattern matching its resource):
///
/// 1. a policy `Deny` rule or a `Deny` grant that matches denies (deny wins);
/// 2. otherwise a policy `Ask` rule that matches makes a person decide, whatever grants allow;
/// 3. otherwise the most specific `Allow` grant allows (`Rules.moreSpecific`);
/// 4. otherwise a person decides.
///
/// An unknown action or an invalid resource is denied. Deny grants and policy rules match ignoring
/// case, allow grants exactly: a resource spelled differently is never permitted more.
[<RequireQualifiedAccess>]
module Rules =
    /// Whether a grant's scope covers a context. A `Once` grant covers nothing: it was spent on its
    /// request. Deny grants compare repository paths ignoring case.
    let covers (grant: Grant) (context: GrantContext) : bool =
        match grant.Scope with
        | GrantScope.Once _ -> false
        | GrantScope.Host -> true
        | GrantScope.Session session -> context.Session = Some session
        | GrantScope.Repo scope ->
            match context.Repo with
            | None -> false
            | Some place ->
                place.Repo = scope.Repo
                && (match scope.Path with
                    | None -> true
                    | Some prefix -> Resource.isUnder (grant.Effect = GrantEffect.Deny) prefix place.Path)

    /// Whether an entry is in force at a time: active, and not past its expiry.
    let inForce (now: int64) (entry: GrantEntry) : bool =
        entry.Status = GrantStatus.Active
        && (match entry.Grant.Expires with
            | Some expires -> now < expires
            | None -> true)

    /// The grants in force at a time.
    let active (now: int64) (state: GrantsState) : Grant list =
        state.Grants
        |> Map.toList
        |> List.map snd
        |> List.filter (inForce now)
        |> List.map (fun entry -> entry.Grant)

    /// Whether a grant's pattern matches a resource: exactly for allows, ignoring case for denies.
    let matches (kind: ResourceKind) (grant: Grant) (resource: string) : bool =
        match grant.Effect with
        | GrantEffect.Allow -> Pattern.matches kind grant.Resource resource
        | GrantEffect.Deny -> Pattern.matchesIgnoringCase kind grant.Resource resource

    let private scopeRank (scope: GrantScope) : int list =
        match scope with
        | GrantScope.Once _ -> [ 4; 0 ]
        | GrantScope.Session _ -> [ 3; 0 ]
        | GrantScope.Repo { Path = Some path } -> [ 2; path.Length + 1 ]
        | GrantScope.Repo { Path = None } -> [ 2; 0 ]
        | GrantScope.Host -> [ 1; 0 ]

    /// Orders two grants of one action: positive if `a` is more specific than `b`. By the pattern's
    /// specificity, then the narrower scope (session, then a repository path, longer first, then a
    /// repository, then the host), then the later grant, then the larger id.
    let moreSpecific (kind: ResourceKind) (a: Grant) (b: Grant) : int =
        match
            Pattern.compareSpecificity (Pattern.specificity kind a.Resource) (Pattern.specificity kind b.Resource)
        with
        | 0 ->
            match List.compareWith compare (scopeRank a.Scope) (scopeRank b.Scope) with
            | 0 ->
                match compare a.At b.At with
                | 0 -> System.String.CompareOrdinal(a.Id.Value, b.Id.Value)
                | c -> c
            | c -> c
        | c -> c

    /// What the rules decide about a query at a time, given the policy and the grants (all of them:
    /// those not in force are left out here).
    let evaluate
        (actions: Actions)
        (policy: PolicyRule list)
        (now: int64)
        (grants: GrantEntry list)
        (query: GrantQuery)
        : RuleDecision =
        match Actions.kindOf query.Action actions with
        | None -> RuleDecision.Denied [ Because.UnknownAction query.Action ]
        | Some kind ->
            match Resource.validate kind query.Resource with
            | Error reason -> RuleDecision.Denied [ Because.InvalidResource reason ]
            | Ok() when
                (match query.Context.Repo with
                 | Some place -> not (Resource.isRepoPath place.Path)
                 | None -> false)
                ->
                RuleDecision.Denied
                    [
                        Because.InvalidResource "the repository path is not canonical and repo-relative"
                    ]
            | Ok() ->
                // Reasons come in a fixed order (rules by id, grants by id), whatever order the
                // policy and the grants are given in.
                let rules =
                    policy
                    |> List.filter (fun rule ->
                        rule.Action = query.Action
                        && Pattern.matchesIgnoringCase kind rule.Resource query.Resource)
                    |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Id, b.Id))

                let applicable =
                    grants
                    |> List.filter (inForce now)
                    |> List.map (fun entry -> entry.Grant)
                    |> List.filter (fun grant ->
                        grant.Action = query.Action
                        && covers grant query.Context
                        && matches kind grant query.Resource)
                    |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Id.Value, b.Id.Value))

                let denials =
                    (rules
                     |> List.filter (fun rule -> rule.Effect = PolicyEffect.Deny)
                     |> List.map Because.Policy)
                    @ (applicable
                       |> List.filter (fun grant -> grant.Effect = GrantEffect.Deny)
                       |> List.map Because.Grant)

                let asks =
                    rules
                    |> List.filter (fun rule -> rule.Effect = PolicyEffect.Ask)
                    |> List.map Because.Policy

                let allows =
                    applicable
                    |> List.filter (fun grant -> grant.Effect = GrantEffect.Allow)
                    |> List.sortWith (fun a b -> moreSpecific kind b a)

                if not denials.IsEmpty then
                    RuleDecision.Denied denials
                elif not asks.IsEmpty then
                    RuleDecision.MustAsk asks
                else
                    match allows with
                    | best :: _ -> RuleDecision.Allowed best
                    | [] -> RuleDecision.MustAsk [ Because.NoGrant ]

    /// What the rules decide about a query in a state.
    let decide (actions: Actions) (policy: PolicyRule list) (now: int64) (state: GrantsState) (query: GrantQuery) =
        evaluate actions policy now (state.Grants |> Map.toList |> List.map snd) query

    /// The decision as a check result, its reasons as text.
    let toCheck (decision: RuleDecision) : GrantCheck =
        match decision with
        | RuleDecision.Allowed grant -> GrantCheck.Allowed grant.Id
        | RuleDecision.MustAsk reasons -> GrantCheck.MustAsk(reasons |> List.map string)
        | RuleDecision.Denied reasons -> GrantCheck.Denied(reasons |> List.map string)
