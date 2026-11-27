/// A simulated session for the render's tests: session commands go through the real session machine,
/// context events are appended as a strategy would append them (the frame reconciled before each call,
/// the suffix it rendered recorded with the reply, receipts recorded through the exclusions), and every
/// call's request is kept.
module Fx.Prompt.Tests.Support

open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Models
open Fx.Sessions
open Fx.Prompt

let alice = Attribution.create "user:alice" "tui"
let system = Attribution.system "strategy"
let second = 1_000_000L
let epoch = 1_767_225_600_000_000L
let machine = Session.machine RequestKinds.builtIn
let model = ModelRef.create "anthropic" "claude-test"

/// Anthropic-like caching: four breakpoints, long lifetimes, no key.
let breakpointCaching: CachingCaps =
    {
        Mode = CacheMode.Breakpoints
        MaxBreakpoints = 4
        Key = false
        LongTtl = true
    }

/// OpenAI-like caching: automatic, with a key.
let keyedCaching: CachingCaps =
    {
        Mode = CacheMode.Automatic
        MaxBreakpoints = 0
        Key = true
        LongTtl = false
    }

let state (kind: string) (data: string) : ProviderState option =
    Some
        {
            Family = "anthropic.messages"
            Model = model
            Kind = kind
            Data = data
        }

let tools: ToolSpec list =
    [
        {
            Name = "cell"
            Description = "Runs a TypeScript cell."
            Parameters =
                Json.Object
                    [
                        "type", Json.String "object"
                        "properties", Json.Object [ "code", Json.Object [ "type", Json.String "string" ] ]
                    ]
            Strict = false
        }
    ]

let template: ChatRequest =
    { ChatRequest.create model [] with
        Tools = tools
    }

let sections: Block list =
    [
        Sections.instructions "You are a careful coding agent."
        Sections.interpreter "Write TypeScript cells."
        Sections.agents
            [
                {
                    Name = "reviewer"
                    Description = "Reviews a change."
                }
                {
                    Name = "explorer"
                    Description = "Finds code."
                }
            ]
    ]

let config (exclusions: Exclusions) (caching: CachingCaps) : RenderConfig =
    PromptSettings.config PromptSettings.defaults sections exclusions caching

/// Exclusions with one list of patterns (the shared matcher, `Fx.Models.Exclusions`).
let exclusionsFrom (patterns: string list) =
    Exclusions.create [ { Origin = "test"; Patterns = patterns } ]

let exclusionsOf (patterns: string list) : Exclusions =
    match exclusionsFrom patterns with
    | Ok exclusions -> exclusions
    | Error issues -> failtest (String.concat "; " (issues |> List.map string))

/// A digest that depends on the text only (string hash codes differ between processes).
let digestOf (text: string) : string =
    "sha256:"
    + System.Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes text)
    )

let file (path: string) (lines: (int * int) list) (text: string) : FileView =
    {
        Path = path
        Digest = digestOf path
        Lines = lines |> List.map (fun (a, b) -> LineRange.create a b)
        Text = text
    }

/// A session, its log, and what the strategy rendered.
type Sim =
    {
        Config: RenderConfig
        Sections: Block list
        Session: StreamId
        Context: StreamId
        State: SessionState
        Now: int64
        ContextSeq: int64
        /// Newest first.
        Items: LogItem list
        Transcript: Transcript
        /// Every call's rendering and request, newest first.
        Calls: (Rendered * ChatRequest) list
        Step: int
        Live: Live
    }

    override this.ToString() = string this.Transcript

let private appendItem (item: LogItem) (sim: Sim) : Sim =
    { sim with
        Items = item :: sim.Items
        Transcript = Transcript.apply sim.Transcript item
    }

/// Sends a session command; a refused command changes nothing.
let send (by: Attribution) (command: Command) (sim: Sim) : Sim =
    let now = sim.Now + second

    match Machine.step machine sim.State { Command = command; By = by; At = now } with
    | Ok step ->
        let first = sim.State.Seq + 1L

        step.Events
        |> List.indexed
        |> List.fold
            (fun sim (i, event) ->
                appendItem (LogItem.Session(EventRef.create sim.Session (first + int64 i), event)) sim)
            { sim with
                State = step.State
                Now = now
            }
    | Error _ -> { sim with Now = now }

/// Appends a context event (through the exclusions, as a strategy records them).
let record (event: ContextEvent) (sim: Sim) : Sim =
    let seq = sim.ContextSeq + 1L

    appendItem
        (LogItem.Context(EventRef.create sim.Context seq, ContextEvent.withhold sim.Config.Exclusions event))
        { sim with ContextSeq = seq }

/// Appends a context event as it is, unfiltered.
let recordRaw (event: ContextEvent) (sim: Sim) : Sim =
    let seq = sim.ContextSeq + 1L
    appendItem (LogItem.Context(EventRef.create sim.Context seq, event)) { sim with ContextSeq = seq }

