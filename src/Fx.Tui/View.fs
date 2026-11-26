namespace Fx.Tui

open System
open System.Globalization
open Fx.Contracts
open Fx.Sessions
open Fx.Grants

/// The TUI's screen as a pure function of its model (spec §19.2): a header, the environment's
/// warnings, the transcript (prompts, activity lines, result cards), what is asked of the person,
/// a status line and the prompt being typed.
[<RequireQualifiedAccess>]
module View =
    let private second = 1_000_000L

    let private spinnerFrames = [| "⠋"; "⠙"; "⠹"; "⠸"; "⠼"; "⠴"; "⠦"; "⠧"; "⠇"; "⠏" |]

    let spinner (now: int64) : string =
        spinnerFrames[int ((now / 100_000L) % int64 spinnerFrames.Length)]

    /// A duration for people: `0.4s`, `12s`, `3m05s`, `1h02m`.
    let duration (micros: int64) : string =
        let micros = max 0L micros
        let seconds = micros / second

        if micros < 10L * second then
            let tenths = micros / 100_000L
            $"{tenths / 10L}.{tenths % 10L}s"
        elif seconds < 60L then
            $"{seconds}s"
        elif seconds < 3600L then
            let s = seconds % 60L
            let pad = if s < 10L then "0" else ""
            $"{seconds / 60L}m{pad}{s}s"
        else
            let m = (seconds / 60L) % 60L
            let pad = if m < 10L then "0" else ""
            $"{seconds / 3600L}h{pad}{m}m"

    /// Tokens for people: `73,416`.
    let tokens (count: int) : string =
        count.ToString("N0", CultureInfo.InvariantCulture)

    /// Tokens in a few characters: `950`, `73.4K`, `1.2M`.
    let shortTokens (count: int) : string =
        if count < 1000 then
            string count
        elif count < 1_000_000 then
            (float count / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        else
            (float count / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"

    /// US dollars: `$2.11`.
    let dollars (amount: decimal) : string =
        "$" + amount.ToString("0.00", CultureInfo.InvariantCulture)

    /// How much of the context window the last call used, in whole percent.
    let percent (usage: SessionUsage) : int option =
        match usage.Tokens, usage.Window with
        | Some used, Some window when window > 0 -> Some(int (int64 used * 100L / int64 window))
        | _ -> None

    /// The session's title: the one it was given, else its first prompt's first line.
    let title (model: Model) : string option =
        match model.Session |> Option.bind _.Title with
        | Some title when not (String.IsNullOrWhiteSpace title) -> Some title
        | _ ->
            Timeline.prompts model.Timeline
            |> List.tryHead
            |> Option.map Text.firstLine
            |> Option.filter (fun line -> line <> "")

    /// The terminal is wide enough for the sidebar.
    let sidebarWidth = 34

    let hasSidebar (model: Model) = model.Width >= 120

    let private s (style: Style) (text: string) = Span.create style text
    let private p (text: string) = Span.plain text

    /// The theme the model draws with.
    let theme (model: Model) : Theme =
        Theme.choose model.Setup.Theme model.Setup.Terminal

    let private bold (t: Theme) = Style.withBold t.Text

    // ---- the header -----------------------------------------------------------------------------

    /// The last two segments of a path, for the header.
    let private shortPath (path: string) : string =
        let parts =
            path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        match List.rev parts with
        | last :: parent :: _ :: _ -> "…/" + parent + "/" + last
        | _ -> path

    let private state (model: Model) : Span list =
        let t = theme model
        let st = model.Timeline.State

        match model.Phase with
        | Phase.Starting _ -> [ s t.Warning "● starting" ]
        | Phase.Stopped _ -> [ s t.Error "● stopped" ]
        | Phase.Ready ->
            match st.Running with
            | Some turn -> [ s t.Success $"● turn {turn.Turn}" ]
            | None when st.Status = Status.Closed -> [ s t.Muted "● closed" ]
            | None when st.Held -> [ s t.Warning "● held" ]
            | None when not st.AwaitingRequests.IsEmpty -> [ s t.Warning "● needs input" ]
            | None -> [ s t.Muted "● idle" ]

    /// The bottom row: the product, the workspace and the session on the left; the session's state,
    /// whether the transcript is scrolled, and the keys that apply on the right (cut first).
    let footer (model: Model) (sidebar: bool) : Row =
        let t = theme model
        let width = model.Width

        let session =
            match model.Session with
            | Some session ->
                let id =
                    if session.Id.Length > 10 then
                        "…" + session.Id.Substring(session.Id.Length - 8)
                    else
                        session.Id

                [ s t.Muted (" · session " + id) ]
            | None -> []

        let branch =
            match model.Env |> Option.bind _.Branch with
            | Some branch -> ":" + branch
            | None -> ""

        let left =
            [
                s (Style.withBold t.Text) model.Setup.Product
                s
                    t.Muted
                    (" · "
                     + shortPath (
                         model.Session
                         |> Option.map _.Workspace
                         |> Option.defaultValue model.Setup.Workspace
                     )
                     + branch)
            ]
            @ session

        let tl = model.Timeline

        let hints =
            match App.cards model with
            | _ when model.Palette.IsSome -> [ "↑↓ select"; "Enter run"; "Esc close" ]
            | Card.Question _ :: _ -> [ "Enter answer"; "Esc interrupt" ]
            | _ :: _ -> [ "keys in [ ] answer"; "←→ Enter" ]
            | [] ->
                if tl.State.Running.IsSome then
                    [ "Enter steer"; "Tab queue"; "Esc interrupt" ]
                else
                    [
                        "Enter send"
                        "Ctrl+P commands"
                        "Alt+Enter newline"
                        "Ctrl+O detail"
                        "Ctrl+C×2 exit"
                    ]

        let scrolled =
            if model.Scroll > 0 then
                [ s t.Warning "↑ scrolled · PgDn  " ]
            else
                []

        // What the session used, unless the sidebar shows it.
        let used =
            let usage = Timeline.usage tl

            let parts =
                [
                    match usage.Tokens with
                    | Some count ->
                        match percent usage with
                        | Some p -> shortTokens count + " (" + string p + "%)"
                        | None -> shortTokens count
                    | None -> ()
                    match usage.Cost with
                    | Some cost -> dollars cost
                    | None -> ()
                ]

            if parts.IsEmpty || sidebar then
                []
            else
                [ s t.Muted (String.concat " · " parts + "  ") ]

        let state = used @ state model
        // As many hints as fit beside the state and the left side.
        let room = width - Row.width state - Row.width scrolled - Row.width left - 2

        let shown =
            hints
            |> List.fold
                (fun (shown: string list) hint ->
                    let next = shown @ [ hint ]

                    if Text.width (String.concat " · " next) + 2 <= room then
                        next
                    else
                        shown)
                []

        let right =
            scrolled
            @ (if shown.IsEmpty then
                   []
               else
                   [ s t.Muted (String.concat " · " shown + "  ") ])
            @ state

        Row.spread width left right

    /// The environment's warnings (spec §18: running unsandboxed is a persistent warning).
    let banner (model: Model) : Row list =
        let t = theme model
        let warn = t.Banner

        match model.Env with
        | Some env ->
            env.Warnings
            |> List.map (fun warning -> Row.fill model.Width warn [ s warn (" ! " + warning) ])
        | None -> []

    // ---- the transcript -------------------------------------------------------------------------

    let private indent (n: int) = p (String(' ', n))

    /// Rows on the theme's panel background, to the screen's edge (a theme that paints none leaves
    /// them as they are).
    let private raise (t: Theme) (width: int) (rows: Row list) : Row list =
        if t.Panel = Color.Default then
            rows |> List.map (Row.fit width)
        else
            rows
            |> List.map (fun row ->
                row
                |> List.map (fun span ->
                    if span.Style.Bg = Color.Default && not span.Style.Reverse then
                        { span with
                            Style = { span.Style with Bg = t.Panel }
                        }
                    else
                        span)
                |> Row.fill width { t.Text with Bg = t.Panel })

    /// A raised block (a prompt sent, the prompt being typed): a bar at its left and, where the theme
    /// paints panels, a row of padding above and below.
    let private panel (t: Theme) (width: int) (bar: Style) (rows: Row list) : Row list =
        let barred = rows |> List.map (fun row -> s bar "┃ " :: row)

        let padded =
            if t.Panel = Color.Default then
                barred
            else
                [ s bar "┃" ] :: barred @ [ [ s bar "┃" ] ]

        raise t width padded

    let private paragraph (width: int) (lead: Span list) (style: Style) (text: string) : Row list =
        Paragraph.wrap width lead style text

    /// Markdown drawn after `lead` on its first row, every other row indented as far.
    let private markdown (t: Theme) (width: int) (lead: Span list) (text: string) : Row list =
        let w = Row.width lead

        Markdown.render t (width - w) text
        |> List.mapi (fun i row -> (if i = 0 then lead else [ indent w ]) @ row)
        |> List.map (Row.fit width)

    /// The same with every colour muted (what is less important: drafts).
    let private muted (t: Theme) (rows: Row list) : Row list =
        rows
        |> List.map (
            List.map (fun span ->
                { span with
                    Style =
                        { span.Style with
                            Fg = t.Muted.Fg
                            Dim = t.Muted.Dim
                        }
                })
        )

    let private lines (width: int) (lead: int) (style: Style) (text: string) : Row list =
        Text.wrap (max 1 (width - lead)) text
        |> List.map (fun line -> Row.fit width [ indent lead; s style line ])

    let private input (t: Theme) (width: int) (input: Input) : Row list =
        match input.Content with
        | InputContent.Text text ->
            Text.wrap (max 1 (width - 3)) text
            |> List.map (fun line -> [ s t.Text line ])
            |> panel t width t.Bar
        | InputContent.Resolved(request, resolution) ->
            paragraph width [ s t.Muted "↩ " ] t.Muted $"{request} {resolution}"
        | InputContent.JobFinished(job, note) ->
            paragraph width [ s t.Muted "↩ " ] t.Muted $"job {job} finished: {note}"

    let private callRows
        (t: Theme)
        (width: int)
        (verbosity: Verbosity)
        (now: int64)
        (live: bool)
        (first: bool)
        (call: CallView)
        : Row list =
        let marker = if call.Changes then s t.Warning "◆" else s t.Accent "◇"

        let status =
            match call.Ended with
            | Some ended when call.IsError -> [ s t.Error "✗ "; s t.Muted (duration (ended - call.Started)) ]
            | Some ended -> [ s t.Success "✓ "; s t.Muted (duration (ended - call.Started)) ]
            | None when live && first -> [ s t.Accent (spinner now + " "); s t.Muted (duration (now - call.Started)) ]
            | None when live -> [ s t.Muted "…" ]
            | None -> [ s t.Warning "not run" ]

        let left =
            [
                indent 2
                marker
                p " "
                s (bold t) (Text.pad 8 call.Label)
                p " "
                p (Text.firstLine call.Detail)
            ]

        let line = Row.spread width left status

        let error =
            if call.IsError && verbosity <> Verbosity.Quiet then
                lines width 6 t.Error (Text.truncate (max 1 (width - 6)) (Text.firstLine call.Output))
            else
                []

        let detail =
            if verbosity = Verbosity.Verbose then
                [
                    match call.Code with
                    | Some code -> yield! lines width 6 t.Muted code
                    | None -> ()
                    if call.Output <> "" && not call.IsError then
                        yield! (Text.firstLines 12 call.Output |> List.collect (lines width 6 t.Muted))
                    elif call.IsError then
                        yield!
                            (Text.firstLines 12 call.Output
                             |> List.skip 1
                             |> List.collect (lines width 6 t.Muted))
                ]
            else
                []

        line :: error @ detail

    /// The requests a turn ended needing that it did not ask in its items (their ids: nothing else is
    /// known of them).
    let private unasked (turn: TurnView) (ids: string list) : string list =
        ids
        |> List.filter (fun id ->
            turn.Items
            |> List.exists (fun item ->
                match item with
                | TurnItem.Asked request -> request.Request.Id = id
                | _ -> false)
            |> not)

    /// The result card of spec §19.2: how the turn ended, its evidence and how to verify it.
    let ending (t: Theme) (width: int) (turn: TurnView) (ended: TurnEnd) : Row list =
        match ended with
        | TurnEnd.Completed(TurnResult.Done(summary, evidence, verify)) ->
            [
                yield! markdown t width [ indent 2; s (Style.withBold t.Success) "✓ Done  " ] summary
                for e in evidence do
                    yield! markdown t width [ indent 4; s (bold t) "Evidence  " ] e.Summary
                match verify with
                | Some verify -> yield! markdown t width [ indent 4; s (bold t) "Verify    " ] verify
                | None -> ()
            ]
        | TurnEnd.Completed(TurnResult.NeedInput ids) ->
            // The questions were shown where the turn asked them; only those it did not are here.
            [
                Row.fit width [ indent 2; s (Style.withBold t.Warning) "? Needs input" ]
                for question in unasked turn ids do
                    yield! markdown t width [ indent 4 ] question
            ]
        | TurnEnd.Completed(TurnResult.Waiting(jobs, note)) ->
            [
                yield! paragraph width [ indent 2; s (Style.withBold t.Warning) "… Waiting  " ] Style.plain note
                if not jobs.IsEmpty then
                    yield Row.fit width [ indent 4; s t.Muted ("on " + String.concat ", " jobs) ]
            ]
        | TurnEnd.Interrupted -> [ Row.fit width [ indent 2; s (Style.withBold t.Warning) "⏹ Interrupted" ] ]
        | TurnEnd.Failed reason ->
            paragraph width [ indent 2; s (Style.withBold t.Error) "✗ Failed  " ] Style.plain reason
        | TurnEnd.Abandoned reason ->
            paragraph width [ indent 2; s (Style.withBold t.Error) "✗ Abandoned  " ] Style.plain reason

    let private turnRows (model: Model) (turn: TurnView) : Row list =
        let t = theme model
        let width = model.Width
        let verbosity = model.Verbosity

        let live =
            turn.Ended.IsNone
            && (model.Timeline.State.Running |> Option.exists (fun r -> r.Turn = turn.Turn))

        let firstOpen =
            turn.Items
            |> List.tryPick (fun item ->
                match item with
                | TurnItem.Call call when call.Ended.IsNone -> Some call.Id
                | _ -> None)

        let cells =
            turn.Items
            |> List.choose (fun item ->
                match item with
                | TurnItem.Call call -> call.Code
                | _ -> None)

        let echoes = Timeline.echoes turn.Items

        [
            for i in turn.Inputs do
                yield! input t width i

            // A painted panel has its own padding.
            if not turn.Inputs.IsEmpty && t.Panel = Color.Default then
                yield Row.empty

            for item in turn.Items do
                match item with
                | TurnItem.Said text when verbosity <> Verbosity.Quiet -> yield! markdown t width [ indent 2 ] text
                | TurnItem.Said _ -> ()
                | TurnItem.Superseded text when verbosity = Verbosity.Verbose ->
                    yield! muted t (markdown t width [ indent 2 ] text)
                | TurnItem.Superseded _ -> ()
                | TurnItem.Call call -> yield! callRows t width verbosity model.Now live (Some call.Id = firstOpen) call
                | TurnItem.Steered inputs ->
                    for i in inputs do
                        match i.Content with
                        | InputContent.Text text -> yield! paragraph width [ indent 2; s t.Accent "↳ " ] t.Accent text
                        | _ -> yield! input t width i
                | TurnItem.Asked request ->
                    // The agent's question is often the reply just shown: it is not shown again.
                    if not (verbosity <> Verbosity.Quiet && Set.contains request.Request.Id echoes) then
                        yield! markdown t width [ indent 2; s t.Warning "? " ] (Timeline.asks request)
                | TurnItem.Settled resolved ->
                    let text =
                        match resolved.Resolution with
                        | Fx.Sessions.Resolution.Answered response ->
                            match response with
                            | Json.String text -> "answered: " + Text.firstLine text
                            | other -> "answered: " + Json.serialize other
                        | Fx.Sessions.Resolution.Cancelled reason -> "withdrawn: " + reason
                        | Fx.Sessions.Resolution.Expired _ -> "expired"

                    yield Row.fit width [ indent 4; s t.Muted ("· " + text) ]
                | TurnItem.Interrupted by -> yield Row.fit width [ indent 2; s t.Warning ("⏹ interrupted by " + by) ]
                | TurnItem.Compacted summary ->
                    yield
                        Row.fit
                            width
                            [
                                indent 2
                                s t.Accent "◇"
                                p " "
                                s (bold t) (Text.pad 8 "Compact")
                                p " "
                                p "the earlier conversation is summarized"
                            ]

                    if verbosity = Verbosity.Verbose then
                        yield! (Text.firstLines 12 summary |> List.collect (lines width 6 t.Muted))

            if not cells.IsEmpty && verbosity <> Verbosity.Verbose then
                let count = cells |> List.sumBy (fun code -> (Text.clean code).Split('\n').Length)
                let noun = if cells.Length = 1 then "cell" else "cells"

                yield
                    Row.spread
                        width
                        [ indent 2; s t.Muted $"▸ {cells.Length} {noun} ({count} lines)" ]
                        [ s t.Muted "[Ctrl+O expand]" ]

            match model.Streaming with
            | Some writing when live && writing.Turn = turn.Turn && writing.Text.Trim() <> "" ->
                let rows = lines width 2 t.Muted (writing.Text.Trim())

                let rows =
                    if rows.Length > 8 then
                        List.skip (rows.Length - 8) rows
                    else
                        rows

                yield! rows
            | _ -> ()

            match turn.Ended with
            | Some(ended, at) ->
                yield Row.empty
                yield! ending t width turn ended
                // Who answered, how long the turn took and how fast the model wrote.
                let modelName =
                    match turn.Calls with
                    | (used, _) :: _ -> Some(string used.Model)
                    | [] -> model.Setup.Model

                let speed =
                    let output = turn.Calls |> List.sumBy (fun (used, _) -> used.Tokens.Output)
                    let micros = turn.Calls |> List.sumBy snd

                    if output > 0 && micros > 0L then
                        let perSecond = float output * 1_000_000.0 / float micros
                        [ perSecond.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s" ]
                    else
                        []

                let who =
                    [ model.Setup.Product ]
                    @ (modelName |> Option.toList)
                    @ [ duration (at - turn.Started) ]
                    @ speed

                yield Row.fit width [ indent 2; s t.Accent "▣ "; s t.Muted (String.concat " · " who) ]
            | None -> ()

            yield Row.empty
        ]

    let transcript (model: Model) : Row list =
        let t = theme model
        let width = model.Width

        let notes =
            [
                if not model.Setup.Trusted then
                    yield!
                        paragraph
                            width
                            [ s t.Warning "! " ]
                            t.Muted
                            $"This project is not trusted: its config is not read. Run `{model.Setup.Binary} trust` here and start again to use it."
                for warning in model.Setup.Warnings do
                    yield! paragraph width [ s t.Warning "! " ] t.Muted warning
                match model.Phase with
                | Phase.Stopped reason -> yield! paragraph width [ s t.Error "✗ " ] Style.plain reason
                | _ -> ()
            ]

        let body =
            Timeline.blocks model.Timeline
            |> List.collect (fun block ->
                match block with
                | Block.Earlier seq ->
                    [
                        Row.fit width [ s t.Muted $"⋯ earlier events (to #{seq}) are not shown" ]
                        Row.empty
                    ]
                | Block.Note text -> paragraph width [ s t.Muted "· " ] t.Muted text
                | Block.Turn turn -> turnRows model turn)

        let notes = if notes.IsEmpty then [] else notes @ [ Row.empty ]
        notes @ body

    // ---- what is asked --------------------------------------------------------------------------

    /// The most pieces of a permission's evidence shown.
    let private maxEvidence = 6

    let private cardRows (model: Model) (card: Card) (total: int) : Row list =
        let t = theme model
        let width = model.Width
        let bar (color: Style) = s color "┃ "

        let of' = if total > 1 then s t.Muted $"  (1 of {total})" else p ""

        // The options, as many on a row as fit.
        let options (color: Style) : Row list =
            let choices = App.choices model card
            let selected = min model.Selected (max 0 (choices.Length - 1))

            let chunks =
                (choices
                 |> List.mapi (fun i (key, label, _) ->
                     let style = if i = selected then t.Selected else t.Text
                     s style $"[{key}] {label}"))
                @ (if model.Answering.Contains card.Id then
                       [ s t.Muted "sending…" ]
                   else
                       [])

            let rows = ResizeArray<Row>()
            let mutable current: Row = [ bar color ]

            for chunk in chunks do
                let used = Row.width current
                let gap = if current.Length > 1 then 2 else 0

                if current.Length > 1 && used + gap + Text.width chunk.Text > width then
                    rows.Add current
                    current <- [ bar color; chunk ]
                else
                    current <- current @ (if gap > 0 then [ p "  " ] else []) @ [ chunk ]

            rows.Add current
            rows |> Seq.map (Row.fit width) |> List.ofSeq

        let text (color: Style) (style: Style) (value: string) =
            Text.wrap (max 1 (width - 2)) value
            |> List.map (fun line -> Row.fit width [ bar color; s style line ])

        match card with
        | Card.Grant request ->
            let c = t.Permission

            [
                Row.fit width [ bar c; s (Style.withBold c) "Permission"; of' ]
                yield! text c (bold t) request.Summary
                Row.fit
                    width
                    [
                        bar c
                        s t.Muted (request.Action.Value + "  ")
                        p (Text.firstLine request.Resource)
                    ]
                // As much evidence as `fx run` shows at the terminal (`GrantPrompts`).
                for evidence in List.truncate maxEvidence request.Evidence do
                    Row.fit width [ bar c; s t.Muted (evidence.Label + ": "); p (Text.firstLine evidence.Text) ]
                if request.Evidence.Length > maxEvidence then
                    Row.fit width [ bar c; s t.Muted $"(and {request.Evidence.Length - maxEvidence} more)" ]
                yield! options c
            ]
        | Card.Decision(_, question, action, output, choices) ->
            let c = t.Warning

            [
                Row.fit width [ bar c; s (Style.withBold c) "Decision"; of' ]
                yield! text c (bold t) question
                for line in Text.firstLines 6 action do
                    Row.fit width [ bar c; s t.Muted ("  " + line) ]
                if output.Trim() <> "" then
                    Row.fit width [ bar c; s t.Muted "It printed:" ]
                    let printed = (Text.clean output).TrimEnd().Split('\n')

                    for line in printed |> Array.skip (max 0 (printed.Length - 4)) do
                        Row.fit width [ bar c; s t.Muted ("  " + line) ]
                yield! options c
                let selected = min model.Selected (max 0 (choices.Length - 1))

                match List.tryItem selected choices with
                | Some(_, _, detail) when detail <> "" -> yield! text c t.Muted detail
                | _ -> ()
            ]
        | Card.Confirm(_, message) ->
            let c = t.Warning

            [
                Row.fit width [ bar c; s (Style.withBold c) "Confirm"; of' ]
                yield! text c (bold t) message
                yield! options c
            ]
        | Card.Choice(_, question, _) ->
            let c = t.Warning

            [
                Row.fit width [ bar c; s (Style.withBold c) "Choose"; of' ]
                yield! text c (bold t) question
                yield! options c
            ]
        | Card.Question(request, question) ->
            let c = t.Accent

            [
                Row.fit width [ bar c; s (Style.withBold c) "Question"; of' ]
                // The transcript shows the question where the agent asked it: not again here.
                if not (Timeline.shows request.Request.Id model.Timeline) then
                    yield! text c (bold t) question
                Row.fit width [ bar c; s t.Muted "Type the answer below; Enter sends it." ]
            ]

    let asked (model: Model) : Row list =
        let t = theme model

        match App.cards model with
        | card :: _ as all -> cardRows model card all.Length |> raise t model.Width
        | [] ->
            App.others model
            |> List.map (fun request ->
                Row.fit
                    model.Width
                    [
                        s t.Muted "┃ "
                        s t.Muted ("waiting for another client: " + request.Request.Title)
                    ])

    /// The most items the palette and the `/` suggestions show.
    let private maxItems = 8

    /// The command palette (or a picker it opened): its title, the query, and the items it shows.
    let palette (model: Model) : Row list =
        let t = theme model
        let width = model.Width

        match model.Palette with
        | None -> []
        | Some palette ->
            let shown = Palette.shown palette
            let selected = min palette.Selected (max 0 (shown.Length - 1))
            // Keep the selected item in view.
            let first = max 0 (selected - maxItems + 1)

            let labelWidth =
                shown
                |> List.map (fun item -> Text.width item.Label)
                |> List.fold max 8
                |> min 36

            let items =
                shown
                |> List.skip (min first shown.Length)
                |> List.truncate maxItems
                |> List.mapi (fun i item ->
                    let style = if first + i = selected then t.Selected else t.Text

                    Row.fit
                        (width - 2)
                        [
                            s style (" " + Text.pad labelWidth item.Label + "  ")
                            s (if first + i = selected then t.Selected else t.Muted) item.Detail
                        ])

            let body =
                if palette.Loading then [ [ s t.Muted " loading…" ] ]
                elif items.IsEmpty then [ [ s t.Muted " nothing matches" ] ]
                else items

            [
                Row.spread (width - 2) [ s (Style.withBold t.Text) palette.Title ] [ s t.Muted "esc  " ]
                [ s (Style.withBold t.Accent) "› "; s t.Text palette.Query ]
                yield! body
            ]
            |> panel t width t.Accent

    /// What a `/` prompt being typed may name.
    let suggestions (model: Model) : Row list =
        let t = theme model

        match model.Palette with
        | Some _ -> []
        | None ->
            App.slashes model
            |> List.truncate 6
            |> List.mapi (fun i command ->
                let style = if i = 0 then t.Selected else t.Text

                Row.fit
                    model.Width
                    [
                        indent 2
                        s style (" " + Text.pad 10 ("/" + command.Name) + " ")
                        s t.Muted ("  " + command.Title)
                        match command.Argument with
                        | Some argument -> s t.Muted ("  " + argument)
                        | None -> ()
                    ])

    /// Prompts sent but not delivered yet: steered ones wait for the next step, queued ones for the
    /// turn after.
    let pending (model: Model) : Row list =
        let t = theme model
        let st = model.Timeline.State

        let text (input: Input) =
            match input.Content with
            | InputContent.Text text -> Some(Text.firstLine text)
            | _ -> None

        [
            for i in st.Inbox do
                match text i with
                | Some text when st.Running.IsSome ->
                    Row.fit model.Width [ indent 2; s t.Accent "↳ "; s t.Muted ("steering (next step): " + text) ]
                | _ -> ()
            for i in st.Queue do
                match text i with
                | Some queued -> Row.fit model.Width [ indent 2; s t.Muted "⋯ "; s t.Muted ("queued: " + queued) ]
                | None -> ()
        ]

    /// What happens (starting, the running action, a notice): nothing when nothing does.
    let status (model: Model) : Row list =
        let t = theme model
        let tl = model.Timeline

        let left =
            match model.Notice with
            | Some n -> [ s (if n.IsError then t.Error else t.Warning) n.Text ]
            | None ->
                match model.Phase with
                | Phase.Starting what -> [ s t.Accent (spinner model.Now + " "); s t.Muted ("starting: " + what) ]
                | Phase.Stopped _ -> [ s t.Error "stopped · Ctrl+C twice exits" ]
                | Phase.Ready ->
                    match tl.State.Running with
                    | Some turn when not (App.cards model).IsEmpty ->
                        [ s t.Warning "● "; s t.Muted $"turn {turn.Turn} · waiting for an answer" ]
                    | Some turn ->
                        let what =
                            match Timeline.running tl with
                            | Some call -> $"{call.Label} {Text.firstLine call.Detail}"
                            | None ->
                                let since = Timeline.lastProgress tl |> Option.defaultValue turn.At

                                let doing =
                                    match model.Streaming |> Option.map (fun w -> w.Tool, w.Text) with
                                    | Some(Some "cell", _) -> "writing a cell "
                                    | Some(Some tool, _) -> $"writing {tool} "
                                    | Some(None, text) when text <> "" -> "writing "
                                    | _ -> "thinking "

                                doing + duration (model.Now - since)

                        [ s t.Accent (spinner model.Now + " "); s t.Muted $"turn {turn.Turn} · {what}" ]
                    | None when tl.State.Held -> [ s t.Warning "held: the last turn did not finish; a prompt goes on" ]
                    | None -> []

        if left.IsEmpty then
            []
        else
            [ Row.fit model.Width (indent 2 :: left) ]

    /// The line under the prompt: the model, the environment and what confines it.
    let private info (model: Model) : Row =
        let t = theme model

        let parts =
            (model.Setup.Model |> Option.toList)
            @ (match model.Env with
               | Some env ->
                   [
                       $"env: {env.Name} ({env.Provider})"
                       match env.Sandbox with
                       | Some driver -> "sandbox: " + driver
                       | None -> ()
                   ]
               | None -> [])

        [ s t.Muted (String.concat " · " parts) ]

    /// The prompt being typed in its box (at most `rows` of it, scrolled to the cursor) with the info
    /// line under it, and the cursor's place in them.
    let editor (model: Model) (rows: int) : Row list * (int * int) =
        let t = theme model
        let width = model.Width
        // The bar and the marker.
        let prefix = 4
        let lines, (row, column) = Editor.layout (max 1 (width - prefix)) model.Editor
        let rows = max 1 rows
        let first = max 0 (min (row - rows + 1) (lines.Length - rows)) |> max 0
        let first = if row < first then row else first
        let shown = lines |> List.skip first |> List.truncate rows

        let lead (i: int) =
            if first + i = 0 then
                s (Style.withBold t.Accent) "› "
            else
                p "  "

        let typed = shown |> List.mapi (fun i line -> [ lead i; p line ])
        let box = panel t width t.Bar (typed @ [ info model ])
        // The padding row a painted panel has above.
        let top = if t.Panel = Color.Default then 0 else 1
        box, (row - first + top, min (width - 1) (column + prefix))

    /// A row on the theme's background: text in the terminal's default colours takes the theme's,
    /// and a theme that paints its background fills the row to the screen's width.
    let paint (t: Theme) (width: int) (row: Row) : Row =
        let row =
            row
            |> List.map (fun span ->
                let style = span.Style

                let style =
                    if style.Fg = Color.Default && not style.Reverse then
                        { style with Fg = t.Text.Fg }
                    else
                        style

                let style =
                    if style.Bg = Color.Default && not style.Reverse then
                        { style with Bg = t.Background }
                    else
                        style

                { span with Style = style })

        if t.Background = Color.Default then
            row
        else
            Row.fill width { t.Text with Bg = t.Background } row |> Row.normalize

    /// The main column's parts, cut to fit its height: what does not fit goes, least important
    /// first (the banner stays, the transcript shrinks). `Room` is the rows left for the transcript,
    /// `All` the transcript's rows (with a row of margin above them).
    let private layout (model: Model) (sidebar: bool) =
        let width = model.Width
        let height = model.Height
        let banner = banner model
        // The palette, while it is open, takes the place of what is asked.
        let asked =
            match palette model with
            | [] -> asked model
            | rows -> rows

        let pending = pending model @ suggestions model
        let status = status model
        let footer = [ footer model sidebar ]

        let editorRows =
            let lines, _ = Editor.layout (max 1 (width - 4)) model.Editor
            min lines.Length (max 1 (min 8 (height / 3)))

        let editor, (cursorRow, cursorColumn) = editor model editorRows
        // What does not fit goes, least important first: the banner stays, the transcript shrinks.
        let fixedRows = footer.Length + status.Length + editor.Length
        let room = max 0 (height - fixedRows)

        let banner =
            List.truncate (min banner.Length (max 0 (room / 3))) banner
            |> fun b -> if room >= 2 then b else []

        let room = room - banner.Length
        let asked = List.truncate (max 0 (room - 1)) asked
        let room = room - asked.Length
        let pending = List.truncate (max 0 (room / 3)) pending
        let room = room - pending.Length
        // A row of margin under the warnings.
        let all =
            match transcript model with
            | [] -> []
            | rows -> Row.empty :: rows

        {|
            Banner = banner
            Asked = asked
            Pending = pending
            Status = status
            Editor = editor
            Cursor = (cursorRow, cursorColumn)
            Footer = footer
            Room = room
            All = all
        |}

    /// How far the transcript can be scrolled up: until its first row is at the top.
    let private maxScroll (all: Row list) (room: int) = max 0 (all.Length - room)

    /// The main column: the warnings, the transcript from the top, what is asked, the prompts
    /// waiting, what happens, the prompt's box and the footer (painted, not lowered yet).
    let private main (model: Model) (sidebar: bool) : Frame =
        let t = theme model
        let width = model.Width
        let height = model.Height
        let l = layout model sidebar
        let banner, asked, pending, status, editor, footer = l.Banner, l.Asked, l.Pending, l.Status, l.Editor, l.Footer
        let cursorRow, cursorColumn = l.Cursor
        let room, all = l.Room, l.All
        let scroll = min model.Scroll (maxScroll all room)

        let visible =
            all |> List.skip (max 0 (all.Length - room - scroll)) |> List.truncate room

        let filler = List.replicate (room - visible.Length) Row.empty

        let rows =
            banner @ visible @ filler @ asked @ pending @ status @ editor @ footer
            |> List.truncate height
            |> List.map (Row.fit width >> Row.normalize)

        let rows =
            rows @ List.replicate (height - rows.Length) Row.empty
            |> List.map (paint t width)

        let editorTop = height - footer.Length - editor.Length

        let cursor =
            match App.cards model with
            | _ when model.Palette.IsSome -> None
            | card :: _ when
                (match card with
                 | Card.Question _ -> false
                 | _ -> true)
                ->
                None
            | _ when editorTop >= 0 && editorTop + cursorRow < height -> Some(editorTop + cursorRow, cursorColumn)
            | _ -> None

        {
            Width = width
            Height = height
            Rows = rows
            Cursor = cursor
        }

    /// The sidebar of a wide terminal: the session's title, how full the context is and what the
    /// session cost; the workspace and its branch at the bottom.
    let sidebar (model: Model) (width: int) (height: int) : Row list =
        let t = theme model
        let usage = Timeline.usage model.Timeline
        let inner = width - 4
        let line (style: Style) (text: string) = Row.fit inner [ s style text ]

        let top =
            [
                Row.empty
                match title model with
                | Some title ->
                    yield!
                        Text.wrap inner title
                        |> List.truncate 3
                        |> List.map (line (Style.withBold t.Text))
                | None -> line (Style.withBold t.Text) model.Setup.Product
                Row.empty
                line (Style.withBold t.Text) "Context"
                match usage.Tokens with
                | Some count ->
                    line t.Muted (tokens count + " tokens")

                    match percent usage with
                    | Some p -> line t.Muted (string p + "% used")
                    | None -> ()
                | None -> line t.Muted "nothing yet"
                match usage.Cost with
                | Some cost -> line t.Muted (dollars cost + " spent")
                | None -> ()
                match usage.Model with
                | Some m -> line t.Muted m
                | None -> ()
            ]

        let workspace =
            let path =
                model.Session
                |> Option.map _.Workspace
                |> Option.defaultValue model.Setup.Workspace

            match model.Env |> Option.bind _.Branch with
            | Some branch -> shortPath path + ":" + branch
            | None -> shortPath path

        let bottom = [ line t.Muted workspace; Row.empty ]
        let top = List.truncate (max 0 (height - bottom.Length)) top
        let filler = List.replicate (max 0 (height - top.Length - bottom.Length)) Row.empty

        top @ filler @ bottom
        |> List.truncate height
        // A painted panel sets the sidebar apart; on the terminal's own colours a rule does.
        |> List.map (fun row -> (if t.Panel = Color.Default then s t.Muted "│ " else indent 2) :: row)
        |> raise t width

    /// Whether the session shows the start screen: it has no turns yet, nothing waits for the
    /// person and nothing failed.
    let isStart (model: Model) : bool =
        let st = model.Timeline.State

        (match model.Phase with
         | Phase.Stopped _ -> false
         | _ -> true)
        && st.Running.IsNone
        && st.Queue.IsEmpty
        && st.Inbox.IsEmpty
        && (App.cards model).IsEmpty
        && (App.others model).IsEmpty
        && model.Timeline.ResetAt.IsNone
        && Timeline.blocks model.Timeline
           |> List.forall (fun block ->
               match block with
               | Block.Turn _
               | Block.Earlier _ -> false
               | Block.Note _ -> true)

    /// The start screen: the product's name in blocks over a prompt box in the middle, the workspace
    /// and its branch and the keys under it; the warnings at the top and the footer at the bottom.
    let private start (model: Model) : Frame =
        let t = theme model
        let width = model.Width
        let height = model.Height
        let box = min 76 (max 20 (width - 4))
        let left = max 0 ((width - box) / 2)
        let at (row: Row) = indent left :: row
        let inner = { model with Width = box }
        let banner = banner model
        let footer = [ footer model false ]

        let editorRows =
            let lines, _ = Editor.layout (max 1 (box - 4)) model.Editor
            min lines.Length (max 1 (min 6 (height / 4)))

        let editor, (cursorRow, cursorColumn) = editor inner editorRows

        let workspace =
            let path =
                model.Session
                |> Option.map _.Workspace
                |> Option.defaultValue model.Setup.Workspace

            match model.Env |> Option.bind _.Branch with
            | Some branch -> shortPath path + ":" + branch
            | None -> shortPath path

        let keys =
            Row.spread box [ s t.Muted workspace ] [ s (Style.withBold t.Text) "Ctrl+P "; s t.Muted "commands" ]

        // What the person should know before they start (an untrusted project, the host's warnings).
        let notes =
            [
                if not model.Setup.Trusted then
                    yield!
                        paragraph
                            box
                            [ s t.Warning "! " ]
                            t.Muted
                            $"This project is not trusted: its config is not read. Run `{model.Setup.Binary} trust` here and start again to use it."
                for warning in model.Setup.Warnings do
                    yield! paragraph box [ s t.Warning "! " ] t.Muted warning
            ]

        let status = status inner
        let room = height - banner.Length - footer.Length
        let below = [ Row.empty; keys ] @ (if notes.IsEmpty then [] else Row.empty :: notes)

        // The palette, while it is open, takes the place of the name.
        let top =
            match palette inner with
            | [] ->
                let name = model.Setup.Product

                let drawn =
                    if
                        Wordmark.width name <= box
                        && room >= Wordmark.Height + editor.Length + below.Length + 4
                    then
                        Wordmark.draw t.Muted (Style.withBold t.Text) name
                        |> List.map (fun row ->
                            let pad = max 0 ((box - Row.width row) / 2)
                            indent pad :: row)
                    else
                        let pad = max 0 ((box - Text.width name) / 2)
                        [ [ indent pad; s (Style.withBold t.Text) name ] ]

                drawn @ [ Row.empty ]
            | rows -> rows

        let block = top @ status @ editor @ below
        let block = List.truncate (max 0 room) block
        let above = max 0 ((room - block.Length) / 2)
        let afterBlock = max 0 (room - above - block.Length)

        let rows =
            banner
            @ List.replicate above Row.empty
            @ (block |> List.map (Row.fit box >> at))
            @ List.replicate afterBlock Row.empty
            @ footer
            |> List.truncate height
            |> List.map (Row.fit width >> Row.normalize)

        let rows =
            rows @ List.replicate (height - rows.Length) Row.empty
            |> List.map (paint t width)

        let editorTop = banner.Length + above + top.Length + status.Length

        let cursor =
            if model.Palette.IsSome || editorTop + cursorRow >= height - footer.Length then
                None
            else
                Some(editorTop + cursorRow, min (width - 1) (left + cursorColumn))

        {
            Width = width
            Height = height
            Rows = rows
            Cursor = cursor
        }

    /// The whole screen: the main column, with the sidebar at its right on a wide terminal.
    /// The model as the next frame shows it: the scroll kept within the transcript (so scrolling back
    /// down starts at once), and, while scrolled up, kept on the same rows as the transcript grows.
    /// The runtime settles the model before each frame.
    let settle (model: Model) : Model =
        if isStart model then
            model
        else
            let sidebar = hasSidebar model

            let column =
                if sidebar then
                    { model with
                        Width = model.Width - sidebarWidth
                    }
                else
                    model

            let l = layout column sidebar
            let total = l.All.Length

            let scroll =
                if model.Scroll > 0 && model.TranscriptRows > 0 && total > model.TranscriptRows then
                    model.Scroll + (total - model.TranscriptRows)
                else
                    model.Scroll

            let scroll = min scroll (maxScroll l.All l.Room)

            if scroll = model.Scroll && total = model.TranscriptRows then
                model
            else
                { model with
                    Scroll = scroll
                    TranscriptRows = total
                }

    let view (model: Model) : Frame =
        let t = theme model

        let frame =
            if isStart model then
                start model
            elif hasSidebar model then
                let mainWidth = model.Width - sidebarWidth
                let left = main { model with Width = mainWidth } true
                let right = sidebar model sidebarWidth model.Height

                { left with
                    Width = model.Width
                    Rows =
                        List.zip left.Rows right
                        |> List.map (fun (row, side) ->
                            let row =
                                Row.fill mainWidth { t.Text with Bg = t.Background } (Row.fit mainWidth row)

                            Row.normalize (row @ side))
                }
            else
                main model false

        frame |> Frame.lower model.Setup.Terminal.Depth
