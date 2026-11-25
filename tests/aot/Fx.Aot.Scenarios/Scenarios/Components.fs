/// The component model, planner and installer (T110, docs/modules/components.md): versions, ranges
/// and codecs; planner determinism and the lockfile text; archive extraction and its refusals; the
/// installer machine on the in-memory fakes; the live install files on a temporary directory (whose
/// path is never printed) and HTTP downloads from a loopback server. Archives are uncompressed tar
/// or fixed bytes wherever a digest is printed: compressed bytes may differ between zlib builds.
module Fx.Aot.Scenarios.Components

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Components
open Fx.Components.Fakes
open Fx.Components.Live
open Fx.Components.Tests.Support
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Machines
open Fx.Machines.Host
open Fx.Sdk

/// A result, without FSharp.Core's reflection-based printing.
let private show (result: Result<'T, 'E>) : string =
    match result with
    | Ok value -> $"ok {value}"
    | Error e -> $"error {e}"

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

let private linux = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)
let private windows = TargetTriple.create Os.Windows Arch.Arm64 None

/// A component whose archive is uncompressed tar, so its digest is the same everywhere.
let private tarTool (version: string) : byte array * Component =
    let archive =
        tar
            [
                Dir $"tool-{version}"
                File($"tool-{version}/bin/tool", $"tool {version}", true)
                File($"tool-{version}/README", "read me", false)
            ]

    let definition = toolComponent version archive

    let definition =
        { definition with
            Methods =
                definition.Methods
                |> List.map (fun m ->
                    match m.Method with
                    | Archive spec ->
                        { m with
                            Method = Archive { spec with Format = ArchiveFormat.Tar }
                        }
                    | _ -> m)
        }

    archive, definition

let model =
    Scenario.lines "components-model" (fun () ->
        [
            for text in [ "1.4.5"; "1.4.5+a7733435c"; "2.0.0-beta.11"; "01.2.3"; "1.2" ] do
                match SemVer.parse text with
                | Ok v -> $"semver {text}: {v}"
                | Error e -> $"semver {text}: {e}"

            let versions = [ "1.0.0-alpha"; "1.0.0-beta.2"; "1.0.0-beta.11"; "1.0.0"; "1.2.0" ]

            for a, b in List.pairwise versions do
                match SemVer.parse a, SemVer.parse b with
                | Ok x, Ok y -> $"compare {a} {b}: {SemVer.compare x y}"
                | _ -> "unparsed"

            for range, version in
                [
                    ">=1.4 <2", "1.9.0"
                    "^0.2.3", "0.3.0"
                    "1", "2.0.0-beta"
                    "~1.4.5", "1.4.9"
                    "1 || ^3", "3.1.0"
                ] do
                match VersionRange.parse range, SemVer.parse version with
                | Ok r, Ok v -> $"range {range} has {version}: {VersionRange.satisfies r v}"
                | Error e, _ -> e
                | _, Error e -> e

            yield!
                TargetTriple.all
                |> List.map (fun t -> $"target {t} exe '{TargetTriple.exeSuffix t}'")

            let _, tool = tarTool "1.2.3"
            let json = Component.codec.Encode tool
            "component " + Json.serializeCanonical json
            $"round trip: {Component.codec.Decode json = Ok tool}"

            let bad =
                (Json.serialize json)
                    .Replace("https://example", "http://example")
                    .Replace("\"strip\":1", "\"strip\":99")

            match Codec.decodeText Component.codec bad with
            | Ok _ -> "accepted"
            | Error issues -> yield! issues |> List.map (fun i -> "issue " + string i)

            $"contribution {ComponentDefinitions.contributionType.Name} privileged {ComponentDefinitions.contributionType.Privileged}"
        ])

