/// Fx.Prompt (T126, docs/modules/prompt.md): a session driven through the session machine with the
/// context events a strategy records, rendered after every item (prefix, history, suffix, breakpoints,
/// cache key, SeenRanges, withheld files), the log through its JSON, collapses and stale reasoning,
/// content-exclusion globs, and the render's settings read through `IVariants`.
module Fx.Aot.Scenarios.Prompt

open System
open System.Security.Cryptography
open System.Text
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Models
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Sessions
open Fx.Prompt

let private alice = Attribution.create "user:alice" "tui"
let private strategy = Attribution.system "strategy"
let private model = ModelRef.create "anthropic" "claude-test"
let private machine = Session.machine RequestKinds.builtIn

let private caching (key: bool) : CachingCaps =
    {
        Mode = (if key then CacheMode.Automatic else CacheMode.Breakpoints)
        MaxBreakpoints = (if key then 0 else 4)
        Key = key
        LongTtl = true
    }

let private exclusionsOf (patterns: string list) =
    Exclusions.create [ { Origin = "test"; Patterns = patterns } ]

let private exclusions =
    match exclusionsOf [ ".env"; "secrets/" ] with
    | Ok exclusions -> exclusions
    | Error issues -> failwith (String.concat "; " (issues |> List.map string))

let private sections =
    [
        Sections.instructions "You are a careful coding agent."
        Sections.interpreter "Write TypeScript cells."
        Sections.agents
            [
                {
                    Name = "reviewer"
                    Description = "Reviews a change."
                }
            ]
    ]

let private config (key: bool) =
    PromptSettings.config PromptSettings.defaults sections exclusions (caching key)

let private template =
    { ChatRequest.create model [] with
        Tools = [ ToolSpec.ofCodec "cell" "Runs a cell." Codec.string ]
    }

let private digest (text: string) =
    "sha256:"
    + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))

let private view (path: string) (first: int) (last: int) (text: string) : FileView =
    {
        Path = path
        Digest = digest path
        Lines = [ LineRange.create first last ]
        Text = text
    }

/// The log a strategy would write: session events through the machine, context events through the
/// exclusions, the suffix it rendered kept with each reply.
type private Log =
    {
        State: SessionState
        Now: int64
        Seq: int64
        Items: LogItem list
        Transcript: Transcript
        Requests: ChatRequest list
    }

let private session = StreamId.create "session" "p1"
let private context = StreamId.create "context" "p1"

let private add (item: LogItem) (log: Log) =
    { log with
        Items = log.Items @ [ item ]
        Transcript = Transcript.apply log.Transcript item
    }

let private send (by: Attribution) (command: Command) (log: Log) : Log =
    let now = log.Now + 1_000_000L

    match Machine.step machine log.State { Command = command; By = by; At = now } with
    | Ok step ->
        let first = log.State.Seq + 1L

        step.Events
        |> List.indexed
        |> List.fold
            (fun log (i, event) -> add (LogItem.Session(EventRef.create session (first + int64 i), event)) log)
            { log with
                State = step.State
                Now = now
            }
    | Error rejection -> failwith (string rejection)

let private record (cfg: RenderConfig) (event: ContextEvent) (log: Log) : Log =
    let seq = log.Seq + 1L

    add
        (LogItem.Context(EventRef.create context seq, ContextEvent.withhold cfg.Exclusions event))
        { log with Seq = seq }

let private turn (log: Log) =
    log.State.Running |> Option.map _.Turn |> Option.defaultValue 0

let private call (cfg: RenderConfig) (live: Live) (parts: Part list) (log: Log) : Log =
    let log =
        match Transcript.reconcile cfg.Sections log.Transcript with
        | Some event -> record cfg event log
        | None -> log

    let rendered = Render.render cfg log.Transcript live

    { log with
        Requests = log.Requests @ [ Render.request cfg template rendered ]
    }
    |> record
        cfg
        (ContextEvent.Replied
            {
                Turn = turn log
                Step = 0
                Shown = rendered.Prompt.Suffix
                Parts = parts
                Used = None
            })

let private returns (cfg: RenderConfig) (id: string) (pieces: Piece list) (log: Log) =
    record
        cfg
        (ContextEvent.Returned
            {
                Turn = turn log
                Step = 0
                Call = Some id
                Name = "cell"
                Pieces = pieces
                IsError = false
                Restarted = None
                Approvals = []
                Jobs = []
            })
        log

