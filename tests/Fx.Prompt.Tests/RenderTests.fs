/// The render: prefix, history and suffix; receipts, breakpoints and the cache key; exclusions, collapses
/// and stale reasoning; views.
module Fx.Prompt.Tests.RenderTests

open Expecto
open Fx.Contracts
open Fx.Models
open Fx.Sessions
open Fx.Prompt
open Fx.Prompt.Tests.Support

let private cfg = config Exclusions.none breakpointCaching

let private texts (parts: Part list) =
    parts
    |> List.choose (fun p ->
        match p with
        | Part.Text t -> Some t.Text
        | _ -> None)

let private reasoning (request: ChatRequest) =
    request.Messages
    |> List.collect _.Parts
    |> List.filter (fun p ->
        match p with
        | Part.Reasoning _ -> true
        | _ -> false)
    |> List.length

/// Where the request's breakpoints are: `system`, or (message, index of the part before it).
let private breakpointPlaces (request: ChatRequest) : string list =
    [
        if request.System |> List.exists (fun p -> p.IsCacheBreakpoint) then
            "system"
        for m, message in List.indexed request.Messages do
            for i, part in List.indexed message.Parts do
                if part.IsCacheBreakpoint then
                    $"{m}.{i}"
    ]

/// One turn with a tool call, its result showing a file, and a final reply.
let private oneTurn (cfg: RenderConfig) =
    start "s1" cfg
    |> steer "fix the bug"
    |> call [ thinking "look first" "sig-1"; toolCall "c1" "read" ]
    |> returns
        "c1"
        [
            Piece.Text "read 3 lines"
            Piece.File(file "src/a.fs" [ 10, 12 ] "let a = 1")
        ]
    |> call [ thinking "now fix" "sig-2"; Part.text "Fixed." ]
    |> finish "fixed"

