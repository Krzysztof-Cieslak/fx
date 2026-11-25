/// The host's composition root (src/Fx.Host) on the JIT and as a native binary: the start (lock,
/// config, the built-ins, the variant registry, the SQLite stores, grants, identity, environments,
/// model access, sessions), signing in the OS user in process, a headless run in an embedded
/// environment, a restart that continues the session, and the codecs of what `fx run --json` prints.
/// The interpreter is disabled in host config, so turns end at their hands: no network, no Bun.
module Fx.Aot.Scenarios.Host

open System
open System.Collections
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Sessions
open Fx.Host
open Fx.Aot.Scenarios

let private temporary () =
    let dir =
        Path.Combine(Path.GetTempPath(), "host-scenario-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private remove (dir: string) =
    try
        for file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
            File.SetAttributes(file, FileAttributes.Normal)

        Directory.Delete(dir, true)
    with _ ->
        ()

let private variables () : (string * string) list =
    [
        for entry in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> do
            match entry.Key, entry.Value with
            | (:? string as name), (:? string as value) -> name, value
            | _ -> ()
    ]
    |> List.sortBy fst

/// A model that must never be called: the turns end before it.
let private noModel: Http = (InMemoryHttp(SystemClock.clock, (fun _ -> None))).Http

let private options (root: string) (explicit: (string * Json) list) : HostOptions =
    let workspace = Path.Combine(root, "workspace")
    Directory.CreateDirectory workspace |> ignore

    let facts: ProcessFacts =
        {
            Variables = variables ()
            CurrentDirectory = workspace
            Binary = Product.binaryName
            BinaryArgs = []
        }

    match Live.options facts workspace explicit with
    | Error e -> failwith e
    | Ok options ->
        { options with
            Paths = HostPaths.under (Path.Combine(root, "data"))
            Http = noModel
        }

let private quiet: RunIo =
    {
        Show = ignore
        Answer = fun _ -> Task.FromResult None
        Interrupt = CancellationToken.None
    }

let private embedded () : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let say (line: string) = lines.Add(Show.escape line)
        let root = temporary ()

        try
            let data = Path.Combine(root, "data")
            Directory.CreateDirectory data |> ignore

            File.WriteAllText(
                Path.Combine(data, "config.json"),
                """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}}"""
            )

            let opts =
                options root [ "model.role.main", Json.String "anthropic/claude-sonnet-5-5" ]

            match! Host.start opts with
            | Error errors -> say ("start failed: " + String.concat "; " errors)
            | Ok host ->
                let mutable session = ""

                try
                    say $"started: {host.Warnings.Length} warnings, lockfile {host.Lockfile.IsSome}"

                    say $"extensions: {host.Extensions.Loaded.Count} active, {host.Extensions.Report.Length} reported"

                    say $"registry has agent.interpreter: {host.Registry.Contains(HostSlots.interpreter.Id)}"

                    match! Host.start opts with
                    | Ok second ->
                        do! Host.stop second
                        say "a second host started"
                    | Error errors ->
                        let locked = (String.concat "; " errors).Contains "another host is running"
                        say $"a second host refused: {locked}"

                    match! HostSessions.signInLocal host "scenario" CancellationToken.None with
                    | Error e -> say $"sign-in failed: {e}"
                    | Ok signedIn ->
                        let roles = signedIn.Roles |> List.map string |> String.concat ", "
                        say $"signed in: {signedIn.Principal.Kind} via {signedIn.Principal.Provider}, roles {roles}"

                    match!
                        Headless.run
                            host
                            {
                                Prompt = "hello"
                                Session = None
                                Title = Some "scenario"
                            }
                            quiet
                            CancellationToken.None
                    with
                    | Error e -> say $"run failed: {e}"
                    | Ok outcome ->
                        session <- outcome.Session

                        let shown = { outcome with Session = "<id>" }

                        say $"run: {Json.serialize (Codec.encode Headless.outcomeCodec shown)}"
                        say $"exit code: {Headless.exitCode outcome}"
                finally
                    (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore

                // A new host on the same data directory continues the session.
                match! Host.start (options root []) with
                | Error errors -> say ("restart failed: " + String.concat "; " errors)
                | Ok host ->
                    try
                        let entries = HostSessions.entries host

                        say
                            $"restarted: {entries.Length} sessions, the same: {entries |> List.exists (fun e -> e.Session = session)}"

                        match!
                            Headless.run
                                host
                                {
                                    Prompt = "again"
                                    Session = Some session
                                    Title = None
                                }
                                quiet
                                CancellationToken.None
                        with
                        | Error e -> say $"second run failed: {e}"
                        | Ok outcome ->
                            let failed =
                                match outcome.Ending with
                                | TurnEnd.Failed reason -> reason.Contains "no interpreter"
                                | _ -> false

                            say $"second run: turn {outcome.Turn}, failed at its hands: {failed}"
                    finally
                        (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore
        finally
            remove root

        return List.ofSeq lines
    }

/// The pure parts: paths, config files, trust and the static list.
let private composition () : string list =
    [
        let config =
            match
                Json.parse
                    """{"variants": {"edit.guard": "require"}, "environments": {"default": {"provider": "local"}}, "colour": 1}"""
            with
            | Ok json -> json
            | Error e -> failwith (string e)

        match ConfigFile.ofJson Fx.Variants.Layer.Host "host.json" config with
        | Ok _ -> "config: accepted"
        | Error issues -> "config: " + Issue.render issues

        let root = if OperatingSystem.IsWindows() then @"C:\work" else "/work"

        let store =
            TrustStore.add false (Path.Combine(root, "app")) 1L TrustStore.empty
            |> Result.defaultWith failwith

        let below = TrustStore.isTrusted false (Path.Combine(root, "app", "src")) store
        let sibling = TrustStore.isTrusted false (Path.Combine(root, "app2")) store
        $"trust: below {below}, sibling {sibling}"
        "trust json: " + Json.serialize (Codec.encode TrustStore.codec store)

        "built-ins: " + String.concat ", " Builtins.ids

        match ModelAuth.resolve [ "OPENAI_API_KEY", "k" ] Fx.Ext.ModelOpenAI.Provider.def with
        | Ok auth -> "openai auth: " + string auth
        | Error e -> "openai auth: " + string e

        match ModelAuth.resolve [] Fx.Ext.ModelAnthropic.Provider.def with
        | Ok auth -> "anthropic auth: " + string auth
        | Error e -> "anthropic auth: " + string e

        "index: "
        + Json.serialize (
            Codec.encode
                SessionIndex.event
                (IndexEvent.Created
                    {
                        Session = "s1"
                        Workspace = "/w"
                        Environment = "e1"
                        Title = None
                    })
        )

        for slot in HostSlots.all do
            $"slot {slot.Id} = {Json.serialize slot.Default} privileged {slot.Privileged}"
    ]

let private bounded (run: unit -> Task<string list>) () : Task<string list> =
    run().WaitAsync(TimeSpan.FromSeconds 240.0)

let scenarios: Scenario list =
    [
        Scenario.lines "host-composition" composition
        Scenario.task "host-embedded" (bounded embedded)
    ]
