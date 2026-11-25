/// Generators and builders shared by the grants tests.
module Fx.Grants.Tests.Support

open System
open FsCheck
open FsCheck.FSharp
open Expecto
open Fx.Grants

/// 300 tests per property; set PROPERTY_TESTS to run more.
let config =
    { FsCheckConfig.defaultConfig with
        maxTest =
            match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
            | null -> 300
            | count -> int count
    }

let actions = Actions.builtIn

let shellExec = ActionName.create "shell.exec"
let fsWrite = ActionName.create "fs.write"
let netConnect = ActionName.create "net.connect"

let kindOf (action: ActionName) =
    Actions.kindOf action actions |> Option.get

/// Microseconds: one second.
let second = 1_000_000L

let t0 = 1_800_000_000L * second

let alice = "user:alice"

// ---- Resources and patterns ------------------------------------------------------------------------

let private pathSegment =
    Gen.elements [ "src"; "Src"; "lib"; "a.fs"; "b.fs"; "docs"; "x"; "secrets" ]

let private word =
    Gen.elements [ "git"; "npm"; "run"; "test"; "status"; "log"; "-n"; "3"; "Push" ]

let private name =
    Gen.elements
        [
            "api.github.com:443"
            "API.github.com:443"
            "registry.npmjs.org:443"
            "bun@1.4.5"
            "copilot/gpt-5"
            "x"
        ]

/// A valid resource of a kind, from a small vocabulary so that patterns often match.
let resource (kind: ResourceKind) : Gen<string> =
    match kind with
    | ResourceKind.Path ->
        gen {
            let! absolute = Gen.frequency [ 1, Gen.constant true; 4, Gen.constant false ]
            let! count = Gen.choose (1, 4)
            let! segments = Gen.listOfLength count pathSegment
            let path = String.concat "/" segments
            return if absolute then "/" + path else path
        }
    | ResourceKind.Command ->
        gen {
            let! count = Gen.choose (1, 4)
            let! words = Gen.listOfLength count word
            return String.concat " " words
        }
    | ResourceKind.Name -> name

let private separatorOf (kind: ResourceKind) =
    match kind with
    | ResourceKind.Path -> Some '/'
    | ResourceKind.Command -> Some ' '
    | ResourceKind.Name -> None

/// A pattern as tokens, so generalizing one keeps it valid.
type PatternToken =
    | Lit of char
    | Sep
    | Star
    | Quest
    | DoubleStar

let tokensOf (kind: ResourceKind) (resource: string) : PatternToken list =
    resource
    |> Seq.map (fun c -> if Some c = separatorOf kind then Sep else Lit c)
    |> List.ofSeq

let render (kind: ResourceKind) (tokens: PatternToken list) : string =
    let sep =
        match separatorOf kind with
        | Some c -> string c
        | None -> ""

    tokens
    |> List.map (fun token ->
        match token with
        | Lit c when c = '*' || c = '?' || c = '\\' -> "\\" + string c
        | Lit c -> string c
        | Sep -> sep
        | Star -> "*"
        | Quest -> "?"
        | DoubleStar -> "**")
    |> String.concat ""

/// The segments of a token list (between separators).
let private segments (tokens: PatternToken list) : PatternToken list list =
    let folder (acc: PatternToken list list) (token: PatternToken) =
        match token, acc with
        | Sep, _ -> [] :: acc
        | _, current :: rest -> (token :: current) :: rest
        | _, [] -> [ [ token ] ]

    tokens |> List.fold folder [ [] ] |> List.map List.rev |> List.rev

let private join (segs: PatternToken list list) : PatternToken list =
    segs |> List.reduce (fun a b -> a @ [ Sep ] @ b)

/// One step that makes a pattern match at least what it matched, and be strictly less specific:
/// a literal becomes `?`, a `?` becomes `*`, a run of a segment's literals becomes `*`, a segment that
/// is just `*` becomes `**` (paths and commands). `None` if no step applies at the index.
let generalize (kind: ResourceKind) (choice: int) (tokens: PatternToken list) : PatternToken list option =
    let segs = segments tokens

    let candidates =
        [
            // literal -> ?
            for si, seg in List.indexed segs do
                for ti, token in List.indexed seg do
                    match token with
                    | Lit _ -> yield (si, ti, "quest")
                    | Quest -> yield (si, ti, "star")
                    | _ -> ()
            // two adjacent literals -> *
            for si, seg in List.indexed segs do
                for ti in 0 .. seg.Length - 2 do
                    match seg[ti], seg[ti + 1] with
                    | Lit _, Lit _ -> yield (si, ti, "run")
                    | _ -> ()
            // [*] -> **
            if separatorOf kind |> Option.isSome then
                for si, seg in List.indexed segs do
                    if seg = [ Star ] then
                        yield (si, 0, "double")
        ]

    let doubleStars =
        segs |> List.filter (fun seg -> seg = [ DoubleStar ]) |> List.length

    let candidates =
        candidates
        |> List.filter (fun (_, _, step) -> step <> "double" || doubleStars < Pattern.MaxDoubleStars)

    if candidates.IsEmpty then
        None
    else
        let si, ti, step = candidates[abs choice % candidates.Length]

        let replace (seg: PatternToken list) =
            match step with
            | "quest" -> seg |> List.mapi (fun i t -> if i = ti then Quest else t)
            | "star" -> seg |> List.mapi (fun i t -> if i = ti then Star else t)
            | "run" -> (seg |> List.take ti) @ [ Star ] @ (seg |> List.skip (ti + 2))
            | _ -> [ DoubleStar ]

        // A star next to a star would read as `**`: collapse it.
        let rec tidy (seg: PatternToken list) =
            match seg with
            | Star :: Star :: rest -> tidy (Star :: rest)
            | head :: rest -> head :: tidy rest
            | [] -> []

        Some(
            segs
            |> List.mapi (fun i seg -> if i = si then tidy (replace seg) else seg)
            |> join
        )

