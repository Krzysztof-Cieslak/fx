/// `Fx.Aot.Diff [--rid <rid>] [--no-publish] [--self-test] [--only <prefix>]`: publishes the subjects for a runtime
/// identifier, runs every case with `dotnet <assembly>.dll` and with the native binary, and fails on
/// any difference. Exit codes: 0 identical, 1 different, 2 usage or setup error.
module Fx.Aot.Diff.Program

open System
open System.IO
open System.Runtime.InteropServices
open Fx.Aot.Diff.Diff
open Fx.Aot.Diff.Subjects

type Options =
    {
        Rid: string
        Publish: bool
        SelfTest: bool
        /// Only the scenarios whose names start with this (e.g. CI's keyring steps: `keyring-`).
        Only: string option
    }

let private usage =
    "Usage: Fx.Aot.Diff [--rid <rid>] [--no-publish] [--self-test] [--only <prefix>]\n\n"
    + "  --rid <rid>    runtime identifier to publish and run (default: this machine's)\n"
    + "  --no-publish   use existing builds in artifacts/ instead of publishing first\n"
    + "  --self-test    run the canary scenarios, which must differ, to prove the diff detects it\n"
    + "  --only <prefix> run only the scenarios whose names start with the prefix\n"

let rec private parse (options: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] -> Ok options
    | "--rid" :: rid :: rest -> parse { options with Rid = rid } rest
    | "--no-publish" :: rest -> parse { options with Publish = false } rest
    | "--self-test" :: rest -> parse { options with SelfTest = true } rest
    | "--only" :: prefix :: rest -> parse { options with Only = Some prefix } rest
    | other :: _ -> Error $"unknown argument: {other}"

/// The repository root: the nearest directory above the working directory with Fx.slnx.
let private findRoot () : string option =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> None
        | d when File.Exists(Path.Combine(d.FullName, "Fx.slnx")) -> Some d.FullName
        | d -> up d.Parent

    up (DirectoryInfo(Directory.GetCurrentDirectory()))

/// The dotnet host that runs this tool (set by `dotnet run`), else the one on PATH.
let private dotnet =
    match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
    | null
    | "" -> "dotnet"
    | path -> path

let private timeout = TimeSpan.FromMinutes 2.0

let private say (line: string) = stdout.WriteLine line

/// Emits a GitHub Actions error annotation when running in GitHub Actions.
let private annotate (message: string) =
    if Environment.GetEnvironmentVariable "GITHUB_ACTIONS" = "true" then
        say $"::error title=JIT-vs-native diff::{message}"

type private Binaries = { Jit: string; Native: string }

/// Where `dotnet publish -c Release -r <rid>` leaves a subject (UseArtifactsOutput layout).
let private binaries (root: string) (rid: string) (subject: Subject) : Binaries =
    let pivot = "release_" + rid
    let exe = if OperatingSystem.IsWindows() then ".exe" else ""

    {
        Jit = Path.Combine(root, "artifacts", "bin", subject.Project, pivot, subject.Assembly + ".dll")
        Native = Path.Combine(root, "artifacts", "publish", subject.Project, pivot, subject.Assembly + exe)
    }

let private projectDir (root: string) (subject: Subject) =
    Path.Combine(Array.ofList (root :: subject.ProjectDir))

let private indent (lines: string list) =
    lines |> List.map (fun l -> "      " + l)

/// Runs one case both ways. `Ok report` holds the differences (empty when identical); `Error` holds
/// problems that make the case unusable either way, such as a failing JIT run.
let private runCase root (bins: Binaries) (case: Case) : Result<string list, string list> * Run option =
    let jit = Processes.capture timeout root dotnet (bins.Jit :: case.Args)
    let native = Processes.capture timeout root bins.Native case.Args

    match jit, native with
    | Error e, _ -> Error [ $"jit: {e}" ], None
    | _, Error e -> Error [ $"native: {e}" ], None
    | Ok j, Ok _ when case.MustSucceed && j.ExitCode <> 0 ->
        Error(
            $"the JIT run failed with exit code {j.ExitCode}:"
            :: (lines j.Stdout @ lines j.Stderr |> List.truncate 20)
        ),
        Some j
    | Ok j, Ok n -> Ok(Diff.compare j n), Some j

