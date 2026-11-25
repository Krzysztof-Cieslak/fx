module Fx.Grants.Tests.RulesTests

open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Grants
open Fx.Grants.Tests.Support

let private grant (id: string) (action: ActionName) (pattern: string) (effect: GrantEffect) (scope: GrantScope) =
    {
        Grant =
            {
                Id = GrantId id
                Action = action
                Resource = pattern
                Effect = effect
                Scope = scope
                By = alice
                At = t0
                Expires = None
                Reason = None
                Origin = GrantOrigin.Direct "test"
            }
        Status = GrantStatus.Active
    }

let private query (action: ActionName) (resource: string) (context: GrantContext) : GrantQuery =
    {
        Action = action
        Resource = resource
        Context = context
    }

let private inSession (session: string) =
    { GrantContext.none with
        Session = Some session
    }

let private inRepo (repo: string) (path: string) =
    { GrantContext.none with
        Repo = Some { Repo = repo; Path = path }
    }

let private evaluate (policy: PolicyRule list) (entries: GrantEntry list) (q: GrantQuery) =
    Rules.evaluate actions policy t0 entries q

let private allowedBy (decision: RuleDecision) =
    match decision with
    | RuleDecision.Allowed g -> Some g.Id.Value
    | _ -> None

let private scenario =
    gen {
        let! action = grantAction
        let! entries = entriesFor action
        let! q = queryFor action
        let! at = now
        return action, entries, q, at
    }

let private policyFor (action: ActionName) : Gen<PolicyRule> =
    gen {
        let! pattern, _ = generalized (kindOf action)
        let! effect = Gen.elements [ PolicyEffect.Ask; PolicyEffect.Deny ]

        return
            {
                Id = "policy/p"
                Action = action
                Resource = pattern
                Effect = effect
                Reason = "test"
            }
    }

