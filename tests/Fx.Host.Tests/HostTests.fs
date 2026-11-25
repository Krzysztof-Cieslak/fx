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
        ]
