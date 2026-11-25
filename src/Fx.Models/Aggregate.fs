namespace Fx.Models

open System

/// A part being received: how it started, its text so far (pieces, newest first), its state.
type OpenPart =
    {
        Start: PartStart
        Pieces: string list
        State: ProviderState option
        Ended: bool
    }

    override this.ToString() =
        let ended = if this.Ended then "ended" else "open"
        $"{this.Start} ({this.Pieces.Length} pieces, {ended})"

/// How a stream ended.
[<RequireQualifiedAccess>]
type Outcome =
    | Finished of StopReason
    | Failed of ChatError

    override this.ToString() =
        match this with
        | Finished stop -> $"finished ({stop})"
        | Failed error -> $"failed ({error})"

/// A response folded from its events so far (spec §4.4: state is a fold over events). `apply` is
/// pure and total: an event that breaks the stream rules is recorded as a violation, and a stream
/// with violations ends as `InvalidResponse`.
type ChatAggregate =
    {
        Start: ResponseStart option
        /// Parts by index.
        Parts: Map<int, OpenPart>
        Usage: Usage
        Outcome: Outcome option
        /// Broken stream rules, newest first.
        Violations: string list
        /// How many events were applied.
        Count: int
    }

    override this.ToString() =
        let outcome = this.Outcome |> Option.map string |> Option.defaultValue "open"
        $"aggregate({this.Count} events, {this.Parts.Count} parts, {outcome}, {this.Violations.Length} violations)"

[<RequireQualifiedAccess>]
module ChatAggregate =
    let empty: ChatAggregate =
        {
            Start = None
            Parts = Map.empty
            Usage = Usage.zero
            Outcome = None
            Violations = []
            Count = 0
        }

    let private violate (message: string) (state: ChatAggregate) : ChatAggregate =
        { state with
            Violations = $"event {state.Count}: {message}" :: state.Violations
        }

    let private withPart (index: int) (update: OpenPart -> Result<OpenPart, string>) (state: ChatAggregate) =
        match state.Parts.TryFind index with
        | None -> violate $"part {index} was never started" state
        | Some part when part.Ended -> violate $"part {index} already ended" state
        | Some part ->
            match update part with
            | Ok part ->
                { state with
                    Parts = state.Parts.Add(index, part)
                }
            | Error message -> violate message state

    let private apply1 (state: ChatAggregate) (event: ChatEvent) : ChatAggregate =
        match state.Outcome with
        | Some _ -> violate "an event after the end of the stream" state
        | None ->
            match event with
            | ChatEvent.Started start ->
                if state.Start.IsSome then
                    violate "started twice" state
                elif state.Count > 0 then
                    violate "started after other events" { state with Start = Some start }
                else
                    { state with Start = Some start }
            | ChatEvent.PartStarted(index, start) ->
                if state.Parts.ContainsKey index then
                    violate $"part {index} started twice" state
                elif index <> state.Parts.Count then
                    violate $"part {index} started out of order (expected part {state.Parts.Count})" state
                else
                    { state with
                        Parts =
                            state.Parts.Add(
                                index,
                                {
                                    Start = start
                                    Pieces = []
                                    State = None
                                    Ended = false
                                }
                            )
                    }
            | ChatEvent.Delta(index, text) ->
                withPart
                    index
                    (fun part ->
                        Ok
                            { part with
                                Pieces = text :: part.Pieces
                            })
                    state
            | ChatEvent.PartState(index, providerState) ->
                withPart
                    index
                    (fun part ->
                        match part.Start, part.State with
                        | PartStart.Refusal, _ -> Error $"part {index} is a refusal, which has no state"
                        | _, Some _ -> Error $"part {index} got its state twice"
                        | _, None -> Ok { part with State = Some providerState })
                    state
            | ChatEvent.PartEnded index -> withPart index (fun part -> Ok { part with Ended = true }) state
            | ChatEvent.UsageUpdated usage ->
                let state =
                    if Usage.isMonotonic state.Usage usage then
                        state
                    else
                        violate "the usage decreased" state

                { state with Usage = usage }
            | ChatEvent.Finished stop ->
                let unended =
                    state.Parts
                    |> Map.toList
                    |> List.filter (fun (_, p) -> not p.Ended)
                    |> List.map fst

                let calls =
                    state.Parts
                    |> Map.exists (fun _ p ->
                        match p.Start with
                        | PartStart.ToolCall _ -> true
                        | _ -> false)

                let state =
                    match unended with
                    | [] -> state
                    | indices ->
                        let list = indices |> List.map string |> String.concat ", "
                        violate $"finished with parts still open: {list}" state

                let state =
                    if stop = StopReason.ToolUse && not calls then
                        violate "stopped for tool use without a tool call" state
                    else
                        state

                { state with
                    Outcome = Some(Outcome.Finished stop)
                }
            | ChatEvent.Failed error ->
                { state with
                    Outcome = Some(Outcome.Failed error)
                }

    /// Applies one event.
    let apply (state: ChatAggregate) (event: ChatEvent) : ChatAggregate =
        let next = apply1 state event
        { next with Count = state.Count + 1 }

    /// Folds a whole stream.
    let ofEvents (events: ChatEvent seq) : ChatAggregate = Seq.fold apply empty events

    let private text (part: OpenPart) : string =
        part.Pieces |> List.rev |> String.concat ""

    let private toPart (part: OpenPart) : Part =
        match part.Start with
        | PartStart.Text ->
            Part.Text
                {
                    Text = text part
                    State = part.State
                    Sources = []
                }
        | PartStart.Reasoning -> Part.Reasoning { Text = text part; State = part.State }
        | PartStart.ToolCall(id, name) ->
            Part.ToolCall
                {
                    Id = id
                    Name = name
                    Arguments = text part
                    State = part.State
                }
        | PartStart.Refusal -> Part.Refusal(text part)

    /// The parts so far, in index order (open ones as they are).
    let parts (state: ChatAggregate) : Part list =
        state.Parts |> Map.toList |> List.map (snd >> toPart)

    /// The stream rules broken so far, oldest first.
    let violations (state: ChatAggregate) : string list = List.rev state.Violations

    /// What the stream amounts to: the response if it finished by the rules, else the failure
    /// (its own, `InvalidResponse` for broken rules, or a stream that ended without finishing).
    let result (state: ChatAggregate) : Result<ChatResponse, ChatFailure> =
        let id = state.Start |> Option.bind (fun s -> s.Id)
        let model = state.Start |> Option.bind (fun s -> s.Model)

        let failure error =
            Error
                {
                    Error = error
                    Id = id
                    Model = model
                    Parts = parts state
                    Usage = state.Usage
                }

        match violations state, state.Outcome with
        | first :: _, _ -> failure (InvalidResponse first)
        | [], None -> failure (InvalidResponse "the stream ended before the response finished")
        | [], Some(Outcome.Failed error) -> failure error
        | [], Some(Outcome.Finished stop) ->
            Ok
                {
                    Id = id
                    Model = model
                    Parts = parts state
                    Stop = stop
                    Usage = state.Usage
                }

