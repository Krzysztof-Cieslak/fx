/// Compaction (spec §5, T206): where a summary can cut the history, which cut to make, the summary
/// request that shares the conversation's cache, and what the history shows after the collapse.
module Fx.Prompt.Tests.CompactionTests

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Models
open Fx.Sessions
open Fx.Prompt
open Fx.Prompt.Tests.Support

let private cfg = config Exclusions.none breakpointCaching

/// A step: the boundary, a reply with one cell, and its receipt.
let private step (i: int) (sim: Sim) : Sim =
    sim
    |> boundary
    |> call [ toolCall $"c{i}" $"code {i}" ]
    |> returns $"c{i}" [ Piece.Text $"result {i}" ]

/// A turn of `steps` steps, still running.
let private session (cfg: RenderConfig) (steps: int) : Sim =
    [ 1..steps ]
    |> List.fold (fun sim i -> step i sim) (start "s1" cfg |> steer "Fix the bug")

let private withoutLast (request: ChatRequest) : ChatRequest =
    let last = List.last request.Messages

    { request with
        Messages =
            (request.Messages |> List.truncate (request.Messages.Length - 1))
            @ [
                { last with
                    Parts = last.Parts |> List.truncate (last.Parts.Length - 1)
                }
            ]
    }

let private lastText (request: ChatRequest) : string =
    match request.Messages |> List.last |> _.Parts |> List.last with
    | Part.Text text -> text.Text
    | other -> failtest $"the request ends with {other}"

/// The visible replies' entries.
let private replyEntries (transcript: Transcript) : int list =
    Transcript.visible transcript
    |> List.choose (fun v ->
        match v with
        | Visible.Entry({ Content = EntryContent.Reply _ } as entry) -> Some entry.Index
        | _ -> None)

let private summaryOf (cut: Cut) (text: string) (sources: ContentSource list) : ContextEvent =
    ContextEvent.Collapsed(Compaction.collapse cut text sources)

let private requestText (request: ChatRequest) : string = requestJson request

