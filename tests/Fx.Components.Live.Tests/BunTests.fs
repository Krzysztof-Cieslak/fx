/// Installs the pinned Bun (the T009 spike's pins) for this machine from GitHub, through the planner,
/// the lockfile and the installer, and probes it. It needs the network, so it runs only when
/// NETWORK_TESTS=1 (CI runs it as its own step on Linux, macOS and Windows).
module Fx.Components.Live.Tests.BunTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Expecto
open Fx.Components
open Fx.Components.Live
open Fx.Components.Live.Tests.LiveEnv
open Fx.Components.Tests.Support
open Fx.Effects
open Fx.Effects.Live
open Fx.Machines.Host
open Fx.Sdk

/// Bun 1.4.5: the release assets and their SHA-256 from the release's SHASUMS256.txt
/// (spikes/bun-kernel/src/pinned-bun.ts). T114 turns this into `fx.runtime.bun`.
let private version = "1.4.5"
let private revision = "1.4.5+a7733435c"

let private assets =
    [
        "bun-linux-x64", "94559b3b528995a49452f9f2404cec74f2efd7d06e0a000a8cd214b02c3666eb"
        "bun-linux-x64-baseline", "dcb23a18a1c2fff12d57bcef696dbbb42eb67fde29f43816ecb595459a24c463"
        "bun-linux-x64-musl", "9b6bdba1fa0914b6adfe48e4438212e2c106d9199b77ba642a9b8a3968a49a59"
        "bun-linux-x64-musl-baseline", "96fb94108eed872ff623ed39007d6b5699a83a5d0086fcfba84921c18145e3e6"
        "bun-linux-aarch64", "18656a3bf0ea5641f5796ae4213b75255579a70a0fded2f337933785568cdf84"
        "bun-linux-aarch64-musl", "d655765d3a0c0fdf707e27c7aa1a4f2c84f1bbc1f759db7e6e3d3c5be414396c"
        "bun-darwin-x64", "a4972e778cce4b6a194d7d289f326aa61576d71d80b7a53e9da2f3739566e5fd"
        "bun-darwin-x64-baseline", "fb0121739a7e80e7511b6bfe4d2116422b48ba18a013acadef0c425348e95891"
        "bun-darwin-aarch64", "b5d6bc2e13caf14e57c89661e89efd073ff16165b41bfe43882616410609cd10"
        "bun-windows-x64", "815ab45ec7f549abf87e6869584b506eab088e24225683957b7e150b47d6de30"
        "bun-windows-x64-baseline", "3c081c73865e7a3453a9d514cc2ba3d699880621bcf67bb17241ff5194cb1d4b"
        "bun-windows-aarch64", "41f57d49c3c6811888d9633c569e40b4eb1f7e92b5e5336e3abd3af03187d77a"
    ]

/// This machine's target and the asset to install on it.
let private machine () : TargetTriple * string =
    let arm = RuntimeInformation.OSArchitecture = Architecture.Arm64
    let arch = if arm then Arch.Arm64 else Arch.X64
    let cpu = if arm then "aarch64" else "x64"

    let baseline =
        if not arm && not Runtime.Intrinsics.X86.Avx2.IsSupported then
            "-baseline"
        else
            ""

    if OperatingSystem.IsWindows() then
        TargetTriple.create Os.Windows arch None, $"bun-windows-{cpu}{baseline}"
    elif OperatingSystem.IsMacOS() then
        TargetTriple.create Os.MacOs arch None, $"bun-darwin-{cpu}{baseline}"
    else
        let musl =
            File.Exists "/etc/alpine-release"
            || Directory.GetFiles("/lib", "ld-musl-*").Length > 0

        let libc = if musl then Libc.Musl else Libc.Glibc
        let suffix = if musl then "-musl" else ""
        TargetTriple.create Os.Linux arch (Some libc), $"bun-linux-{cpu}{suffix}{baseline}"

let private bun (target: TargetTriple) (asset: string) : Component =
    let digest =
        match Digest.parse ("sha256:" + (assets |> List.find (fun (name, _) -> name = asset) |> snd)) with
        | Ok digest -> digest
        | Error e -> failwith e

    { Component.create
          "bun"
          (semver version)
          [
              {
                  Platforms = [ target ]
                  Method =
                      Archive
                          {
                              Url = $"https://github.com/oven-sh/bun/releases/download/bun-v${{version}}/{asset}.zip"
                              Digest = digest
                              Format = ArchiveFormat.Zip
                              Strip = 1
                          }
              }
          ] with
        Layout = { Executables = [ "bun${exeSuffix}" ] }
        Exports =
            {
                Bin = [ "bun", "bun${exeSuffix}" ]
                Env = []
            }
        Probe =
            Some
                {
                    Argv = [ "bun${exeSuffix}"; "--revision" ]
                    Expect = Some "${version}+a7733435c"
                }
    }

[<Tests>]
let tests =
    testList
        "Pinned Bun (network)"
        [
            test "installs the pinned Bun for this machine and probes it" {
                if Environment.GetEnvironmentVariable "NETWORK_TESTS" <> "1" then
                    skiptest "set NETWORK_TESTS=1 to download Bun"

                let target, asset = machine ()

                let lockfile =
                    match
                        Planner.plan
                            PlanSettings.defaults
                            [ bun target asset ]
                            [
                                {
                                    Ref = { Id = "bun"; Version = "1.4" }
                                    By = "test"
                                }
                            ]
                            [ target ]
                    with
                    | Ok lockfile -> lockfile
                    | Error errors -> failtest (errors |> List.map string |> String.concat "; ")

                let outside = tempDirectory ()

                try
                    use client = HttpFetch.client ()

                    let env =
                        { create (Fx.Components.Fakes.FakeFetch(LocalFs.write)) None with
                            Fetch = HttpFetch.create client
                            Shell = LocalShell.create (LocalShell.defaultShell ()) outside
                        }

                    let root = Path.Combine(outside, "runtimes")

                    let settings =
                        InstallerSettings.create root "test" (fun _ -> Eff.ret (ApprovalAnswer.Approved "test"))

                    let log = InMemoryEventLog<InstallerEvent>()
                    let actor = runIn env (ComponentInstaller.start settings log.Log)
                    let mutable keys = 0

                    let components =
                        ComponentInstaller.components actor lockfile target (fun () ->
                            keys <- keys + 1
                            string keys)

                    let request =
                        {
                            Holder = "test"
                            Requires = [ { Id = "bun"; Version = "^1.4" } ]
                        }

                    match
                        (components.Ensure request CancellationToken.None)
                            .WaitAsync(TimeSpan.FromMinutes 5.0)
                            .GetAwaiter()
                            .GetResult()
                    with
                    | Ok [ installed ] ->
                        let exe = installed.Bin |> List.find (fun (name, _) -> name = "bun") |> snd
                        let output = runIn env (Shell.exec exe [ "--version" ])

                        match output with
                        | Ok output ->
                            Expect.equal output.ExitCode 0 "bun runs"
                            Expect.equal ((Output.stdoutText output).Trim()) version "bun --version"
                        | Error e -> failtest (string e)

                        Expect.stringContains revision version "the probe checked the revision"
                    | other -> failtest $"{other}"

                    runIn env (Actor.stop actor)
                finally
                    removeDirectory outside
            }
        ]
