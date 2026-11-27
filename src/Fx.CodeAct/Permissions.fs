namespace Fx.CodeAct

open System
open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Kernel
open Fx.Envd
open Fx.Interpreters
open Fx.Models
open Fx.Sdk
open Fx.Grants

/// What the grants guard does with a call that needs a grant (docs/modules/codeact.md, "Grants").
[<RequireQualifiedAccess>]
type GrantMode =
    /// The grants log decides; a person answers what no grant covers.
    | Ask
    /// As `Ask`, with auto approvals on: commands a sandbox driver confines to the workspace run
    /// without asking (docs/modules/codeact.md, "Approval modes").
    | Auto
    /// The grants log decides; what no grant allows is refused without asking anyone (unattended runs).
    | Deny
    /// Nothing is checked: for sandboxed or throwaway environments only.
    | Off

    override this.ToString() =
        match this with
        | Ask -> "ask"
        | Auto -> "auto"
        | Deny -> "deny"
        | Off -> "off"

/// How a turn's host calls are checked, read once per turn from its profile.
type GrantSettings =
    {
        /// `codeact.grants.mode`.
        Mode: GrantMode
        /// `codeact.grants.allowCommands`: command patterns that run without a grant when written
        /// plainly in the workspace (deny grants and policy still apply).
        AllowCommands: string list
        /// `codeact.grants.askCommands`: command patterns auto approvals never cover (they reach the
        /// network, install software or run a shell's inline command line).
        AskCommands: string list
        /// `codeact.grants.protectedPaths`: workspace paths whose writes need a grant.
        ProtectedPaths: string list
        /// `codeact.grants.timeoutMs`: how long a request waits for a person.
        Timeout: TimeSpan option
        /// The turn's content exclusions (`ContentExclusions.read`): a listed command that names an
        /// excluded file asks, as its output goes to the model.
        Exclusions: Exclusions
    }

    override this.ToString() =
        $"grant settings ({this.Mode}, {this.AllowCommands.Length} commands allowed, {this.ProtectedPaths.Length} protected paths)"