let planner =
    Scenario.lines "components-planner" (fun () ->
        let def (id: string) (version: string) (deps: (string * string) list) =
            { Component.create
                  id
                  (semver version)
                  [
                      {
                          Platforms = []
                          Method =
                              Archive
                                  {
                                      Url = $"https://example.test/{id}-${{version}}.zip"
                                      Digest = digestOf (utf8.GetBytes(id + version))
                                      Format = ArchiveFormat.Zip
                                      Strip = 0
                                  }
                      }
                  ] with
                DependsOn = deps |> List.map (fun (id, range) -> { Id = id; Version = range })
            }

        let catalog =
            [
                def "tool" "1.0.0" [ "lib", "^2" ]
                def "tool" "1.5.0" [ "lib", "^2.1" ]
                def "lib" "2.0.0" []
                def "lib" "2.1.0" []
            ]

        let requires (pairs: (string * string) list) =
            pairs
            |> List.map (fun (id, range) ->
                {
                    Ref = { Id = id; Version = range }
                    By = "scenario"
                })

        let show (result: Result<Lockfile, PlanError list>) =
            match result with
            | Ok lockfile -> (Lockfile.render lockfile).Split('\n') |> List.ofArray |> List.map escape
            | Error errors -> errors |> List.map (fun e -> "error " + escape (string e))

        let planned =
            Planner.plan PlanSettings.defaults catalog (requires [ "tool", "1"; "lib", "<2.1" ]) [ windows; linux ]

        let shuffled =
            Planner.plan
                PlanSettings.defaults
                (List.rev catalog)
                (requires [ "lib", "<2.1"; "tool", "1" ])
                [ linux; windows ]

        [
            yield! show planned
            match planned, shuffled with
            | Ok a, Ok b -> $"hash {Lockfile.hash a}, same in any order: {Lockfile.render a = Lockfile.render b}"
            | _ -> "not planned"
            yield!
                show (
                    Planner.plan
                        PlanSettings.defaults
                        catalog
                        (requires [ "tool", "^2"; "nope", "1"; "lib", "x.1" ])
                        [ linux ]
                )
        ])

/// A sink that records what an extraction writes.
let private recording () : ResizeArray<string> * ExtractSink =
    let written = ResizeArray<string>()
    let join (path: string list) = String.Join("/", path)

    written,
    {
        CreateDirectory =
            fun path ->
                written.Add $"d {join path}"
                Task.FromResult(Ok())
        WriteFile =
            fun path executable content ->
                use reader = new StreamReader(content)
                written.Add $"f {join path} x={executable}: {reader.ReadToEnd()}"
                Task.FromResult(Ok())
        CreateSymlink =
            fun path target ->
                written.Add $"l {join path} -> {target}"
                Task.FromResult(Ok())
        CopyFile =
            fun path source _ ->
                written.Add $"c {join path} <- {join source}"
                Task.FromResult(Ok())
    }

let archives =
    Scenario.task "components-archive" (fun () ->
        task {
            let lines = ResizeArray<string>()

            let run (label: string) (format: ArchiveFormat) (limits: ExtractLimits) (bytes: byte array) =
                task {
                    let written, sink = recording ()
                    use stream = new MemoryStream(bytes)

                    let options =
                        {
                            Format = format
                            Strip = 1
                            Limits = limits
                            Executables = [ "data" ]
                        }

                    let! result = Archive.extract options stream sink CancellationToken.None
                    lines.Add $"{label} ({format}):"

                    for line in written do
                        lines.Add("  " + line)

                    match result with
                    | Ok report -> lines.Add $"  ok: {report}"
                    | Error e -> lines.Add $"  error: {e}"
                }

            let good =
                [
                    Dir "top"
                    File("top/bin/tool", "run", true)
                    File("top/data", "d", false)
                    Symlink("top/link", "bin/tool")
                    Hardlink("top/copy", "top/data")
                ]

            do! run "good" ArchiveFormat.Tar ExtractLimits.defaults (tar good)
            do! run "good" ArchiveFormat.TarGz ExtractLimits.defaults (tarGz good)

            do!
                run
                    "good"
                    ArchiveFormat.Zip
                    ExtractLimits.defaults
                    (zip
                        [
                            Dir "top"
                            File("top/bin/tool", "run", true)
                            File("top/data", "d", false)
                            Symlink("top/link", "bin/tool")
                        ])

            do! run "traversal" ArchiveFormat.Tar ExtractLimits.defaults (tar [ File("top/../../evil", "x", false) ])
            do! run "zip slip" ArchiveFormat.Zip ExtractLimits.defaults (zip [ File("top/../../evil", "x", false) ])
            do! run "escaping link" ArchiveFormat.TarGz ExtractLimits.defaults (tarGz [ Symlink("top/l", "../../x") ])
            do! run "device" ArchiveFormat.Tar ExtractLimits.defaults (tar [ Device "top/dev" ])

            do!
                run
                    "bomb"
                    ArchiveFormat.TarGz
                    { ExtractLimits.defaults with
                        MaxFileBytes = 1000L
                    }
                    (tarGz [ File("top/big", String('0', 100_000), false) ])

            do! run "corrupt" ArchiveFormat.Zip ExtractLimits.defaults (utf8.GetBytes "not a zip")
            return List.ofSeq lines
        })

