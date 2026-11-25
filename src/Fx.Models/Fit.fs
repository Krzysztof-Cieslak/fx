namespace Fx.Models

open System

/// A change made to fit a request to its model (recorded and traced, so nothing changes silently).
[<RequireQualifiedAccess>]
type Adjustment =
    /// A sampling parameter the model does not take was left out.
    | SamplingDropped of parameter: string
    | MaxOutputClamped of requested: int * limit: int
    /// The model does not reason (or not on request): the reasoning request was left out.
    | ReasoningDropped
    | EffortChanged of requested: Effort * used: Effort
    | EffortDropped of requested: Effort
    | BudgetClamped of requested: int * used: int
    | BudgetDropped of requested: int
    /// Media the model does not take became a note in text.
    | MediaOmitted of kind: MediaKind
    /// More cache breakpoints than the model allows: the earliest were left out.
    | BreakpointsDropped of count: int
    | CacheTtlShortened
    | CacheKeyDropped
    | ParallelToolCallsDropped
    | StrictToolsRelaxed
    /// The model cannot be made to call a tool: the choice became `Auto`.
    | ToolChoiceRelaxed of requested: ToolChoice

    override this.ToString() =
        match this with
        | SamplingDropped name -> $"dropped {name}"
        | MaxOutputClamped(requested, limit) -> $"max output {requested} clamped to {limit}"
        | ReasoningDropped -> "dropped the reasoning request"
        | EffortChanged(requested, used) -> $"effort {requested} became {used}"
        | EffortDropped requested -> $"dropped effort {requested}"
        | BudgetClamped(requested, used) -> $"thinking budget {requested} clamped to {used}"
        | BudgetDropped requested -> $"dropped thinking budget {requested}"
        | MediaOmitted kind -> $"omitted {kind} input"
        | BreakpointsDropped count -> $"dropped {count} cache breakpoints"
        | CacheTtlShortened -> "cache time-to-live shortened"
        | CacheKeyDropped -> "dropped the cache key"
        | ParallelToolCallsDropped -> "dropped parallel tool calls"
        | StrictToolsRelaxed -> "tools are not strict"
        | ToolChoiceRelaxed requested -> $"tool choice {requested} became auto"