[<RequireQualifiedAccess>]
module ChatEvents =
    /// The stream rules a stream breaks (empty if none), in order. A wire's streams must break none:
    /// `Started` first if at all; parts numbered from 0 in the order they start; deltas, state (at
    /// most once, never on a refusal) and the end only for started, open parts; usage never
    /// decreasing; exactly one `Finished` or `Failed`, last; `Finished` with every part ended, and
    /// `ToolUse` only with a tool call.
    let check (events: ChatEvent seq) : string list =
        let state = ChatAggregate.ofEvents events

        match state.Outcome with
        | None ->
            ChatAggregate.violations state
            @ [ "the stream has no Finished or Failed event" ]
        | Some _ -> ChatAggregate.violations state

    /// Merges adjacent deltas of one part, so that streams that differ only in how their text was
    /// split compare equal.
    let coalesce (events: ChatEvent list) : ChatEvent list =
        let rec go (acc: ChatEvent list) (events: ChatEvent list) =
            match events, acc with
            | [], _ -> List.rev acc
            | ChatEvent.Delta(i, a) :: rest, ChatEvent.Delta(j, b) :: earlier when i = j ->
                go (ChatEvent.Delta(i, b + a) :: earlier) rest
            | event :: rest, _ -> go (event :: acc) rest

        go [] events
        |> List.filter (fun e ->
            match e with
            | ChatEvent.Delta(_, "") -> false
            | _ -> true)

    /// The canonical stream of a response: start, then each part (start, its text as one delta, its
    /// state, end), the usage, and the stop. `ChatAggregate.result (ChatAggregate.ofEvents
    /// (ofResponse r)) = Ok r` for every response whose parts a stream can carry (no media, tool
    /// results or breakpoints).
    let ofResponse (response: ChatResponse) : ChatEvent list =
        let streamed =
            response.Parts
            |> List.choose (fun part ->
                match part with
                | Part.Text t -> Some(PartStart.Text, t.Text, t.State)
                | Part.Reasoning r -> Some(PartStart.Reasoning, r.Text, r.State)
                | Part.ToolCall c -> Some(PartStart.ToolCall(c.Id, c.Name), c.Arguments, c.State)
                | Part.Refusal text -> Some(PartStart.Refusal, text, None)
                | Part.Media _
                | Part.ToolResult _
                | Part.CacheBreakpoint _ -> None)

        [
            ChatEvent.Started
                {
                    Id = response.Id
                    Model = response.Model
                }

            for index, (start, text, state) in List.indexed streamed do
                ChatEvent.PartStarted(index, start)

                if text <> "" then
                    ChatEvent.Delta(index, text)

                match state with
                | Some state -> ChatEvent.PartState(index, state)
                | None -> ()

                ChatEvent.PartEnded index

            ChatEvent.UsageUpdated response.Usage
            ChatEvent.Finished response.Stop
        ]

    /// The stream of a failure: what arrived, then `Failed`.
    let ofFailure (failure: ChatFailure) : ChatEvent list =
        let arrived =
            ofResponse
                {
                    Id = failure.Id
                    Model = failure.Model
                    Parts = failure.Parts
                    Stop = StopReason.EndTurn
                    Usage = failure.Usage
                }

        (arrived |> List.filter (ChatEvent.isTerminal >> not))
        @ [ ChatEvent.Failed failure.Error ]