[<Tests>]
let tests =
    testList
        "Compaction"
        [
            test "tokens are estimated from characters, parts and tools" {
                Expect.equal (Tokens.ofChars 0) 0 "nothing"
                Expect.equal (Tokens.ofChars 7) 2 "3.5 characters a token"
                Expect.equal (Tokens.ofChars 8) 3 "rounded up"
                Expect.equal (Tokens.ofPart (Part.CacheBreakpoint CacheTtl.Short)) 0 "breakpoints are free"

                let small =
                    Tokens.ofRequest (ChatRequest.create model [ Message.user [ Part.text "hi" ] ])

                let large =
                    Tokens.ofRequest (
                        ChatRequest.create model [ Message.user [ Part.text (String.replicate 700 "a") ] ]
                    )

                Expect.isGreaterThan large (small + 190) "longer text, more tokens"
                Expect.isGreaterThan (Tokens.ofRequest template) 0 "tools count"
            }

            test "cuts lie before each visible reply but the first, and at the end; each collapse applies" {
                let sim = session cfg 3
                let rendered = render sim
                let cuts = Compaction.cuts sim.Transcript rendered template.Tools
                let replies = replyEntries sim.Transcript
                let entries = Transcript.entries sim.Transcript |> Array.ofList

                Expect.equal (cuts |> List.map _.Last) [ replies[1] - 1; replies[2] - 1; entries.Length - 1 ] "where"

                Expect.equal
                    (cuts |> List.map _.Mark)
                    [ Some rendered.Replies[1].End; Some rendered.Replies[2].End; None ]
                    "marks"

                for cut in cuts do
                    Expect.equal cut.From entries[0].Ref "from the first entry"
                    Expect.equal cut.To entries[cut.Last].Ref "to the last one it replaces"
                    let collapsed = sim |> record (summaryOf cut "summary" [])
                    Expect.equal collapsed.Transcript.Collapses.Length 1 "applies"
                    Expect.isEmpty collapsed.Transcript.Issues "no issue"

                let totals = cuts |> List.map (fun c -> c.Head + c.Tail) |> List.distinct
                Expect.equal totals.Length 1 "head and tail split one estimate"
                Expect.isTrue (cuts |> List.pairwise |> List.forall (fun (a, b) -> a.Tail > b.Tail)) "later keeps less"
                Expect.equal (List.last cuts).Tail 0 "the end keeps nothing"
            }

            test "no cut while a call is open, or before a reply to summarize" {
                let first = start "s1" cfg |> steer "Fix the bug" |> boundary
                Expect.isEmpty (Compaction.cuts first.Transcript (render first) template.Tools) "nothing yet"

                let calling = first |> call [ toolCall "c1" "code" ]
                Expect.isEmpty (Compaction.cuts calling.Transcript (render calling) template.Tools) "a call is open"

                let answered = calling |> returns "c1" [ Piece.Text "ok" ]

                match Compaction.cuts answered.Transcript (render answered) template.Tools with
                | [ cut ] -> Expect.isNone cut.Mark "only the end"
                | other -> failtest $"{other}"
            }

            test "the summary request is what the call that ended at the cut sent, then the instruction" {
                let sim = session cfg 3
                let calls = sim.Calls |> List.rev |> List.map snd
                let rendered = render sim
                let cuts = Compaction.cuts sim.Transcript rendered template.Tools

                // Before the second reply: the second call's request, breakpoints and all.
                let summary = Compaction.request cfg template rendered cuts[0] "Summarize."
                Expect.equal (lastText summary) "Summarize." "the instruction last"
                Expect.equal (withoutLast summary) calls[1] "the second call's request, as sent"
                Expect.equal summary.Cache.Key calls[1].Cache.Key "the same cache key"

                let third = Compaction.request cfg template rendered cuts[1] "Summarize."
                Expect.equal (withoutLast third) calls[2] "the third call's"

                // At the end: the request a call would send now.
                let atEnd = Compaction.request cfg template rendered (List.last cuts) "Summarize."
                Expect.equal (withoutLast atEnd) (Render.request cfg template rendered) "the request of now"
            }

            test "after the collapse the prefix is unchanged and the summary stands for what it replaced" {
                let sim = session cfg 3 |> boundary
                let before = request sim
                let rendered = render sim
                let cut = (Compaction.cuts sim.Transcript rendered template.Tools)[1]
                let collapsed = sim |> record (summaryOf cut "We fixed steps 1 and 2." [])
                let after = request collapsed

                Expect.equal after.System before.System "the same prefix"
                Expect.equal after.Cache.Key before.Cache.Key "the same key"
                let text = requestText after
                Expect.stringContains text "[Summary of an earlier part of this conversation:]" "the header"
                Expect.stringContains text "We fixed steps 1 and 2." "the summary"
                Expect.isFalse (text.Contains "Fix the bug") "the first prompt is summarized"
                Expect.isFalse (text.Contains "result 2") "so is the second step"
                Expect.stringContains text "result 3" "the third step stays"
                Expect.isLessThan (Tokens.ofRequest after) (Tokens.ofRequest before) "smaller"
            }

            test "pins survive a collapse: the pins view shows them again" {
                let sim =
                    start "s1" cfg
                    |> steer "Fix the bug"
                    |> pin "plan" (Pin.Value [ Piece.Text "1. read 2. fix" ])
                    |> step 1
                    |> step 2
                    |> step 3

                let pinned (request: ChatRequest) =
                    let text = requestText request

                    (text.Length - text.Replace("1. read 2. fix", "").Length)
                    / "1. read 2. fix".Length

                Expect.equal (pinned (request sim)) 1 "shown once, before the first reply"
                let cut = (Compaction.cuts sim.Transcript (render sim) template.Tools)[0]
                let collapsed = sim |> record (summaryOf cut "summary" [])
                Expect.equal (pinned (request collapsed)) 1 "shown again after the collapse hid it"
            }

            test "withheld content stays withheld; the summary quotes its sources and is withheld with them" {
                let exclusions = exclusionsOf [ "secret/" ]
                let cfg = config exclusions breakpointCaching

                let sim =
                    start "s1" cfg
                    |> steer "Fix the bug"
                    |> boundary
                    |> call [ toolCall "c1" "read" ]
                    |> returns "c1" [ Piece.File(file "secret/key.txt" [] "content of secret/key.txt") ]
                    |> boundary
                    |> call [ toolCall "c2" "read" ]
                    |> returns "c2" [ Piece.File(file "src/a.fs" [ 1, 3 ] "content of src/a.fs") ]
                    |> step 3

                let rendered = render sim
                let cut = Compaction.cuts sim.Transcript rendered template.Tools |> List.last
                let summary = Compaction.request cfg template rendered cut "Summarize."
                Expect.isFalse ((requestText summary).Contains "content of secret/") "never sent to summarize"
                let sources = Compaction.sources summary
                Expect.equal sources [ ContentSource.file "src/a.fs" ] "the files it quotes, whole"

                let collapsed = sim |> record (summaryOf cut "src/a.fs holds the bug." sources)
                Expect.stringContains (requestText (request collapsed)) "src/a.fs holds the bug." "shown"

                // The exclusions change: the summary quoted the file, so it is withheld with it.
                let stricter =
                    { collapsed with
                        Config = config (exclusionsOf [ "secret/"; "src/" ]) breakpointCaching
                    }

                let text = requestText (request stricter)
                Expect.isFalse (text.Contains "src/a.fs holds the bug.") "withheld"
                Expect.stringContains text "[src/a.fs: withheld by content exclusions]" "says so"
            }

            test "the cut keeps the newest history within the limits" {
                let at (n: int64) =
                    EventRef.create (StreamId.create "session" "s1") n

                let cut (last: int) (head: int) (tail: int) =
                    {
                        From = at 1L
                        To = at (int64 last)
                        Last = last
                        Mark = None
                        Head = head
                        Tail = tail
                    }

                let cuts = [ cut 3 100 900; cut 6 400 600; cut 9 700 300; cut 12 1000 0 ]
                Expect.equal (Compaction.choose 300 None cuts |> Option.map _.Last) (Some 9) "the latest that keeps 300"
                Expect.equal (Compaction.choose 0 None cuts |> Option.map _.Last) (Some 12) "keep nothing"
                Expect.equal (Compaction.choose 300 (Some 500) cuts |> Option.map _.Last) (Some 6) "what fits"
                Expect.equal (Compaction.choose 950 None cuts |> Option.map _.Last) (Some 3) "keeps the most"
                Expect.equal (Compaction.choose 300 (Some 50) cuts) None "nothing fits"
            }

            test "a collapse that no longer applies is said so" {
                let sim = session cfg 2
                let cut = (Compaction.cuts sim.Transcript (render sim) template.Tools)[0]
                let collapse = Compaction.collapse cut "summary" []
                Expect.isTrue (Compaction.applies sim.Transcript collapse) "applies"
                let collapsed = sim |> record (ContextEvent.Collapsed collapse)
                Expect.isFalse (Compaction.applies collapsed.Transcript collapse) "not twice"
            }
        ]
