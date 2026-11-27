/// Properties of the render over random sessions (spec §5): appending to the log never changes the
/// prefix or the history rendered before it; every call's request is a prefix of every later one;
/// rendering is deterministic, also from the log's JSON; the cache key ignores the suffix; breakpoints
/// stay within the model's limit; excluded content never shows; collapses rewrite only from their start;
/// every compaction cut applies, and its summary request is what a call sent.
module Fx.Prompt.Tests.PropertyTests

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Models
open Fx.Sessions
open Fx.Prompt
open Fx.Prompt.Tests.Support

/// What a session does; numbers pick among what the state offers.
type Op =
    | Steer of int
    | Queue of int
    /// The model replies: text, one or two calls, with or without reasoning.
    | Call of kind: int
    /// A result for an open call (or a closed one): text, a file, an excluded file, an error, media.
    | Return of call: int * kind: int
    | Boundary
    | Finish
    | Fail
    | Interrupt
    | PinValue of int
    | PinFile of int
    | Unpin of int
    | ChangeSection of int
    | Status of int
    | Touch of int

    override this.ToString() =
        match this with
        | Steer n -> $"steer {n}"
        | Queue n -> $"queue {n}"
        | Call k -> $"call {k}"
        | Return(c, k) -> $"return {c} {k}"
        | Boundary -> "boundary"
        | Finish -> "finish"
        | Fail -> "fail"
        | Interrupt -> "interrupt"
        | PinValue n -> $"pin value {n}"
        | PinFile n -> $"pin file {n}"
        | Unpin n -> $"unpin {n}"
        | ChangeSection n -> $"change section {n}"
        | Status n -> $"status {n}"
        | Touch n -> $"touch {n}"

let private modulo (n: int) (m: int) = ((n % m) + m) % m

let private exclusions = exclusionsOf [ "secret/" ]

/// A file whose text names it, so a request that shows it can be found.
let private shownFile (path: string) (version: int) =
    file path [ 1, 3 ] $"content of {path} v{version}"

let private filePath (n: int) =
    [| "src/a.fs"; "src/b.fs"; "secret/key.txt"; "docs/c.md" |][modulo n 4]

let private apply (sim: Sim) (op: Op) : Sim =
    let calls = sim.Transcript.Open

    match op with
    | Steer n -> sim |> steer $"steer {n}"
    | Queue n -> sim |> queue $"queue {n}"
    // A strategy answers every call before it calls the model again.
    | Call kind when sim.State.Running.IsSome && calls.IsEmpty ->
        let id = $"c{sim.ContextSeq}"
        let signature = $"sig{sim.ContextSeq}"

        let parts =
            match modulo kind 5 with
            | 0 -> [ Part.text "text" ]
            | 1 -> [ toolCall id "a" ]
            | 2 -> [ toolCall (id + "a") "a"; toolCall (id + "b") "b" ]
            | 3 -> [ thinking "think" signature; toolCall id "a" ]
            | _ -> [ thinking "think" signature; Part.text "answer" ]

        sim |> call parts
    | Call _ -> sim
    | Return(n, kind) ->
        let id =
            match calls with
            | [] -> "closed"
            | open' -> (List.item (modulo n open'.Length) open').Id

        let pieces =
            match modulo kind 5 with
            | 0 -> [ Piece.Text "ok" ]
            | 1 -> [ Piece.File(shownFile (filePath n) 1) ]
            | 2 -> [ Piece.File(shownFile "secret/key.txt" 1) ]
            | 3 -> [ Piece.Text "boom" ]
            | _ ->
                [
                    Piece.Media
                        {
                            Kind = MediaKind.Image
                            MediaType = "image/png"
                            Source = MediaSource.Base64 "AAAA"
                            Name = None
                            Sources = []
                        }
                ]

        record
            (ContextEvent.Returned
                {
                    Turn = running sim
                    Step = sim.Step
                    Call = Some id
                    Name = "cell"
                    Pieces = pieces
                    IsError = modulo kind 5 = 3
                    Restarted = None
                    Approvals = []
                })
            sim
    | Boundary when sim.State.Running.IsSome -> sim |> boundary
    | Finish when sim.State.Running.IsSome -> sim |> finish "done"
    | Fail when sim.State.Running.IsSome -> sim |> fail "down"
    | Interrupt -> sim |> interrupt
    | Boundary
    | Finish
    | Fail -> sim
    | PinValue n -> sim |> pin $"v{modulo n 3}" (Pin.Value [ Piece.Text $"value {n}" ])
    | PinFile n -> sim |> pin $"f{modulo n 3}" (Pin.File(filePath n, [ LineRange.create 1 3 ]))
    | Unpin n -> sim |> record (ContextEvent.Unpinned $"v{modulo n 3}")
    | ChangeSection n ->
        { sim with
            Sections =
                sim.Sections
                |> List.mapi (fun i s ->
                    if i = modulo n sim.Sections.Length then
                        { s with
                            Pieces = s.Pieces @ [ Piece.Text $"change {n}" ]
                        }
                    else
                        s)
        }
    | Status n ->
        { sim with
            Live =
                { sim.Live with
                    Status = [ "budget", $"${modulo n 7}" ]
                }
        }
    | Touch n ->
        { sim with
            Live =
                { sim.Live with
                    Files =
                        [ "src/a.fs"; "src/b.fs"; "secret/key.txt"; "docs/c.md" ]
                        |> List.map (fun path -> shownFile path (modulo n 3))
                }
        }

