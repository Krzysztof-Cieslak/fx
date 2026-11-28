namespace Fx.Interpreters

open System
open System.Text
open Fx.Contracts
open Fx.Effects
open Fx.Envd
open Fx.Kernel
open Fx.Sdk
open Fx.Store

/// Options of `shell.run`.
type RunOptions =
    {
        /// The working directory, relative to the workspace (the workspace by default).
        Cwd: string option
        TimeoutMs: int option
        /// Text written to the command's standard input (which is then closed).
        Stdin: string option
    }

    override this.ToString() =
        $"RunOptions(cwd {this.Cwd}, timeout {this.TimeoutMs})"

/// What a command did (spec §4.1 `RunResult`). Its output is bounded: the head and the tail of each
/// stream, with a marker where the middle was left out.
type RunResult =
    {
        Command: string
        /// `None` if it timed out (it was killed).
        ExitCode: int option
        Stdout: string
        Stderr: string
        /// Output was left out of `Stdout` or `Stderr`.
        Truncated: bool
        TimedOut: bool
    }

    override this.ToString() =
        $"RunResult({this.Command}: exit {this.ExitCode}, timed out {this.TimedOut})"

/// A text file as `fs.read` gives it (spec §4.1 `FileText`): whole, or some of its lines. `Lines`,
/// `Size` and `Digest` are the whole file's; `Start`, `End` and `Text` the lines read, so a file read is
/// also a range `fs.editRange` takes (spec §6.8).
type FileText =
    {
        /// Relative to the workspace (with `/`) when inside it.
        Path: string
        /// The lines read, with their endings (the whole file's text when it was read whole).
        Text: string
        Lines: int
        Size: int64
        /// SHA-256 of the content; anchored edits and patches check it (spec §6.8).
        Digest: BlobHash
        /// The first line in `Text`, 1-based.
        Start: int
        /// The last line in `Text`; `Start - 1` when it holds none.
        End: int
    }

    override this.ToString() =
        $"FileText({this.Path}:{this.Start}-{this.End}, {this.Lines} lines, {this.Digest})"

/// Options of `fs.read`.
type ReadOptions =
    {
        /// The first and the last line to read (1-based, inclusive).
        Lines: (int * int) option
    }

    override this.ToString() =
        match this.Lines with
        | Some(first, last) -> $"ReadOptions(lines {first}-{last})"
        | None -> "ReadOptions()"

/// Lines of a file (spec §6.8): what `fs.editRange` replaces, and what edits give back so the same
/// lines can be edited again. `Digest` and `Lines` are the whole file's when the range was made.
type FileRange =
    {
        Path: string
        /// The first line, 1-based.
        Start: int
        /// The last line; `Start - 1` for none (an edit there inserts before `Start`).
        End: int
        /// The lines, with their endings.
        Text: string
        Digest: BlobHash
        Lines: int
    }

    override this.ToString() =
        $"FileRange({this.Path}:{this.Start}-{this.End}, {this.Digest})"

/// What `fs.edit` and `fs.editRange` did to a file: its new digest and line count, how many
/// occurrences were replaced, and a fresh range for the lines each change left.
type Edited =
    {
        Path: string
        Digest: BlobHash
        Lines: int
        Replacements: int
        Ranges: FileRange list
    }

    override this.ToString() =
        $"Edited({this.Path}, {this.Replacements} replacements, {this.Ranges.Length} ranges)"

/// An entry of the workspace, as `fs.list` and `fs.stat` give it.
type FileEntry =
    {
        Path: string
        Name: string
        Kind: EntryKind
        /// Bytes, for files.
        Size: int64
    }

    override this.ToString() = $"{this.Kind} {this.Path}"

/// What a write or a patch left in a file.
type Written =
    {
        Path: string
        /// SHA-256 of the new content; `None` once deleted.
        Digest: BlobHash option
        Lines: int
    }

    override this.ToString() =
        $"Written({this.Path}, {this.Lines} lines)"

/// Where the core bindings act and their bounds. Their values are variants (spec §6.6); the
/// environment's shell, files and processes are the capabilities the bridge runs with.
type CoreSettings =
    {
        /// The workspace (absolute, in the environment's syntax): relative paths resolve against it.
        Workspace: string
        /// The environment's operating system: its path syntax.
        Os: Os
        /// The environment of commands (`shell.run`): envd's, by default; the environment provider
        /// decides what it holds (never the host's credentials, spec §18).
        CommandEnv: EnvVars
        /// Characters kept of each output stream of a command.
        OutputChars: int
        /// The largest file `fs.read` returns.
        ReadBytes: int
        /// Files `fs.read` refuses (`excluded`): content exclusions (spec §6.5, §18), matched on the
        /// path relative to the workspace, with the case rules of the environment's filesystem.
        Exclusions: Fx.Models.Exclusions
    }

    override this.ToString() = $"CoreSettings({this.Workspace})"

