namespace Fx.Aot.Scenarios

open System
open System.Runtime.InteropServices
open System.Threading.Tasks

/// An operating-system family that scenarios can be gated on.
[<RequireQualifiedAccess>]
type Os =
    | Linux
    | MacOS
    | Windows

    override this.ToString() =
        match this with
        | Linux -> "linux"
        | MacOS -> "macos"
        | Windows -> "windows"

/// The platform a scenario runs on. Scenarios can be gated on any part of it.
type Platform =
    {
        Os: Os
        Arch: Architecture
        /// Linux with musl libc (e.g. Alpine) rather than glibc.
        Musl: bool
    }

    override this.ToString() =
        let arch =
            match this.Arch with
            | Architecture.X64 -> "x64"
            | Architecture.Arm64 -> "arm64"
            | Architecture.X86 -> "x86"
            | Architecture.Arm -> "arm"
            | other -> $"arch{int other}"

        let libc = if this.Musl then "-musl" else ""
        $"{this.Os}{libc}-{arch}"

module Platform =
    let current () : Platform =
        let os =
            if OperatingSystem.IsWindows() then Os.Windows
            elif OperatingSystem.IsMacOS() then Os.MacOS
            else Os.Linux

        {
            Os = os
            Arch = RuntimeInformation.ProcessArchitecture
            Musl = RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal)
        }

/// A named piece of code whose text output must be identical on the JIT and as a Native AOT binary.
///
/// Output must be deterministic for a given platform: no times, process ids, temp paths or string
/// hash codes (they are randomized per process).
type Scenario =
    {
        /// Unique kebab-case name, used on the command line.
        Name: string
        /// Whether the scenario runs on a platform. Elsewhere it is not listed and the diff skips it.
        RunsOn: Platform -> bool
        /// Produces the output lines.
        Run: unit -> Task<string list>
    }

    override this.ToString() = this.Name

module Scenario =
    /// A synchronous scenario that runs everywhere.
    let lines (name: string) (run: unit -> string list) : Scenario =
        {
            Name = name
            RunsOn = fun _ -> true
            Run = fun () -> Task.FromResult(run ())
        }

    /// An asynchronous scenario that runs everywhere.
    let task (name: string) (run: unit -> Task<string list>) : Scenario =
        {
            Name = name
            RunsOn = fun _ -> true
            Run = run
        }

    /// Restricts a scenario to platforms that satisfy a predicate.
    let where (predicate: Platform -> bool) (scenario: Scenario) : Scenario =
        { scenario with
            RunsOn = fun p -> scenario.RunsOn p && predicate p
        }

    /// Restricts a scenario to some operating systems.
    let onlyOn (oses: Os list) (scenario: Scenario) : Scenario =
        where (fun p -> List.contains p.Os oses) scenario

    /// Excludes a scenario from some operating systems.
    let except (oses: Os list) (scenario: Scenario) : Scenario =
        where (fun p -> not (List.contains p.Os oses)) scenario

/// Helpers for writing scenario output.
module Show =
    /// Escapes control and non-ASCII characters, so output does not depend on console encodings.
    let escape (text: string) : string =
        let sb = Text.StringBuilder()

        for c in text do
            match c with
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' || c > '~' -> sb.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

    /// The value, or `<null>`.
    let orNull (value: string | null) : string =
        match value with
        | null -> "<null>"
        | v -> v

    /// `label = value` with the value escaped.
    let kv (label: string) (value: string) : string = label + " = " + escape value
