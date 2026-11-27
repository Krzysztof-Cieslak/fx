/// An environment's chosen variables on the host (T138): where a spec came from, what a project's
/// credential asks, and what the resolver passes, asks for and leaves out.
module Fx.Host.Tests.EnvConsentTests

open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Effects
open Fx.Sdk
open Fx.Variants
open Fx.Grants
open Fx.Environments
open Fx.Host

let private file (layer: Layer) (environments: EnvSpec list) : ConfigFile =
    {
        Origin = string layer
        Variants = Selections.create (Source.create layer (string layer)) []
        Environments = environments
        Disabled = []
    }

let private config (host: EnvSpec list) (project: EnvSpec list option) : HostConfig =
    {
        ProjectTrusted = project.IsSome
        Host = file Layer.Host host
        Project = project |> Option.map (file Layer.Project)
        Policies = []
        Explicit = Selections.create (Source.create Layer.Explicit "explicit") []
    }

let private spec (variables: (string * string) list) : EnvSpec =
    { EnvSpec.create "default" "local" with
        Variables = Map.ofList variables
    }

let private machine (sameHost: bool) : EnvMachine =
    {
        Target = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)
        DataDir = "/data"
        SameHost = sameHost
    }

let private ask (attempt: int) (spec: EnvSpec) : EnvVariablesAsk =
    {
        Id = "env-1"
        Attempt = attempt
        Spec = spec
        Machine = machine true
    }

let private token = "ghp_0123456789abcdef"

/// A grants capability that answers every ask from `answer` and keeps what was asked.
let private grants (answer: GrantAsk -> Resolution) (asked: ResizeArray<GrantAsk>) : Grants =
    {
        Check = fun _ _ -> failwith "unused"
        Request =
            fun ask _ ->
                lock asked (fun () -> asked.Add ask)
                Task.FromResult(Ok(AskOutcome.Settled(answer ask)))
        Await = fun _ _ -> failwith "unused"
        Withdraw = fun _ _ _ -> failwith "unused"
    }

let private secrets: Secrets =
    {
        Resolve =
            fun reference _ ->
                Task.FromResult(
                    if reference.Name = "npm" then
                        Ok(Secret(reference, "npm_0123456789"))
                    else
                        Error(SecretNotFound reference)
                )
    }

let private settings (person: (string * string) list) (config: HostConfig) (grants: Grants) : EnvVariableSettings =
    {
        Person = Map.ofList person
        Deny = []
        HostVariables = [ "GH_TOKEN", token; "SHORT_TOKEN", "abc"; "HOME", "/home/u" ]
        Secrets = secrets
        Grants = grants
        Repo = "/work/project"
        Config = config
    }

let private resolve (settings: EnvVariableSettings) (ask: EnvVariablesAsk) : ResolvedVariables =
    (EnvVariableConsent.resolver settings ask CancellationToken.None).Result

let private outcomes (resolved: ResolvedVariables) =
    resolved.Record |> List.map (fun v -> v.Name, v.Outcome)

