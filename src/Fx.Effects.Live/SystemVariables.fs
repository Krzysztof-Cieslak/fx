namespace Fx.Effects.Live

open System
open System.Threading.Tasks
open Fx.Effects

/// The host process's environment variables: the only reader of them for credentials (the `env` auth
/// method and secret source). Read at each call; a process's variables are those it started with,
/// so exporting a key in another shell needs a restarted host.
module SystemVariables =
    let variables: HostVariables =
        {
            Read =
                fun name cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<SecretText option> cancellation
                    elif not (HostVariables.isName name) then
                        Task.FromResult None
                    else
                        match Environment.GetEnvironmentVariable name with
                        | null
                        | "" -> Task.FromResult None
                        | value -> Task.FromResult(Some(SecretText value))
        }