let private run (cfg: RenderConfig) (ops: Op list) : Sim =
    ops |> List.fold apply (start "s1" cfg |> steer "begin")

/// The transcript after each item of the log.
let private states (sim: Sim) : Transcript list =
    items sim |> List.scan Transcript.apply Transcript.empty |> List.tail

let private requestOf (sim: Sim) (transcript: Transcript) : ChatRequest =
    Render.request sim.Config template (Render.render sim.Config transcript sim.Live)

let private fsConfig =
    { FsCheckConfig.defaultConfig with
        maxTest = 150
        endSize = 50
    }

let private roundTrip (event: ContextEvent) : ContextEvent =
    match Codec.decodeText ContextCodecs.event (Codec.encodeText ContextCodecs.event event) with
    | Ok event -> event
    | Error issues -> failtest (Issue.render issues)

/// Checks a request a model could be sent: every call answered by the user message that follows it.
let private valid (request: ChatRequest) =
    let messages = Array.ofList request.Messages

    for i, message in Array.indexed messages do
        let calls =
            message.Parts
            |> List.choose (fun p ->
                match p with
                | Part.ToolCall c when message.Role = Role.Assistant -> Some c.Id
                | _ -> None)

        if not calls.IsEmpty then
            Expect.isLessThan i (messages.Length - 1) "a call is followed by a message"

            let results =
                messages[i + 1].Parts
                |> List.choose (fun p ->
                    match p with
                    | Part.ToolResult r -> Some r.CallId
                    | _ -> None)

            Expect.equal (List.sort results) (List.sort calls) "each call answered next, once"

