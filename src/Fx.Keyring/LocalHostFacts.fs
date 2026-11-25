namespace Fx.Keyring

open System
open System.IO

/// Reads `HostFacts` from this process and machine: the composition root calls it once, at startup.
module LocalHostFacts =
    let private os () =
        if OperatingSystem.IsWindows() then
            HostOs.Windows
        elif OperatingSystem.IsMacOS() then
            HostOs.MacOS
        elif OperatingSystem.IsLinux() then
            HostOs.Linux
        else
            HostOs.Other(Runtime.InteropServices.RuntimeInformation.OSDescription)

    let private exists (path: string) =
        try
            File.Exists path || Directory.Exists path
        with :? IOException ->
            false

    let read () : HostFacts =
        let os = os ()

        let variables =
            Headless.variables
            |> List.choose (fun name ->
                match Environment.GetEnvironmentVariable name with
                | null -> None
                | value -> Some(name, value))
            |> Map.ofList

        let linux = os = HostOs.Linux

        let wslKernel =
            linux
            && (try
                    File
                        .ReadAllText("/proc/sys/kernel/osrelease")
                        .Contains("microsoft", StringComparison.OrdinalIgnoreCase)
                with
                | :? IOException
                | :? UnauthorizedAccessException -> false)

        let runtimeBus =
            match variables.TryFind "XDG_RUNTIME_DIR" with
            | Some dir when linux && Path.IsPathRooted dir -> exists (Path.Combine(dir, "bus"))
            | _ -> false

        {
            Os = os
            Variables = variables
            RuntimeBusExists = runtimeBus
            WslKernel = wslKernel
            ContainerMarker = linux && (exists "/.dockerenv" || exists "/run/.containerenv")
        }
