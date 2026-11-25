/// Test support for the component installer: archives built in memory, an installer environment,
/// and component definitions. Also compiled into Fx.Components.Live.Tests and the AOT scenarios.
module Fx.Components.Tests.Support

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open Fx.Components
open Fx.Effects
open Fx.Sdk

/// An entry of a test archive.
type ArchiveItem =
    | File of path: string * content: string * executable: bool
    | Dir of path: string
    | Symlink of path: string * target: string
    | Hardlink of path: string * target: string
    | Device of path: string

    override this.ToString() =
        match this with
        | File(path, _, _) -> $"file {path}"
        | Dir path -> $"dir {path}"
        | Symlink(path, target) -> $"symlink {path} -> {target}"
        | Hardlink(path, target) -> $"hardlink {path} -> {target}"
        | Device path -> $"device {path}"

let utf8 = UTF8Encoding(false)

let private fileMode (executable: bool) =
    if executable then
        enum<UnixFileMode> 0o755
    else
        enum<UnixFileMode> 0o644

/// A tar archive of the entries, in order. The same entries always give the same bytes (fixed
/// times, ustar format without extended attributes), so digests can be pinned.
let tar (entries: ArchiveItem list) : byte array =
    use output = new MemoryStream()

    do
        use writer = new TarWriter(output, TarEntryFormat.Ustar, true)

        let entry (kind: TarEntryType) (path: string) =
            UstarTarEntry(kind, path, ModificationTime = DateTimeOffset.UnixEpoch, Mode = fileMode false)

        for item in entries do
            let written =
                match item with
                | File(path, content, executable) ->
                    let e = entry TarEntryType.RegularFile path
                    e.DataStream <- new MemoryStream(utf8.GetBytes content)
                    e.Mode <- fileMode executable
                    e
                | Dir path ->
                    let e = entry TarEntryType.Directory path
                    e.Mode <- fileMode true
                    e
                | Symlink(path, target) ->
                    let e = entry TarEntryType.SymbolicLink path
                    e.LinkName <- target
                    e
                | Hardlink(path, target) ->
                    let e = entry TarEntryType.HardLink path
                    e.LinkName <- target
                    e
                | Device path -> entry TarEntryType.CharacterDevice path

            writer.WriteEntry written

    output.ToArray()

/// A gzip-compressed tar archive.
let tarGz (entries: ArchiveItem list) : byte array =
    let plain = tar entries
    use output = new MemoryStream()

    do
        use gzip = new GZipStream(output, CompressionLevel.Fastest, true)
        gzip.Write(plain, 0, plain.Length)

    output.ToArray()

/// A zip archive; Unix modes go in the external attributes, as Unix zip tools write them.
let zip (entries: ArchiveItem list) : byte array =
    use output = new MemoryStream()

    do
        use archive = new ZipArchive(output, ZipArchiveMode.Create, true)

        let add (path: string) (mode: int) (content: string) =
            let entry = archive.CreateEntry(path, CompressionLevel.Fastest)
            entry.ExternalAttributes <- mode <<< 16
            entry.LastWriteTime <- DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            use stream = entry.Open()
            let bytes = utf8.GetBytes content
            stream.Write(bytes, 0, bytes.Length)

        for entry in entries do
            match entry with
            | File(path, content, executable) -> add path ((if executable then 0o755 else 0o644) ||| 0o100000) content
            | Dir path -> add (if path.EndsWith '/' then path else path + "/") 0o040755 ""
            | Symlink(path, target) -> add path 0o120777 target
            | Hardlink(path, _) -> add path 0o100644 ""
            | Device path -> add path 0o020644 ""

    output.ToArray()

/// The digest of bytes.
let digestOf (bytes: byte array) : Digest =
    Sha256(Convert.ToHexStringLower(SHA256.HashData bytes))

/// The archive of a tool: `tool-<version>/bin/tool` (which "prints" its content when the fake shell
/// runs it) and a README, packed with a top directory that `strip 1` removes.
let toolArchive (version: string) : byte array =
    tarGz
        [
            Dir $"tool-{version}"
            Dir $"tool-{version}/bin"
            File($"tool-{version}/bin/tool", $"tool {version}", true)
            File($"tool-{version}/README", "read me", false)
        ]

