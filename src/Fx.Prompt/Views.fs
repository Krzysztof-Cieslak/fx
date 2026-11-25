namespace Fx.Prompt

open System
open System.Globalization

/// What the strategy knows at the moment of a call that is not in the log, gathered through its
/// capabilities before it renders (the render itself does no I/O).
type Live =
    {
        /// Microseconds since the Unix epoch (from `IClock`), for the `time` view.
        Now: int64 option
        /// The current content of live-pinned files (`Views.pinnedFiles` says which to read). A pinned
        /// file missing here is shown as unavailable.
        Files: FileView list
        /// Status lines, e.g. `budget` → `$1.20 of $2.00`, for the `status` view.
        Status: (string * string) list
        /// Blocks for this call only (a loop-guard warning, a reminder to finish): always shown.
        Notes: Block list
    }

    override this.ToString() =
        $"live({this.Files.Length} files, {this.Status.Length} status lines, {this.Notes.Length} notes)"

/// What a view reads: the transcript, what its history shows, and what is live.
type ViewInput =
    {
        Transcript: Transcript
        Visible: Visible list
        Live: Live
    }

    override this.ToString() = $"view input ({this.Transcript})"

/// A dynamic view of the state (spec §5 `cfg.Views`), rendered into the prompt's suffix. A view must be
/// pure, and should show only what changed since the visible history last showed it (`Block.Version`,
/// `Transcript.lastShown`): what a call's suffix showed stays in the history before the reply.
type View =
    {
        Id: string
        Render: ViewInput -> Block list
    }

    override this.ToString() = "view " + this.Id

[<RequireQualifiedAccess>]
module Live =
    let none: Live =
        {
            Now = None
            Files = []
            Status = []
            Notes = []
        }

[<RequireQualifiedAccess>]
module Views =
    /// A block when its version differs from what the visible history last showed under its id.
    let private changed (input: ViewInput) (block: Block) : Block option =
        match Transcript.lastShown block.Id input.Visible with
        | Some shown when shown.Version = block.Version && block.Version.IsSome -> None
        | _ -> Some block

    let private sameLines (a: LineRange list) (b: LineRange list) =
        LineRange.normalize a = LineRange.normalize b

    /// The files live pins show, as (path, lines): what to read into `Live.Files` before rendering.
    let pinnedFiles (transcript: Transcript) : (string * LineRange list) list =
        transcript.Pins
        |> List.choose (fun pin ->
            match pin.Spec.Pin with
            | Pin.File(path, lines) -> Some(Paths.normalize path, LineRange.normalize lines)
            | Pin.Value _ -> None)
        |> List.distinct

    /// `pins`: each pinned value once (again after a collapse hides it), each pinned file whenever its
    /// version or lines changed since it was last shown.
    let pins: View =
        {
            Id = "pins"
            Render =
                fun input ->
                    input.Transcript.Pins
                    |> List.choose (fun pin ->
                        let name = pin.Spec.Name

                        let version, pieces =
                            match pin.Spec.Pin with
                            | Pin.Value pieces -> string pin.Ref, Piece.Text $"Pinned {name}:" :: pieces
                            | Pin.File(path, lines) ->
                                let path = Paths.normalize path

                                match
                                    input.Live.Files
                                    |> List.tryFind (fun f -> Paths.normalize f.Path = path && sameLines f.Lines lines)
                                with
                                | Some view ->
                                    let ranges = LineRange.normalize view.Lines |> List.map string |> String.concat ","

                                    $"{view.Digest}:{ranges}", [ Piece.Text $"Pinned {name}:"; Piece.File view ]
                                | None -> "unavailable", [ Piece.Text $"Pinned {name}: {path} is not available." ]

                        changed
                            input
                            {
                                Id = "pin:" + name
                                Version = Some version
                                Pieces = pieces
                            })
        }

    /// `status`: the status lines (`Live.Status`) whenever they changed.
    let status: View =
        {
            Id = "status"
            Render =
                fun input ->
                    match input.Live.Status with
                    | [] -> []
                    | lines ->
                        let text =
                            lines |> List.map (fun (key, value) -> key + ": " + value) |> String.concat "\n"

                        changed
                            input
                            {
                                Id = "status"
                                Version = Some text
                                Pieces = [ Piece.Text("Status:\n" + text) ]
                            }
                        |> Option.toList
        }

    /// `time`: today's date (UTC) whenever it changed.
    let time: View =
        {
            Id = "time"
            Render =
                fun input ->
                    match input.Live.Now with
                    | None -> []
                    | Some now ->
                        let date =
                            DateTimeOffset
                                .FromUnixTimeMilliseconds(now / 1000L)
                                .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

                        changed
                            input
                            {
                                Id = "time"
                                Version = Some date
                                Pieces = [ Piece.Text $"Today is {date} (UTC)." ]
                            }
                        |> Option.toList
        }

    /// `notes`: the notes for this call (`Live.Notes`), always.
    let notes: View =
        {
            Id = "notes"
            Render = fun input -> input.Live.Notes
        }

    /// The views `prompt.views` chooses from, by id.
    let builtIn: View list = [ pins; status; time ]

    /// The views of these ids, in this order, then `notes` (always last).
    let select (ids: string list) : View list =
        (ids |> List.choose (fun id -> builtIn |> List.tryFind (fun v -> v.Id = id)))
        @ [ notes ]