/// Runs every case of a subject; returns (cases run, failures).
let private runSubject
    (root: string)
    (rid: string)
    (selfTest: bool)
    (only: string option)
    (subject: Subject)
    : int * int =
    let bins = binaries root rid subject
    let mutable ran = 0
    let mutable failed = 0

    /// Reports a case; `expectDifference` inverts the verdict for the canaries.
    let report (expectDifference: bool) (case: Case) (outcome: Result<string list, string list>) =
        ran <- ran + 1
        let title = $"{subject.Name} {case.Name}"

        let fail (details: string list) =
            failed <- failed + 1
            say ("FAIL  " + title)
            indent details |> List.iter say
            annotate $"{title} on {rid}"

        match outcome, expectDifference with
        | Error problems, _ -> fail problems
        | Ok [], false -> say ("same  " + title)
        | Ok differences, false -> fail differences
        | Ok [], true ->
            fail
                [
                    "expected a difference between the JIT and native runs, but they are identical"
                ]
        | Ok differences, true ->
            say ("diff  " + title + " (expected)")
            indent differences |> List.iter say

    say $"{subject.Name}: dotnet {Path.GetRelativePath(root, bins.Jit)} | {Path.GetRelativePath(root, bins.Native)}"

    let cases =
        match subject.Cases with
        | Fixed cases -> cases
        | Listed(list, run) ->
            let listCase =
                {
                    Name = String.concat " " list
                    Args = list
                    MustSucceed = true
                }

            match runCase root bins listCase with
            | Ok differences, Some jit ->
                // The JIT and native lists must agree, even in the self-test.
                report false listCase (Ok differences)

                lines jit.Stdout
                |> List.filter (fun name -> name.Length > 0)
                |> List.map (fun name ->
                    {
                        Name = name
                        Args = run name
                        MustSucceed = true
                    })
            | outcome, _ ->
                report false listCase (Result.bind (fun _ -> Error [ "cannot list cases" ]) outcome)
                []

    let selected =
        match only with
        | Some prefix ->
            cases
            |> List.filter (fun case -> case.Name.StartsWith(prefix, StringComparison.Ordinal))
        | None -> cases

    match only with
    | Some prefix when selected.IsEmpty ->
        failed <- failed + 1
        say $"FAIL  {subject.Name}: no case starts with {prefix}"
    | _ -> ()

    for case in selected do
        report selfTest case (fst (runCase root bins case))

    ran, failed

let private run (root: string) (options: Options) : int =
    let subjects =
        match options.SelfTest, options.Only with
        | true, _ -> [ canaries ]
        | false, Some _ -> [ scenarios ]
        | false, None -> all

    let publishFailed =
        options.Publish
        && subjects
           |> List.distinctBy (fun s -> s.ProjectDir)
           |> List.exists (fun s ->
               say $"publishing {projectDir root s} for {options.Rid}"

               Processes.passThrough root dotnet [ "publish"; projectDir root s; "-c"; "Release"; "-r"; options.Rid ]
               <> 0)

    let missing =
        subjects
        |> List.collect (fun s ->
            let b = binaries root options.Rid s
            [ b.Jit; b.Native ] |> List.filter (File.Exists >> not))

    if publishFailed then
        say "publish failed"
        2
    elif not missing.IsEmpty then
        for path in missing do
            say $"missing {path}"

        say $"publish first (dotnet publish <project> -c Release -r {options.Rid}) or run without --no-publish"
        2
    else
        say $"JIT-vs-native diff on {options.Rid}"

        let ran, failed =
            subjects
            |> List.map (runSubject root options.Rid options.SelfTest options.Only)
            |> List.fold (fun (r, f) (r', f') -> (r + r', f + f')) (0, 0)

        match options.SelfTest, failed with
        | false, 0 ->
            say $"{ran} cases: JIT and native output identical"
            0
        | false, n ->
            say $"{n} of {ran} cases FAILED: JIT and native output differ, or a run failed"
            1
        | true, 0 ->
            say $"self-test passed: the diff detected every canary ({ran} cases)"
            0
        | true, n ->
            say $"self-test FAILED: {n} of {ran} cases"
            1

[<EntryPoint>]
let main argv =
    let defaults =
        {
            Rid = RuntimeInformation.RuntimeIdentifier
            Publish = true
            SelfTest = false
            Only = None
        }

    match parse defaults (List.ofArray argv), findRoot () with
    | Error e, _ ->
        stderr.Write $"{e}\n\n{usage}"
        2
    | _, None ->
        stderr.Write "run from inside the repository (no Fx.slnx found above the working directory)\n"
        2
    | Ok options, Some root -> run root options