[<Tests>]
let tests =
    testList
        "Render"
        [
            test "the prefix is the frame, one system part per section, then a breakpoint" {
                let request = oneTurn cfg |> request

                Expect.equal
                    (texts request.System)
                    [
                        "You are a careful coding agent."
                        "Write TypeScript cells."
                        $"Agents you can call:\n- {Fx.Product.kernelGlobal}.agents.explorer: Finds code.\n- {Fx.Product.kernelGlobal}.agents.reviewer: Reviews a change."
                    ]
                    "sections"

                Expect.equal (List.last request.System) (Part.CacheBreakpoint CacheTtl.Short) "the prefix's breakpoint"
            }

            test "tool calls get their results; receipts show files and count as seen" {
                let sim = oneTurn cfg
                let rendered = render sim
                let request = Render.request sim.Config template rendered

                match request.Messages with
                | [ user1; assistant1; user2; assistant2 ] ->
                    Expect.equal (texts user1.Parts) [ "fix the bug" ] "the prompt"
                    Expect.equal assistant1.Role Role.Assistant "assistant"

                    match user2.Parts |> List.filter (fun p -> not p.IsCacheBreakpoint) with
                    | [ Part.ToolResult result ] ->
                        Expect.equal result.CallId "c1" "answers c1"
                        Expect.isFalse result.IsError "ok"

                        match result.Content with
                        | [ ToolContent.Text text ] ->
                            Expect.stringStarts text "read 3 lines\nsrc/a.fs:10-12 (sha256:" "a header"
                            Expect.stringEnds text "\nlet a = 1" "the lines"
                        | other -> failtest (show other)
                    | other -> failtest (show other)

                    Expect.equal (texts assistant2.Parts) [ "Fixed." ] "the reply"
                    Expect.equal (reasoning request) 2 "reasoning replayed"
                | other -> failtest (show other)

                Expect.isTrue
                    (SeenRanges.covers "src/a.fs" (digestOf "src/a.fs") (LineRange.create 10 12) rendered.Seen)
                    "seen"

                Expect.equal rendered.Stale 0 "nothing stale"
            }

            test "each call's whole request is a prefix of the next" {
                let sim =
                    oneTurn cfg
                    |> steer "and the test"
                    |> call [ toolCall "c2" "test" ]
                    |> returns "c2" [ Piece.Text "ok" ]
                    |> call [ Part.text "Done." ]

                let calls = sim.Calls |> List.rev |> List.map snd

                for a, b in List.pairwise calls do
                    Expect.isTrue (isPrefix (flat a) (flat b)) "a prefix"
                    Expect.equal a.System b.System "the same system parts"

                Expect.isTrue (isPrefix (flat (List.last calls)) (flat (request sim))) "and of what follows"
            }

            test "rolling breakpoints: the end, the prefix and the latest earlier ends, within the limit" {
                let sim =
                    oneTurn cfg
                    |> steer "more"
                    |> call [ toolCall "c2" "x" ]
                    |> returns "c2" [ Piece.Text "ok" ]

                let request = request sim
                Expect.equal (breakpoints request) 4 "four"
                Expect.contains (breakpointPlaces request) "system" "after the prefix"

                let last = List.last request.Messages
                Expect.isTrue (List.last last.Parts).IsCacheBreakpoint "at the end"

                let withToolsBreakpoint =
                    Render.request
                        sim.Config
                        { template with
                            Cache =
                                { template.Cache with
                                    Tools = Some CacheTtl.Short
                                }
                        }
                        (render sim)

                Expect.equal (breakpoints withToolsBreakpoint) 3 "the tools' breakpoint counts"

                let prefixOnly =
                    Render.request
                        { sim.Config with
                            Cache =
                                {
                                    Breakpoints = Breakpoints.Prefix
                                    PrefixTtl = CacheTtl.Long
                                }
                        }
                        template
                        (render sim)

                Expect.equal (breakpointPlaces prefixOnly) [ "system" ] "prefix only"
                Expect.equal (List.last prefixOnly.System) (Part.CacheBreakpoint CacheTtl.Long) "long"

                let off =
                    Render.request
                        { sim.Config with
                            Cache =
                                { sim.Config.Cache with
                                    Breakpoints = Breakpoints.Off
                                }
                        }
                        template
                        (render sim)

                Expect.equal (breakpoints off) 0 "none"
            }

            test "the previous request's end is where this request has a breakpoint" {
                let sim =
                    oneTurn cfg
                    |> steer "more"
                    |> call [ toolCall "c2" "x" ]
                    |> returns "c2" [ Piece.Text "ok" ]

                let previous = sim.Calls.Head |> snd
                let current = request sim

                let contentParts (request: ChatRequest) =
                    request.Messages
                    |> List.sumBy (fun m -> m.Parts |> List.filter (fun p -> not p.IsCacheBreakpoint) |> List.length)

                let n = contentParts previous
                let raw = current.Messages |> List.collect _.Parts |> Array.ofList
                let mutable count = 0
                let mutable after = None

                for i in 0 .. raw.Length - 1 do
                    if not raw[i].IsCacheBreakpoint then
                        count <- count + 1

                        if count = n then
                            after <- Some(i + 1)

                match after with
                | Some i when i < raw.Length -> Expect.isTrue raw[i].IsCacheBreakpoint "a breakpoint where it ended"
                | other -> failtest (show other)
            }

            test "models without breakpoints get the prefix's key, stable under the suffix" {
                let keyed = config Exclusions.none keyedCaching
                let sim = oneTurn keyed |> pin "notes" (Pin.Value [ Piece.Text "remember" ])

                let one = request sim

                let two =
                    Render.request
                        sim.Config
                        template
                        (Render.render
                            sim.Config
                            sim.Transcript
                            { Live.none with
                                Status = [ "budget", "$1" ]
                            })

                Expect.equal (breakpoints one) 0 "no breakpoints"
                Expect.isSome one.Cache.Key "a key"
                Expect.equal one.Cache.Key two.Cache.Key "the suffix does not change it"
                Expect.notEqual (requestJson one) (requestJson two) "though the suffixes differ"
                Expect.equal one.Cache.Key.Value.Length 32 "32 hex digits"

                let otherTools =
                    Render.request
                        sim.Config
                        { template with
                            Tools = template.Tools @ template.Tools
                        }
                        (render sim)

                Expect.notEqual otherTools.Cache.Key one.Cache.Key "the tools are part of the prefix"
            }

            test "a changed section is stated at the end; the prefix stays" {
                let sim = oneTurn cfg

                let sim =
                    { sim with
                        Sections = [ Sections.instructions "Be brief."; sections[1]; sections[2] ]
                    }
                    |> steer "next"
                    |> call [ Part.text "ok" ]

                let calls = sim.Calls |> List.rev |> List.map snd
                Expect.equal (List.head calls).System (List.last calls).System "the same prefix"

                let lastUser =
                    (List.last calls).Messages
                    |> List.last
                    |> _.Parts
                    |> texts
                    |> String.concat "\n"

                Expect.stringContains lastUser "[Context update" "an update"
                Expect.stringContains lastUser "Be brief." "with the new text"
                Expect.isTrue (isPrefix (flat (List.head calls)) (flat (List.last calls))) "append-only"
            }

            test "interrupted calls are answered, so the next request is valid" {
                let request =
                    start "s1" cfg
                    |> steer "go"
                    |> call [ toolCall "c1" "x"; toolCall "c2" "y" ]
                    |> returns "c1" [ Piece.Text "one" ]
                    |> interrupt
                    |> steer "stop that, do this"
                    |> request

                let results =
                    request.Messages
                    |> List.collect _.Parts
                    |> List.choose (fun p ->
                        match p with
                        | Part.ToolResult r -> Some(r.CallId, r.IsError)
                        | _ -> None)

                Expect.equal results [ "c1", false; "c2", true ] "both answered"

                let text = requestJson request
                Expect.stringContains text "No result: the turn was interrupted." "why"
                Expect.stringContains text "[Turn 1 was interrupted.]" "noted"
            }

            test "a collapse shows its summary; replies after its start lose their reasoning" {
                let sim =
                    oneTurn cfg
                    |> steer "two"
                    |> call [ thinking "t3" "sig-3"; Part.text "two done" ]

                let refs = Transcript.entries sim.Transcript |> List.map _.Ref |> Array.ofList

                let collapsed =
                    sim
                    |> record (
                        ContextEvent.Collapsed
                            {
                                From = refs[0]
                                To = refs[3]
                                Summary = [ Piece.Text "Fixed the bug in src/a.fs." ]
                            }
                    )
                    |> call [ thinking "t4" "sig-4"; Part.text "after" ]

                let rendered = render collapsed
                let request = Render.request collapsed.Config template rendered
                let text = requestJson request

                Expect.stringContains text "Summary of an earlier part" "the summary"
                Expect.stringContains text "Fixed the bug in src/a.fs." "its text"
                Expect.isFalse (text.Contains "fix the bug") "the collapsed prompt is gone"
                Expect.isFalse (text.Contains "sig-3") "reasoning after the collapse's start is stale"
                Expect.stringContains text "two done" "its text stays"
                Expect.stringContains text "sig-4" "reasoning made after the collapse stays"
                Expect.equal rendered.Stale 1 "one stale reply"

                Expect.isFalse
                    (SeenRanges.covers "src/a.fs" (digestOf "src/a.fs") (LineRange.create 10 12) rendered.Seen)
                    "collapsed content is no longer seen"
            }

            test "exclusions withhold content everywhere and keep it out of SeenRanges" {
                let ex = exclusionsOf [ "secrets/" ]

                let cfg =
                    { config ex breakpointCaching with
                        Sections = sections @ [ Sections.file (file "secrets/AGENTS.md" [] "TOKEN-1") ]
                    }

                let sim =
                    { start "s1" cfg with
                        Live =
                            { Live.none with
                                Files = [ file "secrets/live.txt" [] "TOKEN-2" ]
                            }
                    }
                    |> steer "go"
                    |> pin "live" (Pin.File("secrets/live.txt", []))
                    |> call [ toolCall "c1" "x" ]
                    |> returns
                        "c1"
                        [
                            Piece.File(file "secrets/k.txt" [ 1, 2 ] "TOKEN-3")
                            Piece.File(file "ok.txt" [] "fine")
                        ]
                    |> call [ thinking "t" "sig"; Part.text "done" ]

                let rendered = render sim
                let text = requestJson (Render.request sim.Config template rendered)

                for token in [ "TOKEN-1"; "TOKEN-2"; "TOKEN-3" ] do
                    Expect.isFalse (text.Contains token) token

                Expect.equal
                    rendered.Withheld
                    [ "secrets/AGENTS.md"; "secrets/k.txt"; "secrets/live.txt" ]
                    "what was withheld"

                Expect.equal (rendered.Seen.Files |> Map.keys |> List.ofSeq) [ "ok.txt" ] "seen"
                Expect.stringContains text "fine" "the rest shows"
                Expect.equal rendered.Stale 0 "recorded withheld: nothing to rewrite"

                // The log never held the content: receipts were recorded through the exclusions.
                let log =
                    items sim
                    |> List.map (fun item ->
                        match item with
                        | LogItem.Context(_, event) -> Fx.Contracts.Codec.encodeText ContextCodecs.event event
                        | LogItem.Session _ -> "")
                    |> String.concat "\n"

                Expect.isFalse (log.Contains "TOKEN-3") "not in the log"
            }

            test "content excluded after it was recorded rewrites the history from there" {
                let recorded =
                    oneTurn cfg |> steer "more" |> call [ thinking "t3" "sig-3"; Part.text "three" ]

                let now =
                    { recorded with
                        Config =
                            { recorded.Config with
                                Exclusions = exclusionsOf [ "src/a.fs" ]
                            }
                    }

                let rendered = render now
                let text = requestJson (Render.request now.Config template rendered)

                Expect.isFalse (text.Contains "let a = 1") "withheld now"
                Expect.stringContains text "sig-1" "reasoning before it stays"
                Expect.isFalse (text.Contains "sig-2") "reasoning after it goes"
                Expect.isFalse (text.Contains "sig-3") "all of it"
                Expect.equal rendered.Stale 2 "two stale replies"
            }

            test "a new frame is a boundary: earlier replies lose their reasoning" {
                let sim =
                    oneTurn cfg
                    |> record (ContextEvent.Framed [ Sections.instructions "New rules." ])
                    |> steer "go on"
                    |> call [ thinking "t3" "sig-3"; Part.text "ok" ]

                let request = request sim
                Expect.equal (texts request.System) [ "New rules." ] "the new prefix"
                let text = requestJson request
                Expect.isFalse (text.Contains "sig-1") "stale"
                Expect.stringContains text "sig-3" "made after the new frame"
            }

            test "the pins view shows a value once, and again once a collapse hides it" {
                let sim =
                    start "s1" cfg
                    |> pin "plan" (Pin.Value [ Piece.Text "1. read 2. fix" ])
                    |> steer "go"
                    |> call [ Part.text "a" ]

                let firstSuffix = (fst sim.Calls.Head).Prompt.Suffix
                Expect.equal (firstSuffix |> List.map _.Id) [ "pin:plan" ] "shown"

                let sim = sim |> steer "more" |> call [ Part.text "b" ]
                Expect.isEmpty (fst sim.Calls.Head).Prompt.Suffix "not again"

                let refs = Transcript.entries sim.Transcript |> List.map _.Ref |> Array.ofList

                let sim =
                    sim
                    |> record (
                        ContextEvent.Collapsed
                            {
                                From = refs[0]
                                To = refs[1]
                                Summary = [ Piece.Text "started" ]
                            }
                    )

                Expect.equal ((render sim).Prompt.Suffix |> List.map _.Id) [ "pin:plan" ] "shown again"
            }

            test "a live file pin shows when its version changes" {
                let v1 = file "src/b.fs" [ 1, 3 ] "v1"

                let sim =
                    { start "s1" cfg with
                        Live = { Live.none with Files = [ v1 ] }
                    }
                    |> pin "b" (Pin.File("src/b.fs", [ LineRange.create 1 3 ]))
                    |> steer "go"
                    |> call [ Part.text "a" ]

                Expect.equal (fst sim.Calls.Head).Prompt.Suffix.Length 1 "shown"

                let sim = sim |> steer "x" |> call [ Part.text "b" ]
                Expect.isEmpty (fst sim.Calls.Head).Prompt.Suffix "unchanged"

                let v2 =
                    { v1 with
                        Digest = "sha256:v2"
                        Text = "v2"
                    }

                let sim =
                    { sim with
                        Live = { Live.none with Files = [ v2 ] }
                    }
                    |> steer "y"
                    |> call [ Part.text "c" ]

                match (fst sim.Calls.Head).Prompt.Suffix with
                | [ block ] -> Expect.equal block.Pieces[1] (Piece.File v2) "the new version"
                | other -> failtest (show other)

                let gone = { sim with Live = Live.none } |> render
                Expect.stringContains (Block.render gone.Prompt.Suffix.Head) "is not available" "unavailable"
            }

            test "status, time and notes" {
                let cfg =
                    { cfg with
                        Views = Views.select [ "status"; "time" ]
                    }

                let live =
                    {
                        Now = Some epoch
                        Files = []
                        Status = [ "budget", "$0.10 of $2.00" ]
                        Notes = [ Block.create "guard" [ Piece.Text "You ran the same cell twice." ] ]
                    }

                let sim = start "s1" cfg |> steer "go" |> callWith live [ Part.text "a" ]

                Expect.equal
                    ((fst sim.Calls.Head).Prompt.Suffix |> List.map Block.render)
                    [
                        "Status:\nbudget: $0.10 of $2.00"
                        "Today is 2026-01-01 (UTC)."
                        "You ran the same cell twice."
                    ]
                    "all three"

                let again = sim |> steer "b" |> callWith { live with Notes = [] } [ Part.text "b" ]
                Expect.isEmpty (fst again.Calls.Head).Prompt.Suffix "nothing changed"

                let later =
                    again
                    |> steer "c"
                    |> callWith
                        { live with
                            Notes = []
                            Now = Some(epoch + 86_400L * second)
                        }
                        [ Part.text "c" ]

                Expect.equal
                    ((fst later.Calls.Head).Prompt.Suffix |> List.map Block.render)
                    [ "Today is 2026-01-02 (UTC)." ]
                    "a new day"
            }

            test "inputs read as the model sees them" {
                let input (content: InputContent) : Input =
                    {
                        Id = 1L
                        By = alice
                        At = epoch
                        Content = content
                    }

                Expect.equal
                    (Render.inputText (input (InputContent.Resolved("d1", Resolution.Answered(Json.String "b")))))
                    "Request d1 was answered: \"b\""
                    "answered"

                Expect.equal
                    (Render.inputText (input (InputContent.Resolved("d1", Resolution.Expired None))))
                    "Request d1 expired without an answer."
                    "expired"

                Expect.equal
                    (Render.inputText (input (InputContent.JobFinished("j1", "built"))))
                    "Job j1 finished: built"
                    "job"
            }
        ]
