/// Runtime and OS integration: product names, platform detection, paths, files, processes and crypto.
module Fx.Aot.Scenarios.Runtime

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open Fx
open Fx.Aot.Scenarios.Show

let product =
    Scenario.lines "product-names" (fun () ->
        [
            kv "name" Product.Name
            kv "version" Product.Version
            kv "pascal" Product.pascalName
            kv "project dir" Product.projectDirName
            kv "env var" (Product.envVar "HOME")
            kv "npm package" (Product.npmPackage "sdk")
            kv "nuget package" (Product.nugetPackage "Sdk")
            kv "extension id" (Product.extensionId "env.local")
            kv "components lock" Product.componentsLockFileName
        ])

let platform =
    Scenario.lines "platform" (fun () ->
        let p = Platform.current ()

        [
            $"platform = {p}"
            $"os = windows {OperatingSystem.IsWindows()}, macos {OperatingSystem.IsMacOS()}, linux {OperatingSystem.IsLinux()}"
            $"process architecture = {RuntimeInformation.ProcessArchitecture}, 64-bit = {Environment.Is64BitProcess}"
            $"processors > 0 = {Environment.ProcessorCount > 0}, process id > 0 = {Environment.ProcessId > 0}"
            $"newline = {escape Environment.NewLine}, path separator = {Path.PathSeparator}"
            $"directory separator = {Path.DirectorySeparatorChar}, alt = {Path.AltDirectorySeparatorChar}"
            $"invariant globalization = {Globalization.CultureInfo.CurrentCulture.Name = String.Empty}"
            $"stopwatch high resolution = {Diagnostics.Stopwatch.IsHighResolution}"
            $"temp path rooted = {Path.IsPathRooted(Path.GetTempPath())}"
        ])

let paths =
    Scenario.lines "paths" (fun () ->
        let combined = Path.Combine("root", "sub", "file.tar.gz")

        [
            kv "combine" combined
            kv "file name" (Path.GetFileName combined)
            kv "extension" (Path.GetExtension combined)
            kv "without extension" (Path.GetFileNameWithoutExtension combined)
            kv "directory" (orNull (Path.GetDirectoryName combined))
            kv "changed extension" (Path.ChangeExtension(combined, ".zip"))
            kv "join" (Path.Join("a", "b/c"))
            $"rooted = {Path.IsPathRooted combined}, invalid chars > 0 = {Path.GetInvalidFileNameChars().Length > 0}"
            kv "relative" (Path.GetRelativePath(Path.Combine("x", "y"), Path.Combine("x", "z", "w")))
        ])

/// Runs `f` in a fresh temporary directory and deletes it afterwards.
let private inTempDir (f: string -> string list) : string list =
    let dir =
        Path.Combine(Path.GetTempPath(), "aot-scenario-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        Directory.Delete(dir, true)

let fileSystem =
    Scenario.lines "file-system" (fun () ->
        inTempDir (fun dir ->
            let file = Path.Combine(dir, "notes.txt")
            File.WriteAllText(file, "line 1\nline 2\r\nline 3")
            File.AppendAllLines(file, [ ""; "appended" ])
            Directory.CreateDirectory(Path.Combine(dir, "sub", "deeper")) |> ignore
            File.WriteAllBytes(Path.Combine(dir, "sub", "data.bin"), [| 0uy; 1uy; 255uy |])

            let entries =
                Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                |> Seq.map (fun p -> Path.GetRelativePath(dir, p).Replace('\\', '/'))
                |> Seq.sort
                |> List.ofSeq

            use stream = File.OpenRead(Path.Combine(dir, "sub", "data.bin"))
            let buffer = Array.zeroCreate<byte> 8
            let read = stream.Read(buffer, 0, buffer.Length)

            [
                kv "contents" (File.ReadAllText file)
                $"lines = {File.ReadAllLines file |> Array.length}, size = {FileInfo(file).Length}"
                $"entries = {entries}"
                $"read = {read} bytes, last = {buffer[read - 1]}"
                $"""exists = {File.Exists file}, missing = {File.Exists(Path.Combine(dir, "missing"))}"""
            ]))

/// Unix permission bits: not available on Windows, so the scenario is gated.
let unixFileMode =
    Scenario.lines "unix-file-mode" (fun () ->
        inTempDir (fun dir ->
            let file = Path.Combine(dir, "script.sh")
            File.WriteAllText(file, "#!/bin/sh\n")

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(
                    file,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

                [ $"mode = {File.GetUnixFileMode file}" ]
            else
                [ "unreachable on Windows" ]))
    |> Scenario.except [ Os.Windows ]

let crypto =
    Scenario.lines "crypto-and-hashing" (fun () ->
        let data = Encoding.UTF8.GetBytes "abc"
        let key = Encoding.UTF8.GetBytes "key"

        [
            $"sha256 = {Convert.ToHexString(SHA256.HashData data)}"
            $"sha512 = {Convert.ToHexString(SHA512.HashData data).Substring(0, 32)}"
            $"hmac = {Convert.ToHexString(HMACSHA256.HashData(key, data))}"
            $"md5 = {Convert.ToHexString(MD5.HashData data)}"
            $"random bytes = {RandomNumberGenerator.GetBytes(16).Length}"
        ])

let scenarios = [ product; platform; paths; fileSystem; unixFileMode; crypto ]