/// The grants guard's variant slots (spec §15). The mode and both lists are privileged: a project or a
/// session can never loosen them (spec §13.2: policy only tightens).
[<RequireQualifiedAccess>]
module GrantSlots =
    let private patterns (kind: ResourceKind) (what: string) : Codec<string list> =
        Codec.list (
            Codec.string
            |> Codec.refine (fun pattern ->
                Pattern.validate kind pattern
                |> Result.mapError (fun reason -> $"not a {what} pattern: {reason}"))
        )

    /// `codeact.grants.mode`: `ask`, `auto`, `deny` or `off`.
    let mode: Slot<GrantMode> =
        Slot.enum
            "codeact.grants.mode"
            "How host calls that need a grant (commands, writes outside the workspace or to protected paths, reads outside it, network) are checked: ask (the grants log decides, a person answers what no grant covers), auto (as ask, but commands a sandbox driver confines to the workspace run without asking; network, installs and writes outside it still ask), deny (what no grant allows is refused without asking: unattended runs) or off (nothing is checked: sandboxed or throwaway environments only)."
            [
                GrantMode.Ask, "ask"
                GrantMode.Auto, "auto"
                GrantMode.Deny, "deny"
                GrantMode.Off, "off"
            ]
            GrantMode.Ask
        |> Slot.privileged

    /// The commands that run without a grant by default: they read and print, write nothing and
    /// start no other program, whatever their arguments.
    let defaultCommands: string list =
        [
            "pwd"
            "ls"
            "ls **"
            "cat **"
            "head **"
            "tail **"
            "wc **"
            "grep **"
            "echo **"
            "git status"
            "git status **"
            "Get-Location"
            "Get-ChildItem"
            "Get-ChildItem **"
            "Get-Content **"
            "Select-String **"
            "Write-Output **"
        ]

    /// `codeact.grants.allowCommands`: command patterns that need no grant.
    let allowCommands: Slot<string list> =
        Slot.create
            "codeact.grants.allowCommands"
            "Commands (grants' command patterns, matched exactly) that run without a grant when written plainly (nothing the shell expands) in the workspace, with no path argument outside it; deny grants and policy still apply. Only commands that read: anything here can run unasked."
            (patterns ResourceKind.Command "command")
            defaultCommands
        |> Slot.privileged

    /// The commands auto approvals never cover by default: they reach the network (downloads, remote
    /// shells, git's remotes, hosting CLIs), install software (package managers' installs, adds and
    /// publishes, which also run packages' scripts) or hand a shell an inline command line the guard
    /// cannot split. A guardrail of known spellings, not a boundary: a script can reach the network
    /// too (docs/modules/codeact.md, "Approval modes").
    let defaultAskCommands: string list =
        [
            // Downloads and remote shells.
            "curl **"
            "wget **"
            "ssh **"
            "scp **"
            "sftp **"
            "rsync **"
            "nc **"
            "ncat **"
            "netcat **"
            "socat **"
            "telnet **"
            "ftp **"
            // git's remotes and hosting CLIs.
            "git clone **"
            "git fetch **"
            "git pull **"
            "git push **"
            "git ls-remote **"
            "git remote update **"
            "git submodule update **"
            "gh **"
            "glab **"
            // Installs and publishes.
            "npm install **"
            "npm i **"
            "npm ci **"
            "npm add **"
            "npm update **"
            "npm publish **"
            "npm exec **"
            "npx **"
            "pnpm install **"
            "pnpm i **"
            "pnpm add **"
            "pnpm update **"
            "pnpm dlx **"
            "pnpm publish **"
            "yarn"
            "yarn install **"
            "yarn add **"
            "yarn up **"
            "yarn dlx **"
            "yarn npm **"
            "bun install **"
            "bun i **"
            "bun add **"
            "bun update **"
            "bun x **"
            "bunx **"
            "bun publish **"
            "pip install **"
            "pip3 install **"
            "pipx **"
            "uv pip install **"
            "uv add **"
            "uv sync **"
            "uv tool **"
            "uvx **"
            "poetry install **"
            "poetry add **"
            "cargo install **"
            "cargo add **"
            "cargo fetch **"
            "cargo update **"
            "cargo publish **"
            "go get **"
            "go install **"
            "go mod download **"
            "dotnet add ** package **"
            "dotnet tool install **"
            "dotnet tool update **"
            "dotnet workload install **"
            "dotnet nuget push **"
            "gem install **"
            "bundle install **"
            "brew install **"
            "brew upgrade **"
            "apt install **"
            "apt-get install **"
            "apk add **"
            "dnf install **"
            "yum install **"
            "snap install **"
            "winget install **"
            "choco install **"
            "scoop install **"
            // A shell's inline command line: the guard does not split it.
            "sh -c **"
            "bash -c **"
            "zsh -c **"
            "dash -c **"
            "pwsh -c **"
            "pwsh -Command **"
            "powershell -c **"
            "powershell -Command **"
            "cmd /c **"
            // PowerShell's downloads and installs.
            "Invoke-WebRequest **"
            "iwr **"
            "Invoke-RestMethod **"
            "irm **"
            "Start-BitsTransfer **"
            "Install-Module **"
            "Install-Package **"
            "Install-Script **"
            "Save-Module **"
            "Update-Module **"
        ]

    /// `codeact.grants.askCommands`: command patterns auto approvals never cover.
    let askCommands: Slot<string list> =
        Slot.create
            "codeact.grants.askCommands"
            "Commands (grants' command patterns, matched ignoring case, after leading variable assignments and wrappers such as env, sudo, nohup, time, timeout, nice and xargs) that auto approvals never run without a person: they reach the network, install software or hand a shell an inline command line. A guardrail, not a boundary."
            (patterns ResourceKind.Command "command")
            defaultAskCommands
        |> Slot.privileged

    /// The workspace paths whose writes need a grant by default: the repository's own data (its
    /// hooks run code) and the project's configuration.
    let defaultProtected: string list =
        [
            ".git"
            ".git/**"
            Fx.Product.projectDirName
            Fx.Product.projectDirName + "/**"
        ]

    /// `codeact.grants.protectedPaths`: workspace paths whose writes need a grant.
    let protectedPaths: Slot<string list> =
        Slot.create
            "codeact.grants.protectedPaths"
            "Paths in the workspace (grants' path patterns, relative to the workspace, matched ignoring case) whose writes need a grant like writes outside it."
            (patterns ResourceKind.Path "path")
            defaultProtected
        |> Slot.privileged

    /// `codeact.grants.timeoutMs`: how long a request waits for a person.
    let timeoutMs: Slot<int option> =
        Slot.create
            "codeact.grants.timeoutMs"
            "Milliseconds a grant request waits for a person before it times out and the call is refused; null to wait until answered or interrupted."
            (Codec.option (Codec.int |> Codec.atLeast 1))
            None

    /// The guard's slots, for the host's registry (`CodeActSlots.read` includes them).
    let all: SlotInfo list =
        [
            Slot.info mode
            Slot.info allowCommands
            Slot.info askCommands
            Slot.info protectedPaths
            Slot.info timeoutMs
        ]

