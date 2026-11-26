namespace Fx.Tui

open System
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

    let header (model: Model) : Row =
        let t = theme model
        let bar = t.Header
        let sep = s bar " · "

        // Most important first: what is cut at the right edge matters least. The session's id is
        // shortened (its random end); the exit prints it whole.
        let parts =
            [
                [ s (Style.withBold bar) (" " + model.Setup.Product) ]
                [
                    s
                        bar
                        (shortPath (
                            model.Session
                            |> Option.map _.Workspace
                            |> Option.defaultValue model.Setup.Workspace
                        ))
                ]
                match model.Env with
                | Some env ->
                    [ s bar $"env: {env.Name} ({env.Provider})" ]

                    match env.Sandbox with
                    | Some driver -> [ s bar ("sandbox: " + driver) ]
                    | None -> ()
                | None -> ()
                match model.Setup.Model with
                | Some m -> [ s bar m ]
                | None -> ()
                match model.Session with
                | Some session ->
                    let id =
                        if session.Id.Length > 10 then
                            "…" + session.Id.Substring(session.Id.Length - 8)
                        else
                            session.Id

                    [ s bar ("session " + id) ]
                | None -> ()
            ]

        let left =
            parts
            |> List.mapi (fun i part -> if i = 0 then part else sep :: part)
            |> List.concat

        let right =
            state model
            |> List.map (fun span ->
                { span with
                    Style =
                        { span.Style with
                            Bg = bar.Bg
                            Reverse = bar.Reverse
                        }
                })

        Row.spread model.Width (left @ [ s bar " " ]) (right @ [ s bar " " ])
        |> Row.fill model.Width bar
        |> List.map (fun span ->
            if span.Style = Style.plain then
                { span with Style = bar }
            else
                span)

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
        | InputContent.Text text -> paragraph width [ s (Style.withBold t.Accent) "› " ] (bold t) text
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
    let ending (t: Theme) (width: int) (turn: TurnView) (ended: TurnEnd) (at: int64) : Row list =
        let took = s t.Muted ("  " + duration (at - turn.Started))
        let tookAlone = s t.Muted (duration (at - turn.Started))

        match ended with
        | TurnEnd.Completed(TurnResult.Done(summary, evidence, verify)) ->
            [
                yield! markdown t width [ indent 2; s (Style.withBold t.Success) "✓ Done  " ] summary
                for e in evidence do
                    yield! markdown t width [ indent 4; s (bold t) "Evidence  " ] e.Summary
                match verify with
                | Some verify -> yield! markdown t width [ indent 4; s (bold t) "Verify    " ] verify
                | None -> ()
                yield Row.fit width [ indent 4; tookAlone ]
            ]
        | TurnEnd.Completed(TurnResult.NeedInput ids) ->
            // The questions were shown where the turn asked them; only those it did not are here.
            [
                Row.fit width [ indent 2; s (Style.withBold t.Warning) "? Needs input"; took ]
                for question in unasked turn ids do
                    yield! markdown t width [ indent 4 ] question
            ]
        | TurnEnd.Completed(TurnResult.Waiting(jobs, note)) ->
            [
                yield! paragraph width [ indent 2; s (Style.withBold t.Warning) "… Waiting  " ] Style.plain note
                if not jobs.IsEmpty then
                    yield Row.fit width [ indent 4; s t.Muted ("on " + String.concat ", " jobs) ]
            ]
        | TurnEnd.Interrupted ->
            [
                Row.fit width [ indent 2; s (Style.withBold t.Warning) "⏹ Interrupted"; took ]
            ]
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
                yield! ending t width turn ended at
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
        | card :: _ as all -> cardRows model card all.Length
        | [] ->
            App.others model
            |> List.map (fun request ->
                Row.fit
                    model.Width
                    [
                        s t.Muted "┃ "
                        s t.Muted ("waiting for another client: " + request.Request.Title)
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

    let status (model: Model) : Row =
        let t = theme model
        let width = model.Width
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

        let hints =
            match App.cards model with
            | Card.Question _ :: _ -> "Enter answer · Esc interrupt"
            | _ :: _ -> "keys in [ ] answer · ←→ Enter"
            | [] ->
                if tl.State.Running.IsSome then
                    "Enter steer · Tab queue · Esc interrupt"
                else
                    "Enter send · Alt+Enter newline · Ctrl+O detail · Ctrl+C×2 exit"

        let scrolled =
            if model.Scroll > 0 then
                [ s t.Warning "↑ scrolled · PgDn  " ]
            else
                []

        let right = scrolled @ [ s t.Muted hints ]

        // What happens comes first; the hints take what room is left.
        if left.IsEmpty then
            Row.fit width right
        elif Row.width left + Row.width right + 1 <= width then
            Row.spread width left right
        else
            Row.fit width left

    /// The prompt's rows (at most `rows`, scrolled to the cursor) and the cursor's place in them.
    let editor (model: Model) (rows: int) : Row list * (int * int) =
        let t = theme model
        let width = model.Width
        let prefix = 2
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

        let rendered = shown |> List.mapi (fun i line -> Row.fit width [ lead i; p line ])

        rendered, (row - first, min (width - 1) (column + prefix))

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

    /// The whole screen.
    let view (model: Model) : Frame =
        let width = model.Width
        let height = model.Height
        let header = [ header model ]
        let banner = banner model
        let asked = asked model
        let pending = pending model
        let status = [ status model ]

        let editorRows =
            let lines, _ = Editor.layout (max 1 (width - 2)) model.Editor
            min lines.Length (max 1 (min 8 (height / 3)))

        let editor, (cursorRow, cursorColumn) = editor model editorRows
        // What does not fit goes, least important first: the banner stays, the transcript shrinks.
        let fixedRows = header.Length + status.Length + editor.Length
        let room = max 0 (height - fixedRows)

        let banner =
            List.truncate (min banner.Length (max 0 (room / 3))) banner
            |> fun b -> if room >= 2 then b else []

        let room = room - banner.Length
        let asked = List.truncate (max 0 (room - 1)) asked
        let room = room - asked.Length
        let pending = List.truncate (max 0 (room / 3)) pending
        let room = room - pending.Length
        let all = transcript model
        let maxScroll = max 0 (all.Length - room)
        let scroll = min model.Scroll maxScroll

        let visible =
            all |> List.skip (max 0 (all.Length - room - scroll)) |> List.truncate room

        let filler = List.replicate (room - visible.Length) Row.empty

        let rows =
            header @ banner @ filler @ visible @ asked @ pending @ status @ editor
            |> List.truncate height
            |> List.map (Row.fit width >> Row.normalize)

        let rows =
            rows @ List.replicate (height - rows.Length) Row.empty
            |> List.map (paint (theme model) width)

        let editorTop = height - editor.Length

        let cursor =
            match App.cards model with
            | card :: _ when
                (match card with
                 | Card.Question _ -> false
                 | _ -> true)
                ->
                None
            | _ when editorTop + cursorRow < height && editorTop >= 0 -> Some(editorTop + cursorRow, cursorColumn)
            | _ -> None

        {
            Width = width
            Height = height
            Rows = rows
            Cursor = cursor
        }
        |> Frame.lower model.Setup.Terminal.Depth
