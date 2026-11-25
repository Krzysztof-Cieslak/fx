/// The host's composition without a kernel (no network needed): its start and lock, config and trust,
/// the variant registry, sign-in, sessions in an embedded environment, and a restart.
module Fx.Host.Tests.HostTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Auth
open Fx.Identity
open Fx.Identity.Host
open Fx.Grants
open Fx.Sessions
open Fx.Host
open Fx.Host.Tests.Support

/// Answers nothing and shows lines into a list.
let private quietIo (lines: ResizeArray<string>) : RunIo =
    {
        Show = fun line -> lock lines (fun () -> lines.Add line)
        Answer = fun request -> failtest $"no grant should be asked for: {request}"
        Decide = fun card -> failtest $"no decision should be asked for: {card}"
        Interrupt = CancellationToken.None
    }

let private hostConfig (place: Place) (text: string) =
    Directory.CreateDirectory place.Data |> ignore
    File.WriteAllText(Path.Combine(place.Data, "config.json"), text)

/// A host without the bun-ts interpreter: environments need no components, turns fail at their hands.
let private withoutKernel =
    """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}}"""

let private run (host: Host) (request: RunRequest) (lines: ResizeArray<string>) : RunOutcome =
    match waitFor 120.0 (Headless.run host request (quietIo lines) CancellationToken.None) with
    | Ok outcome -> outcome
    | Error e ->
        let shown = String.concat "\n" lines
        failtest $"the run failed: {e}; shown: {shown}"