/// A pattern made by generalizing a resource a few times, and that resource.
let generalized (kind: ResourceKind) : Gen<string * string> =
    gen {
        let! r = resource kind
        let! steps = Gen.listOf (Gen.choose (0, 1000))
        let steps = steps |> List.truncate 6

        let tokens =
            steps
            |> List.fold
                (fun tokens choice ->
                    match generalize kind choice tokens with
                    | Some next -> next
                    | None -> tokens)
                (tokensOf kind r)

        return render kind tokens, r
    }

// ---- Grants ----------------------------------------------------------------------------------------

let context: Gen<GrantContext> =
    gen {
        let! session = Gen.optionOf (Gen.elements [ "s1"; "s2" ])
        let! repo = Gen.elements [ "r1"; "r2" ]
        let! path = Gen.elements [ ""; "src"; "src/a"; "docs"; "Src" ]
        let! inRepo = Gen.elements [ true; false ]

        return
            {
                Session = session
                Run = None
                Repo = if inRepo then Some { Repo = repo; Path = path } else None
            }
    }

let scope: Gen<GrantScope> =
    Gen.oneof
        [
            Gen.constant GrantScope.Host
            Gen.elements [ "s1"; "s2" ] |> Gen.map GrantScope.Session
            gen {
                let! repo = Gen.elements [ "r1"; "r2" ]
                let! path = Gen.elements [ None; Some "src"; Some "src/a"; Some "docs" ]
                return GrantScope.Repo { Repo = repo; Path = path }
            }
        ]

let grantAction = Gen.elements [ shellExec; fsWrite; netConnect ]

/// A grant of an action (made directly, so not `Once`), with a pattern generalized from a resource.
let grantFor (action: ActionName) (id: int) : Gen<Grant> =
    gen {
        let! pattern, _ = generalized (kindOf action)
        let! effect = Gen.elements [ GrantEffect.Allow; GrantEffect.Deny ]
        let! scope = scope
        let! at = Gen.choose (0, 100)
        let! expires = Gen.optionOf (Gen.choose (1, 200))

        return
            {
                Id = GrantId("g" + id.ToString("000"))
                Action = action
                Resource = pattern
                Effect = effect
                Scope = scope
                By = alice
                At = t0 + int64 at * second
                Expires = expires |> Option.map (fun e -> t0 + int64 (at + e) * second)
                Reason = None
                Origin = GrantOrigin.Direct "test"
            }
    }

let status: Gen<GrantStatus> =
    Gen.frequency
        [
            6, Gen.constant GrantStatus.Active
            1, Gen.constant GrantStatus.Spent
            1, Gen.constant (GrantStatus.Expired t0)
            1,
            Gen.constant (
                GrantStatus.Revoked
                    {
                        Grant = GrantId "x"
                        By = alice
                        At = t0
                        Reason = None
                    }
            )
        ]

/// Grants of the query's action mostly, with statuses.
let entriesFor (action: ActionName) : Gen<GrantEntry list> =
    gen {
        let! count = Gen.choose (0, 8)

        let! grants =
            [ 0 .. count - 1 ]
            |> List.map (fun i ->
                gen {
                    let! other = Gen.frequency [ 4, Gen.constant false; 1, Gen.constant true ]
                    let! a = if other then grantAction else Gen.constant action
                    let! g = grantFor a i
                    let! s = status
                    return { Grant = g; Status = s }
                })
            |> Gen.sequenceToList

        return grants
    }

/// A query of an action with a resource of its kind.
let queryFor (action: ActionName) : Gen<GrantQuery> =
    gen {
        let! r = resource (kindOf action)
        let! c = context

        return
            {
                Action = action
                Resource = r
                Context = c
            }
    }

let now: Gen<int64> =
    Gen.choose (0, 300) |> Gen.map (fun s -> t0 + int64 s * second)

/// 0 for allowed, 1 for ask, 2 for denied.
let severity (decision: RuleDecision) =
    match decision with
    | RuleDecision.Allowed _ -> 0
    | RuleDecision.MustAsk _ -> 1
    | RuleDecision.Denied _ -> 2

