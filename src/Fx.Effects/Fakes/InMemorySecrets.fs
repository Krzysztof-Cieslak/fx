namespace Fx.Effects.Fakes

open System.Threading.Tasks
open Fx.Effects

/// Secrets held in memory, for tests and for `fx test` environments. Real stores are T121.
module InMemorySecrets =
    /// Secrets from name/value pairs. Throws if a name is invalid (a defect in the setup).
    let create (secrets: (string * string) list) : Secrets =
        let values =
            secrets
            |> List.map (fun (name, value) ->
                match SecretRef.tryCreate name with
                | Ok reference -> reference.Name, value
                | Error reason -> invalidArg (nameof secrets) $"invalid secret name '{name}': {reason}")
            |> Map.ofList

        {
            Resolve =
                fun reference cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<Result<Secret, SecretError>> cancellation
                    else
                        match values.TryFind reference.Name with
                        | Some value -> Task.FromResult(Ok(Secret(reference, value)))
                        | None -> Task.FromResult(Error(SecretNotFound reference))
        }
