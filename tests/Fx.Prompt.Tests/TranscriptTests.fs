/// The transcript: the fold of a session's log and its context events into the history's entries.
module Fx.Prompt.Tests.TranscriptTests

open Expecto
open Fx.Machines
open Fx.Models
open Fx.Sessions
open Fx.Prompt
open Fx.Prompt.Tests.Support

let private cfg = config Exclusions.none breakpointCaching

let private contents (sim: Sim) : EntryContent list =
    Transcript.entries sim.Transcript |> List.map _.Content

let private texts (inputs: Input list) = inputs |> List.map Render.inputText

[<Tests>]
let tests =
    testList
        "Transcript"
        [
            test "inputs enter the history when they are delivered, not when they arrive" {
                let sim =
                    start "s1" cfg
                    |> steer "first"
                    |> call [ Part.text "working" ]
                    |> steer "also this"
                    |> queue "later"
                    |> queue "withdrawn"

                let withdrawn = sim.State.Queue |> List.last
                let sim = sim |> send alice (Command.Withdraw withdrawn.Id) |> boundary

                match contents sim with
                | [ EntryContent.Inputs(1, 0, first); EntryContent.Reply _; EntryContent.Inputs(1, 1, steered) ] ->
                    Expect.equal (texts first) [ "first" ] "the turn's input"
                    Expect.equal (texts steered) [ "also this" ] "the steer, at the boundary"
                | other -> failtest (show other)

                let sim = sim |> finish "done"

                match contents sim |> List.last with
                | EntryContent.Inputs(2, 0, queued) -> Expect.equal (texts queued) [ "later" ] "the queued prompt"
                | other -> failtest (show other)

                Expect.isEmpty sim.Transcript.Pending "nothing pending"
            }

            test "calls left without a result are closed with why" {
                let sim =
                    start "s1" cfg
                    |> steer "go"
                    |> call [ toolCall "c1" "1"; toolCall "c2" "2" ]
                    |> returns "c1" [ Piece.Text "one" ]

                Expect.equal (sim.Transcript.Open |> List.map _.Id) [ "c2" ] "c2 open"

                let sim = sim |> interrupt

                match contents sim |> List.skip 2 with
                | [ EntryContent.Receipt(_, true)
                    EntryContent.Closed([ c2 ], reason)
                    EntryContent.Ended(1, TurnEnd.Interrupted) ] ->
                    Expect.equal c2.Id "c2" "c2"
                    Expect.equal reason "the turn was interrupted" "why"
                | other -> failtest (show other)

                Expect.isEmpty sim.Transcript.Open "none open"
            }

            test "a result for a call that is not open is read as text" {
                let sim =
                    start "s1" cfg
                    |> steer "go"
                    |> call [ toolCall "c1" "1" ]
                    |> returns "c1" [ Piece.Text "one" ]
                    |> returns "c1" [ Piece.Text "again" ]
                    |> returns "zz" [ Piece.Text "who?" ]

                match contents sim |> List.skip 2 with
                | [ EntryContent.Receipt(_, true); EntryContent.Receipt(_, false); EntryContent.Receipt(_, false) ] ->
                    ()
                | other -> failtest (show other)
            }

            test "a completed turn adds no entry of its own; a failed one does" {
                let sim = start "s1" cfg |> steer "go" |> call [ Part.text "done" ] |> finish "ok"
                Expect.equal sim.Transcript.Count 2 "inputs and the reply"

                let sim = sim |> steer "again" |> fail "model down"

                match contents sim |> List.last with
                | EntryContent.Ended(2, TurnEnd.Failed "model down") -> ()
                | other -> failtest (show other)
            }

            test "pins are kept in pin order; pinning a name again moves it to the end" {
                let sim =
                    start "s1" cfg
                    |> pin "a" (Pin.Value [ Piece.Text "1" ])
                    |> pin "b" (Pin.File("src/b.fs", []))
                    |> pin "a" (Pin.Value [ Piece.Text "2" ])

                Expect.equal (sim.Transcript.Pins |> List.map _.Spec.Name) [ "b"; "a" ] "order"

                let sim = sim |> record (ContextEvent.Unpinned "b")
                Expect.equal (sim.Transcript.Pins |> List.map _.Spec.Name) [ "a" ] "unpinned"
                Expect.equal (Views.pinnedFiles sim.Transcript) [] "no live files"
            }

            test "collapses must keep calls with their results and nest" {
                let sim =
                    start "s1" cfg
                    |> steer "one"
                    |> call [ toolCall "c1" "1" ]
                    |> returns "c1" [ Piece.Text "r1" ]
                    |> call [ Part.text "done one" ]
                    |> finish "1"
                    |> steer "two"
                    |> call [ Part.text "done two" ]
                    |> finish "2"

                let refs = Transcript.entries sim.Transcript |> List.map _.Ref |> Array.ofList

                let collapse (a: int) (b: int) (sim: Sim) =
                    record
                        (ContextEvent.Collapsed
                            {
                                From = refs[a]
                                To = refs[b]
                                Summary = [ Piece.Text $"summary {a}-{b}" ]
                            })
                        sim

                // Entries: 0 inputs, 1 reply (c1), 2 receipt, 3 reply, 4 inputs, 5 reply.
                let split = sim |> collapse 0 1
                Expect.isEmpty split.Transcript.Collapses "refused"
                Expect.stringContains split.Transcript.Issues.Head "split a tool call" "why"

                let inner = sim |> collapse 0 3
                Expect.equal inner.Transcript.Collapses.Length 1 "applied"

                let overlapping = inner |> collapse 3 4
                Expect.equal overlapping.Transcript.Collapses.Length 1 "overlap refused"
                Expect.stringContains overlapping.Transcript.Issues.Head "overlaps" "why"

                let again = inner |> collapse 0 3
                Expect.stringContains again.Transcript.Issues.Head "collapsed already" "why"

                let outer = inner |> collapse 0 5
                Expect.equal outer.Transcript.Collapses.Length 2 "nested"

                match Transcript.visible outer.Transcript with
                | [ Visible.Summary range ] -> Expect.equal (range.Start, range.End) (0, 5) "the outer one shows"
                | other -> failtest (show other)

                match Transcript.visible inner.Transcript with
                | [ Visible.Summary range; Visible.Entry e4; Visible.Entry e5 ] ->
                    Expect.equal (range.Start, range.End, e4.Index, e5.Index) (0, 3, 4, 5) "summary, then the rest"
                | other -> failtest (show other)

                let missing =
                    sim
                    |> record (
                        ContextEvent.Collapsed
                            {
                                From = EventRef.create (StreamId "session/nope") 1L
                                To = refs[1]
                                Summary = []
                            }
                    )

                Expect.stringContains missing.Transcript.Issues.Head "not in the history" "why"
            }

            test "reconcile frames once, then states changes as updates" {
                let t = Transcript.empty

                match Transcript.reconcile sections t with
                | Some(ContextEvent.Framed framed) -> Expect.equal framed sections "framed"
                | other -> failtest (show other)

                let t =
                    Transcript.applyContext (EventRef.create (StreamId "c/1") 1L) (ContextEvent.Framed sections) t

                Expect.isNone (Transcript.reconcile sections t) "nothing changed"
                Expect.isNone (Transcript.reconcile (List.rev sections) t) "order alone is no change"

                let changed = Sections.instructions "Be brief."
                let added = Sections.repoMap [ Piece.Text "src/" ]

                let next = [ changed; sections[1]; added ] // the agents section went away

                match Transcript.reconcile next t with
                | Some(ContextEvent.Updated update) ->
                    Expect.equal update.Changed [ changed; added ] "changed and added"
                    Expect.equal update.Removed [ "agents" ] "removed"

                    let t =
                        Transcript.applyContext (EventRef.create (StreamId "c/1") 2L) (ContextEvent.Updated update) t

                    Expect.equal t.Frame (Some sections) "the frame stays"
                    Expect.equal t.Current [ changed; sections[1]; added ] "the current sections"
                    Expect.isNone (Transcript.reconcile next t) "up to date"
                | other -> failtest (show other)
            }

            test "a fork's history is its parent's up to the fork, then its own" {
                let parent =
                    start "p" cfg
                    |> steer "parent one"
                    |> call [ Part.text "p1" ]
                    |> finish "1"
                    |> steer "parent two"
                    |> call [ Part.text "p2" ]
                    |> finish "2"

                // Fork after turn 1: the parent's session events up to it, and its context events then.
                let forkAt =
                    items parent
                    |> List.pick (fun item ->
                        match item with
                        | LogItem.Session(ref, Event.TurnEnded(1, _, _)) -> Some ref.Seq
                        | _ -> None)

                let upToFork =
                    items parent
                    |> List.takeWhile (fun item ->
                        match item with
                        | LogItem.Session(ref, _) -> ref.Seq <= forkAt
                        | LogItem.Context _ -> true)
                    |> List.filter (fun item ->
                        match item with
                        | LogItem.Context(_, ContextEvent.Replied r) -> r.Turn = 1
                        | _ -> true)

                let child =
                    { start "c" cfg with
                        Transcript = Transcript.ofItems upToFork
                    }
                    |> steer "child one"
                    |> call [ Part.text "c1" ]

                let said =
                    Transcript.entries child.Transcript
                    |> List.collect (fun e ->
                        match e.Content with
                        | EntryContent.Inputs(_, _, inputs) -> texts inputs
                        | EntryContent.Reply r ->
                            r.Parts
                            |> List.choose (fun p ->
                                match p with
                                | Part.Text t -> Some t.Text
                                | _ -> None)
                        | _ -> [])

                Expect.equal said [ "parent one"; "p1"; "child one"; "c1" ] "the chain"

                let streams =
                    Transcript.entries child.Transcript |> List.map _.Ref.Stream |> List.distinct

                Expect.equal streams.Length 4 "each event keeps its own stream"
            }
        ]
