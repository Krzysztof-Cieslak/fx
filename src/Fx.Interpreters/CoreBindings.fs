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

/// A text file as `fs.read` gives it (spec §4.1 `FileText`).
type FileText =
    {
        /// Relative to the workspace (with `/`) when inside it.
        Path: string
        Text: string
        Lines: int
        Size: int64
        /// SHA-256 of the content; anchored edits and patches check it (spec §6.8).
        Digest: BlobHash
    }

    override this.ToString() =
        $"FileText({this.Path}, {this.Lines} lines, {this.Digest})"

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
        }

/// The core bindings (spec §6.5 "env-side"): `shell.run`, `fs.read`, `fs.write`, `fs.list`,
/// `fs.stat` and `fs.patch`, over the environment's capabilities (envd's, or the local ones in
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

        let fileText: Codec<FileText> =
            Codec.record {
                let! path = Codec.field "path" (fun (f: FileText) -> f.Path) Codec.string
                and! text = Codec.field "text" (fun f -> f.Text) Codec.string
                and! lines = Codec.field "lines" (fun f -> f.Lines) (Codec.int |> Codec.atLeast 0)
                and! size = Codec.field "size" (fun f -> f.Size) (Codec.int64 |> Codec.atLeast 0L)
                and! digest = Codec.field "digest" (fun f -> f.Digest) digest

                return
                    {
                        Path = path
                        Text = text
                        Lines = lines
                        Size = size
                        Digest = digest
                    }
            }
            |> Codec.describe "A text file: its content, line count, size in bytes and digest."
            |> Codec.named "FileText"

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

    /// `fs.read(path)`: a text file, whole (up to `ReadBytes`).
    let fsRead<'R when 'R :> IFsOps and 'R :> IScope> (settings: CoreSettings) : HostBinding<'R> =
        HostBinding.create1
            "fs.read"
            "Reads a text file (UTF-8)."
            (Arg.required "path" "Relative to the workspace." Codec.string)
            Codecs.fileText
            (fun path ->
                eff {
                    let full = resolve settings path

                    match! FsOps.readRange full 0L settings.ReadBytes with
                    | Error e -> return Error(fsFailed e)
                    | Ok chunk when not chunk.AtEnd ->
                        return
                            Error(
                                failure
                                    "tooLarge"
                                    $"{path} has {chunk.FileSize} bytes, more than {settings.ReadBytes}: read parts of it with a command"
                            )
                    | Ok chunk when not (Unicode.Utf8.IsValid(ReadOnlySpan chunk.Content)) ->
                        return Error(failure "notText" $"{path} is not UTF-8 text")
                    | Ok chunk ->
                        let text = withoutBom (utf8.GetString chunk.Content)

                        return
                            Ok
                                {
                                    Path = display settings full
                                    Text = text
                                    Lines = lineCount text
                                    Size = chunk.FileSize
                                    Digest = BlobHash.ofBytes chunk.Content
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
        ]
