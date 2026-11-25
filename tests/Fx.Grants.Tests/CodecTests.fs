/// The JSON of the grants log: a scenario that appends every kind of event, kept as version 1 wrote it
/// (events are facts kept forever: readers must read it in every later version), and its final state.
module Fx.Grants.Tests.CodecTests

open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Contracts
open Fx.Grants
open Fx.Grants.Tests.Support

let private repoContext =
    {
        Session = Some "s1"
        Run = Some "run1"
        Repo = Some { Repo = "r1"; Path = "src/billing" }
    }

/// A grants log that goes through every event and status.
let everything: GrantCommand list =
    let at n = t0 + int64 n * second

    let push =
        { ask "call-1" shellExec "git push origin main" repoContext with
            Evidence =
                [
                    {
                        Label = "branch"
                        Text = "main is 2 commits ahead"
                    }
                ]
            Suggestions = [ "git push origin *"; "git **" ]
            Expires = Some(at 600)
        }

    let write = ask "call-2" fsWrite "src/billing/retry.fs" repoContext

    let install =
        { ask "install-bun" (ActionName.create "component.install") "bun@1.4.5" GrantContext.none with
            Requester =
                {
                    Principal = "system"
                    Agent = None
                    Extension = Some "fx.runtime.bun"
                }
        }

    let dotenv = ask "dotenv" (ActionName.create "env.dotenv") ".env" repoContext

    let slow =
        { ask "call-3" shellExec "make" repoContext with
            Expires = Some(at 30)
        }

    let answer effect scope resource expires : GrantAnswer =
        {
            Effect = effect
            Scope = scope
            Resource = resource
            Expires = expires
            Reason = Some "reviewed"
        }

    [
        command alice (at 1) (GrantOp.Request(requestOf push "q1" (at 1)))
        command
            alice
            (at 2)
            (GrantOp.Answer(RequestId "q1", answer GrantEffect.Allow ScopeChoice.Once None None, GrantId "g1"))
        command alice (at 3) (GrantOp.Request(requestOf write "q2" (at 3)))
        command
            "user:bob"
            (at 4)
            (GrantOp.Answer(
                RequestId "q2",
                answer GrantEffect.Allow (ScopeChoice.Repo(Some "src")) (Some "src/**") (Some(at 3600)),
                GrantId "g2"
            ))
        command "system" (at 5) (GrantOp.Request(requestOf install "q3" (at 5)))
        command
            alice
            (at 6)
            (GrantOp.Answer(RequestId "q3", answer GrantEffect.Allow ScopeChoice.Host None None, GrantId "g3"))
        command alice (at 7) (GrantOp.Request(requestOf dotenv "q4" (at 7)))
        command
            alice
            (at 8)
            (GrantOp.Answer(RequestId "q4", answer GrantEffect.Deny ScopeChoice.Once None None, GrantId "g4"))
        command
            alice
            (at 9)
            (GrantOp.Grant
                {
                    Id = GrantId "g5"
                    Action = netConnect
                    Resource = "*.github.com:443"
                    Effect = GrantEffect.Allow
                    Scope = GrantScope.Session "s1"
                    By = alice
                    At = at 9
                    Expires = None
                    Reason = None
                    Origin = GrantOrigin.Direct "tui"
                })
        command
            alice
            (at 10)
            (GrantOp.Grant
                {
                    Id = GrantId "g6"
                    Action = shellExec
                    Resource = "rm **"
                    Effect = GrantEffect.Deny
                    Scope = GrantScope.Host
                    By = alice
                    At = at 10
                    Expires = Some(at 20)
                    Reason = Some "never"
                    Origin = GrantOrigin.Direct "cli"
                })
        command "user:bob" (at 11) (GrantOp.Revoke(GrantId "g3", Some "pin a newer version"))
        command "system" (at 20) (GrantOp.Expire(GrantId "g6"))
        command alice (at 21) (GrantOp.Request(requestOf slow "q5" (at 21)))
        command "system" (at 30) (GrantOp.Close(RequestId "q5", CloseReason.TimedOut))
        command alice (at 31) (GrantOp.Request(requestOf (ask "call-4" shellExec "ls" repoContext) "q6" (at 31)))
        command alice (at 32) (GrantOp.Close(RequestId "q6", CloseReason.Withdrawn "the turn was interrupted"))
        command
            alice
            (at 33)
            (GrantOp.Request(requestOf (ask "call-5" fsWrite "src/billing/a.fs" repoContext) "q7" (at 33)))
        command "system" (at 34) (GrantOp.Close(RequestId "q7", CloseReason.Covered(GrantId "g2")))
        command
            alice
            (at 35)
            (GrantOp.Request(requestOf (ask "call-6" shellExec "rm -rf build" repoContext) "q8" (at 35)))
        command "system" (at 36) (GrantOp.Close(RequestId "q8", CloseReason.Refused [ "deny by policy host/no-rm" ]))
        command alice (at 37) (GrantOp.Request(requestOf (ask "call-7" fsWrite "README.md" repoContext) "q9" (at 37)))
        command "system" (at 38) (GrantOp.EndSession "s1")
        command
            alice
            (at 39)
            (GrantOp.Request(requestOf (ask "call-8" shellExec "dotnet test" GrantContext.none) "q10" (at 39)))
    ]