/// The installer on the in-memory fakes. On a thread-pool thread: FakeClock needs inline continuations.
let installer =
    Scenario.task "components-installer" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()
                let fs = InMemoryFs(clock.Clock, false)
                let files = InMemoryInstallFiles(fs.Fs, fs.FsRead)
                let fetch = FakeFetch(fs.Fs)
                let outside = if OperatingSystem.IsWindows() then @"C:\base" else "/base"
                let root = Path.Combine(outside, "runtimes")

                let env: InstallerEnv =
                    {
                        Clock = clock.Clock
                        Trace = Trace.none
                        FsRead = fs.FsRead
                        Fs = fs.Fs
                        Shell = (probeShell fs.FsRead).Shell
                        Fetch = fetch.Fetch
                        InstallFiles = files.InstallFiles
                        Scope = Scope.root
                    }

                let serve (version: string) =
                    let archive, definition = tarTool version
                    fetch.Serve(toolUrl version, archive)
                    specOf target definition

                let v1 = serve "1.0.0"
                let v2 = serve "2.0.0"
                let lines = ResizeArray<string>()
                let mutable key = 0

                let start (name: string) (log: InMemoryEventLog<InstallerEvent>) =
                    let settings =
                        { InstallerSettings.create root name (fun _ -> Eff.ret (ApprovalAnswer.Approved "scenario")) with
                            RetryDelay = TimeSpan.FromSeconds 1.0
                        }

                    wait (Eff.run env (ComponentInstaller.start settings log.Log))

                let ensure actor holder spec =
                    key <- key + 1

                    let result =
                        wait (Eff.run env (ComponentInstaller.ensure actor (string key) holder spec))

                    lines.Add $"ensure {spec.Key} for {holder}: {show result}"

                let send actor command =
                    key <- key + 1
                    lines.Add $"{command}: {wait (Eff.run env (ComponentInstaller.send actor (string key) command))}"

                let logA = InMemoryEventLog<InstallerEvent>()
                let a = start "a" logA
                ensure a "h" v1
                ensure a "h" v1
                fetch.FailNext(toolUrl "2.0.0", Network(toolUrl "2.0.0", "reset"))
                let installing = Eff.run env (ComponentInstaller.ensure a "retry" "h" v2)

                while not installing.IsCompleted do
                    clock.Advance(TimeSpan.FromSeconds 1.0)

                lines.Add $"ensure {v2.Key} after a retry: {show (wait installing)}"

                let logB = InMemoryEventLog<InstallerEvent>()
                let b = start "b" logB
                ensure b "g" v1
                send a (InstallerCommand.Hold("h", [ v2.Key ]))
                send a InstallerCommand.Prune
                send b (InstallerCommand.Hold("g", []))
                send a InstallerCommand.Prune

                let bad = tar [ File("../evil", "x", false) ]
                fetch.Serve("https://example.test/bad.tar", bad)

                let badSpec =
                    specOf
                        target
                        (Component.create
                            "bad"
                            (semver "1.0.0")
                            [
                                {
                                    Platforms = []
                                    Method =
                                        Archive
                                            {
                                                Url = "https://example.test/bad.tar"
                                                Digest = digestOf bad
                                                Format = ArchiveFormat.Tar
                                                Strip = 0
                                            }
                                }
                            ])

                ensure a "h" badSpec

                for stream, log in [ "a", logA; "b", logB ] do
                    for recorded in log.Events(ComponentInstaller.stream stream) do
                        lines.Add $"{recorded.Ref} {recorded.Event}"

                lines.Add("tree: " + String.Join(", ", listTree fs.FsRead root))
                wait (Eff.run env (Actor.stop a))
                wait (Eff.run env (Actor.stop b))
                List.ofSeq lines)
        ))