[<Tests>]
let tests =
    testList
        "Render properties"
        [
            testPropertyWithConfig
                fsConfig
                "appending never changes the prefix or the history before it"
                (fun (ops: Op list) ->
                    let sim = run (config exclusions breakpointCaching) ops
                    let all = states sim
                    let final = requestOf sim (List.last all)

                    for transcript in all do
                        let request = requestOf sim transcript

                        if transcript.Frame.IsSome then
                            Expect.equal request.System final.System "the same prefix"

                        // The history alone (no suffix: the live views are not in the log yet).
                        let noViews = { sim.Config with Views = [] }

                        let history =
                            Render.request noViews template (Render.render noViews transcript sim.Live)

                        // Before a frame is recorded the prefix is whatever the sections are then.
                        let messages (request: ChatRequest) = flat { request with System = [] }

                        Expect.isTrue
                            (isPrefix (messages history) (messages final))
                            "the history is a prefix of every later one")

            testPropertyWithConfig
                fsConfig
                "every call's whole request is a prefix of every later one, and valid"
                (fun (ops: Op list) ->
                    let sim = run (config exclusions breakpointCaching) ops
                    let calls = sim.Calls |> List.rev |> List.map snd
                    let final = flat (request sim)

                    for a, b in List.pairwise calls do
                        Expect.isTrue (isPrefix (flat a) (flat b)) "a prefix of the next call"

                    for request in calls do
                        Expect.isTrue (isPrefix (flat request) final) "a prefix of what follows"
                        Expect.isLessThanOrEqual (breakpoints request) 4 "within the limit")

            testPropertyWithConfig
                fsConfig
                "requests made with no call open are valid for a provider"
                (fun (ops: Op list) ->
                    let sim = run (config exclusions breakpointCaching) ops

                    for transcript in states sim do
                        if transcript.Open.IsEmpty then
                            valid (requestOf sim transcript))

            testPropertyWithConfig
                fsConfig
                "rendering is deterministic, also from the log's JSON"
                (fun (ops: Op list) ->
                    let sim = run (config exclusions keyedCaching) ops
                    let once = requestJson (request sim)
                    Expect.equal (requestJson (request sim)) once "twice alike"

                    let read =
                        items sim
                        |> List.map (fun item ->
                            match item with
                            | LogItem.Context(ref, event) -> LogItem.Context(ref, roundTrip event)
                            | LogItem.Session(ref, event) ->
                                match Codec.decodeText Codecs.event (Codec.encodeText Codecs.event event) with
                                | Ok event -> LogItem.Session(ref, event)
                                | Error issues -> failtest (Issue.render issues))

                    let fromJson = requestOf sim (Transcript.ofItems read)
                    Expect.equal (requestJson fromJson) once "the same bytes from the stored log")

            testPropertyWithConfig
                fsConfig
                "the cache key depends on the prefix only"
                (fun (ops: Op list) (status: int) ->
                    let sim = run (config exclusions keyedCaching) ops
                    let keys = sim.Calls |> List.map (fun (_, r) -> r.Cache.Key) |> List.distinct
                    Expect.isLessThanOrEqual keys.Length 1 "one key for the conversation"

                    let other =
                        Render.request
                            sim.Config
                            template
                            (Render.render
                                sim.Config
                                sim.Transcript
                                { sim.Live with
                                    Status = [ "x", string status ]
                                    Notes = [ Block.create "n" [ Piece.Text "note" ] ]
                                })

                    Expect.equal other.Cache.Key (request sim).Cache.Key "the suffix does not change it")

            testPropertyWithConfig fsConfig "excluded content never shows, and is never seen" (fun (ops: Op list) ->
                let sim = run (config exclusions breakpointCaching) (PinFile 2 :: Touch 0 :: ops)

                let rendered = render sim

                for text in (request sim :: (sim.Calls |> List.map snd)) |> List.map requestJson do
                    Expect.isFalse (text.Contains "content of secret/") "never in a request"

                for path in rendered.Seen.Files |> Map.keys do
                    Expect.isFalse (Exclusions.excludes exclusions path) "never seen")

            testPropertyWithConfig
                fsConfig
                "a collapse rewrites the history only from where it starts"
                (fun (ops: Op list) (a: int) (b: int) ->
                    let sim = run (config exclusions breakpointCaching) ops
                    let entries = Transcript.entries sim.Transcript |> Array.ofList

                    if entries.Length > 0 then
                        let first = min (modulo a entries.Length) (modulo b entries.Length)
                        let last = max (modulo a entries.Length) (modulo b entries.Length)

                        let collapsed =
                            sim
                            |> record (
                                ContextEvent.Collapsed
                                    {
                                        From = entries[first].Ref
                                        To = entries[last].Ref
                                        Summary = [ Piece.Text "summary" ]
                                    }
                            )

                        if collapsed.Transcript.Collapses.Length = 1 then
                            // The log up to the item that made the first collapsed entry renders a
                            // history that the collapsed one still starts with.
                            let before =
                                items sim
                                |> List.takeWhile (fun item -> item.Ref <> entries[first].Ref)
                                |> Transcript.ofItems
                                |> fun t -> { t with Frame = sim.Transcript.Frame }

                            let noViews = { sim.Config with Views = [] }

                            let history (transcript: Transcript) =
                                Render.request noViews template (Render.render noViews transcript Live.none)
                                |> flat

                            Expect.isTrue
                                (isPrefix (history before) (history collapsed.Transcript))
                                "unchanged before the collapse"

                            if collapsed.Transcript.Open.IsEmpty then
                                valid (requestOf collapsed collapsed.Transcript)
                        else
                            Expect.isNonEmpty collapsed.Transcript.Issues "a refused collapse says why")

            testPropertyWithConfig
                fsConfig
                "every compaction cut applies; its summary request is what a call sent, then the instruction"
                (fun (ops: Op list) ->
                    let sim = run (config exclusions breakpointCaching) ops
                    let rendered = render sim
                    let now = request sim
                    let calls = sim.Calls |> List.rev |> List.map snd |> Array.ofList

                    // The calls whose replies are in the history, by the replies' entries.
                    let callOf =
                        Transcript.entries sim.Transcript
                        |> List.filter (fun e -> e.Content.IsReply)
                        |> List.mapi (fun i e -> e.Index, calls[i])
                        |> Map.ofList

                    for cut in Compaction.cuts sim.Transcript rendered template.Tools do
                        let summary = Compaction.request sim.Config template rendered cut "Summarize."
                        let sent = flat summary

                        let expected =
                            match cut.Mark with
                            | Some _ -> callOf[cut.Last + 1]
                            | None -> now

                        Expect.equal (List.truncate (sent.Length - 1) sent) (flat expected) "what was sent"
                        Expect.equal summary.System expected.System "the same prefix and breakpoint"
                        Expect.isLessThanOrEqual (breakpoints summary) 4 "within the limit"
                        Expect.isFalse ((requestJson summary).Contains "content of secret/") "nothing excluded"

                        let collapsed =
                            sim
                            |> record (
                                ContextEvent.Collapsed(Compaction.collapse cut "summary" (Compaction.sources summary))
                            )

                        Expect.equal
                            collapsed.Transcript.Collapses.Length
                            (sim.Transcript.Collapses.Length + 1)
                            "applies"

                        let after = request collapsed
                        Expect.equal after.System now.System "the prefix is never rewritten"
                        valid after
                        Expect.isFalse ((requestJson after).Contains "content of secret/") "nothing excluded")
        ]