let private scenario =
    let state, events, outcomes = run GrantLog.initial everything

    match outcomes |> List.tryFind Result.isError with
    | Some(Error e) -> failwith ("the scenario was refused: " + string e)
    | _ -> events, state

let private events = fst scenario
let private final = snd scenario

let private lines (events: GrantEvent list) : string =
    events
    |> List.map (fun event -> Json.serializeCanonical (Codecs.event.Encode event) + "\n")
    |> String.concat ""

let private tags (schema: Schema) : string list =
    let rec find (schema: Schema) =
        match schema.Kind with
        | SchemaKind.Named named -> find named.Body
        | SchemaKind.Union union -> union.Cases |> List.map _.Tag
        | _ -> []

    find schema

let private roundTrips (codec: Codec<'T>) (value: 'T) =
    Codec.decodeText codec (Json.serialize (codec.Encode value)) = Ok value

[<Tests>]
let tests =
    testList
        "Grant codecs"
        [
            test "the scenario appends every kind of event and reaches every status" {
                let seen =
                    events
                    |> List.map (fun event ->
                        match Codecs.event.Encode event with
                        | Json.Object(("kind", Json.String tag) :: _) -> tag
                        | other -> failtest (Json.serialize other))
                    |> List.distinct
                    |> List.sort

                Expect.equal seen (tags Codecs.event.Schema |> List.sort) "every event"

                let statuses =
                    final.Grants
                    |> Map.toList
                    |> List.map (fun (_, e) ->
                        match Codecs.status.Encode e.Status with
                        | Json.Object(("kind", Json.String tag) :: _) -> tag
                        | other -> failtest (Json.serialize other))
                    |> List.distinct
                    |> List.sort

                Expect.equal statuses (tags Codecs.status.Schema |> List.sort) "every grant status"

                let closures =
                    final.Requests
                    |> Map.toList
                    |> List.choose (fun (_, e) ->
                        match e.Status with
                        | RequestStatus.Closed c ->
                            match Codecs.closeReason.Encode c.Why with
                            | Json.Object(("kind", Json.String tag) :: _) -> Some tag
                            | _ -> None
                        | _ -> None)
                    |> List.distinct
                    |> List.sort

                Expect.equal closures (tags Codecs.closeReason.Schema |> List.sort) "every close reason"
                Expect.equal (GrantLog.pending final |> List.map _.Id) [ RequestId "q10" ] "one pending"
            }

            test "version 1's events are read as they were written, forever" {
                let kept = Golden.kept "events-v1.jsonl" (lines events)

                let read =
                    kept.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Codec.decodeText Codecs.event line with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.render issues))
                    |> List.ofArray

                Expect.equal read events "the same events"
                Expect.equal (lines read) kept "written back the same"
                Expect.equal (GrantLog.replay read) final "the same state"
            }

            test "the state's JSON" {
                Golden.expect "state.json" (Json.serializeIndented (Codecs.state.Encode final) + "\n")
                Expect.isTrue (roundTrips Codecs.state final) "round trip"
            }

            test "the commands' JSON round-trips" {
                for c in everything do
                    Expect.isTrue (roundTrips Codecs.command c) (string c)
            }

            test "answers and asks read as clients write them" {
                let read codec text =
                    Codec.decodeText codec text |> Result.mapError Issue.render

                Expect.equal
                    (read Codecs.answer """{"effect":"allow","scope":{"kind":"session"}}""")
                    (Ok
                        {
                            Effect = GrantEffect.Allow
                            Scope = ScopeChoice.Session
                            Resource = None
                            Expires = None
                            Reason = None
                        })
                    "a session answer"

                Expect.equal
                    (read Codecs.answer """{"effect":"deny","scope":{"kind":"repo","path":"src"},"resource":"src/**"}""")
                    (Ok
                        {
                            Effect = GrantEffect.Deny
                            Scope = ScopeChoice.Repo(Some "src")
                            Resource = Some "src/**"
                            Expires = None
                            Reason = None
                        })
                    "a repo answer"

                Expect.isError (read Codecs.answer """{"effect":"maybe","scope":{"kind":"once"}}""") "a bad effect"

                Expect.equal
                    (read
                        Codecs.ask
                        """{"key":"k","action":"shell.exec","resource":"ls","requester":{"principal":"user:alice"},"summary":"List"}""")
                    (Ok(
                        { ask "k" shellExec "ls" GrantContext.none with
                            Requester =
                                {
                                    Principal = alice
                                    Agent = None
                                    Extension = None
                                }
                            Summary = "List"
                        }
                    ))
                    "defaults"

                Expect.isError
                    (read Codecs.query """{"action":"Shell.Exec","resource":"ls"}""")
                    "action names are lowercase-led"
            }

            test "action definitions and policy rules" {
                for def in Actions.builtInDefs do
                    Expect.isTrue (roundTrips Codecs.actionDef def) def.Name.Value

                Expect.isError
                    (Codec.decodeText
                        Codecs.actionDef
                        """{"name":"ext.thing","resource":"name","description":"x","scopes":["host"]}""")
                    "once is required"

                let rule =
                    {
                        Id = "host/no-push"
                        Action = shellExec
                        Resource = "git push **"
                        Effect = PolicyEffect.Deny
                        Reason = "review first"
                    }

                Expect.isTrue (roundTrips Codecs.policyRule rule) "rule"
            }

            test "errors keep their codes" {
                let errors =
                    [
                        GrantError.UnknownAction "x.y"
                        GrantError.InvalidResource("a", "b")
                        GrantError.InvalidPattern("a", "b")
                        GrantError.InvalidRequest "r"
                        GrantError.InvalidAnswer "r"
                        GrantError.InvalidGrant "r"
                        GrantError.UnknownRequest(RequestId "q")
                        GrantError.NotPending(RequestId "q")
                        GrantError.UnknownGrant(GrantId "g")
                        GrantError.NotActive(GrantId "g")
                        GrantError.NotAPerson "system"
                        GrantError.NotDue "x"
                        GrantError.DuplicateId "g"
                        GrantError.Conflict("grants.not-pending", "m")
                        GrantError.Unavailable "stopped"
                    ]

                Expect.equal
                    (errors |> List.map _.Code |> List.distinct |> List.length)
                    (tags Codecs.error.Schema).Length
                    "one code per case"

                for e in errors do
                    Expect.isTrue (roundTrips Codecs.error e) e.Code
            }

            testPropertyWithConfig config "events of any script round-trip"
            <| Prop.forAll (Arb.fromGen Scripts.script) (fun commands ->
                let _, events, _ = run GrantLog.initial commands

                events |> List.forall (roundTrips Codecs.event)
                && commands |> List.forall (roundTrips Codecs.command))
        ]