[<Tests>]
let tests =
    testList
        "Environment variables"
        [
            test "a spec from host config is the person's; one a project defines is the project's" {
                let hostSpec = spec [ "GH_TOKEN", "host:GH_TOKEN" ]

                Expect.equal
                    (EnvVariableConsent.origin (config [ hostSpec ] None) hostSpec)
                    VariableOrigin.HostSpec
                    "host"

                Expect.equal
                    (EnvVariableConsent.origin (config [ hostSpec ] (Some [ hostSpec ])) hostSpec)
                    VariableOrigin.ProjectSpec
                    "the project defines it too"

                Expect.equal
                    (EnvVariableConsent.origin (config [] None) hostSpec)
                    VariableOrigin.ProjectSpec
                    "recorded variables no host file holds are a project's"
            }

            test "a project's credential asks for this start and this project, never the host" {
                let request =
                    EnvVariables.requests
                        Map.empty
                        (Map.ofList [ "GH_TOKEN", "host:GH_TOKEN" ])
                        VariableOrigin.ProjectSpec
                        []
                    |> fst
                    |> List.head

                let grantAsk =
                    EnvVariableConsent.grantAsk "/work/project" (ask 3 (spec [])) request
                    |> Option.get

                Expect.equal grantAsk.Key "env.credential/env-1/3/GH_TOKEN" "key"
                Expect.equal grantAsk.Action.Value "env.credential" "action"
                Expect.equal grantAsk.Resource "GH_TOKEN" "resource"

                match GrantLog.request Actions.builtIn grantAsk (RequestId "q1") 1L with
                | Ok made ->
                    Expect.equal made.Offer [ ScopeKind.Once; ScopeKind.Repo ] "offer"

                    Expect.equal
                        (GrantsCommand.allowing made)
                        (Some "fx grants allow env.credential GH_TOKEN --scope repo")
                        "headless names the command"

                    Expect.stringContains (GrantPrompts.question made) "[r] always for this project" "terminal"
                    Expect.equal (GrantPrompts.parse made "r").Scope (ScopeChoice.Repo None) "r"
                    Expect.equal (GrantPrompts.parse made "a").Effect GrantEffect.Deny "no host answer"
                | Error e -> failtest (string e)
            }

            test "a repository grant covers that project only" {
                let grant: Grant =
                    {
                        Id = GrantId "g1"
                        Action = ActionName.create "env.credential"
                        Resource = "GH_TOKEN"
                        Effect = GrantEffect.Allow
                        Scope = GrantScope.Repo { Repo = "/work/project"; Path = None }
                        By = "user:alice"
                        At = 1L
                        Expires = None
                        Reason = None
                        Origin = GrantOrigin.Direct "cli"
                    }

                let context repo =
                    { GrantContext.none with
                        Repo = Some { Repo = repo; Path = "" }
                    }

                Expect.isTrue (Rules.covers grant (context "/work/project")) "this project"
                Expect.isFalse (Rules.covers grant (context "/work/other")) "another"
                Expect.isFalse (Rules.covers grant GrantContext.none) "no project"
            }

            test "the person's variables pass without asking; plain ones too" {
                let asked = ResizeArray()

                let resolved =
                    resolve
                        (settings
                            [ "GH_TOKEN", "host:GH_TOKEN" ]
                            (config [] (Some [ spec [ "RUST_LOG", "debug" ] ]))
                            (grants (fun _ -> Resolution.Denied [ "no" ]) asked))
                        (ask 1 (spec [ "RUST_LOG", "debug" ]))

                Expect.isEmpty asked "nothing asked"
                Expect.equal resolved.Values [ "GH_TOKEN", token; "RUST_LOG", "debug" ] "values"
                Expect.equal resolved.Secrets [ "GH_TOKEN", token ] "only the credential is redacted"
            }

            test "a project's credential asks once per start; allowed, it passes with the grant" {
                let asked = ResizeArray()
                let projectSpec = spec [ "GH_TOKEN", "host:GH_TOKEN"; "NPM_TOKEN", "secret:npm" ]

                let settings =
                    settings
                        []
                        (config [] (Some [ projectSpec ]))
                        (grants (fun _ -> Resolution.Allowed(GrantId "g7")) asked)

                let first = resolve settings (ask 1 projectSpec)
                let second = resolve settings (ask 2 projectSpec)

                Expect.equal
                    (asked |> Seq.map _.Key |> List.ofSeq)
                    [
                        "env.credential/env-1/1/GH_TOKEN"
                        "secret.read/env-1/1/NPM_TOKEN"
                        "env.credential/env-1/2/GH_TOKEN"
                        "secret.read/env-1/2/NPM_TOKEN"
                    ]
                    "a key per start"

                Expect.equal
                    (outcomes first)
                    [
                        "GH_TOKEN", VariableOutcome.Granted "g7"
                        "NPM_TOKEN", VariableOutcome.Granted "g7"
                    ]
                    "granted"

                Expect.equal first.Secrets [ "GH_TOKEN", token; "NPM_TOKEN", "npm_0123456789" ] "redacted"
                Expect.equal second.Values.Length 2 "again"
            }

            test "a denied or unanswered credential is left out, and the record says why" {
                let asked = ResizeArray()
                let projectSpec = spec [ "GH_TOKEN", "host:GH_TOKEN"; "NPM_TOKEN", "secret:npm" ]

                let answer (ask: GrantAsk) =
                    if ask.Resource = "GH_TOKEN" then
                        Resolution.Denied [ "denied at the terminal" ]
                    else
                        Resolution.Cancelled "nobody could answer: not run from a terminal"

                let resolved =
                    resolve (settings [] (config [] (Some [ projectSpec ])) (grants answer asked)) (ask 1 projectSpec)

                Expect.isEmpty resolved.Values "nothing passed"

                Expect.equal
                    (outcomes resolved)
                    [
                        "GH_TOKEN", VariableOutcome.Denied "denied at the terminal"
                        "NPM_TOKEN", VariableOutcome.Denied "nobody could answer: not run from a terminal"
                    ]
                    "record"
            }

            test "a variable without a value, or too short to redact, is left out without asking" {
                let asked = ResizeArray()

                let projectSpec =
                    spec [ "MISSING_TOKEN", "host:NOPE_TOKEN"; "SHORT_TOKEN", "host:SHORT_TOKEN" ]

                let resolved =
                    resolve
                        (settings
                            [ "SHORT_TOKEN", "host:SHORT_TOKEN" ]
                            (config [] (Some [ projectSpec ]))
                            (grants (fun _ -> Resolution.Allowed(GrantId "g")) asked))
                        (ask 1 projectSpec)

                Expect.isEmpty asked "nothing asked"
                Expect.isEmpty resolved.Values "nothing passed"

                match outcomes resolved with
                | [ "SHORT_TOKEN", VariableOutcome.Unavailable short
                    "MISSING_TOKEN", VariableOutcome.Unavailable missing ] ->
                    Expect.stringContains short "shorter than 8" "short"
                    Expect.stringContains missing "NOPE_TOKEN" "missing"
                | other -> failtest $"{other}"
            }

            test "environments on another machine get no variables yet" {
                let resolved =
                    resolve
                        (settings
                            [ "GH_TOKEN", "host:GH_TOKEN" ]
                            (config [] None)
                            (grants (fun _ -> failwith "asked") (ResizeArray())))
                        { ask 1 (spec []) with
                            Machine = machine false
                        }

                Expect.isEmpty resolved.Values "values"
                Expect.equal resolved.Record.Length 1 "recorded"
            }
        ]
