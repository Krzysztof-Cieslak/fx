namespace Fx.Auth

open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Sdk

/// A secret source bound to its environment: what the resolver asks.
[<NoEquality; NoComparison>]
type BoundSource =
    {
        Id: string
        Resolve: SecretRef -> CancellationToken -> Task<Result<SecretText option, string>>
    }

    override this.ToString() = "source " + this.Id

/// The host's `Secrets` (spec §11.4): `secret:<name>` references resolved from the secret sources in
/// the order `secrets.sources` gives. Values are never journaled or traced: only references and
/// outcomes.
[<RequireQualifiedAccess>]
module SecretResolver =
    type private ScopeEnv =
        {
            Scope: Scope
        }

        interface IScope with
            member env.Scope = env.Scope

    /// A contributed source in its environment: its extension's, with the host's store and variables.
    let bind (store: CredentialStore) (variables: HostVariables) (source: Contributed<SecretSource>) : BoundSource =
        let sourceEnv: SecretSourceEnv =
            {
                Extension = source.Env
                CredentialStore = store
                HostVariables = variables
            }

        {
            Id = source.Value.Id
            Resolve =
                fun reference cancellation ->
                    SecretSourceEnv.within
                        sourceEnv
                        (source.Value.Resolve reference)
                        {
                            Scope = Scope.ofCancellation cancellation
                        }
        }

    /// Asks the sources in order: the first that has the secret answers. A source that cannot tell
    /// (a locked keyring) does not stop the others; if none has the secret, the reference is
    /// unavailable with the first source's reason, else not found.
    let create (sources: BoundSource list) : Secrets =
        {
            Resolve =
                fun reference cancellation ->
                    task {
                        let mutable found = None
                        let mutable failure = None
                        let mutable remaining = sources

                        while found.IsNone && not remaining.IsEmpty do
                            let source = remaining.Head
                            remaining <- remaining.Tail

                            match! source.Resolve reference cancellation with
                            | Ok(Some value) -> found <- Some value
                            | Ok None -> ()
                            | Error reason ->
                                if failure.IsNone then
                                    failure <- Some(source.Id + ": " + reason)

                        match found, failure with
                        | Some value, _ -> return Ok(Secret(reference, value.Reveal()))
                        | None, Some reason -> return Error(SecretUnavailable(reference, reason))
                        | None, None -> return Error(SecretNotFound reference)
                    }
        }