[<RequireQualifiedAccess>]
module GrantSettings =
    /// The slots' defaults.
    let defaults: GrantSettings =
        {
            Mode = GrantSlots.mode.Default
            AllowCommands = GrantSlots.allowCommands.Default
            AskCommands = GrantSlots.askCommands.Default
            ProtectedPaths = GrantSlots.protectedPaths.Default
            Timeout = None
            Exclusions = Exclusions.none
        }

    /// Reads the settings through `IVariants`.
    let read<'R when 'R :> IVariants> : Eff<'R, GrantSettings> =
        eff {
            let! mode = Variants.get GrantSlots.mode
            let! commands = Variants.get GrantSlots.allowCommands
            let! asks = Variants.get GrantSlots.askCommands
            let! paths = Variants.get GrantSlots.protectedPaths
            let! timeout = Variants.get GrantSlots.timeoutMs
            let! exclusions = ContentExclusions.read

            return
                {
                    Mode = mode
                    AllowCommands = commands
                    AskCommands = asks
                    ProtectedPaths = paths
                    Timeout = timeout |> Option.map (fun ms -> TimeSpan.FromMilliseconds(float ms))
                    // Settings that do not compile fail the turn (the strategy checks them first).
                    Exclusions =
                        match ContentExclusions.compile exclusions with
                        | Ok compiled -> compiled
                        | Error _ -> Exclusions.none
                }
        }

    type private VariantsEnv =
        {
            Variants: Variants
        }

        interface IVariants with
            member env.Variants = env.Variants

    /// The settings of a variants interpreter (a turn's profile), whatever the caller's environment.
    let ofVariants<'R> (variants: Variants) : Eff<'R, GrantSettings> = fun _ -> read { Variants = variants }

/// Where host calls act: the workspace, its operating system and its shell's language.
type Workplace =
    {
        /// Absolute, in the environment's syntax.
        Workspace: string
        Os: Os
        Shell: ShellDialect
    }

    override this.ToString() =
        $"{this.Workspace} ({this.Os}, {this.Shell})"

module Workplace =
    /// A workspace with its OS's shell: PowerShell on Windows, sh elsewhere (as `shell.run` runs).
    let create (workspace: string) (os: Os) : Workplace =
        {
            Workspace = workspace
            Os = os
            Shell =
                if os = Os.Windows then
                    ShellDialect.PowerShell
                else
                    ShellDialect.Sh
        }

/// What a host call needs from the grants log: one action on one canonical resource.
type GrantNeed =
    {
        Action: ActionName
        /// Canonical for the action's kind (`Resource.validate`).
        Resource: string
        /// Where in the workspace it happens (repo-relative, `/`), for repository-scoped grants: the
        /// file, or a command's working directory; "" for the root and for anything outside.
        Place: string
        /// One line for the prompt.
        Summary: string
        Evidence: Evidence list
        /// Broader patterns a person may grant instead (each matches the resource).
        Suggestions: string list
        /// Listed in `codeact.grants.allowCommands` and written plainly: allowed when no grant or
        /// policy says otherwise, without asking.
        Listed: bool
        /// A command written plainly in the workspace and not in `codeact.grants.askCommands`: auto
        /// approvals run it without asking where a sandbox driver confines the hands, when no grant or
        /// policy says otherwise.
        Confined: bool
    }

    override this.ToString() =
        this.Action.Value
        + " "
        + this.Resource
        + (if this.Listed then " (listed)" else "")
        + (if this.Confined then " (confined)" else "")

/// What a host call needs before it may run.
[<RequireQualifiedAccess>]
type CallNeeds =
    /// These grants, in order; none for a call that acts only on the workspace.
    | Needs of GrantNeed list
    /// It is refused outright: what it acts on cannot be named as a grant's resource.
    | Refused of reason: string

    override this.ToString() =
        match this with
        | Needs needs -> "needs " + String.Join(", ", needs |> List.map string)
        | Refused reason -> "refused: " + reason

/// How the calls of one binding map to grants. A binding that runs programs, writes, reads outside
/// the workspace or connects anywhere must have one; calls of bindings without one need nothing.
[<NoEquality; NoComparison>]
type BindingRule =
    {
        Binding: string
        Needs: GrantSettings -> Workplace -> HostCall -> CallNeeds
    }

    override this.ToString() = "rule for " + this.Binding

