/// The conformance suite of the `runtime` contribution type (v1; spec §6.2, §18,
/// docs/modules/runtimes.md): what every runtime spec must hold to, whoever contributes it. Pure:
/// it plans the component for every target and launches it on paths made up for each target, with
/// a hostile launcher environment. A runtime's live behaviour (its flags, positive controls) is
/// tested by its own package, as `LiveTests` does for Bun.
module Fx.Ext.RuntimeBun.Tests.RuntimeConformance

open System
open Expecto
open Fx.Components
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// The launcher's environment: what a user's shell may hold, injection vectors and secrets
/// included, with Windows' spelling of some names.
let hostile: (string * string) list =
    [
        "PATH", "/usr/bin"
        "Path", @"C:\Windows\system32"
        "LANG", "C.UTF-8"
        "HOME", "/home/me"
        "USERPROFILE", @"C:\Users\me"
        "TMPDIR", "/tmp"
        "SECRET_TOKEN", "do-not-leak"
        "AWS_SECRET_ACCESS_KEY", "do-not-leak"
        for vector in InjectionVectors.patterns do
            vector.TrimEnd '*' + (if vector.EndsWith '*' then "X" else ""), "evil"
            (vector.TrimEnd '*' + (if vector.EndsWith '*' then "x" else "")).ToLowerInvariant(), "evil"
    ]

/// Absolute paths in the target's form.
let private pathOn (target: TargetTriple) (segments: string list) =
    match target.Os with
    | Os.Windows -> @"C:\" + String.Join(@"\", segments)
    | _ -> "/" + String.Join("/", segments)

/// The component as the installer would hand it over on a target, in a made-up install directory.
let installedOn (target: TargetTriple) (spec: RuntimeSpec) : InstalledComponent =
    let dir = pathOn target [ "rt"; spec.Component.Id; string spec.Component.Version ]
    let separator = if target.Os = Os.Windows then @"\" else "/"

    let path (relative: string) =
        match Component.expand [ "exeSuffix", TargetTriple.exeSuffix target ] relative with
        | Ok relative -> dir + separator + relative.Replace("/", separator)
        | Error message -> failtest message

    {
        Id = spec.Component.Id
        Version = spec.Component.Version
        Dir = dir
        Bin =
            spec.Component.Exports.Bin
            |> List.map (fun (name, relative) -> name, path relative)
        Env =
            spec.Component.Exports.Env
            |> List.map (fun (name, value) ->
                name,
                (match Component.expand [ "dir", dir ] value with
                 | Ok value -> value
                 | Error message -> failtest message))
    }

/// Owned directories in the target's form.
let ownedOn (target: TargetTriple) : OwnedDirs =
    let under (name: string) = pathOn target [ "owned"; "s1"; name ]

    {
        Root = pathOn target [ "owned"; "s1" ]
        Cwd = under "cwd"
        Home = under "home"
        Config = under "config"
        Cache = under "cache"
        Temp = under "tmp"
    }

let sessionOn (target: TargetTriple) (trusted: bool) : LaunchSession =
    {
        Target = target
        Owned = ownedOn target
        Workspace = Some(pathOn target [ "work"; "project" ])
        ProjectTrusted = trusted
        HostEnvironment = hostile
    }

/// The targets a runtime supports: its component's platforms, or every target a method is for.
let targetsOf (spec: RuntimeSpec) : TargetTriple list =
    if spec.Component.Platforms.IsEmpty then
        spec.Component.Methods
        |> List.collect (fun m ->
            if m.Platforms.IsEmpty then
                TargetTriple.all
            else
                m.Platforms)
        |> List.distinct
    else
        spec.Component.Platforms

let private launched (spec: RuntimeSpec) (target: TargetTriple) (trusted: bool) (program: string list) : ProcSpec =
    match RuntimeSpec.launch spec (installedOn target spec) (sessionOn target trusted) program with
    | Ok proc -> proc
    | Error issues -> failtest $"{target}: {Issue.render issues}"