/// Fits requests to what their model can do (spec §10.3 "capability-driven requests"), so adapters
/// get only what their model takes, and every change is reported.
[<RequireQualifiedAccess>]
module Fit =
    /// The text that stands for media a model does not take.
    let omitted (kind: MediaKind) : string =
        $"[{kind} omitted: this model does not take {kind} input]"

    let private nearestEffort (available: Effort list) (requested: Effort) : Effort option =
        available
        |> List.sortBy (fun e -> abs (Effort.rank e - Effort.rank requested), -(Effort.rank e))
        |> List.tryHead

    /// The request fitted to the model, with the changes made; an error if it cannot be served at all
    /// (tools for a model without tool calls, structured output for one without it, not a chat model).
    let request (info: ModelInfo) (request: ChatRequest) : Result<ChatRequest * Adjustment list, ChatError> =
        let caps = info.Caps
        let changes = ResizeArray<Adjustment>()

        let usesTools =
            not request.Tools.IsEmpty
            || request.Messages
               |> List.exists (fun m ->
                   m.Parts
                   |> List.exists (fun p ->
                       match p with
                       | Part.ToolCall _
                       | Part.ToolResult _ -> true
                       | _ -> false))

        if info.Kind <> ModelKind.Chat then
            Error(Unavailable $"{info.Ref} is not a chat model")
        elif usesTools && not caps.Tools.Calls then
            Error(Unavailable $"{info.Ref} does not call tools")
        elif
            (match request.Output with
             | OutputFormat.Json _ -> not caps.StructuredOutput
             | OutputFormat.Text -> false)
        then
            Error(Unavailable $"{info.Ref} has no structured output")
        else
            // Sampling.
            let s = request.Sampling

            let drop (name: string) (supported: bool) (value: 'T option) : 'T option =
                match value with
                | Some _ when not supported ->
                    changes.Add(Adjustment.SamplingDropped name)
                    None
                | v -> v

            let sampling =
                {
                    Temperature = drop "temperature" caps.Sampling.Temperature s.Temperature
                    TopP = drop "topP" caps.Sampling.TopP s.TopP
                    TopK = drop "topK" caps.Sampling.TopK s.TopK
                    Seed = drop "seed" caps.Sampling.Seed s.Seed
                    Stop =
                        if not s.Stop.IsEmpty && not caps.Sampling.Stop then
                            changes.Add(Adjustment.SamplingDropped "stop")
                            []
                        else
                            s.Stop
                }

            // Output limit.
            let maxOutput =
                match request.MaxOutputTokens with
                | Some n when n > info.Limits.Output ->
                    changes.Add(Adjustment.MaxOutputClamped(n, info.Limits.Output))
                    Some info.Limits.Output
                | n -> n

            // Reasoning.
            let reasoning =
                match request.Reasoning, caps.Reasoning with
                | None, _ -> None
                | Some _, None ->
                    changes.Add Adjustment.ReasoningDropped
                    None
                | Some r, Some rc ->
                    let effort =
                        match r.Effort with
                        | Some e when not (List.contains e rc.Efforts) ->
                            match nearestEffort rc.Efforts e with
                            | Some used ->
                                changes.Add(Adjustment.EffortChanged(e, used))
                                Some used
                            | None ->
                                changes.Add(Adjustment.EffortDropped e)
                                None
                        | e -> e

                    let budget =
                        match r.BudgetTokens, rc.Budget with
                        | None, _ -> None
                        | Some b, None ->
                            changes.Add(Adjustment.BudgetDropped b)
                            None
                        | Some b, Some range ->
                            let used = TokenRange.clamp range b

                            if used <> b then
                                changes.Add(Adjustment.BudgetClamped(b, used))

                            Some used

                    Some
                        { r with
                            Effort = effort
                            BudgetTokens = budget
                        }

            // Media, in content and in tool results.
            let media (part: MediaPart) : MediaPart option =
                if List.contains part.Kind caps.Media then
                    Some part
                else
                    changes.Add(Adjustment.MediaOmitted part.Kind)
                    None

            let fitPart (part: Part) : Part =
                match part with
                | Part.Media m ->
                    match media m with
                    | Some m -> Part.Media m
                    | None -> Part.text (omitted m.Kind)
                | Part.ToolResult r ->
                    Part.ToolResult
                        { r with
                            Content =
                                r.Content
                                |> List.map (fun c ->
                                    match c with
                                    | ToolContent.Media m ->
                                        match media m with
                                        | Some m -> ToolContent.Media m
                                        | None -> ToolContent.Text(omitted m.Kind)
                                    | c -> c)
                        }
                | part -> part

            let system = request.System |> List.map fitPart

            let messages =
                request.Messages
                |> List.map (fun m ->
                    { m with
                        Parts = m.Parts |> List.map fitPart
                    })

            // Cache breakpoints: keep the last ones the model allows; shorten long lifetimes.
            let breakpoints (parts: Part list) =
                parts
                |> List.sumBy (fun p ->
                    match p with
                    | Part.CacheBreakpoint _ -> 1
                    | _ -> 0)

            let total =
                breakpoints system
                + (messages |> List.sumBy (fun m -> breakpoints m.Parts))
                + (if request.Cache.Tools.IsSome then 1 else 0)

            let excess =
                if caps.Caching.Mode = CacheMode.Breakpoints then
                    max 0 (total - caps.Caching.MaxBreakpoints)
                else
                    0

            if excess > 0 then
                changes.Add(Adjustment.BreakpointsDropped excess)

            let mutable toDrop = excess
            let mutable shortened = false

            let ttl (t: CacheTtl) =
                if t = CacheTtl.Long && not caps.Caching.LongTtl then
                    shortened <- true
                    CacheTtl.Short
                else
                    t

            let toolsBreakpoint =
                match request.Cache.Tools with
                | Some _ when toDrop > 0 ->
                    toDrop <- toDrop - 1
                    None
                | Some t -> Some(ttl t)
                | None -> None

            let dropBreakpoints (parts: Part list) =
                parts
                |> List.choose (fun p ->
                    match p with
                    | Part.CacheBreakpoint _ when toDrop > 0 ->
                        toDrop <- toDrop - 1
                        None
                    | Part.CacheBreakpoint t -> Some(Part.CacheBreakpoint(ttl t))
                    | p -> Some p)

            let system = dropBreakpoints system

            let messages =
                messages
                |> List.map (fun m ->
                    { m with
                        Parts = dropBreakpoints m.Parts
                    })

            if shortened then
                changes.Add Adjustment.CacheTtlShortened

            let key =
                match request.Cache.Key with
                | Some _ when not caps.Caching.Key ->
                    changes.Add Adjustment.CacheKeyDropped
                    None
                | k -> k

            // Tools.
            let parallelCalls =
                match request.ParallelToolCalls with
                | Some true when not caps.Tools.Parallel ->
                    changes.Add Adjustment.ParallelToolCallsDropped
                    None
                | p -> p

            let tools =
                if request.Tools |> List.exists (fun t -> t.Strict) && not caps.Tools.Strict then
                    changes.Add Adjustment.StrictToolsRelaxed
                    request.Tools |> List.map (fun t -> { t with Strict = false })
                else
                    request.Tools

            let choice =
                match request.ToolChoice with
                | ToolChoice.Required
                | ToolChoice.Tool _ when not caps.Tools.Forced ->
                    changes.Add(Adjustment.ToolChoiceRelaxed request.ToolChoice)
                    ToolChoice.Auto
                | c -> c

            Ok(
                { request with
                    System = system
                    Messages = messages
                    Tools = tools
                    ToolChoice = choice
                    ParallelToolCalls = parallelCalls
                    Sampling = sampling
                    Reasoning = reasoning
                    MaxOutputTokens = maxOutput
                    Cache = { Key = key; Tools = toolsBreakpoint }
                },
                List.ofSeq changes
            )