let toolUrl (version: string) =
    $"https://example.test/tool/v{version}/tool.tar.gz"

let semver (text: string) =
    match SemVer.parse text with
    | Ok v -> v
    | Error e -> failwith e

/// The definition of the tool at a version, installable from its archive on every target.
let toolComponent (version: string) (archive: byte array) : Component =
    { Component.create
          "tool"
          (semver version)
          [
              {
                  Platforms = []
                  Method =
                      Archive
                          {
                              Url = "https://example.test/tool/v${version}/tool.tar.gz"
                              Digest = digestOf archive
                              Format = ArchiveFormat.TarGz
                              Strip = 1
                          }
              }
          ] with
        Layout = { Executables = [ "bin/tool" ] }
        Exports =
            {
                Bin = [ "tool", "bin/tool" ]
                Env = [ "TOOL_HOME", "${dir}" ]
            }
        Probe =
            Some
                {
                    Argv = [ "bin/tool"; "--version" ]
                    Expect = Some "tool ${version}"
                }
    }

/// The install spec of a component on a target, as the lockfile would give it.
let specOf (target: TargetTriple) (definition: Component) : InstallSpec =
    let method = (List.head definition.Methods).Method

    let method =
        match method with
        | Archive spec ->
            Archive
                { spec with
                    Url =
                        match Component.expand [ "version", string definition.Version ] spec.Url with
                        | Ok url -> url
                        | Error e -> failwith e
                }
        | other -> other

    { InstallSpec.create definition.Id definition.Version target method definition.Layout with
        Exports = definition.Exports
        Probe = definition.Probe
        Size = definition.Size
        DependsOn = definition.DependsOn |> List.map (fun d -> d.Id)
    }

/// The target tests install for.
let target = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)

/// An installer's environment: the capabilities its pipeline uses.
type InstallerEnv =
    {
        Clock: Clock
        Trace: Trace
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        Fetch: Fetch
        InstallFiles: InstallFiles
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITrace with
        member env.Trace = env.Trace

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IShell with
        member env.Shell = env.Shell

    interface IFetch with
        member env.Fetch = env.Fetch

    interface IInstallFiles with
        member env.InstallFiles = env.InstallFiles

    interface IScopeWith<InstallerEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"InstallerEnv({env.Scope})"

/// A shell that "runs" a program by printing its file's content (read with `read`): the probe of a
/// tool archive prints `tool <version>`. A file whose content starts with `exit <n>` exits with n.
let probeShell (read: FsRead) : Fx.Effects.Fakes.ScriptedShell =
    Fx.Effects.Fakes.ScriptedShell(fun command ->
        match command.Spec.Program with
        | Program.Exec(file, _) ->
            match (read.ReadFile file Threading.CancellationToken.None).GetAwaiter().GetResult() with
            | Ok bytes ->
                let content = utf8.GetString bytes

                if content.StartsWith("exit ", StringComparison.Ordinal) then
                    Some(Ok(Fx.Effects.Fakes.ScriptedShell.output (int (content.Substring 5)) "" "failed"))
                else
                    Some(Fx.Effects.Fakes.ScriptedShell.ok (content + "\n"))
            | Error _ -> Some(Error(NotStarted(file, "no such file")))
        | Program.Script _ -> None)

/// Runs an effect and waits for it, failing after a minute.
let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

/// Every file and directory under a directory, relative, `/`-separated and sorted (`d` marks
/// directories), read through `FsRead`.
let listTree (read: FsRead) (root: string) : string list =
    let rec walk (dir: string) (prefix: string) : string list =
        match (read.List dir Threading.CancellationToken.None).GetAwaiter().GetResult() with
        | Error _ -> []
        | Ok entries ->
            entries
            |> List.collect (fun entry ->
                let name = prefix + entry.Name

                match entry.Kind with
                | EntryKind.Directory -> (name + "/") :: walk entry.Path (name + "/")
                | _ -> [ name ])

    walk root "" |> List.sort