let tests (name: string) (spec: RuntimeSpec) : Test =
    testList
        name
        [
            test "the spec is valid and travels as the runtime contribution type's JSON" {
                Expect.equal (RuntimeSpec.validate spec |> List.map string) [] "valid"

                let t = RuntimeSpecs.contributionType

                match t.OfJson (t.ToJson spec) None with
                | Ok decoded -> Expect.equal decoded spec "round trip"
                | Error issues -> failtest (Issue.render issues)
            }

            test "it installs from a digest-pinned archive on every target it supports, and is probed" {
                let targets = targetsOf spec
                Expect.isNonEmpty targets "targets"
                Expect.isSome spec.Component.Probe "an install is probed before it is activated"

                let requirement =
                    {
                        Ref =
                            {
                                Id = spec.Component.Id
                                Version = string spec.Component.Version
                            }
                        By = "conformance"
                    }

                match Planner.plan PlanSettings.defaults [ spec.Component ] [ requirement ] targets with
                | Error errors -> failtest (errors |> List.map string |> String.concat "; ")
                | Ok lockfile ->
                    for target in targets do
                        match Lockfile.installs target [ spec.Component.Id ] lockfile with
                        | Ok [ install ] ->
                            match install.Method with
                            | Archive archive ->
                                Expect.stringStarts archive.Url "https://" $"{target}: https"
                                Expect.isFalse (archive.Url.Contains "${") $"{target}: expanded URL"
                            | other -> failtest $"{target}: {other}"
                        | other -> failtest $"{target}: {other}"
            }

            test "its launch on every target: from the install directory, in an owned directory" {
                for target in targetsOf spec do
                    let installed = installedOn target spec
                    let proc = launched spec target false []

                    match proc.Program with
                    | Exec(exe, _) ->
                        Expect.stringStarts exe installed.Dir $"{target}: the program is in the install directory"
                        Expect.isTrue (RuntimeSpec.isAbsoluteOn target.Os exe) $"{target}: absolute"
                    | other -> failtest $"{target}: {other}"

                    if spec.Launch.Cwd = LaunchCwd.Owned then
                        Expect.equal proc.Cwd (Some (ownedOn target).Cwd) $"{target}: owned cwd"
            }

            test "its launch adds the untrusted overrides before the program until the project is trusted" {
                for target in targetsOf spec do
                    let program = [ pathOn target [ "srv"; "main.js" ]; "--flag" ]

                    match (launched spec target false program).Program, (launched spec target true program).Program with
                    | Exec(_, untrusted), Exec(_, trusted) ->
                        Expect.equal
                            (List.length untrusted - List.length trusted)
                            spec.Launch.UntrustedOverrides.Length
                            $"{target}: the overrides"

                        Expect.equal
                            (List.skip (untrusted.Length - program.Length) untrusted)
                            program
                            $"{target}: the program comes last, as given"

                        Expect.equal (List.skip (trusted.Length - program.Length) trusted) program $"{target}: trusted"
                    | other -> failtest $"{target}: {other}"
            }

            test "its environment is fresh: no injection vector, no secret, only what the spec names" {
                for target in targetsOf spec do
                    let ignoreCase = target.Os = Os.Windows
                    let installed = installedOn target spec
                    let owned = ownedOn target

                    match (launched spec target false []).Env with
                    | Exactly vars ->
                        let names = vars |> List.map fst

                        for name in names do
                            Expect.isFalse (InjectionVectors.isVector name) $"{target}: {name} is a vector"

                            let named =
                                spec.Launch.EnvAllowlist
                                |> List.exists (fun pattern -> EnvPattern.matches ignoreCase pattern name)
                                || spec.Launch.EnvSet |> List.exists (fun (set, _) -> set = name)
                                || installed.Env |> List.exists (fun (export, _) -> export = name)

                            Expect.isTrue named $"{target}: {name} is neither allowlisted, set nor exported"

                        Expect.isFalse (List.contains "SECRET_TOKEN" names) $"{target}: secrets stay out"

                        Expect.equal
                            (names
                             |> List.distinctBy (fun n -> if ignoreCase then n.ToUpperInvariant() else n))
                            names
                            $"{target}: one value per name"

                        // A value from an owned placeholder points into the owned directories.
                        for setName, template in spec.Launch.EnvSet do
                            if template.StartsWith("${owned.", StringComparison.Ordinal) then
                                match vars |> List.tryFind (fun (n, _) -> n = setName) with
                                | Some(_, value) -> Expect.stringStarts value owned.Root $"{target}: {setName} is owned"
                                | None -> failtest $"{target}: {setName} is missing"
                    | other -> failtest $"{target}: {other}"
            }

            test "a variant of the spec that lets a vector through is refused" {
                for vector in InjectionVectors.patterns do
                    let lacking =
                        { spec with
                            Launch =
                                { spec.Launch with
                                    EnvDeny = InjectionVectors.patterns |> List.filter (fun p -> p <> vector)
                                }
                        }

                    Expect.isNonEmpty (RuntimeSpec.validate lacking) $"without {vector}"

                    Expect.isError
                        (RuntimeSpec.launch
                            lacking
                            (installedOn TargetTriple.all.Head spec)
                            (sessionOn TargetTriple.all.Head false)
                            [])
                        $"launch without {vector}"
            }
        ]
