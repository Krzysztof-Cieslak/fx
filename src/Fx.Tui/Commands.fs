namespace Fx.Tui

open System

/// What a command does.
[<RequireQualifiedAccess>]
type CommandAction =
    /// A new session in this workspace.
    | NewSession
    /// Pick another session of this host.
    | Sessions
    /// The session's main model (`model.role.main`), from the next turn on.
    | Model
    /// Cycle the transcript's detail.
    | Detail
    /// Pick the theme.
    | Theme
    | Exit

    override this.ToString() =
        match this with
        | NewSession -> "new"
        | Sessions -> "sessions"
        | Model -> "model"
        | Detail -> "detail"
        | Theme -> "theme"
        | Exit -> "exit"

/// A command of the palette (`Ctrl+P`) and of the prompt (`/name`): values, so later screens add
/// their own.
type TuiCommand =
    {
        /// The name typed after `/`.
        Name: string
        /// What it does, for people.
        Title: string
        /// The keys that also run it, if any.
        Keys: string option
        /// What it takes after its name, if anything (`provider/model`).
        Argument: string option
        Action: CommandAction
    }

    override this.ToString() = "/" + this.Name

[<RequireQualifiedAccess>]
module Commands =
    let all: TuiCommand list =
        [
            {
                Name = "new"
                Title = "Start a new session"
                Keys = None
                Argument = None
                Action = CommandAction.NewSession
            }
            {
                Name = "sessions"
                Title = "Switch to another session"
                Keys = None
                Argument = None
                Action = CommandAction.Sessions
            }
            {
                Name = "model"
                Title = "Use another model from the next turn on"
                Keys = None
                Argument = Some "provider/model"
                Action = CommandAction.Model
            }
            {
                Name = "detail"
                Title = "Show more or less of each turn"
                Keys = Some "Ctrl+O"
                Argument = None
                Action = CommandAction.Detail
            }
            {
                Name = "theme"
                Title = "Change the colours"
                Keys = None
                Argument = Some "auto|dark|light|ansi16"
                Action = CommandAction.Theme
            }
            {
                Name = "exit"
                Title = "Leave (the session stays)"
                Keys = Some "Ctrl+C×2"
                Argument = None
                Action = CommandAction.Exit
            }
        ]

    /// How well `query` matches `text`: its characters in order (case ignored), better when they
    /// are together and early; `None` when they are not all there.
    let score (query: string) (text: string) : int option =
        let query = query.Trim().ToLowerInvariant()
        let text = text.ToLowerInvariant()

        if query = "" then
            Some 0
        else
            let mutable at = 0
            let mutable total = 0
            let mutable last = -2
            let mutable missing = false

            for c in query do
                if not missing then
                    let found = text.IndexOf(c, at)

                    if found < 0 then
                        missing <- true
                    else
                        // Together is better than apart, early better than late.
                        total <- total + (if found = last + 1 then 3 else 1) - min 2 (found / 8)
                        last <- found
                        at <- found + 1

            if missing then None else Some total

    /// The commands that match a query, best first (a name's match counts more than a title's).
    let search (query: string) (commands: TuiCommand list) : TuiCommand list =
        commands
        |> List.choose (fun command ->
            match score query command.Name, score query command.Title with
            | Some name, Some title -> Some(command, max (name * 2) title)
            | Some name, None -> Some(command, name * 2)
            | None, Some title -> Some(command, title)
            | None, None -> None)
        |> List.sortByDescending snd
        |> List.map fst

    /// The command a prompt names (`/name argument`): `None` when the prompt is not a command,
    /// `Error` when it names none of them.
    let parse (commands: TuiCommand list) (text: string) : Result<TuiCommand * string, string> option =
        let text = text.Trim()

        if not (text.StartsWith('/')) || text.StartsWith("//", StringComparison.Ordinal) then
            None
        else
            let rest = text.Substring 1
            let space = rest.IndexOf ' '

            let name, argument =
                if space < 0 then
                    rest, ""
                else
                    rest.Substring(0, space), rest.Substring(space + 1).Trim()

            match commands |> List.tryFind (fun c -> c.Name = name.ToLowerInvariant()) with
            | Some command -> Some(Ok(command, argument))
            | None -> Some(Error("/" + name))