/// The live install files on a temporary directory.
let local =
    Scenario.task "components-local" (fun () ->
        task {
            let dir =
                Path.Combine(Path.GetTempPath(), "components-scenario-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory dir |> ignore
            let files = LocalInstallFiles.files
            let none = CancellationToken.None
            let lines = ResizeArray<string>()

            try
                for format, pack in [ ArchiveFormat.TarGz, tarGz; ArchiveFormat.Zip, zip ] do
                    let archive = Path.Combine(dir, $"archive-{format}")

                    File.WriteAllBytes(
                        archive,
                        pack
                            [
                                Dir "top"
                                File("top/bin/tool", "run", true)
                                File("top/README", "read", false)
                            ]
                    )

                    let destination = Path.Combine(dir, $"out-{format}")

                    let! result =
                        files.Extract
                            {
                                Archive = archive
                                Destination = destination
                                Options =
                                    {
                                        Format = format
                                        Strip = 1
                                        Limits = ExtractLimits.defaults
                                        Executables = []
                                    }
                            }
                            none

                    lines.Add $"{format}: {show result}"

                    for file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories) |> Array.sort do
                        let relative = Path.GetRelativePath(destination, file).Replace('\\', '/')

                        let mode =
                            if OperatingSystem.IsWindows() then
                                ""
                            else
                                " executable="
                                + string ((File.GetUnixFileMode file).HasFlag UnixFileMode.UserExecute)

                        lines.Add $"  {relative}{mode}: {File.ReadAllText file}"

                let! hash = files.Hash (Path.Combine(dir, "out-tar.gz", "README")) none
                lines.Add $"hash: {show hash}"
                let lockPath = Path.Combine(dir, "locks", "x.lock")
                let! first = files.TryLock lockPath none
                let! second = files.TryLock lockPath none

                lines.Add
                    $"lock: {show (first |> Result.map Option.isSome)}, again: {show (second |> Result.map Option.isSome)}"

                match first with
                | Ok(Some held) -> do! held.Release()
                | _ -> ()

                let! third = files.TryLock lockPath none
                lines.Add $"after release: {show (third |> Result.map Option.isSome)}"

                match third with
                | Ok(Some held) -> do! held.Release()
                | _ -> ()

                let! replaced =
                    files.ReplaceFile (Path.Combine(dir, "roots.json")) (utf8.GetBytes "[]") none

                let! deleted = files.DeleteTree (Path.Combine(dir, "out-zip")) none
                let gone = not (Directory.Exists(Path.Combine(dir, "out-zip")))
                lines.Add $"replaced: {show replaced}, deleted: {show deleted}, gone: {gone}"
            finally
                try
                    Directory.Delete(dir, true)
                with _ ->
                    ()

            return List.ofSeq lines
        })

/// Downloads from a loopback HTTP server: one request per connection.
let http =
    Scenario.task "components-http" (fun () ->
        task {
            let body = Array.init 100_000 (fun i -> byte (i % 251))
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start()
            let port = (listener.LocalEndpoint :?> IPEndPoint).Port
            use stop = new CancellationTokenSource()

            let serve (client: TcpClient) =
                task {
                    use client = client
                    use stream = client.GetStream()
                    let buffer = Array.zeroCreate<byte> 4096
                    let request = StringBuilder()

                    while not (request.ToString().Contains "\r\n\r\n") do
                        let! n = stream.ReadAsync(buffer, 0, buffer.Length)

                        if n = 0 then
                            failwith "closed"

                        request.Append(Encoding.ASCII.GetString(buffer, 0, n)) |> ignore

                    let path = request.ToString().Split(' ').[1]

                    let status, extra, content =
                        match path with
                        | "/archive" -> 200, "", body
                        | "/moved" -> 302, "Location: /archive\r\n", [||]
                        | _ -> 404, "", [||]

                    let head =
                        Encoding.ASCII.GetBytes
                            $"HTTP/1.1 {status} X\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{extra}\r\n"

                    do! stream.WriteAsync(head, 0, head.Length)
                    do! stream.WriteAsync(content, 0, content.Length)
                }

            let accepting =
                task {
                    try
                        while true do
                            let! client = listener.AcceptTcpClientAsync stop.Token
                            serve client |> ignore
                    with _ ->
                        ()
                }

            let dir =
                Path.Combine(Path.GetTempPath(), "components-http-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory dir |> ignore
            use client = HttpFetch.client ()
            let fetch = HttpFetch.create client
            let lines = ResizeArray<string>()

            try
                for path, max in
                    [
                        "/archive", 1_000_000L
                        "/moved", 1_000_000L
                        "/missing", 1_000_000L
                        "/archive", 1000L
                    ] do
                    let request =
                        {
                            Url = $"http://127.0.0.1:{port}{path}"
                            Destination = Path.Combine(dir, "download")
                            MaxBytes = max
                        }

                    let! result =
                        (fetch.Download request CancellationToken.None).WaitAsync(TimeSpan.FromMinutes 1.0)

                    let shown =
                        match result with
                        | Ok hash -> $"ok {hash}"
                        | Error(HttpStatus(_, status)) -> $"status {status}"
                        | Error(FetchError.TooLarge(_, limit)) -> $"too large ({limit})"
                        | Error e -> $"error {e.GetType().Name}"

                    lines.Add $"{path} (max {max}): {shown}"
            finally
                stop.Cancel()
                listener.Stop()

                try
                    Directory.Delete(dir, true)
                with _ ->
                    ()

            do! accepting.WaitAsync(TimeSpan.FromSeconds 10.0)
            return List.ofSeq lines
        })

let scenarios = [ model; planner; archives; installer; local; http ]