let start (id: string) (cfg: RenderConfig) : Sim =
    {
        Config = cfg
        Sections = cfg.Sections
        Session = StreamId.create "session" id
        Context = StreamId.create "context" id
        State = Session.initial
        Now = epoch
        ContextSeq = 0L
        Items = []
        Transcript = Transcript.empty
        Calls = []
        Step = 0
        Live = Live.none
    }
    |> send
        alice
        (Command.Start
            {
                Id = id
                Profile = "sha256:base"
                Overrides = []
                Parent = None
                Title = None
            })

let running (sim: Sim) : int =
    sim.State.Running |> Option.map _.Turn |> Option.defaultValue 0

let steer (text: string) (sim: Sim) =
    send alice (Command.Prompt(text, PromptMode.Steer)) sim

let queue (text: string) (sim: Sim) =
    send alice (Command.Prompt(text, PromptMode.Queue)) sim

/// The step boundary: delivers what waits in the inbox.
let boundary (sim: Sim) =
    let step = sim.Step + 1

    send system (Command.TakeInputs(running sim, step)) { sim with Step = step }

let finish (summary: string) (sim: Sim) =
    send system (Command.EndTurn(running sim, TurnResult.Done(summary, [], None))) { sim with Step = 0 }

let fail (reason: string) (sim: Sim) =
    send system (Command.FailTurn(running sim, reason)) { sim with Step = 0 }

let interrupt (sim: Sim) =
    send alice Command.Interrupt { sim with Step = 0 }

/// What the strategy does for a call: reconcile the frame, render, keep the request, and record the
/// model's reply with the suffix it saw.
let callWith (live: Live) (parts: Part list) (sim: Sim) : Sim =
    let sim =
        match Transcript.reconcile sim.Sections sim.Transcript with
        | Some event -> record event sim
        | None -> sim

    // The strategy renders with the sections as they are now (the frame, once recorded, wins).
    let cfg =
        { sim.Config with
            Sections = sim.Sections
        }

    let rendered = Render.render cfg sim.Transcript live
    let request = Render.request cfg template rendered

    { sim with
        Calls = (rendered, request) :: sim.Calls
    }
    |> record (
        ContextEvent.Replied
            {
                Turn = running sim
                Step = sim.Step
                Shown = rendered.Prompt.Suffix
                Parts = parts
                Used = None
            }
    )

let call (parts: Part list) (sim: Sim) = callWith sim.Live parts sim

let toolCall (id: string) (code: string) : Part =
    Part.ToolCall
        {
            Id = id
            Name = "cell"
            Arguments = Json.serialize (Json.Object [ "code", Json.String code ])
            State = None
        }

let thinking (text: string) (signature: string) : Part =
    Part.Reasoning
        {
            Text = text
            State = state "signature" signature
        }

let returns (callId: string) (pieces: Piece list) (sim: Sim) : Sim =
    record
        (ContextEvent.Returned
            {
                Turn = running sim
                Step = sim.Step
                Call = Some callId
                Name = "cell"
                Pieces = pieces
                IsError = false
                Restarted = None
                Approvals = []
            })
        sim

let pin (name: string) (pin: Pin) (sim: Sim) =
    record (ContextEvent.Pinned { Name = name; Pin = pin }) sim

/// The render of the simulated session now.
let render (sim: Sim) : Rendered =
    Render.render sim.Config sim.Transcript sim.Live

let request (sim: Sim) : ChatRequest =
    Render.request sim.Config template (render sim)

/// The log, oldest first.
let items (sim: Sim) : LogItem list = List.rev sim.Items

/// A request as (role, part) pairs, cache breakpoints left out: equal pairs mean equal content
/// however parts were grouped into messages.
let flat (request: ChatRequest) : (string * string) list =
    let encode (part: Part) =
        Json.serializeCanonical (Part.codec.Encode part)

    [
        for part in request.System do
            match part with
            | Part.CacheBreakpoint _ -> ()
            | part -> "system", encode part
        for message in request.Messages do
            for part in message.Parts do
                match part with
                | Part.CacheBreakpoint _ -> ()
                | part -> string message.Role, encode part
    ]

/// A value for a failure message: lists item by item.
let show (value: 'T) : string =
    match box value with
    | :? string as text -> text
    | :? System.Collections.IEnumerable as items ->
        "[" + String.concat "; " (Seq.cast<obj> items |> Seq.map string) + "]"
    | null -> "none"
    | value -> string value

let isPrefix (a: 'T list) (b: 'T list) : bool =
    a.Length <= b.Length && List.truncate a.Length b = a

let requestJson (request: ChatRequest) : string =
    Json.serializeCanonical (ChatRequest.codec.Encode request)

let breakpoints (request: ChatRequest) : int =
    let count (parts: Part list) =
        parts
        |> List.filter (fun p ->
            match p with
            | Part.CacheBreakpoint _ -> true
            | _ -> false)
        |> List.length

    count request.System + (request.Messages |> List.sumBy (fun m -> count m.Parts))
