namespace Fx.Effects

open System
open System.Threading
open System.Threading.Tasks

/// The host process's environment variables, read when they are needed: how the `env` auth method
/// and the `env` secret source take credentials from the environment (spec §11.3–11.4), e.g.
/// `ANTHROPIC_API_KEY` in CI. Values are `SecretText`, so they never print and a recording
/// interpreter journals names and outcomes only. Host-only: environments get the variables the host
/// scrubbed for them, never this (spec §18).
///
/// Interpreters: `SystemVariables` (Fx.Effects.Live), `InMemoryVariables` (Fx.Effects.Fakes).
type HostVariables =
    {
        /// The value of a variable; `None` if it is unset or empty (an empty credential is none).
        Read: string -> CancellationToken -> Task<SecretText option>
    }

    override _.ToString() = "HostVariables"

/// An environment that can read the host's environment variables.
type IHostVariables =
    abstract HostVariables: HostVariables

module HostVariables =
    /// Whether a name can be an environment variable's on every OS: 1–128 characters of
    /// `A-Z a-z 0-9 _`, not starting with a digit.
    let isName (name: string) : bool =
        not (String.IsNullOrEmpty name)
        && name.Length <= 128
        && not (Char.IsAsciiDigit name[0])
        && Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_') name

    let read<'R when 'R :> IHostVariables and 'R :> IScope> (name: string) : Eff<'R, SecretText option> =
        fun env -> env.HostVariables.Read name env.Scope.Cancellation

    /// No variables at all.
    let none: HostVariables =
        {
            Read =
                fun _ cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<SecretText option> cancellation
                    else
                        Task.FromResult None
        }
