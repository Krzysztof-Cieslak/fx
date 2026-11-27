/// The CodeAct grants guard (T127b, docs/modules/codeact.md "Grants"): the sh and PowerShell command
/// parsers, host calls as grants, and the guard over the grants service on the fake clock (a listed
/// command, a request answered and denied, a call's requests at once, mode deny, a cancelled wait;
/// T134's auto approvals: on with a confining driver, a person's switch, policy, no driver).
module Fx.Aot.Scenarios.CodeActGrants

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Kernel
open Fx.Sdk
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Grants
open Fx.Grants.Host
open Fx.CodeAct

let private render (line: CommandLine) : string =
    match line with
    | CommandLine.Opaque reason -> "opaque: " + reason
    | CommandLine.Parsed commands ->
        commands
        |> List.map (fun command ->
            String.Join(
                " ",
                command.Words
                |> List.map (fun w -> if w.Expands then "<" + w.Text + ">" else w.Text)
            )
            + (command.Writes |> List.map (fun w -> " > " + w.Text) |> String.concat "")
            + (command.Reads |> List.map (fun w -> " < " + w.Text) |> String.concat ""))
        |> String.concat " ; "
        |> fun text -> "parsed: " + text

/// Command lines split as sh and PowerShell 7 would split them, or given up on.
let syntax =
    Scenario.lines "codeact-shell-syntax" (fun () ->
        let sh =
            [
                "git status && npm test || echo failed"
                "cat a.txt | grep -n \"x y\" > out.txt 2>&1 < in.txt"
                "echo $(git rev-parse HEAD) `date` \"$HOME\" ~/x {a,b}"
                "(cd src && make) ; { a; b; }"
                "FOO=1 make all >/dev/null"
                "for f in *; do rm $f; done"
                "cat <<EOF\nx\nEOF"
                "diff <(ls a) <(ls b)"
                "echo 'unclosed"
            ]
            |> List.map (fun line ->
                "sh "
                + Json.quote line
                + " -> "
                + render (ShellSyntax.parse ShellDialect.Sh line))

        let ps =
            [
                "dotnet build; dotnet test && echo ok"
                "Get-Content x | Select-String foo *> log.txt"
                "Write-Output $(Remove-Item x) (Get-Date) $env:TEMP 'it''s'"
                "& \"C:\\Program Files\\x.exe\" -a > $null"
                "ForEach-Object { Remove-Item $_ }"
                "[IO.File]::Delete('x')"
                "echo $x.Delete()"
            ]
            |> List.map (fun line ->
                "ps "
                + Json.quote line
                + " -> "
                + render (ShellSyntax.parse ShellDialect.PowerShell line))

        sh
        @ ps
        @ [
            "resource: "
            + string (ShellSyntax.resource [ { Text = "a\n b"; Expands = false } ])
        ])

let private call (cell: string) (binding: string) (args: Json list) : HostCall =
    {
        CellId = Some cell
        Binding = binding
        Args = args |> List.map Value.Inline
    }

let private showNeeds (needs: CallNeeds) : string =
    match needs with
    | CallNeeds.Refused reason -> "refused: " + reason
    | CallNeeds.Needs needs ->
        needs
        |> List.map (fun n ->
            n.Action.Value
            + " "
            + Json.quote n.Resource
            + (if n.Listed then " listed" else "")
            + (if n.InWorkspace then " in-workspace" else "")
            + " at "
            + Json.quote n.Place
            + " suggest "
            + String.Join(",", n.Suggestions))
        |> String.concat "; "
        |> fun text -> "needs [" + text + "]"

