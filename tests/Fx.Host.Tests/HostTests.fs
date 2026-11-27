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
open Fx.Grants.Host
open Fx.Sessions
open Fx.CodeAct
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

/// Downloads that fail: an install that gets past its approval stops at the download (no network).
let private noDownloads: Fx.Components.Fetch =
    {
        Download = fun request _ -> Task.FromResult(Error(Fx.Components.FetchError.HttpStatus(request.Url, 404)))
    }

/// A run whose person answers grant requests with `answer` (`None`: nobody can, no terminal), keeping
/// what was asked and shown.
type private Terminal(answer: GrantAnswer option) =
    let asked = ResizeArray<GrantRequest>()
    let lines = ResizeArray<string>()

    member _.Asked = lock asked (fun () -> List.ofSeq asked)
    member _.Lines = lock lines (fun () -> List.ofSeq lines)

    member _.Io: RunIo =
        {
            Show = fun line -> lock lines (fun () -> lines.Add line)
            Answer =
                fun request ->
                    lock asked (fun () -> asked.Add request)
                    Task.FromResult answer
            Decide = fun card -> failtest $"no decision should be asked for: {card}"
            Interrupt = CancellationToken.None
        }

let private reply (effect: GrantEffect) : GrantAnswer option =
    Some
        {
            Effect = effect
            Scope = ScopeChoice.Once
            Resource = None
            Expires = None
            Reason = Some "at the terminal"
        }

/// One `fx run -p` in its own host process: the error it ended with (these runs never reach a turn).
let private failedRun (options: HostOptions) (terminal: Terminal) : string =
    let host = start options

    try
        match
            waitFor
                120.0
                (Headless.run
                    host
                    {
                        Prompt = "Install what you need"
                        Session = None
                        Title = None
                    }
                    terminal.Io
                    CancellationToken.None)
        with
        | Error e -> e
        | Ok outcome -> failtest $"the run ended {outcome}"
    finally
        stop host

let private grantsCommand (options: HostOptions) (command: GrantsCommand) : string list =
    match waitFor 60.0 (Commands.grants options command) with
    | Ok lines -> lines
    | Error errors -> failtest (String.concat "\n" errors)

/// What the report says about fx.exthost.bun, unless it activated.
let private exthostReport (host: Host) : string list =
    host.Extensions.Report
    |> List.filter (fun entry -> entry.Id = "fx.exthost.bun")
    |> List.map string
    |> List.filter (fun line -> not (line.EndsWith "activated"))

/// The options with a package on the `bun` runtime from host config (its directory is empty: it needs
/// the Bun extension host before anything is read from it).
let private withBunPackage (p: Place) (opts: HostOptions) : HostOptions =
    let dir = Path.Combine(p.Data, "..", "hello")
    Directory.CreateDirectory dir |> ignore

    { opts with
        Extensions =
            [
                Fx.Extensions.Candidate.create
                    Fx.Extensions.Source.HostConfig
                    (Fx.Extensions.Package.Manifest(Fx.Sdk.Manifest.create "tests.hello" "1.0.0" "bun", Some dir))
            ]
    }