// ---- Commands --------------------------------------------------------------------------------------

let command (by: string) (at: int64) (op: GrantOp) : GrantCommand = { Command = op; By = by; At = at }

let ask (key: string) (action: ActionName) (resource: string) (context: GrantContext) : GrantAsk =
    {
        Key = key
        Action = action
        Resource = resource
        Context = context
        Requester =
            {
                Principal = alice
                Agent = Some "build"
                Extension = None
            }
        Summary = "Do " + resource
        Evidence = []
        Suggestions = []
        Scopes = []
        Expires = None
    }

let requestOf (ask: GrantAsk) (id: string) (at: int64) : GrantRequest =
    match GrantLog.request actions ask (RequestId id) at with
    | Ok request -> request
    | Error e -> failtest (string e)

/// Runs commands from a state: rejected ones change nothing. The state, every event, every outcome.
let run (state: GrantsState) (commands: GrantCommand list) =
    commands
    |> List.fold
        (fun (state, events, outcomes) command ->
            match GrantLog.decide actions state command with
            | Ok decided -> List.fold GrantLog.evolve state decided, events @ decided, outcomes @ [ Ok decided ]
            | Error e -> state, events, outcomes @ [ Error e ])
        (state, [], [])

/// Random scripts of commands over a few ids, keys, sessions and principals, a second apart.
module Scripts =
    let private requestIds =
        Gen.choose (0, 6) |> Gen.map (fun n -> RequestId("q" + string n))

    let private grantIds =
        Gen.choose (0, 6) |> Gen.map (fun n -> GrantId("g" + string n))

    let private principal =
        Gen.frequency
            [
                4, Gen.constant alice
                1, Gen.constant "system"
                1, Gen.constant "device:device-pairing:k1"
            ]

    let private scopeChoice =
        Gen.elements
            [
                ScopeChoice.Once
                ScopeChoice.Session
                ScopeChoice.Repo None
                ScopeChoice.Repo(Some "src")
                ScopeChoice.Host
            ]

    /// One command at step `i`.
    let private step (i: int) : Gen<GrantCommand> =
        let at = t0 + int64 i * second

        gen {
            let! by = principal

            let! op =
                Gen.frequency
                    [
                        4,
                        gen {
                            let! key = Gen.choose (0, 4)
                            let! action = grantAction
                            let! r = resource (kindOf action)
                            let! c = context
                            let! expires = Gen.optionOf (Gen.choose (1, 10))

                            let a =
                                { ask ("k" + string key) action r c with
                                    Expires = expires |> Option.map (fun e -> at + int64 e * second)
                                }

                            match GrantLog.request actions a (RequestId("q" + string i)) at with
                            | Ok request -> return GrantOp.Request request
                            | Error _ -> return GrantOp.EndSession "none"
                        }
                        4,
                        gen {
                            let! id = requestIds
                            let! effect = Gen.elements [ GrantEffect.Allow; GrantEffect.Deny ]
                            let! scope = scopeChoice
                            let! expires = Gen.optionOf (Gen.choose (1, 10))

                            return
                                GrantOp.Answer(
                                    id,
                                    {
                                        Effect = effect
                                        Scope = scope
                                        Resource = None
                                        Expires = expires |> Option.map (fun e -> at + int64 e * second)
                                        Reason = None
                                    },
                                    GrantId("g" + string i)
                                )
                        }
                        2,
                        gen {
                            let! action = grantAction
                            let! g = grantFor action i

                            return
                                GrantOp.Grant
                                    { g with
                                        By = by
                                        At = at
                                        Expires = g.Expires |> Option.map (fun _ -> at + 3L * second)
                                    }
                        }
                        2,
                        gen {
                            let! id = grantIds
                            return GrantOp.Revoke(id, None)
                        }
                        2, grantIds |> Gen.map GrantOp.Expire
                        2,
                        gen {
                            let! id = requestIds

                            let! why =
                                Gen.elements
                                    [
                                        CloseReason.TimedOut
                                        CloseReason.Withdrawn "done"
                                        CloseReason.Refused [ "no" ]
                                        CloseReason.Covered(GrantId "g1")
                                        CloseReason.SessionEnded
                                    ]

                            return GrantOp.Close(id, why)
                        }
                        1, Gen.elements [ "s1"; "s2" ] |> Gen.map GrantOp.EndSession
                    ]

            return command by at op
        }

    let script: Gen<GrantCommand list> =
        gen {
            let! count = Gen.choose (0, 40)
            return! [ 1..count ] |> List.map step |> Gen.sequenceToList
        }

let decideOk (state: GrantsState) (command: GrantCommand) : GrantEvent list * GrantsState =
    match GrantLog.decide actions state command with
    | Ok events -> events, List.fold GrantLog.evolve state events
    | Error e -> failtest ("refused: " + string e)

let decideError (state: GrantsState) (command: GrantCommand) : GrantError =
    match GrantLog.decide actions state command with
    | Ok events -> failtest ("accepted: " + String.concat ", " (events |> List.map string))
    | Error e -> e
