/// The strategy's pure parts: tools from codecs, the loop guard, line edits, receipts, the merged
/// history, the checks of a result, questions and the recorded surface.
module Fx.CodeAct.Tests.UnitTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Envd
open Fx.Interpreters
open Fx.Sessions
open Fx.Prompt
open Fx.Variants
open Fx.CodeAct

let private call (name: string) (arguments: string) : ToolCall =
    {
        Id = "c1"
        Name = name
        Arguments = arguments
        State = None
    }

let private meta (cause: EventRef option) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 0L
        By = Attribution.system "test"
        Key = IdempotencyKey "k"
        Cause = cause
    }

[<Tests>]
let tests =
    testList
        "CodeAct units"
        [
            test "the surfaces' tools come from codecs" {
                let cellsOnly = Tools.specs ActionSurface.CellsOnly "TypeScript"
                Expect.equal (cellsOnly |> List.map (fun t -> t.Name)) [ "cell" ] "cells-only"

                Expect.equal
                    (Tools.specs ActionSurface.Hybrid "TypeScript" |> List.map (fun t -> t.Name))
                    [ "cell"; "read"; "edit"; "bash" ]
                    "hybrid"

                Expect.equal
                    (Json.serializeCanonical cellsOnly.Head.Parameters)
                    (Json.serializeCanonical (Schema.toJsonSchema Tools.cellCodec.Schema))
                    "the schema of the codec"

                Expect.stringContains cellsOnly.Head.Description "TypeScript code" "the language"
            }

            test "tool calls decode into actions, or say why not" {
                Expect.equal
                    (Tools.decode ActionSurface.CellsOnly (call "cell" """{"code":"x"}"""))
                    (Ok(ToolAction.Cell "x"))
                    "a cell"

                Expect.equal
                    (Tools.decode ActionSurface.Hybrid (call "read" """{"path":"a.txt","offset":2}"""))
                    (Ok(
                        ToolAction.Read
                            {
                                Path = "a.txt"
                                Offset = Some 2
                                Limit = None
                            }
                    ))
                    "a read"

                Expect.equal
                    (Tools.decode ActionSurface.Hybrid (call "edit" """{"path":"a","old":"x","new":"y"}"""))
                    (Ok(
                        ToolAction.Edit
                            {
                                Path = "a"
                                Old = "x"
                                New = "y"
                                All = false
                            }
                    ))
                    "an edit"

                match Tools.decode ActionSurface.CellsOnly (call "bash" """{"command":"ls"}""") with
                | Error message -> Expect.stringContains message "There is no tool named \"bash\"" "not on cells-only"
                | Ok action -> failtest $"{action}"

                match Tools.decode ActionSurface.Hybrid (call "edit" """{"path":"a"}""") with
                | Error message -> Expect.stringContains message "old" "the missing field"
                | Ok action -> failtest $"{action}"

                match Tools.decode ActionSurface.Hybrid (call "cell" "{\"code\": ") with
                | Error message -> Expect.stringContains message "not JSON" "cut arguments"
                | Ok action -> failtest $"{action}"
            }

            test "the loop guard warns one before the limit and stops at it; 0 turns it off" {
                let print = LoopGuard.fingerprint "cell" """{"code":"x"}""" [ Piece.Text "same" ]

                Expect.equal
                    print
                    (LoopGuard.fingerprint "cell" """{ "code" : "x" }""" [ Piece.Text "same" ])
                    "arguments compare canonically"

                Expect.notEqual
                    print
                    (LoopGuard.fingerprint "cell" """{"code":"x"}""" [ Piece.Text "other" ])
                    "results count"

                let g1, v1 = LoopGuard.observe 3 print LoopGuard.empty
                let g2, v2 = LoopGuard.observe 3 print g1
                let _, v3 = LoopGuard.observe 3 print g2
                Expect.equal [ v1; v2; v3 ] [ LoopVerdict.Fine; LoopVerdict.Warn 2; LoopVerdict.Stop 3 ] "verdicts"

                let off =
                    [ 1..5 ]
                    |> List.fold (fun (g, _) _ -> LoopGuard.observe 0 print g) (LoopGuard.empty, LoopVerdict.Fine)
                    |> snd

                Expect.equal off LoopVerdict.Fine "off"
            }

            test "an edit becomes the lines between the common head and tail" {
                Expect.equal
                    (Hybrid.lineEdit [ "a"; "b"; "c" ] [ "a"; "B"; "c" ])
                    { Line = 2; Count = 1; Lines = [ "B" ] }
                    "one line"

                Expect.equal
                    (Hybrid.lineEdit [ "a"; "c" ] [ "a"; "b"; "c" ])
                    { Line = 2; Count = 0; Lines = [ "b" ] }
                    "an insertion"

                Expect.equal
                    (Hybrid.lineEdit [ "a"; "b"; "b" ] [ "a"; "b" ])
                    { Line = 3; Count = 1; Lines = [] }
                    "a deletion"

                let applied =
                    TextPatch.applyText "x\r\ny\r\nz\r\n" [ Hybrid.lineEdit [ "x"; "y"; "z" ] [ "x"; "Y"; "z" ] ]

                Expect.equal applied (Ok("x\r\nY\r\nz\r\n", 3)) "line endings kept"
            }

            test "a read shows numbered lines as a file piece, and says how to read on" {
                let file: FileText =
                    {
                        Path = "src/a.fs"
                        Text = "one\ntwo\nthree\nfour\n"
                        Lines = 4
                        Size = 19L
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                    }

                match Receipts.read file (Some 2) (Some 2) 100 with
                | [ Piece.File view; Piece.Text more ] ->
                    Expect.equal view.Text "     2\ttwo\n     3\tthree" "numbered"
                    Expect.equal view.Lines [ LineRange.create 2 3 ] "the lines shown"
                    Expect.stringContains more "Lines 2–3 of 4. Read on with offset 4." "more"
                | other -> failtest $"{other}"

                match Receipts.read file None None 100 with
                | [ Piece.File view ] -> Expect.isEmpty view.Lines "the whole file"
                | other -> failtest $"{other}"

                match Receipts.read file None None 3 with
                | [ Piece.File _; Piece.Text more ] -> Expect.stringContains more "offset 4" "bounded"
                | other -> failtest $"{other}"
            }

            test "context events go after the session event their cause names" {
                let session = Session.stream "s1"
                let context = ContextLog.stream "s1"

                let sessionEvent seq : Recorded<Event> =
                    {
                        Stream = session
                        Seq = seq
                        Event = Event.Closed(Attribution.system "test")
                        Meta = meta None
                    }

                let contextEvent seq cause : Recorded<ContextEvent> =
                    {
                        Stream = context
                        Seq = seq
                        Event = ContextEvent.Unpinned(string seq)
                        Meta = meta (Some(EventRef.create session cause))
                    }

                let refs =
                    ContextLog.merge
                        session
                        [ sessionEvent 1L; sessionEvent 2L; sessionEvent 3L ]
                        [ contextEvent 1L 1L; contextEvent 2L 1L; contextEvent 3L 3L ]
                        None
                    |> List.map (fun item -> string item.Ref)

                Expect.equal
                    refs
                    [
                        "session/s1#1"
                        "context/s1#1"
                        "context/s1#2"
                        "session/s1#2"
                        "session/s1#3"
                        "context/s1#3"
                    ]
                    "merged"

                let upTo =
                    ContextLog.merge
                        session
                        [ sessionEvent 1L; sessionEvent 2L ]
                        [ contextEvent 1L 1L; contextEvent 2L 2L ]
                        (Some 1L)
                    |> List.map (fun item -> string item.Ref)

                Expect.equal upTo [ "session/s1#1"; "context/s1#1" ] "a parent up to the fork point"
            }

            test "a result's checks: a summary, evidence when required, open requests, known jobs" {
                let empty = Session.initial

                let check policy result =
                    Ensure.all policy empty |> List.collect (fun c -> c result)

                Expect.isEmpty (check EvidencePolicy.Optional (TurnResult.Done("did it", [], None))) "fine"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.Done(" ", [], None))) "blank summary"
                Expect.isNonEmpty (check EvidencePolicy.Required (TurnResult.Done("did it", [], None))) "no evidence"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.NeedInput [ "r1" ])) "unknown request"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.Waiting([ "j1" ], "x"))) "unknown job"

                let issue =
                    check EvidencePolicy.Optional (TurnResult.NeedInput [ "r1" ]) |> List.head

                Expect.stringContains (string issue) "$.result.requests[0]" "at the result's path"
            }

            test "a question is an input request of its kind, bounded" {
                let request =
                    Questions.request
                        "q-1-2"
                        {
                            Question = String.replicate 9000 "a"
                            Reason = "reply"
                        }

                Expect.equal request.Kind Questions.KindId "kind"
                Expect.equal request.Reply Reply.AsInput "an input"
                Expect.equal request.Lifetime Lifetime.Session "across turns"

                match Questions.payload.Decode request.Payload with
                | Ok question -> Expect.isLessThanOrEqual question.Question.Length Questions.MaxChars "bounded"
                | Error issues -> failtest $"{issues}"

                Expect.isSome (RequestKinds.tryFind Questions.KindId Questions.kinds) "registered"
            }

            test "a conversation keeps the surface its frame recorded" {
                let sections = Instructions.sections ActionSurface.Hybrid "interpreter" None
                Expect.equal (Instructions.recordedSurface sections) (Some ActionSurface.Hybrid) "hybrid"

                Expect.equal
                    (Instructions.recordedSurface (Instructions.sections ActionSurface.CellsOnly "x" None))
                    (Some ActionSurface.CellsOnly)
                    "cells-only"

                Expect.equal (Instructions.recordedSurface []) None "none yet"

                Expect.equal
                    (sections |> List.map (fun s -> s.Id))
                    [ "instructions"; "tools"; "interpreter" ]
                    "the prefix's sections"
            }

            test "a sandbox that confines the hands' processes is described, from its policy" {
                let policy: Fx.Sdk.SandboxPolicy =
                    {
                        Workspace = "/src/app"
                        WorkspaceAccess = Fx.Sdk.WorkspaceAccess.ReadWrite
                        Writable = [ "/home/u/.fx/environments/e1/owned" ]
                        Readable = []
                        Protected = [ "/src/app/.git/hooks"; "/src/app/.git/config"; "/src/app/.fx" ]
                        Denied = [ "/home/u/.fx"; "/home/u/.ssh"; "/src/app/.env" ]
                        Temp = Some "/home/u/.fx/environments/e1/tmp"
                        Network = Fx.Sdk.NetworkAccess.Allowed
                    }

                let sections =
                    Instructions.sections ActionSurface.Hybrid "interpreter" (Some policy)

                Expect.equal
                    (sections |> List.map (fun s -> s.Id))
                    [ "instructions"; "tools"; "interpreter"; "sandbox" ]
                    "the sandbox last"

                let text = Instructions.sandbox policy

                Expect.stringContains
                    text
                    "except `.git/hooks`, `.git/config` and `.fx`: they stay read-only"
                    "what stays read-only, relative to the workspace"

                Expect.stringContains text "Device or resource busy" "how git's refusal reads"
                Expect.stringContains text "- `.env` cannot be read." "hidden in the workspace"
                Expect.stringContains text "Credentials are hidden" "credentials"
                Expect.stringContains text "the home directory included, is read-only" "the home"
                Expect.stringContains text "The network is available." "the network"
                Expect.isFalse (text.Contains "/home/u") "no paths outside the workspace"

                let reviewer =
                    Instructions.sandbox
                        { policy with
                            WorkspaceAccess = Fx.Sdk.WorkspaceAccess.ReadOnly
                            Network = Fx.Sdk.NetworkAccess.Denied
                        }

                Expect.stringContains reviewer "The workspace is read-only" "a reviewer"
                Expect.stringContains reviewer "There is no network" "no network"
            }

            test "quotes carry provenance, are withheld when a source is excluded, and are never seen" {
                let quote =
                    Piece.Quote
                        {
                            Text = "SECRET=1"
                            Sources = [ ContentSource.file ".env" ]
                        }

                Expect.equal (Piece.sources quote) [ ContentSource.file ".env" ] "sources"

                match Piece.parts [ quote ] with
                | [ Part.Text t ] -> Expect.equal t.Sources [ ContentSource.file ".env" ] "the part quotes it"
                | other -> failtest $"{other}"

                let exclusions =
                    match
                        Exclusions.create
                            [
                                {
                                    Origin = "host"
                                    Patterns = [ "*.env" ]
                                }
                            ]
                    with
                    | Ok e -> e
                    | Error issues -> failtest $"{issues}"

                Expect.equal (Withhold.pieces exclusions [ quote ]) ([ Piece.Withheld ".env" ], true) "withheld"
                Expect.equal (SeenRanges.addPieces [ quote ] SeenRanges.empty) SeenRanges.empty "not seen"

                match Piece.codec.Decode(Piece.codec.Encode quote) with
                | Ok decoded -> Expect.equal decoded quote "round trip"
                | Error issues -> failtest $"{issues}"
            }

            test "the strategy's slots are registered and read" {
                let variants = Fx.Variants.Fakes.FakeVariants.create CodeActSlots.read []

                let settings =
                    (Eff.run () (CodeActSettings.ofVariants variants)).GetAwaiter().GetResult()

                Expect.equal settings.Surface ActionSurface.CellsOnly "the default surface"
                Expect.equal settings.TextOnly TextOnlyPolicy.Nudge "text only"
                Expect.equal settings.MaxSteps 100 "steps"
                Expect.equal settings.Model None "no model until configured"

                let ids = CodeActSlots.all |> List.map (fun s -> s.Id.Value)
                Expect.allEqual (ids |> List.map (fun id -> id.StartsWith "codeact.")) true "namespaced"
            }
        ]