[<Tests>]
let tests =
    testList
        "Host"
        [
            testCase
                "an install nobody can answer is withdrawn, never a person's denial: the next run asks again, in either order"
            <| fun () ->
                let p = place "headless-install"

                let opts =
                    { options p (Model()) [ "model.role.main", Json.String modelId ] with
                        Fetch = noDownloads
                    }

                let installs (terminal: Terminal) =
                    terminal.Asked
                    |> List.filter (fun r -> r.Action.Value = "component.install")
                    |> List.map (fun r -> r.Resource)

                // Headless first: the request is withdrawn by the host, saying how to allow it beforehand.
                let nobody = Terminal None
                let e = failedRun opts nobody
                Expect.stringContains e "not approved" "the install did not happen"
                Expect.stringContains e "nobody could answer" "why"
                Expect.isFalse (e.Contains "denied by") "nobody denied it"

                let bun =
                    match installs nobody with
                    | [ resource ] -> resource
                    | other -> failtest $"asked {other}"

                Expect.stringStarts bun "bun@" "Bun"

                Expect.isTrue
                    (nobody.Lines
                     |> List.exists (fun l ->
                         l.Contains "needs a person's approval"
                         && l.Contains $"`{Fx.Product.binaryName} grants allow component.install {bun}`"))
                    $"the command to run beforehand: {nobody.Lines}"

                // Then from a terminal: a person is asked again, and denies once.
                let person = Terminal(reply GrantEffect.Deny)
                let e = failedRun opts person
                Expect.equal (installs person) [ bun ] "asked again"
                Expect.stringContains e "denied by user:" "the person's denial"

                // Once means once: the next run asks again, and the person allows it this time.
                let person = Terminal(reply GrantEffect.Allow)
                let e = failedRun opts person
                Expect.equal (installs person) [ bun ] "asked again after a denial once"
                Expect.stringContains e "download failed" "approved: the install went on to its download"

                // A terminal first, headless next: the allowance was for that attempt only.
                let nobody = Terminal None
                let e = failedRun opts nobody
                Expect.equal (installs nobody) [ bun ] "asked, nobody answered"
                Expect.stringContains e "nobody could answer" "withdrawn again"

                // The grants log records a person's answers only: the allowance once, spent; nothing for
                // the requests nobody answered.
                Expect.equal (grantsCommand opts (GrantsCommand.List false)) [ "no grants" ] "none in force"

                match grantsCommand opts (GrantsCommand.List true) with
                | [ line ] ->
                    Expect.stringContains line $"allow component.install {bun}  (once:" line
                    Expect.stringContains line "[spent]" line
                | other -> failtest $"{other}"

                // The person allows it beforehand: a headless run installs without asking anyone.
                let made =
                    grantsCommand
                        opts
                        (GrantsCommand.Grant(GrantEffect.Allow, "component.install", bun, GrantsScope.Host, None))

                let id =
                    match made with
                    | [ line ] ->
                        Expect.stringContains line $"allow component.install {bun}  (host) by user:local-os-user:" line
                        line.Split(' ').[0]
                    | other -> failtest $"{other}"

                Expect.equal (grantsCommand opts (GrantsCommand.List false)) made "listed"

                let nobody = Terminal None
                let e = failedRun opts nobody
                Expect.isEmpty nobody.Asked "nobody asked"
                Expect.stringContains e "download failed" "approved by the grant"

                Expect.equal (grantsCommand opts (GrantsCommand.Revoke(id, Some "done"))) [ $"revoked {id}" ] "revoked"

                Expect.equal (grantsCommand opts (GrantsCommand.List false)) [ "no grants" ] "none in force"

                match
                    grantsCommand opts (GrantsCommand.List true)
                    |> List.filter (fun l -> l.StartsWith id)
                with
                | [ line ] -> Expect.stringContains line "[revoked" "kept, revoked"
                | other -> failtest $"{other}"

            testCase "a request left pending by a host that stopped while it asked is put to the next run's person"
            <| fun () ->
                let p = place "left-pending"
                hostConfig p withoutKernel
                let opts = options p (Model()) [ "model.role.main", Json.String modelId ]
                let host = start opts

                let left =
                    try
                        let ask: GrantAsk =
                            {
                                Key = "test/left-by-a-crash"
                                Action = ActionName.create "component.install"
                                Resource = "tool@1.0.0"
                                Context = GrantContext.none
                                Requester =
                                    {
                                        Principal = "system"
                                        Agent = None
                                        Extension = None
                                    }
                                Summary = "Install tool 1.0.0"
                                Evidence = []
                                Suggestions = []
                                Scopes = []
                                Expires = None
                            }

                        match
                            wait ((Fx.Grants.Host.GrantsService.grants host.Grants).Request ask CancellationToken.None)
                        with
                        | Ok(AskOutcome.Pending id) -> id
                        | other -> failtest $"{other}"
                    finally
                        stop host

                let nobody = Terminal None
                let host = start opts

                try
                    match
                        waitFor
                            120.0
                            (Headless.run
                                host
                                {
                                    Prompt = "Look around"
                                    Session = None
                                    Title = None
                                }
                                nobody.Io
                                CancellationToken.None)
                    with
                    | Ok _ -> ()
                    | Error e -> failtest e

                    Expect.equal (nobody.Asked |> List.map _.Id) [ left ] "put to the person"

                    Expect.isTrue
                        (nobody.Lines
                         |> List.exists (fun l -> l.Contains "Install tool 1.0.0" && l.Contains "nobody can answer"))
                        $"{nobody.Lines}"

                    Expect.isEmpty (Fx.Grants.Host.GrantsService.pending host.Grants).Requests "withdrawn"
                finally
                    stop host

            testCase "fx grants refuses what the grants log does not take, and waits for a running host"
            <| fun () ->
                let p = place "grants-command"
                let opts = options p (Model()) []

                let refused (command: GrantsCommand) =
                    match waitFor 60.0 (Commands.grants opts command) with
                    | Ok lines -> failtest $"granted: {lines}"
                    | Error errors -> String.concat "\n" errors

                Expect.stringContains
                    (refused (GrantsCommand.Grant(GrantEffect.Allow, "install", "bun@*", GrantsScope.Host, None)))
                    "not an action name"
                    "a bad name"

                Expect.stringContains
                    (refused (GrantsCommand.Grant(GrantEffect.Allow, "nothing.known", "x", GrantsScope.Host, None)))
                    "nothing.known"
                    "an unknown action"

                Expect.stringContains
                    (refused (
                        GrantsCommand.Grant(GrantEffect.Allow, "shell.exec", "ls", GrantsScope.Session "nope", None)
                    ))
                    "no session nope"
                    "an unknown session"

                Expect.stringContains (refused (GrantsCommand.Revoke("g-unknown", None))) "g-unknown" "an unknown grant"

                let host = start opts

                try
                    Expect.stringContains (refused (GrantsCommand.List false)) "a host is running" "one writer"
                finally
                    stop host

            testCase "fx run checks the model and its credentials before the session's environment installs anything"
            <| fun () ->
                let p = place "no-credentials"

                let opts =
                    { options p (Model()) [ "model.role.main", Json.String modelId ] with
                        Fetch = noDownloads
                        HostVariables = Fx.Effects.Fakes.InMemoryVariables.create []
                    }

                let nobody = Terminal None
                let e = failedRun opts nobody
                Expect.stringContains e "the model call would fail: unavailable: anthropic: no credentials" e
                Expect.stringContains e "ANTHROPIC_API_KEY" "how to provide them"
                Expect.isEmpty nobody.Asked "nothing to install was asked for"
                Expect.isEmpty nobody.Lines "no session was made"

                let host = start opts

                try
                    Expect.isEmpty (HostSessions.entries host) "no session"
                    Expect.isEmpty (Fx.Grants.Host.GrantsService.pending host.Grants).Requests "no request"
                finally
                    stop host

                let unset = { opts with Explicit = [] }
                let e = failedRun unset (Terminal None)
                Expect.equal e "no model is configured for the main role (model.role.main)" "no model"

            testCase
                "fx.exthost.bun: its files are written with hash checks; a Bun nobody approved is not asked for, and the warning says how"
            <| fun () ->
                let p = place "exthost-unapproved"

                let opts =
                    { options p (Model()) [] with
                        Fetch = noDownloads
                    }

                let main =
                    Path.Combine(opts.Paths.Extensions, "fx.exthost.bun", "packages", "exthost-bun", "src", "main.ts")

                let expected =
                    Fx.Ext.ExthostBun.ExthostBun.sources
                    |> List.find (fun file -> file.Name = Fx.Ext.ExthostBun.ExthostBun.Main)

                let host = start opts

                try
                    Expect.equal (File.ReadAllText main) expected.Content "written from the binary"

                    Expect.isTrue
                        (File.Exists(Path.Combine(opts.Paths.Extensions, "fx.exthost.bun", "config", "bunfig.toml")))
                        "the runtime's files too"

                    // Nothing runs on `bun`: no warning, only the report says why.
                    Expect.isEmpty host.Warnings "no warnings"

                    match exthostReport host with
                    | [ line ] ->
                        Expect.stringContains line "[components] failed" line
                        Expect.stringContains line "needs a person's approval" line
                    | other -> failtest $"{other}"

                    Expect.isFalse
                        (Fx.Extensions.Registry.active host.Extensions.State
                         |> List.contains "fx.exthost.bun")
                        "not active"

                    Expect.contains
                        (Fx.Extensions.Registry.active host.Extensions.State)
                        "fx.interpreter.bun-ts"
                        "the rest starts"
                finally
                    stop host

                // A file changed on disk is written again. With a package on `bun`, the warning says how.
                File.WriteAllText(main, "// changed")
                let host = start (withBunPackage p opts)

                try
                    Expect.equal (File.ReadAllText main) expected.Content "written again"

                    match host.Warnings |> List.filter (fun w -> w.Contains "fx.exthost.bun") with
                    | [ warning ] ->
                        Expect.stringContains warning "needs a person's approval" warning

                        Expect.stringContains
                            warning
                            $"`{Product.binaryName} grants allow component.install bun@*`"
                            warning
                    | other -> failtest $"{other}"

                    Expect.exists host.Warnings (fun w -> w.StartsWith "tests.hello") "the package's own failure"
                finally
                    stop host

            testCase "fx.exthost.bun: a host grant of the component approves its install without asking anyone"
            <| fun () ->
                let p = place "exthost-granted"

                let opts =
                    { options p (Model()) [] with
                        Fetch = noDownloads
                    }

                grantsCommand
                    opts
                    (GrantsCommand.Grant(GrantEffect.Allow, "component.install", "bun@*", GrantsScope.Host, None))
                |> ignore

                let host = start opts

                try
                    match exthostReport host with
                    | [ warning ] ->
                        // Approved by the grant: the (fake) download is what fails.
                        Expect.stringContains warning "download failed" warning
                        Expect.isFalse (warning.Contains "approval") warning
                    | other -> failtest $"{other}"
                finally
                    stop host

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

                    // fx.exthost.bun's Bun is not approved, but nothing runs on `bun`: no warning.
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

            testCase
                "auto approvals: a trusted project cannot turn them on; the host can, unless its policy forbids them"
            <| fun () ->
                let p = place "approval-modes"

                p.Write(
                    Path.Combine(Product.projectDirName, "config.json"),
                    """{"variants": {"codeact.grants.mode": "auto"}}"""
                )

                let opts = options p (Model()) []

                match wait (Commands.trust opts p.Workspace) with
                | Ok _ -> ()
                | Error e -> failtest e

                // A cloned repository cannot stop the guard asking: the mode is privileged.
                match waitFor 180.0 (Host.start opts) with
                | Error issues ->
                    Expect.isTrue
                        (issues
                         |> List.exists (fun i -> i.Contains "codeact.grants.mode" && i.Contains "privileged"))
                        $"{issues}"
                | Ok h ->
                    stop h
                    failtest "a project turned auto approvals on"

                p.Write(Path.Combine(Product.projectDirName, "config.json"), "{}")

                // The person running the host can (host config, or the command line).
                hostConfig p """{"variants": {"codeact.grants.mode": "auto"}}"""
                let h = start (options p (Model()) [])

                try
                    Expect.equal
                        (Fx.Variants.Resolution.value GrantSlots.mode h.Resolution)
                        GrantMode.Auto
                        "the host's mode"
                finally
                    stop h

                // Policy forbids it: the host does not start in auto, and its grants policy denies
                // `approvals.auto` (no person turns it on: ProtocolTests).
                File.WriteAllText(
                    Path.Combine(p.Data, "policy.json"),
                    """{"forbid": {"codeact.grants.mode": ["auto"]}}"""
                )

                match waitFor 180.0 (Host.start (options p (Model()) [])) with
                | Error issues ->
                    Expect.isTrue
                        (issues
                         |> List.exists (fun i -> i.Contains "codeact.grants.mode" && i.Contains "forbidden"))
                        $"{issues}"
                | Ok h ->
                    stop h
                    failtest "started in auto under a policy that forbids it"

                hostConfig p "{}"
                let h = start (options p (Model()) [])

                try
                    let query: GrantQuery =
                        {
                            Action = AutoApprovals.action
                            Resource = "bubblewrap"
                            Context =
                                {
                                    Session = Some "s1"
                                    Run = None
                                    Repo = None
                                }
                        }

                    match wait (Eff.run h.Env (GrantsService.check h.Grants query)) with
                    | Ok(GrantCheck.Denied reasons) ->
                        Expect.isTrue
                            (reasons |> List.exists (fun r -> r.Contains "does not allow auto approvals"))
                            $"{reasons}"
                    | other -> failtest $"{other}"
                finally
                    stop h

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

            testCase
                "the sandbox is opt-in: by default fx run says sandbox: off, without a warning; auto confines where a driver can and warns where none can"
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

                let environment (lines: ResizeArray<string>) =
                    match lines |> Seq.filter (fun l -> l.StartsWith "env: ") |> List.ofSeq with
                    | [ line ] -> line
                    | other -> failtest $"one environment line: {other}"

                let request =
                    {
                        Prompt = "Look around"
                        Session = None
                        Title = None
                    }

                // The default: no sandbox, and nothing to warn about; the environment's line says so.
                let p = place "unsandboxed"
                hostConfig p withoutKernel
                let lines = ResizeArray<string>()
                let host = start (options p (Model()) [ "model.role.main", Json.String modelId ])

                try
                    let outcome = run host request lines
                    let sandbox = sandboxOf host outcome.Session
                    Expect.equal (sandbox.Driver, sandbox.Confines, sandbox.Notes) ("none", false, []) "off"
                    Expect.isEmpty (warnings lines) "no warning"
                    Expect.equal (environment lines) "env: default (local) · sandbox: off" "the environment's line"
                finally
                    stop host

                // The host's config opts in with auto: the OS's driver where it confines (bubblewrap,
                // Seatbelt), else none with a warning saying why (Windows, or no bubblewrap here).
                let p = place "sandboxed"

                hostConfig
                    p
                    """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}, "variants": {"sandbox.driver": "auto"}}"""

                let lines = ResizeArray<string>()
                let host = start (options p (Model()) [ "model.role.main", Json.String modelId ])

                try
                    let outcome = run host request lines
                    let sandbox = sandboxOf host outcome.Session

                    if sandbox.Confines then
                        Expect.isEmpty (warnings lines) "no warning"
                        Expect.contains [ "bubblewrap"; "seatbelt" ] sandbox.Driver "the OS's driver"

                        Expect.equal
                            (environment lines)
                            $"env: default (local) · sandbox: {sandbox.Driver}"
                            "the environment's line names the driver"
                    else
                        if
                            Environment.GetEnvironmentVariable "SANDBOX_TESTS" = "1"
                            && not (OperatingSystem.IsWindows())
                        then
                            failtest $"a driver must confine here: {sandbox}"

                        Expect.stringEnds (environment lines) "sandbox: off" "off"

                        match warnings lines with
                        | [ warning ] ->
                            Expect.stringContains warning "runs unsandboxed" "the warning"
                            Expect.stringContains warning "the sandbox the host asked for cannot run here" "why"

                            if OperatingSystem.IsWindows() then
                                Expect.stringContains warning "Windows" "Windows says why"
                        | other -> failtest $"the unsandboxed warning, once: {other}"
                finally
                    stop host

                // The command line requires a driver that cannot run here: the run fails, never falls back.
                let elsewhere =
                    if OperatingSystem.IsMacOS() then
                        "bubblewrap"
                    else
                        "seatbelt"

                let p = place "required"
                hostConfig p withoutKernel
                let lines = ResizeArray<string>()

                let host =
                    start (
                        options
                            p
                            (Model())
                            [
                                "model.role.main", Json.String modelId
                                "sandbox.driver", Json.String elsewhere
                            ]
                    )

                try
                    match waitFor 120.0 (Headless.run host request (quietIo lines) CancellationToken.None) with
                    | Error e -> Expect.stringContains e elsewhere "the environment failed, saying why"
                    | Ok outcome -> failtest $"ran unsandboxed: {outcome}"
                finally
                    stop host

            testCase "a project cannot turn the sandbox on (or off): sandbox.driver is privileged"
            <| fun () ->
                let p = place "project-sandbox"
                hostConfig p withoutKernel

                p.Write(
                    Path.Combine(Product.projectDirName, "config.json"),
                    """{"variants": {"sandbox.driver": "auto"}}"""
                )

                let opts = options p (Model()) [ "model.role.main", Json.String modelId ]

                match wait (Commands.trust opts p.Workspace) with
                | Ok _ -> ()
                | Error e -> failtest e

                match wait (Host.start opts) with
                | Error errors ->
                    let text = String.concat "\n" errors
                    Expect.stringContains text "sandbox.driver" "the slot"
                    Expect.stringContains text "privileged" "why"
                | Ok host ->
                    stop host
                    failtest "a trusted project's config selected a privileged slot"
        ]