/// Host calls as grants on a Linux and a Windows workplace.
let needs =
    Scenario.lines "codeact-grant-needs" (fun () ->
        let unix = Workplace.create "/home/me/ws" Os.Linux
        let windows = Workplace.create "C:\\Users\\me\\ws" Os.Windows

        let cases =
            [
                unix, call "t1-s1-1" "shell.run" [ Json.String "ls -la && npm run test -- --watch" ]
                unix, call "t1-s1-1" "shell.run" [ Json.String "cat /etc/passwd > /tmp/x" ]
                unix, call "t1-s1-1" "shell.run" [ Json.String "cd /tmp && echo x > y" ]
                unix, call "t1-s1-1" "shell.run" [ Json.String "make"; Json.Object [ "cwd", Json.String "src/app" ] ]
                unix, call "t1-s1-1" "shell.run" [ Json.String "cd src && dotnet test 2>&1 | tail -n 5" ]
                unix, call "t1-s1-1" "shell.run" [ Json.String "env CI=1 timeout 60 curl -s https://example.com" ]
                unix, call "t1-s1-1" "shell.run" [ Json.String "git push origin main; bash -c 'rm -rf x'" ]
                unix, call "t1-s1-1" "fs.write" [ Json.String ".git/hooks/pre-commit"; Json.String "x" ]
                unix, call "t1-s1-1" "fs.write" [ Json.String "../../x"; Json.String "x" ]
                unix, call "t1-s1-1" "fs.read" [ Json.String "/etc/hosts" ]
                windows, call "t1-s1-1" "shell.run" [ Json.String "Get-Content .\\a.txt; dotnet test > D:\\out.txt" ]
                windows, call "t1-s1-1" "fs.write" [ Json.String "\\\\server\\share\\x"; Json.String "x" ]
            ]

        let fetch =
            Permissions.connects "net.fetch" (fun call ->
                match call.Args with
                | [ Value.Inline(Json.String url) ] -> Permissions.endpoint url |> Result.map List.singleton
                | _ -> Error "net.fetch takes a URL")

        [
            for place, c in cases do
                yield
                    string place.Shell
                    + " "
                    + c.Binding
                    + " -> "
                    + showNeeds (Permissions.needs Permissions.core GrantSettings.defaults place c)
            yield
                "net.fetch -> "
                + showNeeds (
                    Permissions.needs
                        (fetch :: Permissions.core)
                        GrantSettings.defaults
                        unix
                        (call "t1-s1-1" "net.fetch" [ Json.String "https://API.example.com/x" ])
                )
        ])

