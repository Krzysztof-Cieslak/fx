/// Golden renders of representative sessions, and the JSON of context events as version 1 wrote it.
module Fx.Prompt.Tests.GoldenTests

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Models
open Fx.Sessions
open Fx.Prompt
open Fx.Prompt.Tests.Support

/// A coding session: a turn with reasoning, cells, a file read and an error; a steer at a step
/// boundary; a queued prompt answered in a second turn; a pinned value and a live pinned file; a
/// status line.
let coding: Sim =
    let v1 =
        file "src/billing/Retry.fs" [ 14, 31 ] "type RetryConfig = { Attempts: int }"

    { start "coding" (config (exclusionsOf [ ".env" ]) breakpointCaching) with
        Live =
            { Live.none with
                Files = [ v1 ]
                Status = [ "budget", "$0.12 of $2.00" ]
            }
    }
    |> steer "Make retries exponential."
    |> pin "retry" (Pin.File("src/billing/Retry.fs", [ LineRange.create 14 31 ]))
    |> call
        [
            thinking "Read the config first." "sig-a"
            toolCall "toolu_1" "read('src/billing/Retry.fs')"
        ]
    |> returns "toolu_1" [ Piece.Text "FileText(lines=412)"; Piece.File v1 ]
    |> queue "Then update the docs."
    |> call
        [
            thinking "Edit and test." "sig-b"
            toolCall "toolu_2" "edit"
            toolCall "toolu_3" "test"
        ]
    |> returns "toolu_2" [ Piece.Text "edited 1 range"; Piece.File(file ".env" [] "SECRET=1") ]
    |> record (
        ContextEvent.Returned
            {
                Turn = 1
                Step = 0
                Call = Some "toolu_3"
                Name = "cell"
                Pieces = [ Piece.output 60 (String.replicate 30 "ok " + "FAILED: 1 test") ]
                IsError = true
            }
    )
    |> steer "Keep the old API too."
    |> boundary
    |> pin "plan" (Pin.Value [ Piece.Text "1. config 2. call sites 3. docs" ])
    |> call [ Part.text "Done: exponential retries, old API kept." ]
    |> finish "retries"
    |> call [ Part.text "Docs updated." ]

/// An interrupted turn with a call in flight, then a failed one.
let interrupted: Sim =
    start "interrupted" (config Exclusions.none breakpointCaching)
    |> steer "Run the slow tests."
    |> call [ toolCall "call_1" "slow"; toolCall "call_2" "slower" ]
    |> returns "call_1" [ Piece.Text "passed" ]
    |> interrupt
    |> steer "Never mind, check the build."
    |> fail "the model was unreachable"
    |> steer "Try again."

/// A conversation whose first turn was collapsed after a second one, and whose instructions changed.
let collapsed: Sim =
    let sim =
        start "collapsed" (config Exclusions.none breakpointCaching)
        |> steer "Find the bug."
        |> call [ thinking "Search." "sig-1"; toolCall "c1" "grep" ]
        |> returns "c1" [ Piece.File(file "src/a.fs" [ 7, 7 ] "let x = 1 / 0") ]
        |> call [ Part.text "Division by zero in src/a.fs:7." ]
        |> finish "found"
        |> steer "Fix it."
        |> call [ thinking "Patch." "sig-2"; Part.text "Fixed." ]
        |> finish "fixed"

    let refs = Transcript.entries sim.Transcript |> List.map _.Ref

    { sim with
        Sections =
            [ Sections.instructions "You are a careful coding agent. Prefer small diffs." ]
            @ sim.Sections.Tail
    }
    |> record (
        ContextEvent.Collapsed
            {
                From = refs.Head
                To = refs[3]
                Summary = [ Piece.Text "Found a division by zero at src/a.fs:7." ]
            }
    )
    |> steer "Add a test."
    |> call [ thinking "Test." "sig-3"; Part.text "Added." ]

/// What a render shows, for golden files.
let private golden (sim: Sim) : string =
    let rendered = render sim

    Json.serializeIndented (
        Json.Object
            [
                "request", ChatRequest.codec.Encode(Render.request sim.Config template rendered)
                "seen", SeenRanges.codec.Encode rendered.Seen
                "withheld", Json.Array(rendered.Withheld |> List.map Json.String)
                "stale", Json.int rendered.Stale
                "issues", Json.Array(sim.Transcript.Issues |> List.map Json.String)
            ]
    )
    + "\n"

let private contextLines (sim: Sim) : string =
    items sim
    |> List.choose (fun item ->
        match item with
        | LogItem.Context(_, event) -> Some(Json.serializeCanonical (ContextCodecs.event.Encode event) + "\n")
        | LogItem.Session _ -> None)
    |> String.concat ""

/// Every kind of context event.
let private everyKind: Sim =
    collapsed
    |> pin "plan" (Pin.Value [ Piece.Text "plan" ])
    |> record (ContextEvent.Unpinned "plan")
    |> record (ContextEvent.Framed [ Sections.instructions "New frame." ])
    |> record (
        ContextEvent.Returned
            {
                Turn = 3
                Step = 1
                Call = None
                Name = "cell"
                Pieces =
                    [
                        Piece.Ref
                            {
                                Kind = "blob"
                                Id = "sha256:0f"
                                Type = Some "string"
                                Preview =
                                    {
                                        Text = "\"lo…\"(len=9000)"
                                        Truncated = true
                                    }
                            }
                        Piece.Withheld ".env"
                        Piece.Media
                            {
                                Kind = MediaKind.Image
                                MediaType = "image/png"
                                Source = MediaSource.Url "https://example.com/a.png"
                                Name = Some "a.png"
                                Sources = []
                            }
                    ]
                IsError = false
            }
    )

[<Tests>]
let tests =
    testList
        "Golden"
        [
            test "a coding session" { Golden.expect "render-coding.json" (golden coding) }
            test "an interrupted and a failed turn" { Golden.expect "render-interrupted.json" (golden interrupted) }
            test "a collapsed conversation with changed instructions" {
                Golden.expect "render-collapsed.json" (golden collapsed)
            }

            test "version 1's context events are read as they were written, forever" {
                let tags =
                    items everyKind
                    |> List.choose (fun item ->
                        match item with
                        | LogItem.Context(_, event) ->
                            match ContextCodecs.event.Encode event with
                            | Json.Object(("kind", Json.String tag) :: _) -> Some tag
                            | other -> failtest (Json.serialize other)
                        | LogItem.Session _ -> None)
                    |> List.distinct
                    |> List.sort

                Expect.equal
                    tags
                    [
                        "collapsed"
                        "framed"
                        "pinned"
                        "replied"
                        "returned"
                        "unpinned"
                        "updated"
                    ]
                    "every kind"

                let written = contextLines everyKind
                let kept = Golden.kept "context-events-v1.jsonl" written

                let read =
                    kept.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Codec.decodeText ContextCodecs.event line with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.render issues))
                    |> List.ofArray

                Expect.equal
                    (read
                     |> List.map (fun e -> Json.serializeCanonical (ContextCodecs.event.Encode e) + "\n")
                     |> String.concat "")
                    kept
                    "written back the same"
            }
        ]