/// Host calls as grants (docs/modules/codeact.md, "Grants"): `shell.run` is `shell.exec` for each
/// simple command of its line (and `fs.write` for files it redirects to outside the workspace), writes
/// outside the workspace or to protected paths are `fs.write`, reads outside it `fs.read`, and network
/// bindings `net.connect`. Pure: resources are read from the call's arguments and the workplace, never
/// from the filesystem, so symbolic links are not followed (the sandbox is the boundary, spec §18).
[<RequireQualifiedAccess>]
module Permissions =
    let shellExec = ActionName.create "shell.exec"
    let fsRead = ActionName.create "fs.read"
    let fsWrite = ActionName.create "fs.write"
    let netConnect = ActionName.create "net.connect"

    /// The most grants one call asks a person for; a command line with more is asked about whole.
    [<Literal>]
    let MaxNeeds = 16

    let private bounded (limit: int) (text: string) =
        if text.Length <= limit then
            text
        else
            text.Substring(0, limit - 1) + "…"

    /// Where a path is, as grants name it.
    [<RequireQualifiedAccess>]
    type Location =
        /// In the workspace: relative to it, canonical ("" for the workspace itself).
        | Inside of relative: string
        /// Outside it: absolute, canonical, `/`-separated.
        | Outside of absolute: string
        /// A path grants cannot name (a UNC share, …).
        | Unnamed of reason: string

        override this.ToString() =
            match this with
            | Inside relative -> "inside " + relative
            | Outside absolute -> "outside " + absolute
            | Unnamed reason -> "unnamed: " + reason

    /// Where a path (relative to `cwd`, an absolute path in the environment's syntax, or absolute)
    /// is: `..` resolved, compared with the workspace by the case rules of its OS (as content
    /// exclusions are).
    let locate (place: Workplace) (cwd: string) (path: string) : Location =
        let full = CoreBindings.resolve (CoreSettings.defaults cwd place.Os) path

        let relative =
            CoreBindings.exclusionPath (CoreSettings.defaults place.Workspace place.Os) full

        if WorkspacePath.isAbsolute relative then
            match Resource.validate ResourceKind.Path relative with
            | Ok() -> Location.Outside relative
            | Error reason -> Location.Unnamed reason
        else
            match relative with
            | "" -> Location.Inside ""
            | _ ->
                match Resource.validate ResourceKind.Path relative with
                | Ok() -> Location.Inside relative
                | Error reason -> Location.Unnamed reason

    /// A path's directory in the environment's syntax (for `cwd` of later lookups).
    let private absolute (place: Workplace) (cwd: string) (path: string) : string =
        CoreBindings.resolve (CoreSettings.defaults cwd place.Os) path

    let private repoPlace (relative: string) =
        if Resource.isRepoPath relative then relative else ""

    let private isProtected (settings: GrantSettings) (relative: string) =
        relative <> ""
        && settings.ProtectedPaths
           |> List.exists (fun pattern -> Pattern.matchesIgnoringCase ResourceKind.Path pattern relative)

    /// The patterns offered with a request, broader first last; each valid and matching.
    let private suggest (kind: ResourceKind) (resource: string) (patterns: string list) : string list =
        patterns
        |> List.distinct
        |> List.filter (fun pattern ->
            pattern <> Pattern.exact resource
            && Pattern.validate kind pattern = Ok()
            && Pattern.matches kind pattern resource)

    let private pathSuggestions (path: string) : string list =
        let cut = path.LastIndexOf '/'

        if cut > 0 then
            [ Pattern.exact (path.Substring(0, cut)) + "/**" ]
        else
            []
        |> suggest ResourceKind.Path path

    let private commandSuggestions (resource: string) : string list =
        match resource.Split(' ') |> List.ofArray with
        | program :: sub :: _ :: _ when not (sub.StartsWith '-') ->
            [
                Pattern.exact program + " " + Pattern.exact sub + " **"
                Pattern.exact program + " **"
            ]
        | program :: _ :: _ -> [ Pattern.exact program + " **" ]
        | _ -> []
        |> suggest ResourceKind.Command resource

    let private evidence (label: string) (text: string) : Evidence =
        {
            Label = label
            Text = bounded GrantLog.MaxEvidenceText text
        }

    /// What a write to a path needs: nothing in the workspace outside protected paths.
    let write (settings: GrantSettings) (place: Workplace) (cwd: string) (via: string) (path: string) : CallNeeds =
        match locate place cwd path with
        | Location.Unnamed reason -> CallNeeds.Refused $"{path} cannot be named in a grant ({reason})"
        | Location.Inside relative when not (isProtected settings relative) -> CallNeeds.Needs []
        | Location.Inside relative ->
            CallNeeds.Needs
                [
                    {
                        Action = fsWrite
                        Resource = relative
                        Place = repoPlace relative
                        Summary = bounded 300 $"Write {relative} (a protected path in the workspace)"
                        Evidence = [ evidence "through" via ]
                        Suggestions = pathSuggestions relative
                        Listed = false
                        Confined = false
                    }
                ]
        | Location.Outside outside ->
            CallNeeds.Needs
                [
                    {
                        Action = fsWrite
                        Resource = outside
                        Place = ""
                        Summary = bounded 300 $"Write {outside} (outside the workspace)"
                        Evidence = [ evidence "through" via ]
                        Suggestions = pathSuggestions outside
                        Listed = false
                        Confined = false
                    }
                ]

    /// What a read of a path needs: nothing in the workspace.
    let read (place: Workplace) (via: string) (path: string) : CallNeeds =
        match locate place place.Workspace path with
        | Location.Unnamed reason -> CallNeeds.Refused $"{path} cannot be named in a grant ({reason})"
        | Location.Inside _ -> CallNeeds.Needs []
        | Location.Outside outside ->
            CallNeeds.Needs
                [
                    {
                        Action = fsRead
                        Resource = outside
                        Place = ""
                        Summary = bounded 300 $"Read {outside} (outside the workspace)"
                        Evidence = [ evidence "through" via ]
                        Suggestions = pathSuggestions outside
                        Listed = false
                        Confined = false
                    }
                ]

    let private concat (needs: CallNeeds list) : CallNeeds =
        match
            needs
            |> List.tryPick (fun n ->
                match n with
                | CallNeeds.Refused reason -> Some reason
                | _ -> None)
        with
        | Some reason -> CallNeeds.Refused reason
        | None ->
            needs
            |> List.collect (fun n ->
                match n with
                | CallNeeds.Needs list -> list
                | CallNeeds.Refused _ -> [])
            |> CallNeeds.Needs

    // ---- Commands ---------------------------------------------------------------------------------

    /// Commands that change the working directory of the commands after them.
    let private changesDirectory (shell: ShellDialect) (program: string) : bool =
        match shell with
        | ShellDialect.Sh -> List.contains program [ "cd"; "chdir"; "pushd"; "popd" ]
        | ShellDialect.PowerShell ->
            List.contains
                (program.ToLowerInvariant())
                [
                    "cd"
                    "chdir"
                    "sl"
                    "set-location"
                    "pushd"
                    "popd"
                    "push-location"
                    "pop-location"
                ]

    /// The most working directories a line's commands are followed in; more make it unknown.
    [<Literal>]
    let private MaxDirectories = 16

    /// Where later commands run after a command that changes the directory (`cd x`, `pushd x`,
    /// `Set-Location x`, `popd`), given where they may run before it: any of those, or those changed
    /// to the plain path it names (subshells and groups are flattened, so a `cd` in one may have been
    /// undone; the set holds every directory the line may be in). `None`: unknown (`cd` alone or to
    /// `-`, options, an expansion).
    let private changed (place: Workplace) (dirs: string list) (simple: SimpleCommand) : string list option =
        let program = (List.head simple.Words).Text.ToLowerInvariant()

        match List.tail simple.Words with
        | [] when program = "popd" || program = "pop-location" -> Some dirs
        | [ target ] when
            not target.Expands
            && target.Text <> ""
            && not (target.Text.StartsWith '-')
            && program <> "popd"
            && program <> "pop-location"
            ->
            let next =
                dirs @ (dirs |> List.map (fun dir -> absolute place dir target.Text))
                |> List.distinct

            if next.Length > MaxDirectories then None else Some next
        | _ -> None

    /// Whether a path is out of a listed command's reach: outside the workspace, unnamed, or a file
    /// content exclusions keep from models.
    let private hidden (settings: GrantSettings) (place: Workplace) (cwd: string) (path: string) : bool =
        match locate place cwd path with
        | Location.Inside relative -> (Exclusions.test settings.Exclusions relative).IsSome
        | _ -> true

    /// Whether an argument of a listed command may name something out of its reach.
    let private escapes (settings: GrantSettings) (place: Workplace) (cwd: string) (argument: string) : bool =
        if argument = "" then
            false
        elif argument.StartsWith '-' then
            // An option with a value attached (`-f/etc/x`, `--file=../x`): only plain ones are listed.
            argument.Contains '/' || argument.Contains '\\' || argument.Contains ".."
        elif place.Os = Os.Windows && argument.Contains ':' then
            true
        else
            hidden settings place cwd argument

    /// Programs that run the command after their own options (`env A=1 curl …`, `timeout 10 curl …`).
    let private wrappers =
        set
            [
                "env"
                "sudo"
                "doas"
                "nohup"
                "time"
                "nice"
                "ionice"
                "command"
                "exec"
                "stdbuf"
                "timeout"
                "xargs"
                "setsid"
                "unbuffer"
                "chronic"
                "caffeinate"
            ]

    let private isAssignment (text: string) =
        let eq = text.IndexOf '='

        eq > 0
        && (Char.IsAsciiLetter text[0] || text[0] = '_')
        && text.Substring(0, eq)
           |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_')

    /// A wrapper's numeric argument (`timeout 10`, `nice -n 5`, `timeout 1.5m`).
    let private isAmount (text: string) =
        let digits = text.TrimEnd([| 's'; 'm'; 'h'; 'd' |])

        digits <> ""
        && Char.IsAsciiDigit digits[0]
        && digits |> Seq.forall (fun c -> Char.IsAsciiDigit c || c = '.')

    /// A program's name without its directory and Windows' `.exe` (`/usr/bin/curl` is `curl`).
    let private programName (text: string) : string =
        let name =
            match text.LastIndexOfAny [| '/'; '\\' |] with
            | cut when cut >= 0 -> text.Substring(cut + 1)
            | _ -> text

        if name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) then
            name.Substring(0, name.Length - 4)
        else
            name

    /// The command a simple command runs, past leading variable assignments and wrappers with their
    /// options, its program named without its directory: what `codeact.grants.askCommands` matches.
    let private payload (words: Word list) : string option =
        let mutable rest = words
        let mutable wrapped = false
        let mutable going = true

        while going do
            match rest with
            | word :: tail when isAssignment word.Text -> rest <- tail
            | word :: tail when wrapped && (word.Text.StartsWith '-' || isAmount word.Text) -> rest <- tail
            | word :: tail when wrappers.Contains(programName word.Text) ->
                rest <- tail
                wrapped <- true
            | _ -> going <- false

        match rest with
        | [] -> None
        | program :: args ->
            ShellSyntax.resource (
                { program with
                    Text = programName program.Text
                }
                :: args
            )

    /// Whether auto approvals must leave a command to a person (`codeact.grants.askCommands`).
    let asksAlways (settings: GrantSettings) (words: Word list) : bool =
        match payload words with
        | None -> false
        | Some command ->
            settings.AskCommands
            |> List.exists (fun pattern -> Pattern.matchesIgnoringCase ResourceKind.Command pattern command)

    /// The whole line as one command for a person to judge.
    let private opaque (command: string) (reason: string) (cwdNote: Evidence list) : CallNeeds =
        match ShellSyntax.normalize command with
        | "" -> CallNeeds.Needs []
        | resource ->
            match Resource.validate ResourceKind.Command resource with
            | Error why -> CallNeeds.Refused $"the command cannot be named in a grant ({why})"
            | Ok() ->
                CallNeeds.Needs
                    [
                        {
                            Action = shellExec
                            Resource = resource
                            Place = ""
                            Summary = bounded 300 $"Run `{resource}`"
                            Evidence =
                                [ evidence "not split into commands" reason; evidence "command line" command ]
                                @ cwdNote
                            Suggestions = []
                            Listed = false
                            Confined = false
                        }
                    ]

    /// What a command line needs: `shell.exec` for each simple command (unless listed), `fs.write`
    /// for redirections outside the workspace or to protected paths. A command written plainly (no
    /// word the shell expands, in the workspace, no argument or input outside it or excluded) is
    /// `Listed` when `codeact.grants.allowCommands` has it and `Confined` unless
    /// `codeact.grants.askCommands` has it. A line the parser does not follow, a redirection to a name
    /// the shell expands or to a relative path after a change of directory, or more than `MaxNeeds`
    /// grants make the whole line one command.
    let command (settings: GrantSettings) (place: Workplace) (cwd: string option) (line: string) : CallNeeds =
        let start =
            cwd
            |> Option.map (absolute place place.Workspace)
            |> Option.defaultValue place.Workspace

        let startPlace =
            match locate place place.Workspace start with
            | Location.Inside relative -> repoPlace relative
            | _ -> ""

        let cwdNote =
            match cwd with
            | Some dir -> [ evidence "working directory" dir ]
            | None -> []

        let inside (dir: string) =
            match locate place place.Workspace dir with
            | Location.Inside _ -> true
            | _ -> false

        match ShellSyntax.parse place.Shell line with
        | CommandLine.Opaque reason -> opaque line reason cwdNote
        | CommandLine.Parsed commands ->
            let needs = ResizeArray<CallNeeds>()
            let mutable failure: string option = None
            // The directories later commands may run in, while they are known.
            let mutable current = Some [ start ]

            for simple in commands do
                if failure.IsNone then
                    let program = simple.Words |> List.tryHead |> Option.map (fun w -> w.Text)

                    match program with
                    | Some program when changesDirectory place.Shell program ->
                        current <- current |> Option.bind (fun dirs -> changed place dirs simple)
                    | _ ->
                        match ShellSyntax.resource simple.Words with
                        | None -> ()
                        | Some resource ->
                            match Resource.validate ResourceKind.Command resource with
                            | Error why -> failure <- Some why
                            | Ok() ->
                                // Written plainly in the workspace, from every directory it may run in.
                                let plain =
                                    simple.Words |> List.forall (fun w -> not w.Expands)
                                    && (match current with
                                        | Some dirs ->
                                            dirs
                                            |> List.forall (fun dir ->
                                                inside dir
                                                && simple.Words
                                                   |> List.tail
                                                   |> List.forall (fun w -> not (escapes settings place dir w.Text))
                                                && simple.Reads
                                                   |> List.forall (fun w ->
                                                       not w.Expands && not (hidden settings place dir w.Text)))
                                        | None -> false)

                                let listed =
                                    plain
                                    && settings.AllowCommands
                                       |> List.exists (fun pattern ->
                                           Pattern.matches ResourceKind.Command pattern resource)

                                let multi = commands.Length > 1 || resource <> ShellSyntax.normalize line

                                needs.Add(
                                    CallNeeds.Needs
                                        [
                                            {
                                                Action = shellExec
                                                Resource = resource
                                                Place = if current = Some [ start ] then startPlace else ""
                                                Summary = bounded 300 $"Run `{resource}`"
                                                Evidence =
                                                    (if multi then [ evidence "command line" line ] else []) @ cwdNote
                                                Suggestions = commandSuggestions resource
                                                Listed = listed
                                                Confined = plain && not (asksAlways settings simple.Words)
                                            }
                                        ]
                                )

                        for target in simple.Writes do
                            if failure.IsNone then
                                if target.Expands then
                                    failure <- Some "a redirection to a file named by an expansion"
                                else
                                    match current with
                                    | Some [ dir ] ->
                                        needs.Add(write settings place dir "a redirection of shell.run" target.Text)
                                    | _ when RuntimeSpec.isAbsoluteOn place.Os target.Text ->
                                        needs.Add(
                                            write
                                                settings
                                                place
                                                place.Workspace
                                                "a redirection of shell.run"
                                                target.Text
                                        )
                                    | _ ->
                                        failure <- Some "a redirection to a relative path after a change of directory"

            match failure with
            | Some reason -> opaque line reason cwdNote
            | None ->
                match concat (List.ofSeq needs) with
                | CallNeeds.Refused reason -> CallNeeds.Refused reason
                | CallNeeds.Needs list ->
                    // One grant per action and resource; listed (or confined) only if it is everywhere
                    // it occurs.
                    let merged =
                        list
                        |> List.groupBy (fun n -> n.Action, n.Resource)
                        |> List.map (fun (_, same) ->
                            { same.Head with
                                Listed = same |> List.forall (fun n -> n.Listed)
                                Confined = same |> List.forall (fun n -> n.Confined)
                            })

                    if (merged |> List.filter (fun n -> not n.Listed)).Length > MaxNeeds then
                        opaque line $"more than {MaxNeeds} commands that need a grant" cwdNote
                    else
                        CallNeeds.Needs merged

    // ---- Bindings ---------------------------------------------------------------------------------

    let private arg (codec: Codec<'T>) (index: int) (call: HostCall) : 'T option =
        match List.tryItem index call.Args with
        | Some value ->
            match Value.decode codec value with
            | Ok decoded -> Some decoded
            | Error _ -> None
        | None -> None

    /// `shell.run(command, options?)`.
    let shellRun: BindingRule =
        {
            Binding = "shell.run"
            Needs =
                fun settings place call ->
                    match arg Codec.string 0 call with
                    | None -> CallNeeds.Needs []
                    | Some line ->
                        let options =
                            match List.tryItem 1 call.Args with
                            | None
                            | Some(Value.Inline Json.Null) -> Some None
                            | Some _ -> arg CoreBindings.Codecs.runOptions 1 call |> Option.map Some

                        match options with
                        // Arguments that do not decode: the binding refuses them, nothing runs.
                        | None -> CallNeeds.Needs []
                        | Some options -> command settings place (options |> Option.bind (fun o -> o.Cwd)) line
        }

    /// `fs.write(path, text)`.
    let fsWriteRule: BindingRule =
        {
            Binding = "fs.write"
            Needs =
                fun settings place call ->
                    match arg Codec.string 0 call with
                    | None -> CallNeeds.Needs []
                    | Some path -> write settings place place.Workspace "fs.write" path
        }

    /// `fs.patch(patches)`: every file a patch creates, edits or deletes.
    let fsPatchRule: BindingRule =
        {
            Binding = "fs.patch"
            Needs =
                fun settings place call ->
                    match arg (Codec.list CoreBindings.Codecs.filePatch) 0 call with
                    | None -> CallNeeds.Needs []
                    | Some patches ->
                        patches
                        |> List.map (fun patch ->
                            let path, what =
                                match patch with
                                | FilePatch.Create(path, _) -> path, "fs.patch (create)"
                                | FilePatch.Edit(path, _, _) -> path, "fs.patch (edit)"
                                | FilePatch.Delete(path, _) -> path, "fs.patch (delete)"

                            write settings place place.Workspace what path)
                        |> concat
        }

    /// `fs.read(path)`.
    let fsReadRule: BindingRule =
        {
            Binding = "fs.read"
            Needs =
                fun _ place call ->
                    match arg Codec.string 0 call with
                    | None -> CallNeeds.Needs []
                    | Some path -> read place "fs.read" path
        }

    /// The `net.connect` resource of a URL: its host (lowercase) and port (`api.github.com:443`).
    let endpoint (url: string) : Result<string, string> =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, NonNull uri when not (String.IsNullOrEmpty uri.Host) && uri.Port > 0 ->
            Ok(uri.Host.ToLowerInvariant() + ":" + string uri.Port)
        | _ -> Error $"{Json.quote url} is not a URL with a host"

    /// A network binding's rule: `net.connect` for each `host:port` its call connects to (`targets`;
    /// `endpoint` makes them from URLs). A call whose targets cannot be read is refused.
    let connects (binding: string) (targets: HostCall -> Result<string list, string>) : BindingRule =
        {
            Binding = binding
            Needs =
                fun _ _ call ->
                    match targets call with
                    | Error reason -> CallNeeds.Refused reason
                    | Ok targets ->
                        targets
                        |> List.distinct
                        |> List.map (fun target ->
                            match Resource.validate ResourceKind.Name target with
                            | Error why -> CallNeeds.Refused $"{target} cannot be named in a grant ({why})"
                            | Ok() ->
                                let host =
                                    match target.LastIndexOf ':' with
                                    | cut when cut > 0 -> Some(target.Substring(0, cut))
                                    | _ -> None

                                CallNeeds.Needs
                                    [
                                        {
                                            Action = netConnect
                                            Resource = target
                                            Place = ""
                                            Summary = bounded 300 $"Connect to {target}"
                                            Evidence = [ evidence "through" binding ]
                                            Suggestions =
                                                host
                                                |> Option.map (fun h -> [ Pattern.exact h + ":*" ])
                                                |> Option.defaultValue []
                                                |> suggest ResourceKind.Name target
                                            Listed = false
                                            Confined = false
                                        }
                                    ])
                        |> concat
        }

    /// The core bindings' rules (`fs.list` and `fs.stat` show names and sizes, not content: they need
    /// nothing).
    let core: BindingRule list = [ shellRun; fsWriteRule; fsPatchRule; fsReadRule ]

    /// What a call needs, by its binding's rule; nothing for bindings without one.
    let needs (rules: BindingRule list) (settings: GrantSettings) (place: Workplace) (call: HostCall) : CallNeeds =
        match rules |> List.tryFind (fun rule -> rule.Binding = call.Binding) with
        | None -> CallNeeds.Needs []
        | Some rule -> rule.Needs settings place call

    /// A guard that sees calls without a cell as made by `caller` (a direct tool's call), so that
    /// the grants it asks for are keyed by it. The call that runs is unchanged.
    let attribute (caller: string) (guard: HostCall -> 'T) : HostCall -> 'T =
        fun call ->
            guard (
                if call.CellId.IsNone then
                    { call with CellId = Some caller }
                else
                    call
            )
