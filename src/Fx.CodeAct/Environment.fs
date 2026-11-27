namespace Fx.CodeAct

open System
open System.Security.Cryptography
open System.Text
open Fx.Effects
open Fx.Envd
open Fx.Environments
open Fx.Kernel
open Fx.Interpreters
open Fx.Sdk

/// An opened environment's capabilities (envd's, through `EnvClient`) as a session's hands: what its
/// kernels start with and its bindings act on (spec §3: kernels run in the environment).
type EnvHands =
    {
        Clock: Clock
        Client: EnvClient
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IFs with
        member env.FsRead = env.Client.FsRead
        member env.Fs = env.Client.Fs

    interface IFsOps with
        member env.FsOps = env.Client.FsOps

    interface IShell with
        member env.Shell = env.Client.Shell

    interface IScopeWith<EnvHands> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"EnvHands({env.Client})"

/// What a session's kernels need from the host besides the environment.
[<NoEquality; NoComparison>]
type KernelPlan =
    {
        /// The interpreter (from the `interpreter` contributions in effect; `interpreter` variant).
        Provider: InterpreterProvider
        /// The runtime it names (from the `runtime` contributions in effect).
        Runtime: RuntimeSpec
        Options: KernelOptions
        /// Whether the project is trusted (until it is, the runtime's untrusted overrides apply).
        ProjectTrusted: bool
        /// The launcher's variables: on the host's machine, the host's scrubbed ones
        /// (`HostEnvironment.scrub`); only the runtime's allowlist reaches a kernel.
        Launcher: (string * string) list
        /// Checked before every host call (T127b: grants), after the sandbox's own check; `Ok` with
        /// what no grant in force allowed (`Hands.Guard`).
        Guard: Fx.Kernel.HostCall -> Eff<EnvHands, Result<Fx.Prompt.Approval list, HostCallError>>
        /// What the session's agent may not do in the environment's sandbox (spec §7.1): e.g. a
        /// reviewer's read-only workspace. Its processes, kernels and bindings get the tighter policy.
        Sandbox: SandboxOverrides
    }

    override this.ToString() =
        $"kernel plan ({this.Provider.Id} on {this.Runtime})"

[<RequireQualifiedAccess>]
module EnvHands =
    /// A launch's owned directories' name: the session (hashed: ids may hold any character) and the
    /// launch's number, so every kernel gets fresh ones (`RuntimeSpec.prepare`).
    let launchName (session: string) (launch: int) : string =
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes session)
        $"kernel-{Convert.ToHexStringLower(digest).Substring(0, 12)}-{launch}"

    /// A session's hands in an opened environment (`IEnvs.Open`): its capabilities, its workspace and
    /// OS, and the interpreter's kernels prepared on its filesystem (assets at `EnvLayout.Assets`, owned
    /// directories under `EnvLayout.Owned`, the components installed for it) and started with its shell.
    /// The kernels live in `scope` (the host's), not a turn's. Every process (commands, kernels) starts
    /// in the environment's sandbox under the plan's overrides, and every host call is checked against
    /// the same policy first (`SandboxGuard`).
    let ofHandle
        (clock: Clock)
        (scope: Scope)
        (plan: KernelPlan)
        (session: string)
        (handle: EnvHandle)
        : Hands<EnvHands> =
        let handle = EnvHandle.attenuate plan.Sandbox handle
        let policy = handle.Sandbox.Policy
        // Below the recording taps: the journal, the context and the model only ever see redacted
        // output (T138).
        let client = Redacting.client handle.Redaction handle.Client

        let interpreterSession (launch: int) : InterpreterSession =
            {
                Provider = plan.Provider
                Runtime = plan.Runtime
                Installed = handle.Installed
                AssetsRoot = handle.Layout.Assets
                Launch =
                    {
                        Target = handle.Machine.Target
                        Owned = EnvLayout.owned handle.Layout (launchName session launch)
                        Workspace = Some handle.Layout.Workspace
                        ProjectTrusted = plan.ProjectTrusted
                        HostEnvironment = plan.Launcher
                    }
            }

        {
            Env =
                {
                    Clock = clock
                    Client = client
                    Scope = scope
                }
            Workspace = handle.Layout.Workspace
            Os = handle.Layout.Os
            CommandEnv = Inherit []
            Interpreter = Interpreter.ofProvider plan.Options interpreterSession
            Guard =
                fun call ->
                    eff {
                        match! SandboxGuard.check policy handle.Layout.Os call with
                        | Error e -> return Error e
                        | Ok() -> return! plan.Guard call
                    }
            Snapshots = Some handle.Client.Snapshots
            Sandbox = if handle.Sandbox.Driver.Confines then Some policy else None
            Variables =
                handle.Variables
                |> List.map (fun variable ->
                    {
                        Name = variable.Name
                        Missing =
                            match variable.Outcome with
                            | VariableOutcome.Passed
                            | VariableOutcome.Granted _ -> None
                            | VariableOutcome.Denied reason
                            | VariableOutcome.Unavailable reason -> Some reason
                    })
        }
