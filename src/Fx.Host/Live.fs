namespace Fx.Host

open System
open System.Runtime.InteropServices
open System.Threading.Tasks
open Fx.Effects
open Fx.Effects.Live
open Fx.Components.Live
open Fx.Envd.Live
open Fx.Environments
open Fx.Keyring
open Fx.Sdk
open Fx.Transport.Http

/// What the entry point read from the process once: its variables, the current directory, the running
/// binary. Everything else the host gets from capabilities.
type ProcessFacts =
    {
        Variables: (string * string) list
        /// The current directory, absolute.
        CurrentDirectory: string
        /// The running binary and the arguments before a command (the assembly on the JIT).
        Binary: string
        BinaryArgs: string list
    }

    override this.ToString() = $"process ({this.CurrentDirectory})"

/// The host's live interpreters (the imperative shell): the system clock and randomness, the local
/// filesystem and processes, HTTP, the installer's downloads and disk, the OS keyring and private
/// files, embedded envd, the SQLite stores.
[<RequireQualifiedAccess>]
module Live =
    /// envd in this process (embedded mode), as environments ask for it.
    let embed (launch: EmbeddedLaunch) : Task<Result<Embedded, string>> =
        task {
            let options =
                { EnvdOptions.defaults with
                    Workspace = Some launch.Workspace
                    State = Some launch.State
                    Shell = launch.Shell
                    Environment = Some launch.Environment
                }

            match! EnvdMain.embed options with
            | Error e -> return Error e
            | Ok embedded ->
                return
                    Ok
                        {
                            Transport = embedded.Transport
                            Stop = embedded.Stop
                        }
        }

    /// This machine's target.
    let target () : Result<TargetTriple, string> =
        match TargetTriple.ofRid RuntimeInformation.RuntimeIdentifier with
        | Some target -> Ok target
        | None -> Error $"this platform ({RuntimeInformation.RuntimeIdentifier}) is not a target"

    let private variable (facts: ProcessFacts) (name: string) =
        facts.Variables
        |> List.tryFind (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd
        |> Option.filter (fun v -> v <> "")

    /// The host's options with the live interpreters, for a workspace and the command line's
    /// selections. The data directory comes from the variables (`FX_HOME`, else the user's).
    let options
        (facts: ProcessFacts)
        (workspace: string)
        (explicit: (string * Fx.Contracts.Json) list)
        : Result<HostOptions, string> =
        let windows = OperatingSystem.IsWindows()

        match HostPaths.dataDirectory (variable facts) windows, target () with
        | Error e, _
        | _, Error e -> Error e
        | Ok data, Ok target ->
            let clock = SystemClock.clock
            let ids = SystemIds.create clock

            let home =
                (if windows then
                     variable facts "USERPROFILE"
                 else
                     variable facts "HOME")
                |> Option.defaultValue data

            let name = variable facts (if windows then "USERNAME" else "USER")
            let paths = HostPaths.under data
            let host = LocalHostFacts.read ()

            Ok
                {
                    Paths = paths
                    Workspace = workspace
                    Variables = facts.Variables
                    Explicit = explicit
                    Target = target
                    Home = home
                    Account =
                        {
                            Id = PeerCredentials.currentAccount ()
                            Name = name
                        }
                    Binary = facts.Binary
                    BinaryArgs = facts.BinaryArgs
                    Clock = clock
                    Ids = ids
                    Trace = Trace.none
                    FsRead = LocalFs.read
                    Fs = LocalFs.write
                    Shell = LocalShell.create (LocalShell.defaultShell ()) workspace
                    Http = SystemHttp.create (SystemHttp.client ())
                    Fetch = HttpFetch.create (HttpFetch.client ())
                    InstallFiles = LocalInstallFiles.files
                    Keyring = LocalKeyring.create host.Os (LocalKeyring.settings host)
                    PrivateFiles = LocalPrivateFiles.create paths.Credentials Fx.Product.Name
                    HostVariables = SystemVariables.variables
                    Prompts = Fx.Keyring.Headless.prompts host = Prompts.Allow
                    Embed = Some embed
                    OpenStores = HostStores.live clock ids
                    Extensions = []
                    StopTimeout = TimeSpan.FromSeconds 10.0
                }