let private signed (text: string) (signature: string) =
    Part.Reasoning
        {
            Text = text
            State =
                Some
                    {
                        Family = "anthropic.messages"
                        Model = model
                        Kind = "signature"
                        Data = signature
                    }
        }

let private callPart (id: string) =
    Part.ToolCall
        {
            Id = id
            Name = "cell"
            Arguments = "{\"code\":\"read()\"}"
            State = None
        }

let private live =
    {
        Now = Some 1_767_225_600_000_000L
        Files = [ view "src/a.fs" 1 2 "let a = 1\nlet b = 2"; view "secrets/k.txt" 1 1 "TOKEN" ]
        Status = [ "budget", "$0.10 of $2.00" ]
        Notes = []
    }

let private conversation (cfg: RenderConfig) : Log =
    let empty =
        {
            State = Session.initial
            Now = 1_767_225_600_000_000L
            Seq = 0L
            Items = []
            Transcript = Transcript.empty
            Requests = []
        }

    empty
    |> send
        alice
        (Command.Start
            {
                Id = "p1"
                Profile = "sha256:base"
                Overrides = []
                Parent = None
                Title = None
            })
    |> send alice (Command.Prompt("Fix the bug.", PromptMode.Steer))
    |> record
        cfg
        (ContextEvent.Pinned
            {
                Name = "a"
                Pin = Pin.File("src/a.fs", [ LineRange.create 1 2 ])
            })
    |> record
        cfg
        (ContextEvent.Pinned
            {
                Name = "k"
                Pin = Pin.File("secrets/k.txt", [ LineRange.create 1 1 ])
            })
    |> call cfg live [ signed "Read it." "sig-1"; callPart "c1"; callPart "c2" ]
    |> returns cfg "c1" [ Piece.Text "read"; Piece.File(view "src/b.fs" 10 12 "let c = 3") ]
    |> returns cfg "c2" [ Piece.File(view ".env" 1 1 "KEY=1") ]
    |> send alice (Command.Prompt("Also the docs.", PromptMode.Steer))
    |> send strategy (Command.TakeInputs(1, 1))
    |> call cfg live [ signed "Done." "sig-2"; Part.text "Fixed, docs too." ]
    |> send strategy (Command.EndTurn(1, TurnResult.Done("fixed", [], None)))
    |> send alice (Command.Prompt("Run the tests.", PromptMode.Steer))
    |> call cfg live [ callPart "c3" ]
    |> send alice Command.Interrupt

let private flat (request: ChatRequest) : string list =
    [
        for part in request.System do
            if not part.IsCacheBreakpoint then
                "system " + Json.serializeCanonical (Part.codec.Encode part)
        for message in request.Messages do
            for part in message.Parts do
                if not part.IsCacheBreakpoint then
                    string message.Role + " " + Json.serializeCanonical (Part.codec.Encode part)
    ]

let private isPrefix (a: string list) (b: string list) =
    a.Length <= b.Length && List.truncate a.Length b = a

let private breakpointCount (request: ChatRequest) =
    request.System @ (request.Messages |> List.collect _.Parts)
    |> List.filter _.IsCacheBreakpoint
    |> List.length