type private Env =
    {
        Clock: Clock
        Ids: Ids
        Trace: Trace
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

let private showResult (result: Result<Fx.Prompt.Approval list, HostCallError>) : string =
    match result with
    | Ok [] -> "allowed"
    | Ok approvals ->
        let shown =
            approvals
            |> List.map (fun a ->
                a.Action
                + " "
                + a.Resource
                + " by "
                + (match a.By with
                   | Fx.Prompt.ApprovedBy.Auto -> "auto"
                   | Fx.Prompt.ApprovedBy.Person _ -> "a person"))

        "allowed (" + String.Join(", ", shown) + ")"
    | Error e -> string e

/// The guard over the grants service on the fake clock. On a thread-pool thread (see grants-service).
let guard =
    Scenario.task "codeact-grant-guard" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Ids = FakeIds(17UL, clock.Clock).Ids
                        Trace = Trace.none
                        Scope = Scope.root
                    }

                let run (effect: Eff<Env, 'T>) : 'T = wait (Eff.run env effect)
                let alice = Attribution.create "user:alice" "tui"
                let out = Collections.Generic.List<string>()
                let store = InMemoryEventLog<Payload>()

                let service =
                    run (GrantsService.start (GrantsSettings.create (GrantStoreLog.hostLocal store.Log)))

                let turn: TurnStart =
                    {
                        Session = "s1"
                        Turn = 1
                        Profile = "p1"
                        Inputs =
                            [
                                {
                                    Id = 2L
                                    By = alice
                                    At = 0L
                                    Content = InputContent.Text "go"
                                }
                            ]
                        Seq = 3L
                    }

                let guardWith (settings: GrantSettings) =
                    GrantGuard.create<Env>
                        (GrantSetup.create (GrantsService.grants service) (SessionLog.typed store.Log) turn settings)
                        (Workplace.create "/home/me/ws" Os.Linux)

                let guard = guardWith GrantSettings.defaults

                let shell (cell: string) (line: string) =
                    call cell "shell.run" [ Json.String line ]

                let pendingNow () =
                    (GrantsService.pending service).Requests

                let until (count: int) =
                    let deadline = DateTime.UtcNow.AddSeconds 30.0

                    while (pendingNow ()).Length < count do
                        if DateTime.UtcNow > deadline then
                            failwith $"never {count} requests pending (30 s)"

                        Thread.Sleep 5

                    pendingNow ()

                let answer (request: GrantRequest) (effect: GrantEffect) =
                    let reply: GrantAnswer =
                        {
                            Effect = effect
                            Scope = ScopeChoice.Once
                            Resource = None
                            Expires = None
                            Reason = Some "decided"
                        }

                    run (GrantsService.answer service alice request.Id reply) |> ignore

                out.Add("listed: " + showResult (run (guard (shell "t1-s1-1" "ls -la"))))

                let waiting = Eff.run env (guard (shell "t1-s1-2" "mkdir made"))
                let asked = until 1

                for request in asked do
                    out.Add(
                        "asked: "
                        + request.Key
                        + " "
                        + request.Summary
                        + " suggest "
                        + String.Join(",", request.Suggestions)
                        + " evidence "
                        + String.Join(",", request.Evidence |> List.map string)
                    )

                answer asked.Head GrantEffect.Allow
                out.Add("answered: " + showResult (wait waiting))
                out.Add("again: " + showResult (run (guard (shell "t1-s1-2" "mkdir made"))))

                let both = Eff.run env (guard (shell "t1-s1-3" "mkdir a && mkdir b"))
                let two = until 2
                answer (two |> List.find (fun r -> r.Resource = "mkdir b")) GrantEffect.Deny
                out.Add("both: " + showResult (wait both))

                let deny =
                    guardWith
                        { GrantSettings.defaults with
                            Mode = GrantMode.Deny
                        }

                out.Add("mode deny: " + showResult (run (deny (shell "t1-s1-4" "mkdir c"))))

                // Auto approvals (T134): the host's mode runs commands written plainly in the workspace,
                // sandbox or not; in mode deny without auto nothing runs unasked.
                let autoWith (mode: GrantMode) (sandbox: string) =
                    GrantGuard.create<Env>
                        { GrantSetup.create
                              (GrantsService.grants service)
                              (SessionLog.typed store.Log)
                              turn
                              { GrantSettings.defaults with
                                  Mode = mode
                              } with
                            Sandbox = sandbox
                        }
                        (Workplace.create "/home/me/ws" Os.Linux)

                let auto = autoWith GrantMode.Auto "bubblewrap"

                out.Add(
                    "auto: "
                    + showResult (run (auto (shell "t1-s1-6" "mkdir e && ls && dotnet test")))
                )

                out.Add(
                    "auto, unsandboxed: "
                    + showResult (run ((autoWith GrantMode.Auto "none") (shell "t1-s1-6" "mkdir e2")))
                )

                out.Add(
                    "deny, unsandboxed: "
                    + showResult (run ((autoWith GrantMode.Deny "none") (shell "t1-s1-6" "mkdir e3")))
                )


                // A cancelled wait (before the person's switch below, which would run it unasked).
                use cancel = new CancellationTokenSource()

                let cancelled =
                    Eff.run
                        { env with
                            Scope =
                                { env.Scope with
                                    Cancellation = cancel.Token
                                }
                        }
                        (guard (shell "t1-s1-5" "mkdir d"))

                until 1 |> ignore
                cancel.Cancel()

                let outcome =
                    try
                        wait cancelled |> showResult
                    with :? OperationCanceledException ->
                        "cancelled"

                out.Add("interrupted: " + outcome)

                // A person's switch: a session grant of approvals.auto, in mode deny.
                let grant =
                    run (
                        GrantsService.grant
                            service
                            alice
                            {
                                Action = AutoApprovals.action
                                Resource = "*"
                                Effect = GrantEffect.Allow
                                Scope = GrantScope.Session "s1"
                                Expires = None
                                Reason = Some "auto"
                            }
                    )

                out.Add(
                    "switched on: "
                    + (match grant with
                       | Ok _ -> "granted"
                       | Error e -> string e)
                )

                out.Add(
                    "switched, mode deny: "
                    + showResult (run ((autoWith GrantMode.Deny "bubblewrap") (shell "t1-s1-7" "mkdir g")))
                )

                out.Add(
                    "switched, a download in mode deny: "
                    + showResult (run ((autoWith GrantMode.Deny "bubblewrap") (shell "t1-s1-8" "mkdir h; curl -O x")))
                )

                for mode, driver, check in
                    [
                        GrantMode.Auto, "bubblewrap", Ok(GrantCheck.MustAsk [ "no grant applies" ])
                        GrantMode.Auto, "none", Ok(GrantCheck.MustAsk [ "no grant applies" ])
                        GrantMode.Ask, "bubblewrap", Ok(GrantCheck.MustAsk [ "no grant applies" ])
                        GrantMode.Off, "seatbelt", Ok(GrantCheck.MustAsk [ "no grant applies" ])
                        GrantMode.Auto, "none", Ok(GrantCheck.Denied [ "policy says no" ])
                        GrantMode.Ask, "none", Ok(GrantCheck.Allowed(GrantId "g1"))
                        GrantMode.Auto, "seatbelt", Ok(GrantCheck.MustAsk [ "policy asks" ])
                    ] do
                    out.Add($"decide {mode} {driver} -> {AutoApprovals.decide mode driver check}")

                let policy: Fx.Variants.Policy =
                    {
                        Origin = "policy.json"
                        Pins = []
                        Forbids = [ "codeact.grants.mode", [ Json.String "auto" ] ]
                    }

                out.Add(
                    "policy -> "
                    + String.Join(", ", AutoApprovals.policyRules [ policy ] |> List.map string)
                )

                for entry in (GrantsService.snapshot service).State.Requests |> Map.toList |> List.map snd do
                    out.Add("request " + entry.Request.Resource + ": " + string entry.Status)

                run (GrantsService.stop service)
                List.ofSeq out)
        ))

let scenarios = [ syntax; needs; guard ]