[<Tests>]
let tests =
    testList
        "Host"
        [
            testCase "a host starts, signs in its OS user, and holds its data directory against a second host"
            <| fun () ->
                let p = place "lock"
                let model = Model()
                let host = start (options p model [])

                try
                    match wait (HostSessions.signInLocal host "test" CancellationToken.None) with
                    | Ok session ->
                        Expect.equal session.Principal.Kind PrincipalKind.User "a person"
                        Expect.equal session.Principal.Provider "local-os-user" "provider"
                        Expect.equal session.Roles [ Role.Owner ] "owns the host"
                        Expect.stringStarts (ClientSession.attribution session).Via "test/" "the client"
                    | Error e -> failtest e

                    match waitFor 60.0 (Host.start (options p model [])) with
                    | Ok second ->
                        stop second
                        failtest "a second host started on the same data directory"
                    | Error errors ->
                        Expect.stringContains (String.concat "\n" errors) "another host is running" "refused"

                    Expect.isEmpty host.Warnings "no warnings"
                    Expect.isSome host.Lockfile "components planned"
                finally
                    stop host

                // Released: the next host starts, with the same machine id.
                let machine = File.ReadAllText(Path.Combine(p.Data, "machine-id"))
                let again = start (options p model [])
                stop again
                Expect.equal (File.ReadAllText(Path.Combine(p.Data, "machine-id"))) machine "the same machine"

            testCase "config errors stop the start, every one listed, and nothing stays held"
            <| fun () ->
                let p = place "config"
                hostConfig p """{"variants": {"edit.gaurd": "warn"}, "colour": "blue"}"""

                match waitFor 60.0 (Host.start (options p (Model()) [ "agent.actionSurface", Json.String "both" ])) with
                | Ok host ->
                    stop host
                    failtest "started"
                | Error errors ->
                    let text = String.concat "\n" errors
                    Expect.stringContains text "$.colour: unknown member" "the unknown member"
                    Expect.isFalse (text.Contains "edit.gaurd") "variants are resolved after the files read"

                hostConfig p """{"variants": {"edit.gaurd": "warn"}}"""

                match waitFor 60.0 (Host.start (options p (Model()) [ "agent.actionSurface", Json.String "both" ])) with
                | Ok host ->
                    stop host
                    failtest "started"
                | Error errors ->
                    let text = String.concat "\n" errors

                    Expect.stringContains
                        text
                        "edit.gaurd: unknown variant slot; did you mean \"edit.guard\"?"
                        "the typo"

                    Expect.stringContains text "agent.actionSurface" "the bad flag"

                // The lock was released.
                hostConfig p "{}"
                stop (start (options p (Model()) []))

            testCase "a project's config is read only once the project is trusted"
            <| fun () ->
                let p = place "trust"

                p.Write(
                    Path.Combine(Product.projectDirName, "config.json"),
                    """{"variants": {"edit.guard": "require"}}"""
                )

                let opts = options p (Model()) []

                let explain () =
                    match wait (Commands.variants opts VariantsView.Explain) with
                    | Ok text -> text
                    | Error errors -> failtest (String.concat "\n" errors)

                let before = explain ()
                Expect.stringContains before "edit.guard = \"warn\" (default)" "not read"
                Expect.stringContains before "is not trusted" "says why"

                match wait (Commands.trust opts p.Workspace) with
                | Ok message -> Expect.stringContains message "trusted" "trusted"
                | Error e -> failtest e

                let after = explain ()
                Expect.stringContains after "edit.guard = \"require\" (project" "read"

                match wait (Commands.trusted opts) with
                | Ok text -> Expect.stringContains text p.Workspace "listed"
                | Error e -> failtest e

                match wait (Commands.untrust opts p.Workspace) with
                | Ok _ -> Expect.stringContains (explain ()) "edit.guard = \"warn\" (default)" "not read again"
                | Error e -> failtest e

            testCase "fx variants lists every slot, the extensions' among them, and their schema"
            <| fun () ->
                let opts = options (place "variants") (Model()) []

                match wait (Commands.variants opts VariantsView.List) with
                | Ok text ->
                    for slot in
                        [
                            "agent.actionSurface"
                            "codeact.budget.steps"
                            "agent.interpreter"
                            "egress.exclusions.host"
                        ] do
                        Expect.stringContains text (slot + " = ") slot

                    Expect.stringContains
                        text
                        "host.store.durability = \"full\" [\"full\", \"normal\"] (privileged)"
                        "privileged"
                | Error errors -> failtest (String.concat "\n" errors)

                match wait (Commands.variants opts VariantsView.Schema) with
                | Ok text ->
                    match Json.parse text with
                    | Ok schema -> Expect.isSome (Json.tryField "properties" schema) "a JSON Schema"
                    | Error e -> failtest (string e)
                | Error errors -> failtest (String.concat "\n" errors)

            testCase "fx auth stores a credential the host's model requests use, and waits for a running host"
            <| fun () ->
                let p = place "auth"
                hostConfig p """{"variants": {"credentials.store": "file"}}"""
                let opts = options p (Model()) []
                let flows = ResizeArray<string>()

                let paste: FlowRunner =
                    fun flow _ ->
                        flows.Add(string flow)

                        match flow with
                        | Fx.Sdk.AuthFlow.Paste _ ->
                            Task.FromResult(Ok(Fx.Sdk.FlowResult.Pasted(SecretText "sk-test-openai")))
                        | other -> Task.FromResult(Error $"not a paste: {other}")

                let auth (command: AuthCommand) =
                    match waitFor 60.0 (Commands.auth opts paste command) with
                    | Ok(code, lines) ->
                        let text = String.concat "\n" lines
                        Expect.isFalse (text.Contains "sk-test-openai") "no secret in the output"
                        code, text
                    | Error errors -> failtest (String.concat "\n" errors)

                let code, text = auth (AuthCommand.Login("openai", None))
                Expect.equal code 0 text
                Expect.equal (List.ofSeq flows) [ "paste" ] "the paste flow ran"
                Expect.stringContains text "Stored the api_key credential for openai in the file store" "stored"
                Expect.isTrue (Directory.Exists(Path.Combine(p.Data, "credentials"))) "in the data directory"

                let code, text = auth AuthCommand.List
                Expect.equal code 0 text
                Expect.stringContains text "openai" "listed"

                let code, text = auth AuthCommand.Status
                Expect.equal code 0 text
                Expect.stringContains text "Credential store: file" "the store"
                Expect.stringContains text "anthropic  env" "anthropic from its variable"
                Expect.stringContains text "openai     api_key" "openai from the store"

                let host = start opts

                try
                    Expect.isEmpty host.Warnings "every provider's methods check"

                    match
                        wait (
                            Eff.run
                                (Host.within host CancellationToken.None)
                                (AuthService.resolve host.Auth Fx.Ext.ModelOpenAI.Provider.def)
                        )
                    with
                    | Ok(resolved, kind) ->
                        Expect.equal kind "api_key" "the stored credential"

                        Expect.equal resolved.Headers [ "authorization", "Bearer sk-test-openai" ] "what the wire gets"
                    | Error e -> failtest (string e)

                    match waitFor 60.0 (Commands.auth opts paste AuthCommand.List) with
                    | Ok _ -> failtest "fx auth ran beside a running host"
                    | Error errors -> Expect.stringContains (String.concat "\n" errors) "a host is running" "refused"
                finally
                    stop host

                let code, text = auth (AuthCommand.Logout "openai")
                Expect.equal code 0 text
                Expect.stringContains text "Removed openai" "removed"

                let _, text = auth AuthCommand.Status
                Expect.isFalse (text.Contains "openai     api_key") "not from the store again"

            testCase "a session in an embedded environment runs its turns, and a restarted host continues it"
            <| fun () ->
                let p = place "session"
                hostConfig p withoutKernel
                let lines = ResizeArray<string>()
                let host = start (options p (Model()) [ "model.role.main", Json.String modelId ])

                let first =
                    try
                        run
                            host
                            {
                                Prompt = "Make the greeting say hello"
                                Session = None
                                Title = Some "greeting"
                            }
                            lines
                    finally
                        stop host

                Expect.equal first.Turn 1 "the first turn"

                match first.Ending with
                | TurnEnd.Failed reason -> Expect.stringContains reason "no interpreter \"bun-ts\" is in effect" "why"
                | other -> failtest (string other)

                Expect.equal (Headless.exitCode first) 1 "exit code"

                // The session's environment: fx.env.local, in this process, on the workspace.
                let host = start (options p (Model()) [])

                try
                    match HostSessions.entries host with
                    | [ entry ] ->
                        Expect.equal entry.Session first.Session "indexed"
                        Expect.equal entry.Workspace p.Workspace "workspace"
                        Expect.equal entry.Title (Some "greeting") "title"

                        let record =
                            match wait (host.Env.Envs.Get entry.Environment CancellationToken.None) with
                            | Some record -> record
                            | None -> failtest "no environment"

                        Expect.equal record.Spec.Provider "local" "local provider"
                        Expect.equal record.Transport (Some Fx.Sdk.TransportKind.InProcess) "embedded"
                    | other -> failtest $"{other}"

                    let state = wait (HostSessions.state host first.Session CancellationToken.None)
                    Expect.equal state.Turns 1 "one turn so far"

                    // The session keeps the profile it was bound to: the model chosen on the first run.
                    let second =
                        run
                            host
                            {
                                Prompt = "Again"
                                Session = Some first.Session
                                Title = None
                            }
                            lines

                    Expect.equal second.Turn 2 "the second turn"
                    Expect.equal second.Ending first.Ending "the same failure"

                    match
                        waitFor
                            60.0
                            (Headless.run
                                host
                                {
                                    Prompt = "x"
                                    Session = Some "nope"
                                    Title = None
                                }
                                (quietIo lines)
                                CancellationToken.None)
                    with
                    | Error e -> Expect.stringContains e "no session nope" "unknown session"
                    | Ok outcome -> failtest (string outcome)
                finally
                    stop host

            testCase "sessions' environments get the host's sandbox; fx run warns once when it is none"
            <| fun () ->
                let sandboxOf (host: Host) (session: string) =
                    match HostSessions.entries host |> List.tryFind (fun e -> e.Session = session) with
                    | Some entry ->
                        match wait (host.Env.Envs.Get entry.Environment CancellationToken.None) with
                        | Some record -> record.Sandbox |> Option.defaultWith (fun () -> failtest "no sandbox recorded")
                        | None -> failtest "no environment"
                    | None -> failtest "not indexed"

                let warnings (lines: ResizeArray<string>) =
                    lines |> Seq.filter (fun l -> l.StartsWith "warning: ") |> List.ofSeq

                let request =
                    {
                        Prompt = "Look around"
                        Session = None
                        Title = None
                    }

                // The host turns sandboxing off: the environment runs unsandboxed, and fx run says so once.
                let p = place "unsandboxed"
                hostConfig p withoutKernel
                let lines = ResizeArray<string>()

                let host =
                    start (
                        options
                            p
                            (Model())
                            [ "model.role.main", Json.String modelId; "sandbox.driver", Json.String "none" ]
                    )

                try
                    let outcome = run host request lines
                    let sandbox = sandboxOf host outcome.Session
                    Expect.equal (sandbox.Driver, sandbox.Confines) ("none", false) "none"

                    match warnings lines with
                    | [ warning ] ->
                        Expect.stringContains warning "runs unsandboxed" "the warning"
                        Expect.stringContains warning "sandbox.driver = none" "why"
                    | other -> failtest $"one warning: {other}"
                finally
                    stop host

                // The host's default: the OS's driver where it confines (bubblewrap, Seatbelt), else
                // none with a warning saying why (Windows).
                let p = place "sandboxed"
                hostConfig p withoutKernel
                let lines = ResizeArray<string>()
                let host = start (options p (Model()) [ "model.role.main", Json.String modelId ])

                try
                    let outcome = run host request lines
                    let sandbox = sandboxOf host outcome.Session

                    if sandbox.Confines then
                        Expect.isEmpty (warnings lines) "no warning"
                        Expect.contains [ "bubblewrap"; "seatbelt" ] sandbox.Driver "the OS's driver"
                    else
                        if
                            Environment.GetEnvironmentVariable "SANDBOX_TESTS" = "1"
                            && not (OperatingSystem.IsWindows())
                        then
                            failtest $"a driver must confine here: {sandbox}"

                        Expect.equal (warnings lines).Length 1 "the unsandboxed warning, once"

                        if OperatingSystem.IsWindows() then
                            Expect.stringContains (warnings lines).Head "Windows" "Windows says why"
                finally
                    stop host
        ]