type private Env =
    {
        Variants: Variants
        Scope: Scope
    }

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let scenarios: Scenario list =
    [
        Scenario.lines "prompt-render" (fun () ->
            let cfg = config false
            let log = conversation cfg
            let rendered = Render.render cfg log.Transcript live
            let request = Render.request cfg template rendered
            let withheld = String.concat "," rendered.Withheld
            let ends = rendered.Ends |> List.map string |> String.concat " "

            let prefixes =
                log.Requests @ [ request ]
                |> List.pairwise
                |> List.forall (fun (a, b) -> isPrefix (flat a) (flat b))

            let text = Json.serialize (ChatRequest.codec.Encode request)
            let secrets = text.Contains "TOKEN" || text.Contains "KEY=1"

            [
                $"items {log.Items.Length}, entries {log.Transcript.Count}, open {log.Transcript.Open.Length}"
                for entry in Transcript.entries log.Transcript do
                    escape (string entry)
                $"withheld {withheld}; stale {rendered.Stale}"
                "seen " + Json.serializeCanonical (SeenRanges.codec.Encode rendered.Seen)
                $"breakpoints {breakpointCount request}"
                $"ends {ends}"
                "request " + escape (Json.serializeCanonical (ChatRequest.codec.Encode request))
                $"each request a prefix of the next: {prefixes}"
                $"secrets shown: {secrets}"
            ])

        Scenario.lines "prompt-replay" (fun () ->
            let cfg = config true
            let log = conversation cfg

            // The log through its JSON renders the same bytes.
            let read =
                log.Items
                |> List.map (fun item ->
                    match item with
                    | LogItem.Session(ref, event) ->
                        match Codec.decodeText Codecs.event (Codec.encodeText Codecs.event event) with
                        | Ok event -> LogItem.Session(ref, event)
                        | Error issues -> failwith (Issue.render issues)
                    | LogItem.Context(ref, event) ->
                        match Codec.decodeText ContextCodecs.event (Codec.encodeText ContextCodecs.event event) with
                        | Ok event -> LogItem.Context(ref, event)
                        | Error issues -> failwith (Issue.render issues))

            let direct = Render.request cfg template (Render.render cfg log.Transcript live)

            let replayed =
                Render.request cfg template (Render.render cfg (Transcript.ofItems read) live)

            let quiet = Render.request cfg template (Render.render cfg log.Transcript Live.none)

            let refs = Transcript.entries log.Transcript |> List.map _.Ref

            let collapsed =
                log
                |> record
                    cfg
                    (ContextEvent.Collapsed
                        {
                            From = refs.Head
                            To = refs[3]
                            Summary = [ Piece.Text "Read src/b.fs." ]
                        })

            let collapsedRendered = Render.render cfg collapsed.Transcript live

            let same =
                Json.serializeCanonical (ChatRequest.codec.Encode direct) =
                    Json.serializeCanonical (ChatRequest.codec.Encode replayed)

            let key = direct.Cache.Key |> Option.defaultValue "none"

            let collapsedHistory =
                Render.request cfg template collapsedRendered
                |> fun request -> Json.serializeCanonical (ChatRequest.codec.Encode request)

            [
                $"same bytes from JSON: {same}"
                $"key {key}"
                $"key ignores the suffix: {direct.Cache.Key = quiet.Cache.Key}"
                $"breakpoints with a key: {breakpointCount direct}"
                $"collapses {collapsed.Transcript.Collapses.Length}, issues {collapsed.Transcript.Issues.Length}, stale {collapsedRendered.Stale}"
                for v in Transcript.visible collapsed.Transcript do
                    escape (string v)
                "collapsed " + escape collapsedHistory
            ])

        Scenario.lines "prompt-exclusions" (fun () ->
            let patterns =
                [ ".env"; "/secrets"; "**/keys/*.pem"; "build/"; "id_[rd]sa"; "log?.txt" ]

            let compiled =
                match exclusionsOf patterns with
                | Ok exclusions -> exclusions
                | Error issues -> failwith (String.concat "; " (issues |> List.map string))

            let paths =
                [
                    ".env"
                    "app/.ENV"
                    "app/.envrc"
                    "secrets/a.txt"
                    "app/secrets/a.txt"
                    "x\\keys\\a.pem"
                    "keys/a.pem"
                    "build"
                    "build/out.txt"
                    ".ssh/id_rsa"
                    ".ssh/id_ecdsa"
                    "log1.txt"
                    "log10.txt"
                ]

            [
                for path in paths do
                    $"{path}: {Exclusions.excludes compiled path}"
                match exclusionsOf [ "ok"; "!neg"; "" ] with
                | Ok _ -> "accepted"
                | Error issues -> "refused: " + String.concat " | " (issues |> List.map string)
            ])

        Scenario.lines "prompt-settings" (fun () ->
            let read (selections: (string * Json) list) =
                let env =
                    {
                        Variants = FakeVariants.create (Slots.all @ PromptSlots.all) selections
                        Scope = Scope.root
                    }

                (Eff.run env PromptSettings.read).GetAwaiter().GetResult()

            let selected =
                read
                    [
                        "prompt.views", Json.Array [ Json.String "time" ]
                        "prompt.cache.breakpoints", Json.String "prefix"
                    ]

            [
                string (read [])
                string selected
                (PromptSettings.config selected [] Exclusions.none (caching false)).Views
                |> List.map _.Id
                |> String.concat ","
                for slot in PromptSlots.all do
                    $"{slot.Id} = {Json.serialize slot.Default}"
            ])
    ]
