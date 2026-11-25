/// The pinned Bun the reference kernel runs on, for the tests: download, verify against the pinned
/// SHA-256, extract, probe (`bun --revision`), as the Bun kernel spike does (docs/spikes/bun-kernel.md).
/// The kernel never runs on a Bun from PATH. T114 (`fx.runtime.bun`) replaces this with a component
/// the installer manages; keep the pin in step with spikes/bun-kernel/src/pinned-bun.ts until then.
module Fx.Kernel.Tests.PinnedBun

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Threading.Tasks

let version = "1.4.5"
let revision = "1.4.5+a7733435c"
let releaseUrl = $"https://github.com/oven-sh/bun/releases/download/bun-v{version}"

/// Release asset (without `.zip`) to the SHA-256 of the zip, from the release's SHASUMS256.txt.
let digests =
    Map.ofList
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

/// The asset for this machine.
let target () : string =
    let os =
        if OperatingSystem.IsWindows() then "windows"
        elif OperatingSystem.IsMacOS() then "darwin"
        else "linux"

    let arch =
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.Arm64 -> "aarch64"
        | _ -> "x64"

    let musl =
        RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal)

    let baseline = arch = "x64" && not System.Runtime.Intrinsics.X86.Avx2.IsSupported

    $"bun-{os}-{arch}"
    + (if musl then "-musl" else "")
    + (if baseline then "-baseline" else "")

let private exeName (target: string) =
    if target.StartsWith("bun-windows-", StringComparison.Ordinal) then
        "bun.exe"
    else
        "bun"

let private sha256 (bytes: byte array) =
    Convert.ToHexStringLower(SHA256.HashData bytes)

let private run (exe: string) (args: string list) (cwd: string) : Task<int * string> =
    task {
        let info = ProcessStartInfo(exe, args)
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        info.WorkingDirectory <- cwd
        info.Environment["DO_NOT_TRACK"] <- "1"

        use proc =
            match Process.Start info with
            | null -> failwith $"{exe} did not start"
            | p -> p

        let output = proc.StandardOutput.ReadToEndAsync()
        let errors = proc.StandardError.ReadToEndAsync()
        do! proc.WaitForExitAsync()
        let! out = output
        let! err = errors
        return proc.ExitCode, out + err
    }

let private download (url: string) : Task<byte array> =
    task {
        use client = new HttpClient(Timeout = TimeSpan.FromMinutes 5.0)
        let mutable result = None
        let mutable attempt = 0

        while result.IsNone do
            attempt <- attempt + 1

            try
                let! bytes = client.GetByteArrayAsync url
                result <- Some bytes
            with _ when attempt < 3 ->
                do! Task.Delay(attempt * 2000)

        return result.Value
    }

/// Installs (or re-verifies) the pinned Bun under `artifacts/kernel-bun-ts/runtimes` and returns
/// its executable.
let private install () : Task<string> =
    task {
        let target = target ()

        let digest =
            match Map.tryFind target digests with
            | Some d -> d
            | None -> failwith $"no pinned Bun for {target}"

        let dir = Repo.path [ "artifacts"; "kernel-bun-ts"; "runtimes"; version; target ]
        let exe = Path.Combine(dir, exeName target)
        let record = Path.Combine(dir, "archive.sha256")

        let installed =
            File.Exists exe
            && File.Exists record
            && File.ReadAllText(record).Trim() = digest

        if not installed then
            let! archive = download $"{releaseUrl}/{target}.zip"
            let actual = sha256 archive

            if actual <> digest then
                failwith $"digest mismatch for {target}.zip: expected sha256:{digest}, got sha256:{actual}"

            use zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read)

            let entry =
                match zip.GetEntry($"{target}/{exeName target}") with
                | null -> failwith $"{target}.zip has no {exeName target}"
                | e -> e

            let staging = dir + ".staging-" + string Environment.ProcessId

            if Directory.Exists staging then
                Directory.Delete(staging, true)

            Directory.CreateDirectory staging |> ignore
            let staged = Path.Combine(staging, exeName target)
            entry.ExtractToFile staged

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(
                    staged,
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.UserExecute
                    ||| UnixFileMode.GroupRead
                    ||| UnixFileMode.GroupExecute
                )

            File.WriteAllText(Path.Combine(staging, "archive.sha256"), digest)

            if Directory.Exists dir then
                Directory.Delete(dir, true)

            Directory.CreateDirectory(Path.GetDirectoryName dir |> nonNull) |> ignore
            Directory.Move(staging, dir)

        let! code, probe = run exe [ "--revision" ] dir

        if code <> 0 || probe.Trim() <> revision then
            failwith $"pinned Bun probe failed: expected {revision}, got {probe.Trim()} (exit {code})"

        return exe
    }

/// The reference kernel bundled into one file with the pinned Bun, as a release build would.
type Prepared =
    {
        Bun: string
        Kernel: string
    }

    override this.ToString() = $"{this.Bun} {this.Kernel}"

let private bundle (bun: string) : Task<string> =
    task {
        let output =
            Repo.path [ "artifacts"; "kernel-bun-ts"; $"kernel-{Environment.ProcessId}.js" ]

        let entry = Path.Combine(Repo.kernelPackage, "src", "main.ts")

        let! code, log =
            run bun [ "build"; entry; "--target=bun"; $"--outfile={output}" ] Repo.kernelPackage

        if code <> 0 then
            failwith $"bundling the kernel failed: {log}"

        return output
    }

let private prepared =
    lazy
        (task {
            let! bun = install ()
            let! kernel = bundle bun
            return { Bun = bun; Kernel = kernel }
        })

/// The pinned Bun and the bundled kernel, or why they are not available.
let prepare () : Task<Result<Prepared, string>> =
    task {
        try
            let! p = prepared.Force()
            return Ok p
        with e ->
            return Error(e.GetType().Name + ": " + e.Message)
    }