[<Tests>]
let tests =
    testList
        "Effective rules"
        [
            test "no grant: ask" {
                Expect.equal
                    (evaluate [] [] (query shellExec "git status" GrantContext.none))
                    (RuleDecision.MustAsk [ Because.NoGrant ])
                    "ask"
            }

            test "the most specific allow decides" {
                let broad = grant "g1" shellExec "git **" GrantEffect.Allow GrantScope.Host
                let narrow = grant "g2" shellExec "git status" GrantEffect.Allow GrantScope.Host

                let decision =
                    evaluate [] [ broad; narrow ] (query shellExec "git status" GrantContext.none)

                Expect.equal (allowedBy decision) (Some "g2") "exact beats `**`"

                let other =
                    evaluate [] [ broad; narrow ] (query shellExec "git log" GrantContext.none)

                Expect.equal (allowedBy other) (Some "g1") "only the broad one matches"
            }

            test "deny wins over any allow, however specific" {
                let allow =
                    grant "g1" shellExec "git push origin main" GrantEffect.Allow GrantScope.Host

                let deny = grant "g2" shellExec "git push **" GrantEffect.Deny GrantScope.Host

                match evaluate [] [ allow; deny ] (query shellExec "git push origin main" GrantContext.none) with
                | RuleDecision.Denied [ Because.Grant g ] -> Expect.equal g.Id.Value "g2" "the deny"
                | other -> failtest (string other)
            }

            test "deny grants match ignoring case, allows exactly" {
                let allow = grant "g1" fsWrite "src/**" GrantEffect.Allow GrantScope.Host
                let deny = grant "g2" fsWrite "secrets/**" GrantEffect.Deny GrantScope.Host

                Expect.equal
                    (severity (evaluate [] [ allow; deny ] (query fsWrite "Secrets/key" GrantContext.none)))
                    2
                    "the deny matches another spelling"

                Expect.equal
                    (severity (evaluate [] [ allow; deny ] (query fsWrite "SRC/a.fs" GrantContext.none)))
                    1
                    "the allow does not"
            }

            test "scopes: session, repository and path prefix, host" {
                let session = grant "g1" fsWrite "**" GrantEffect.Allow (GrantScope.Session "s1")

                let repo =
                    grant "g2" fsWrite "**" GrantEffect.Allow (GrantScope.Repo { Repo = "r1"; Path = Some "src" })

                let q c = query fsWrite "a.fs" c
                Expect.equal (allowedBy (evaluate [] [ session ] (q (inSession "s1")))) (Some "g1") "its session"
                Expect.equal (allowedBy (evaluate [] [ session ] (q (inSession "s2")))) None "another session"
                Expect.equal (allowedBy (evaluate [] [ repo ] (q (inRepo "r1" "src/a")))) (Some "g2") "under the path"
                Expect.equal (allowedBy (evaluate [] [ repo ] (q (inRepo "r1" "src")))) (Some "g2") "the path"
                Expect.equal (allowedBy (evaluate [] [ repo ] (q (inRepo "r1" "srcx")))) None "a sibling"
                Expect.equal (allowedBy (evaluate [] [ repo ] (q (inRepo "r2" "src")))) None "another repository"
                Expect.equal (allowedBy (evaluate [] [ repo ] (q GrantContext.none))) None "no repository"
            }

            test "the narrower scope wins between equally specific patterns" {
                let host = grant "g1" fsWrite "**" GrantEffect.Allow GrantScope.Host
                let session = grant "g2" fsWrite "**" GrantEffect.Allow (GrantScope.Session "s1")

                let repo =
                    grant "g3" fsWrite "**" GrantEffect.Allow (GrantScope.Repo { Repo = "r1"; Path = None })

                let context =
                    { inRepo "r1" "src" with
                        Session = Some "s1"
                    }

                Expect.equal
                    (allowedBy (evaluate [] [ host; repo; session ] (query fsWrite "a.fs" context)))
                    (Some "g2")
                    "session"

                Expect.equal (allowedBy (evaluate [] [ host; repo ] (query fsWrite "a.fs" context))) (Some "g3") "repo"
            }

            test "policy: deny beats grants, ask overrides allows" {
                let allow = grant "g1" shellExec "git **" GrantEffect.Allow GrantScope.Host

                let rule effect =
                    {
                        Id = "host/no-push"
                        Action = shellExec
                        Resource = "git push **"
                        Effect = effect
                        Reason = "pushes need a person"
                    }

                let q = query shellExec "git push origin" GrantContext.none

                match evaluate [ rule PolicyEffect.Deny ] [ allow ] q with
                | RuleDecision.Denied [ Because.Policy r ] -> Expect.equal r.Id "host/no-push" "denied by policy"
                | other -> failtest (string other)

                match evaluate [ rule PolicyEffect.Ask ] [ allow ] q with
                | RuleDecision.MustAsk [ Because.Policy _ ] -> ()
                | other -> failtest (string other)

                Expect.equal
                    (allowedBy (
                        evaluate [ rule PolicyEffect.Ask ] [ allow ] (query shellExec "git log" GrantContext.none)
                    ))
                    (Some "g1")
                    "the rule does not match"
            }

            test "unknown actions, invalid resources and contexts are denied" {
                Expect.equal
                    (severity (evaluate [] [] (query (ActionName.create "x.unknown") "a" GrantContext.none)))
                    2
                    "unknown action"

                Expect.equal (severity (evaluate [] [] (query fsWrite "src/../x" GrantContext.none))) 2 "`..`"
                Expect.equal (severity (evaluate [] [] (query fsWrite "a.fs" (inRepo "r1" "src/../x")))) 2 "context"
            }

            test "expired, revoked and spent grants do not apply" {
                let g = grant "g1" shellExec "**" GrantEffect.Allow GrantScope.Host

                let expiring =
                    { g with
                        Grant =
                            { g.Grant with
                                Expires = Some(t0 + second)
                            }
                    }

                let q = query shellExec "ls" GrantContext.none
                Expect.equal (allowedBy (Rules.evaluate actions [] t0 [ expiring ] q)) (Some "g1") "before"
                Expect.equal (allowedBy (Rules.evaluate actions [] (t0 + second) [ expiring ] q)) None "at expiry"

                for status in [ GrantStatus.Spent; GrantStatus.Expired t0 ] do
                    Expect.equal (allowedBy (evaluate [] [ { g with Status = status } ] q)) None (string status)
            }

            testPropertyWithConfig config "deny dominance: a matching deny grant in force denies, whatever else applies"
            <| Prop.forAll (Arb.fromGen scenario) (fun (action, entries, q, at) ->
                let deny =
                    {
                        Grant =
                            {
                                Id = GrantId "deny"
                                Action = action
                                Resource = Pattern.exact q.Resource
                                Effect = GrantEffect.Deny
                                Scope = GrantScope.Host
                                By = alice
                                At = t0
                                Expires = None
                                Reason = None
                                Origin = GrantOrigin.Direct "test"
                            }
                        Status = GrantStatus.Active
                    }

                severity (Rules.evaluate actions [] at (deny :: entries) q) = 2)

            testPropertyWithConfig config "the allowing grant is in force, applies, and is the most specific that does"
            <| Prop.forAll (Arb.fromGen scenario) (fun (_, entries, q, at) ->
                match Rules.evaluate actions [] at entries q with
                | RuleDecision.Allowed chosen ->
                    let kind = kindOf q.Action

                    let applicable =
                        entries
                        |> List.filter (Rules.inForce at)
                        |> List.map (fun e -> e.Grant)
                        |> List.filter (fun g ->
                            g.Action = q.Action
                            && Rules.covers g q.Context
                            && Rules.matches kind g q.Resource)

                    List.contains chosen applicable
                    && chosen.Effect = GrantEffect.Allow
                    && applicable |> List.forall (fun g -> g.Effect = GrantEffect.Allow)
                    && applicable |> List.forall (fun g -> Rules.moreSpecific kind chosen g >= 0)
                | _ -> true)

            testPropertyWithConfig config "policy only tightens: adding a rule never permits more"
            <| Prop.forAll
                (Arb.fromGen (
                    gen {
                        let! action, entries, q, at = scenario
                        let! rules = Gen.listOf (policyFor action)
                        let! extra = policyFor action
                        return entries, q, at, rules |> List.truncate 4, extra
                    }
                ))
                (fun (entries, q, at, rules, extra) ->
                    severity (Rules.evaluate actions (extra :: rules) at entries q)
                    >= severity (Rules.evaluate actions rules at entries q))

            testPropertyWithConfig config "expiry: at or after its expiry a grant is as good as absent"
            <| Prop.forAll (Arb.fromGen scenario) (fun (_, entries, q, at) ->
                let withoutExpired =
                    entries
                    |> List.filter (fun e ->
                        match e.Grant.Expires with
                        | Some expires -> at < expires
                        | None -> true)

                Rules.evaluate actions [] at entries q = Rules.evaluate actions [] at withoutExpired q)

            testPropertyWithConfig config "only grants in force of the query's action matter, in any order"
            <| Prop.forAll
                (Arb.fromGen (
                    gen {
                        let! _, entries, q, at = scenario
                        let! shuffled = Gen.shuffle entries
                        return entries, List.ofArray shuffled, q, at
                    }
                ))
                (fun (entries, shuffled, q, at) ->
                    let relevant =
                        entries
                        |> List.filter (fun e -> e.Grant.Action = q.Action && Rules.inForce at e)

                    let decision = Rules.evaluate actions [] at entries q

                    decision = Rules.evaluate actions [] at shuffled q
                    && decision = Rules.evaluate actions [] at relevant q)

            testPropertyWithConfig config "scopes: a grant applies only in its session, repository and path"
            <| Prop.forAll (Arb.fromGen scenario) (fun (_, entries, q, at) ->
                match Rules.evaluate actions [] at entries q with
                | RuleDecision.Allowed g ->
                    match g.Scope with
                    | GrantScope.Session s -> q.Context.Session = Some s
                    | GrantScope.Repo scope ->
                        match q.Context.Repo with
                        | Some place ->
                            place.Repo = scope.Repo
                            && (scope.Path
                                |> Option.forall (fun p -> place.Path = p || place.Path.StartsWith(p + "/")))
                        | None -> false
                    | GrantScope.Host -> true
                    | GrantScope.Once _ -> false
                | _ -> true)
        ]