[<RequireQualifiedAccess>]
module CoreSettings =
    /// The bounds the Bun kernel spike used for output (16 000 characters), and 1 MiB files.
    let defaults (workspace: string) (os: Os) : CoreSettings =
        {
            Workspace = workspace
            Os = os
            CommandEnv = Inherit []
            OutputChars = 16000
            ReadBytes = 1024 * 1024
            Exclusions = Fx.Models.Exclusions.none
        }

/// The core bindings (spec §6.5 "env-side"): `shell.run`, `fs.read`, `fs.write`, `fs.list`,
/// `fs.stat`, `fs.patch`, `fs.edit` and `fs.editRange`, over the environment's capabilities (envd's, or the local ones in
/// embedded mode). Paths are relative to the workspace (or absolute).
[<RequireQualifiedAccess>]
module CoreBindings =
    let private utf8 = UTF8Encoding(false)

    // ---- Codecs (the bridge's declarations document them for the model) --------------------------

    module Codecs =
        let runOptions: Codec<RunOptions> =
            Codec.record {
                let! cwd =
                    Codec.optionalField
                        "cwd"
                        (fun (o: RunOptions) -> o.Cwd)
                        (Codec.string
                         |> Codec.describe "Relative to the workspace; the workspace by default.")

                and! timeout =
                    Codec.optionalField
                        "timeoutMs"
                        (fun o -> o.TimeoutMs)
                        (Codec.int
                         |> Codec.atLeast 1
                         |> Codec.describe "Kills the command after this many milliseconds.")

                and! stdin =
                    Codec.optionalField
                        "stdin"
                        (fun o -> o.Stdin)
                        (Codec.string |> Codec.describe "Written to the command's standard input.")

                return
                    {
                        Cwd = cwd
                        TimeoutMs = timeout
                        Stdin = stdin
                    }
            }
            |> Codec.describe "How to run a command."
            |> Codec.named "RunOptions"

        let runResult: Codec<RunResult> =
            Codec.record {
                let! command = Codec.field "command" (fun (r: RunResult) -> r.Command) Codec.string

                and! exitCode =
                    Codec.optionalField
                        "exitCode"
                        (fun r -> r.ExitCode)
                        (Codec.int |> Codec.describe "Absent if the command timed out.")

                and! stdout = Codec.field "stdout" (fun r -> r.Stdout) Codec.string
                and! stderr = Codec.field "stderr" (fun r -> r.Stderr) Codec.string

                and! truncated =
                    Codec.field
                        "truncated"
                        (fun r -> r.Truncated)
                        (Codec.bool
                         |> Codec.describe "The middle of stdout or stderr was left out (marked in the text).")

                and! timedOut = Codec.field "timedOut" (fun r -> r.TimedOut) Codec.bool

                return
                    {
                        Command = command
                        ExitCode = exitCode
                        Stdout = stdout
                        Stderr = stderr
                        Truncated = truncated
                        TimedOut = timedOut
                    }
            }
            |> Codec.describe "What a command did; a non-zero exit code is not an error."
            |> Codec.named "RunResult"

        let digest: Codec<BlobHash> =
            Fx.Store.Codecs.blobHash
            |> Codec.describe "SHA-256 of the content: sha256:<hex>."

        let private start =
            Codec.int |> Codec.atLeast 1 |> Codec.describe "The first line, 1-based."

        let private stop =
            Codec.int
            |> Codec.atLeast 0
            |> Codec.describe "The last line, inclusive; start - 1 when there are none."

        let private lineCount =
            Codec.int |> Codec.atLeast 0 |> Codec.describe "The whole file's line count."

        let private inOrder (first: int) (last: int) =
            if last < first - 1 then
                Error $"end ({last}) is before start ({first})"
            else
                Ok()

        let fileText: Codec<FileText> =
            Codec.record {
                let! path = Codec.field "path" (fun (f: FileText) -> f.Path) Codec.string

                and! text =
                    Codec.field
                        "text"
                        (fun f -> f.Text)
                        (Codec.string
                         |> Codec.describe "The lines read (start to end), with their line endings.")

                and! lines = Codec.field "lines" (fun f -> f.Lines) lineCount

                and! size =
                    Codec.field
                        "size"
                        (fun f -> f.Size)
                        (Codec.int64
                         |> Codec.atLeast 0L
                         |> Codec.describe "The whole file's size in bytes.")

                and! digest = Codec.field "digest" (fun f -> f.Digest) digest
                and! first = Codec.field "start" (fun f -> f.Start) start
                and! last = Codec.field "end" (fun f -> f.End) stop

                return
                    ({
                        Path = path
                        Text = text
                        Lines = lines
                        Size = size
                        Digest = digest
                        Start = first
                        End = last
                    }
                    : FileText)
            }
            |> Codec.refine (fun f -> inOrder f.Start f.End)
            |> Codec.describe
                "A text file, or the lines of it read: also a range fs.editRange takes (lines, size and digest are the whole file's)."
            |> Codec.named "FileText"

        let readOptions: Codec<ReadOptions> =
            Codec.record {
                let! lines =
                    Codec.optionalField
                        "lines"
                        (fun (o: ReadOptions) -> o.Lines)
                        (Codec.tuple2 (Codec.int |> Codec.atLeast 1) (Codec.int |> Codec.atLeast 1)
                         |> Codec.refine (fun (first, last) ->
                             if last < first then
                                 Error $"the last line ({last}) is before the first ({first})"
                             else
                                 Ok())
                         |> Codec.describe
                             "The first and the last line to read, 1-based and inclusive; lines past the end are left out.")

                return { Lines = lines }
            }
            |> Codec.describe "What to read of a file."
            |> Codec.named "ReadOptions"

        let fileRange: Codec<FileRange> =
            Codec.record {
                let! path = Codec.field "path" (fun (r: FileRange) -> r.Path) Codec.string
                and! first = Codec.field "start" (fun r -> r.Start) start
                and! last = Codec.field "end" (fun r -> r.End) stop

                and! text =
                    Codec.field
                        "text"
                        (fun r -> r.Text)
                        (Codec.string |> Codec.describe "The lines, with their line endings.")

                and! digest = Codec.field "digest" (fun r -> r.Digest) digest
                and! lines = Codec.field "lines" (fun r -> r.Lines) lineCount

                return
                    ({
                        Path = path
                        Start = first
                        End = last
                        Text = text
                        Digest = digest
                        Lines = lines
                    }
                    : FileRange)
            }
            |> Codec.refine (fun r -> inOrder r.Start r.End)
            |> Codec.describe
                "Lines of a file, as fs.read gives them or an edit left them (digest and lines are the whole file's)."
            |> Codec.named "FileRange"

        let textChange: Codec<TextChange> =
            Codec.record {
                let! old =
                    Codec.field
                        "old"
                        (fun (c: TextChange) -> c.Old)
                        (Codec.string
                         |> Codec.nonEmpty
                         |> Codec.describe "The text to replace, exactly as in the file (any line endings).")

                and! replacement =
                    Codec.field "new" (fun c -> c.New) (Codec.string |> Codec.describe "The text to put in its place.")

                and! all =
                    Codec.defaultField
                        "all"
                        (fun c -> c.All)
                        false
                        (Codec.bool
                         |> Codec.describe "Replace every occurrence; otherwise `old` must occur exactly once.")

                return
                    {
                        Old = old
                        New = replacement
                        All = all
                    }
            }
            |> Codec.describe "Replaces text in a file."
            |> Codec.named "TextChange"

        /// One change or a list of them (applied in order).
        let textChanges: Codec<TextChange list> =
            let many = Codec.list textChange |> Codec.minItems 1

            Codec.create
                (Schema.ofKind (SchemaKind.AnyOf [ textChange.Schema; many.Schema ]))
                (fun changes ->
                    match changes with
                    | [ one ] -> textChange.Encode one
                    | _ -> many.Encode changes)
                (fun json ->
                    match json with
                    | Json.Array _ -> many.Decode json
                    | _ -> textChange.Decode json |> Result.map List.singleton)

        let edited: Codec<Edited> =
            Codec.record {
                let! path = Codec.field "path" (fun (e: Edited) -> e.Path) Codec.string
                and! digest = Codec.field "digest" (fun e -> e.Digest) digest

                and! lines =
                    Codec.field
                        "lines"
                        (fun e -> e.Lines)
                        (Codec.int |> Codec.atLeast 0 |> Codec.describe "Its line count.")

                and! replacements =
                    Codec.field
                        "replacements"
                        (fun e -> e.Replacements)
                        (Codec.int
                         |> Codec.atLeast 1
                         |> Codec.describe "How many occurrences were replaced (1 for a range).")

                and! ranges =
                    Codec.field
                        "ranges"
                        (fun e -> e.Ranges)
                        (Codec.list fileRange
                         |> Codec.describe
                             "The lines each change left, in file order: fresh ranges, to edit the same lines again.")

                return
                    {
                        Path = path
                        Digest = digest
                        Lines = lines
                        Replacements = replacements
                        Ranges = ranges
                    }
            }
            |> Codec.describe "A file as an edit left it."
            |> Codec.named "Edited"

        let entryKind: Codec<EntryKind> =
            Codec.enum
                [
                    EntryKind.File, "file"
                    EntryKind.Directory, "directory"
                    EntryKind.Symlink, "symlink"
                    EntryKind.Other, "other"
                ]
            |> Codec.named "EntryKind"

        let fileEntry: Codec<FileEntry> =
            Codec.record {
                let! path =
                    Codec.field
                        "path"
                        (fun (e: FileEntry) -> e.Path)
                        (Codec.string |> Codec.describe "Relative to the workspace, with /.")

                and! name = Codec.field "name" (fun e -> e.Name) Codec.string
                and! kind = Codec.field "kind" (fun e -> e.Kind) entryKind

                and! size =
                    Codec.field
                        "size"
                        (fun e -> e.Size)
                        (Codec.int64 |> Codec.atLeast 0L |> Codec.describe "Bytes, for files.")

                return
                    {
                        Path = path
                        Name = name
                        Kind = kind
                        Size = size
                    }
            }
            |> Codec.describe "A file, directory or link in the workspace."
            |> Codec.named "FileEntry"

        let written: Codec<Written> =
            Codec.record {
                let! path = Codec.field "path" (fun (w: Written) -> w.Path) Codec.string

                and! digest =
                    Codec.optionalField
                        "digest"
                        (fun w -> w.Digest)
                        (digest |> Codec.describe "Absent once the file was deleted.")

                and! lines = Codec.field "lines" (fun w -> w.Lines) (Codec.int |> Codec.atLeast 0)

                return
                    {
                        Path = path
                        Digest = digest
                        Lines = lines
                    }
            }
            |> Codec.describe "A file as a write or a patch left it."
            |> Codec.named "Written"

        let textEdit: Codec<TextEdit> =
            Codec.record {
                let! line =
                    Codec.field
                        "line"
                        (fun (e: TextEdit) -> e.Line)
                        (Codec.int
                         |> Codec.atLeast 1
                         |> Codec.describe "The first line replaced, 1-based; the line count + 1 appends.")

                and! count =
                    Codec.field
                        "count"
                        (fun e -> e.Count)
                        (Codec.int
                         |> Codec.atLeast 0
                         |> Codec.describe "How many lines are replaced; 0 inserts before `line`.")

                and! lines =
                    Codec.field
                        "lines"
                        (fun e -> e.Lines)
                        (Codec.list Codec.string |> Codec.describe "The new lines, without line endings.")

                return
                    {
                        Line = line
                        Count = count
                        Lines = lines
                    }
            }
            |> Codec.describe "Replaces lines of the file as it is (edits of one file must not overlap)."
            |> Codec.named "TextEdit"

        let filePatch: Codec<FilePatch> =
            Codec.union "op" {
                let! create =
                    Codec.case2
                        "create"
                        (fun path text -> Create(path, text))
                        (Field.required "path" Codec.string)
                        (Field.required "text" Codec.string)

                and! edit =
                    Codec.case3
                        "edit"
                        (fun path digest edits -> Edit(path, digest, edits))
                        (Field.required "path" Codec.string)
                        (Field.optional "digest" digest)
                        (Field.required "edits" (Codec.list textEdit))

                and! delete =
                    Codec.case2
                        "delete"
                        (fun path digest -> Delete(path, digest))
                        (Field.required "path" Codec.string)
                        (Field.optional "digest" digest)

                return
                    fun value ->
                        match value with
                        | Create(path, text) -> create path text
                        | Edit(path, digest, edits) -> edit path digest edits
                        | Delete(path, digest) -> delete path digest
            }
            |> Codec.describeCase "create" "Creates a text file; fails if the path exists."
            |> Codec.describeCase "edit" "Edits a text file; with its digest (from fs.read), only if it is unchanged."
            |> Codec.describeCase "delete" "Deletes a file; with its digest, only if it is unchanged."
            |> Codec.describe "A change to one file."
            |> Codec.named "FilePatch"

    // ---- Paths ----------------------------------------------------------------------------------

    let private separator (settings: CoreSettings) =
        if settings.Os = Os.Windows then '\\' else '/'

    /// A path as a binding gets it, absolute in the environment's syntax: absolute paths as they
    /// are, relative ones under the workspace (`.` and `..` resolved; `/` accepted everywhere).
    let resolve (settings: CoreSettings) (path: string) : string =
        if RuntimeSpec.isAbsoluteOn settings.Os path then
            path
        else
            let segments =
                path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.fold
                    (fun (kept: string list) segment ->
                        match segment, kept with
                        | ".", _ -> kept
                        | "..", last :: rest when last <> ".." -> rest
                        | _ -> segment :: kept)
                    []
                |> List.rev

            let sep = separator settings
            let root = settings.Workspace.TrimEnd([| '/'; '\\' |])

            match segments with
            | [] -> settings.Workspace
            | _ -> root + string sep + String.Join(string sep, segments)

    /// A path as a binding reports it: relative to the workspace, with `/`, when inside it.
    let display (settings: CoreSettings) (path: string) : string =
        let root = settings.Workspace.TrimEnd([| '/'; '\\' |])

        let comparison =
            if settings.Os = Os.Linux then
                StringComparison.Ordinal
            else
                StringComparison.OrdinalIgnoreCase

        if String.Equals(path.TrimEnd([| '/'; '\\' |]), root, comparison) then
            "."
        elif
            path.Length > root.Length
            && path.StartsWith(root, comparison)
            && (path[root.Length] = '/' || path[root.Length] = '\\')
        then
            path.Substring(root.Length + 1).Replace('\\', '/')
        else
            path

    let private parent (settings: CoreSettings) (path: string) : string option =
        let i = path.LastIndexOfAny([| '/'; '\\' |])

        if i <= 0 then
            None
        elif settings.Os = Os.Windows && i = 2 && path[1] = ':' then
            None
        else
            Some(path.Substring(0, i))

    // ---- Text -----------------------------------------------------------------------------------

    /// At most `limit` characters: the head and the tail, with one marker line where the middle was
    /// left out (never half a surrogate pair).
    let bound (limit: int) (text: string) : string * bool =
        if text.Length <= limit then
            text, false
        else
            let mutable head = limit / 2
            let mutable tail = text.Length - (limit - head)

            if head > 0 && Char.IsHighSurrogate text[head - 1] then
                head <- head - 1

            if tail < text.Length && Char.IsLowSurrogate text[tail] then
                tail <- tail + 1

            let omitted = tail - head

            text.Substring(0, head)
            + $"\n… [{omitted} chars omitted] …\n"
            + text.Substring tail,
            true

    let private lineCount (text: string) : int =
        if text.Length = 0 then
            0
        else
            let newlines = text |> Seq.sumBy (fun c -> if c = '\n' then 1 else 0)
            if text.EndsWith '\n' then newlines else newlines + 1

    let private withoutBom (text: string) =
        if text.StartsWith '\uFEFF' then text.Substring 1 else text

    // ---- Failures -------------------------------------------------------------------------------

    let private fsFailed (error: FsError) =
        HostBinding.failed EnvCodecs.fsError error

    let private failure (code: string) (message: string) =
        HostCallError.Failed(code, message, None)

    // ---- The bindings ---------------------------------------------------------------------------

    /// The groups' descriptions.
    let groups: (string * string) list =
        [
            "shell", "Commands in the workspace's shell."
            "fs", "The workspace's files."
        ]

    /// `shell.run(command, options?)`: runs a command line with the environment's shell (`/bin/sh`;
    /// PowerShell 7 on Windows) in the workspace.
    let shellRun<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: CoreSettings)
        : HostBinding<'R> =
        HostBinding.create2
            "shell.run"
            "Runs a command line in the workspace with its shell (sh; PowerShell 7 on Windows) and waits for it."
            (Arg.required "command" "The command line." Codec.string, Arg.optional "options" "" Codecs.runOptions)
            Codecs.runResult
            (fun (command, options) ->
                eff {
                    let options =
                        options
                        |> Option.defaultValue
                            {
                                Cwd = None
                                TimeoutMs = None
                                Stdin = None
                            }

                    let run =
                        Shell.run
                            {
                                Spec =
                                    {
                                        Program = Program.Script command
                                        Cwd =
                                            Some(
                                                options.Cwd
                                                |> Option.map (resolve settings)
                                                |> Option.defaultValue settings.Workspace
                                            )
                                        Env = settings.CommandEnv
                                    }
                                Stdin = options.Stdin |> Option.map utf8.GetBytes |> Option.defaultValue [||]
                            }

                    let! outcome =
                        match options.TimeoutMs with
                        | Some ms -> Eff.timeout (TimeSpan.FromMilliseconds(float ms)) run
                        | None -> run |> Eff.map Some

                    match outcome with
                    | None ->
                        return
                            Ok
                                {
                                    Command = command
                                    ExitCode = None
                                    Stdout = ""
                                    Stderr = ""
                                    Truncated = false
                                    TimedOut = true
                                }
                    | Some(Error(NotStarted(program, reason))) ->
                        return Error(failure "notStarted" $"could not start {program}: {reason}")
                    | Some(Ok output) ->
                        let stdout, cutOut = bound settings.OutputChars (utf8.GetString output.Stdout)

                        let stderr, cutErr = bound settings.OutputChars (utf8.GetString output.Stderr)

                        return
                            Ok
                                {
                                    Command = command
                                    ExitCode = Some output.ExitCode
                                    Stdout = stdout
                                    Stderr = stderr
                                    Truncated = cutOut || cutErr
                                    TimedOut = false
                                }
                })

    /// A path as content exclusions match it: normalized (`..` resolved, so `../repo/x` inside the
    /// workspace is `x`), relative to the workspace when inside it.
    let exclusionPath (settings: CoreSettings) (full: string) : string =
        let path = Fx.Models.WorkspacePath.normalize full
        let root = Fx.Models.WorkspacePath.normalize settings.Workspace

        let comparison =
            if settings.Os = Os.Linux then
                StringComparison.Ordinal
            else
                StringComparison.OrdinalIgnoreCase

        let prefix = if root.EndsWith '/' then root else root + "/"

        if String.Equals(path, root, comparison) then
            ""
        elif path.Length > prefix.Length && path.StartsWith(prefix, comparison) then
            path.Substring prefix.Length
        else
            path

    /// The failure of reading an excluded file: code `excluded`, with why as data.
    let excluded (path: string) (found: Fx.Models.ExclusionMatch) : HostCallError =
        HostCallError.Failed(
            "excluded",
            $"{path} is excluded: content exclusions keep it from models ({found.Origin}: {found.Pattern})",
            Some(Fx.Models.ExclusionMatch.codec.Encode found)
        )

    /// A text file as the file bindings read it: whole, without a byte order mark.
    type private Read =
        {
            Text: string
            Size: int64
            Digest: BlobHash
        }

    /// Reads a text file whole (up to `ReadBytes`) for a binding: `excluded` for a file content
    /// exclusions keep from models (nothing of it is read), `tooLarge` (with what to do `instead`),
    /// `notText`, or the filesystem's failure.
    let private readText<'R when 'R :> IFsOps and 'R :> IScope>
        (settings: CoreSettings)
        (path: string)
        (instead: string)
        : Eff<'R, Result<Read, HostCallError>> =
        eff {
            let full = resolve settings path

            match Fx.Models.Exclusions.test settings.Exclusions (exclusionPath settings full) with
            | Some found -> return Error(excluded path found)
            | None ->
                match! FsOps.readRange full 0L settings.ReadBytes with
                | Error e -> return Error(fsFailed e)
                | Ok chunk when not chunk.AtEnd ->
                    return
                        Error(
                            failure
                                "tooLarge"
                                $"{path} has {chunk.FileSize} bytes, more than {settings.ReadBytes}: {instead}"
                        )
                | Ok chunk when not (Unicode.Utf8.IsValid(ReadOnlySpan chunk.Content)) ->
                    return Error(failure "notText" $"{path} is not UTF-8 text")
                | Ok chunk ->
                    return
                        Ok
                            {
                                Text = withoutBom (utf8.GetString chunk.Content)
                                Size = chunk.FileSize
                                Digest = BlobHash.ofBytes chunk.Content
                            }
        }

    /// `fs.read(path, options?)`: a text file, whole (up to `ReadBytes`), or the lines `options.lines`
    /// names; `excluded` for a file content exclusions keep from models (nothing of it is read).
    let fsRead<'R when 'R :> IFsOps and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        HostBinding.create2
            "fs.read"
            "Reads a text file (UTF-8): whole, or with options.lines only those lines (start to end), which fs.editRange can then replace. Fails (data.code excluded) for files content exclusions keep from models."
            (Arg.required "path" "Relative to the workspace." Codec.string, Arg.optional "options" "" Codecs.readOptions)
            Codecs.fileText
            (fun (path, options) ->
                eff {
                    match! readText settings path "read parts of it with a command" with
                    | Error e -> return Error e
                    | Ok file ->
                        let lines = lineCount file.Text

                        let whole: FileText =
                            {
                                Path = display settings (resolve settings path)
                                Text = file.Text
                                Lines = lines
                                Size = file.Size
                                Digest = file.Digest
                                Start = 1
                                End = lines
                            }

                        match options |> Option.bind (fun o -> o.Lines) with
                        | None -> return Ok whole
                        | Some(first, _) when first > lines + 1 ->
                            return Error(failure "outOfRange" $"{path} has {lines} lines: there is no line {first}")
                        | Some(first, last) ->
                            let last = min last lines

                            return
                                Ok
                                    { whole with
                                        Text = TextEdits.slice file.Text first last
                                        Start = first
                                        End = last
                                    }
                })

    /// `fs.write(path, text)`: creates or replaces a text file atomically, with its directories.
    let fsWrite<'R when 'R :> IFs and 'R :> IFsOps and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        HostBinding.create2
            "fs.write"
            "Creates or replaces a text file (UTF-8), and the directories it needs."
            (Arg.required "path" "Relative to the workspace." Codec.string, Arg.required "text" "" Codec.string)
            Codecs.written
            (fun (path, text) ->
                eff {
                    let full = resolve settings path
                    let bytes = utf8.GetBytes text

                    let! made =
                        match parent settings full with
                        | Some dir -> Fs.createDirectory dir
                        | None -> EffResult.ok ()

                    match made with
                    | Error e -> return Error(fsFailed e)
                    | Ok() ->
                        match! FsOps.writeAtomic full bytes with
                        | Error e -> return Error(fsFailed e)
                        | Ok() ->
                            return
                                Ok
                                    {
                                        Path = display settings full
                                        Digest = Some(BlobHash.ofBytes bytes)
                                        Lines = lineCount text
                                    }
                })

    let private entry (settings: CoreSettings) (e: Entry) : FileEntry =
        {
            Path = display settings e.Path
            Name = e.Name
            Kind = e.Kind
            Size = e.Size
        }

    /// `fs.list(path?)`: a directory's entries, sorted by name.
    let fsList<'R when 'R :> IFsRead and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        HostBinding.create1
            "fs.list"
            "Lists a directory (the workspace by default), sorted by name."
            (Arg.withDefault "path" "Relative to the workspace." "." Codec.string)
            (Codec.list Codecs.fileEntry)
            (fun path ->
                Fs.list (resolve settings path)
                |> EffResult.map (List.map (entry settings))
                |> EffResult.mapError fsFailed)

    /// `fs.stat(path)`: one entry; a missing path is the error `notFound`.
    let fsStat<'R when 'R :> IFsRead and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        HostBinding.create1
            "fs.stat"
            "Describes a file or directory; fails (data.code notFound) if there is none."
            (Arg.required "path" "Relative to the workspace." Codec.string)
            Codecs.fileEntry
            (fun path ->
                Fs.stat (resolve settings path)
                |> EffResult.map (entry settings)
                |> EffResult.mapError fsFailed)

    /// `fs.patch(patches)`: structured edits (envd's apply-patch), all checked before any is written.
    let fsPatch<'R when 'R :> IFsOps and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        let absolute (patch: FilePatch) =
            match patch with
            | Create(path, text) -> Create(resolve settings path, text)
            | Edit(path, digest, edits) -> Edit(resolve settings path, digest, edits)
            | Delete(path, digest) -> Delete(resolve settings path, digest)

        HostBinding.create1
            "fs.patch"
            "Applies changes to files: every change is checked first, then all are written; nothing is written if one fails (e.g. data.code stale: a file changed since it was read)."
            (Arg.required "patches" "" (Codec.list Codecs.filePatch))
            (Codec.list Codecs.written)
            (fun patches ->
                FsOps.applyPatch (patches |> List.map absolute)
                |> EffResult.map (
                    List.map (fun (f: PatchedFile) ->
                        {
                            Path = display settings f.Path
                            Digest = f.Digest
                            Lines = f.Lines
                        })
                )
                |> EffResult.mapError (HostBinding.failed EnvCodecs.patchError))

    /// Writes line edits to a file as it was read (one patch, checked against the digest read), and
    /// says what they left: `placed`, the lines to give back as fresh ranges.
    let private patchLines<'R when 'R :> IFsOps and 'R :> IScope>
        (settings: CoreSettings)
        (path: string)
        (file: Read)
        (edits: TextEdit list)
        (placed: (int * int) list)
        (replacements: int)
        : Eff<'R, Result<Edited, HostCallError>> =
        eff {
            let full = resolve settings path

            match TextPatch.applyText file.Text edits with
            | Error reason -> return Error(failure "invalidEdit" reason)
            | Ok(text, _) ->
                match! FsOps.applyPatch [ Edit(full, Some file.Digest, edits) ] with
                | Error e -> return Error(HostBinding.failed EnvCodecs.patchError e)
                | Ok [ {
                           PatchedFile.Digest = Some digest
                           Lines = lines
                       } ] ->
                    let shown = display settings full

                    let ranges =
                        placed
                        |> List.map (fun (first, last) ->
                            // In a file without a final line ending, a last empty line is no line.
                            let last = max (first - 1) (min last lines)

                            ({
                                Path = shown
                                Start = first
                                End = last
                                Text = TextEdits.slice text first last
                                Digest = digest
                                Lines = lines
                            }
                            : FileRange))

                    return
                        Ok(
                            {
                                Path = shown
                                Digest = digest
                                Lines = lines
                                Replacements = replacements
                                Ranges = ranges
                            }
                            : Edited
                        )
                | Ok other -> return Error(failure "invalidEdit" $"the patch of {path} gave {other.Length} files")
        }

    let private changeFailed (path: string) (count: int) (why: ChangeFailure) : HostCallError =
        let which (index: int) =
            if count > 1 then $"Change {index + 1} of {count}: " else ""

        let after (index: int) =
            if index > 0 then
                " (as the changes before it left it)"
            else
                ""

        match why with
        | ChangeFailure.Missing i ->
            failure
                "missing"
                $"{which i}`old` does not occur in {path}{after i}: copy it from the file as read (without line numbers), including whitespace."
        | ChangeFailure.Ambiguous(i, n) ->
            failure
                "ambiguous"
                $"{which i}`old` occurs {n} times in {path}{after i}: include more of the surrounding text so it occurs once, or set `all` to replace every occurrence."
        | ChangeFailure.Same i -> failure "unchanged" $"{which i}`old` and `new` are the same: nothing to change."
        | ChangeFailure.Unchanged -> failure "unchanged" "The changes leave every line as it was: nothing to change."

    /// `fs.edit(path, changes)`: edits by text (spec §6.8). Changes apply in order, each to the text
    /// the ones before it left (line endings as `\n`); the lines that differ become one patch, checked
    /// against the digest read. Errors read as hybrid's `edit` says them.
    let fsEdit<'R when 'R :> IFsOps and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        HostBinding.create2
            "fs.edit"
            "Replaces text in a file: each change's old text must occur exactly once (else data.code missing or ambiguous) unless all is set. Changes apply in order, each to the text the ones before it left, and are written together, only if the file did not change meanwhile. Returns the lines each change left as fresh ranges."
            (Arg.required "path" "Relative to the workspace." Codec.string,
             Arg.required "changes" "One change, or a list applied in order." Codecs.textChanges)
            Codecs.edited
            (fun (path, changes) ->
                eff {
                    match! readText settings path "edit it with a command" with
                    | Error e -> return Error e
                    | Ok file ->
                        let lf (text: string) = text.Replace("\r\n", "\n")

                        let changes =
                            changes
                            |> List.map (fun c ->
                                { c with
                                    Old = lf c.Old
                                    New = lf c.New
                                })

                        match TextEdits.apply (lf file.Text) changes with
                        | Error why -> return Error(changeFailed path changes.Length why)
                        | Ok changed ->
                            return!
                                patchLines
                                    settings
                                    path
                                    file
                                    changed.Edits
                                    (TextEdits.placed changed.Edits)
                                    changed.Replacements
                })

    /// `fs.editRange(range, text)`: replaces exactly a range's lines, if they still hold what the
    /// range says (else `stale`, with the lines there now as data); the new lines come back as a range.
    let fsEditRange<'R when 'R :> IFsOps and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        HostBinding.create2
            "fs.editRange"
            "Replaces exactly the lines of a range with text, if they still are what the range holds; otherwise fails with data.code stale and the lines there now (a FileRange) as data.data. Returns the new lines as a fresh range."
            (Arg.required "range" "From fs.read, or a fresh range of an edit." Codecs.fileRange,
             Arg.required
                 "text"
                 "The new lines; a final line ending is optional, and empty text removes the lines."
                 Codec.string)
            Codecs.edited
            (fun (range, text) ->
                eff {
                    match! readText settings range.Path "edit it with a command" with
                    | Error e -> return Error e
                    | Ok file ->
                        let total = lineCount file.Text
                        let current = TextEdits.lines (TextEdits.slice file.Text range.Start range.End)

                        if range.End > total || current <> TextEdits.lines range.Text then
                            let shown = display settings (resolve settings range.Path)
                            let first = min range.Start (total + 1)
                            let last = max (first - 1) (min range.End total)

                            let now: FileRange =
                                {
                                    Path = shown
                                    Start = first
                                    End = last
                                    Text = TextEdits.slice file.Text first last
                                    Digest = file.Digest
                                    Lines = total
                                }

                            return
                                Error(
                                    HostCallError.Failed(
                                        "stale",
                                        $"Lines {range.Start}–{range.End} of {shown} are not what the range holds: the file changed since they were read (it has {total} lines now). The error's data.data holds lines {first}–{last} as they are now: edit those, or read the file again.",
                                        Some(Codecs.fileRange.Encode now)
                                    )
                                )
                        else
                            let replacement = TextEdits.lines text

                            if replacement = current then
                                return
                                    Error(
                                        failure
                                            "unchanged"
                                            $"The text is what lines {range.Start}–{range.End} of {range.Path} already are: nothing to change."
                                    )
                            else
                                let edit = TextEdits.lineEdit current replacement

                                return!
                                    patchLines
                                        settings
                                        range.Path
                                        file
                                        [
                                            { edit with
                                                Line = range.Start + edit.Line - 1
                                            }
                                        ]
                                        [ range.Start, range.Start + replacement.Length - 1 ]
                                        1
                })

    /// Every core binding.
    let all<'R when 'R :> IShell and 'R :> IFs and 'R :> IFsOps and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: CoreSettings)
        : HostBinding<'R> list =
        [
            shellRun settings
            fsRead settings
            fsWrite settings
            fsList settings
            fsStat settings
            fsPatch settings
            fsEdit settings
            fsEditRange settings
        ]
