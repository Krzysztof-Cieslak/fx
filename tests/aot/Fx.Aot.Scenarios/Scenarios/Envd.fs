/// envd (T111, docs/modules/envd.md): one scripted session against envd built from the live
/// interpreters, through the host's proxies: in process (embedded mode), and with this program as an
/// `envd-peer` child on stdio, so the protocol, the interpreters and the composition root run under
/// Native AOT on both ends. Paths, process ids and times are never printed.
module Fx.Aot.Scenarios.Envd

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd
open Fx.Envd.Live
open Fx.Pty
open Fx.Rpc

let private none = CancellationToken.None
let private windows = OperatingSystem.IsWindows()
let private utf8 (text: string) = UTF8Encoding(false).GetBytes text
let private text (bytes: byte array) = UTF8Encoding(false).GetString bytes
let private normalize (text: string) = text.Replace("\r\n", "\n")

let private orFail (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> invalidOp $"unexpected error: {e}"

let private temporary () =
    let dir =
        Path.Combine(Path.GetTempPath(), "envd-scenario-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private remove (dir: string) =
    try
        Directory.Delete(dir, true)
    with _ ->
        ()

/// The session: the same calls, whatever the transport.
let private session (client: EnvClient) (workspace: string) : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let say (line: string) = lines.Add(escape line)
        let at (name: string) = Path.Combine(workspace, name)
        let platform = client.Platform
        let libc = platform.Libc |> Option.defaultValue "none"

        let features =
            String.Join(",", platform.Features |> List.filter (fun f -> f <> Feature.Snapshots))

        say $"platform: {platform.Os} {platform.Arch} {libc} sep={platform.PathSeparator} features={features}"
        say $"platform: workspace announced: {platform.Workspace = workspace}"

        // Files.
        let! written = client.Fs.WriteFile (at "a.txt") (utf8 "hello\nworld\n") none
        orFail written
        let! entries = client.FsOps.StatMany [ at "a.txt"; at "missing.txt" ] none

        for entry in entries do
            match entry with
            | Ok e -> say $"stat: {e.Name} {e.Kind} {e.Size}"
            | Error(NotFound _) -> say "stat: not found"
            | Error e -> say $"stat: {e}"

        let! chunk = client.FsOps.ReadRange (at "a.txt") 6L 5 none
        let chunk = orFail chunk
        say $"range: {text chunk.Content} of {chunk.FileSize}"
        let! atomic = client.FsOps.WriteAtomic (at "b.txt") (utf8 "atomic") none
        orFail atomic
        let! b = client.FsRead.ReadFile (at "b.txt") none
        say $"atomic: {text (orFail b)}"

        let! patched =
            client.FsOps.ApplyPatch
                [
                    Edit(
                        at "a.txt",
                        Some(FsOpsOver.digest (utf8 "hello\nworld\n")),
                        [
                            {
                                Line = 2
                                Count = 1
                                Lines = [ "WORLD" ]
                            }
                        ]
                    )
                ]
                none

        for file in orFail patched do
            let digest = file.Digest |> Option.map string |> Option.defaultValue "none"
            say $"patch: {file.Lines} lines, {digest}"

        let! stale =
            client.FsOps.ApplyPatch [ Delete(at "a.txt", Some(FsOpsOver.digest (utf8 "old"))) ] none

        match stale with
        | Error(Stale _) -> say "patch: stale refused"
        | other -> say $"patch: {other}"

        let! found = client.FsOps.Search (SearchQuery.lines workspace "WOR") none

        for m in (orFail found).Matches do
            say $"search: {m}"

        // Processes on pipes and on a terminal.
        let script =
            if windows then
                "[Console]::Out.Write([Console]::In.ReadToEnd().ToUpper()); exit 3"
            else
                "tr a-z A-Z; exit 3"

        let! ran =
            client.Shell.Run (Command.script script |> Command.withStdinText "piped\n") none

        let output = orFail ran
        say $"shell: {normalize (Output.stdoutText output)} exit {output.ExitCode}"

        let program =
            if windows then
                Exec("pwsh", [ "-NoLogo"; "-NoProfile"; "-Command"; "Write-Host \"@@pty=$(1+2)\"" ])
            else
                Script "echo \"@@pty=$((1+2))\""

        let! spawned =
            client.Terminal.Spawn
                { PtySpec.script "" with
                    Program = program
                }
                none

        let pty = orFail spawned
        let transcript = Transcript pty
        do! transcript.ReadToEnd none
        let! code = pty.Wait none
        do! pty.Close()
        let marker = transcript.Plain.Contains "@@pty=3"
        say $"pty: marker seen {marker}, exit {code}"

        // Blobs.
        let! blob = client.Blobs.Put (utf8 "blob content") none
        let! back = client.Blobs.Get blob.Hash none
        say $"blobs: {blob} -> {text (orFail back)}"

        // Watching.
        let! watching =
            client.FsOps.Watch
                {
                    Root = workspace
                    Recursive = true
                    Debounce = TimeSpan.FromMilliseconds 50.0
                }
                none

        let watcher = orFail watching
        let! _ = client.Fs.WriteFile (at "watched.txt") (utf8 "x") none
        let mutable seen = false

        while not seen do
            match! watcher.Next none with
            | Some batch -> seen <- batch.Changes |> List.exists (fun c -> c.Path.EndsWith "watched.txt")
            | None -> invalidOp "the watch ended"

        do! watcher.Close()
        say "watch: saw watched.txt"

        // Snapshots, where git is.
        if Fx.Envd.Platform.has Feature.Snapshots platform then
            let! first = client.Snapshots.Take "first" none
            let first = orFail first
            let! _ = client.Fs.WriteFile (at "c.txt") (utf8 "c") none
            let! second = client.Snapshots.Take "second" none
            let second = orFail second
            let! changes = client.Snapshots.Diff first second none
            say $"""snapshots: {String.Join(", ", orFail changes)}"""
            let! restored = client.Snapshots.Restore first None none
            orFail restored
            let exists = File.Exists(at "c.txt")
            say $"snapshots: restored, c.txt exists: {exists}"
        else
            say "snapshots: no git"

        return List.ofSeq lines
    }

let private env () : ConnectionEnv =
    {
        Clock = SystemClock.clock
        Scope = Scope.root
    }

let private connect (transport: Transport) : Task<EnvClient> =
    task {
        match! Eff.run (env ()) (EnvClient.connect transport) with
        | Ok client -> return client
        | Error e -> return invalidOp $"connect failed: {e}"
    }

let private inProcess () : Task<string list> =
    task {
        let workspace = temporary ()
        let state = temporary ()

        try
            match!
                EnvdMain.start
                    { EnvdOptions.defaults with
                        Workspace = Some workspace
                        State = Some state
                    }
            with
            | Error e -> return [ $"start failed: {e}" ]
            | Ok instance ->
                let client, server =
                    InProcess.pair
                        (256 * 1024)
                        (ConnectionLimits.frames ConnectionLimits.defaults)
                        (ConnectionLimits.frames ConnectionLimits.defaults)

                EnvdMain.serve instance.Envd server |> ignore
                let! client = connect client
                let! lines = session client workspace
                let! _ = EnvClient.close client
                do! instance.Dispose()
                return lines
        finally
            remove workspace
            remove state
    }

/// The `envd-peer` command: `envd --stdio` for a workspace and state directory.
let servePeer (workspace: string) (state: string) : int =
    EnvdMain.runStdio
        { EnvdOptions.defaults with
            Workspace = Some workspace
            State = Some state
        }
        (Console.OpenStandardInput())
        (Console.OpenStandardOutput())
        (fun line -> stderr.WriteLine line)

let private stdioChild () : Task<string list> =
    task {
        let workspace = temporary ()
        let state = temporary ()

        try
            let info = Fx.Aot.Scenarios.Rpc.Peer.startInfo ()
            // The same program, in its envd mode instead of rpc-peer.
            let args = List.ofSeq info.ArgumentList |> List.filter (fun a -> a <> "rpc-peer")
            info.ArgumentList.Clear()

            for arg in args @ [ "envd-peer"; workspace; state ] do
                info.ArgumentList.Add arg

            use child =
                match Process.Start info with
                | null -> invalidOp "envd-peer did not start"
                | child -> child

            let transport =
                StreamTransport.create
                    (ConnectionLimits.frames ConnectionLimits.defaults)
                    child.StandardOutput.BaseStream
                    child.StandardInput.BaseStream

            let! client = connect transport
            let! lines = session client workspace
            let! _ = EnvClient.close client
            do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 30.0)
            return lines @ [ $"child exit {child.ExitCode}" ]
        finally
            remove workspace
            remove state
    }

let private bounded (run: unit -> Task<string list>) () : Task<string list> =
    run().WaitAsync(TimeSpan.FromSeconds 120.0)

let scenarios: Scenario list =
    [
        Scenario.task "envd-in-process" (bounded inProcess)
        Scenario.task "envd-stdio-child" (bounded stdioChild)
    ]
