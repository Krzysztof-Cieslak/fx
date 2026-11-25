/// `fx.store.ephemeral` (spec §11.4, §16.4): a credential store in the host's memory, forgotten when
/// the host stops. Nothing is written anywhere: for CI and throwaway hosts, with credentials from
/// the environment (the `env` auth method and secret source). A native extension built only on the
/// public SDK. See README.md and docs/modules/auth.md.
module Fx.Ext.StoreEphemeral.StoreEphemeral

open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Effects
open Fx.Sdk

/// What `credentials.store` selects.
[<Literal>]
let Kind = "ephemeral"

/// A store in memory. Each call makes a new, empty one. Thread-safe.
let create () : CredentialStore =
    let gate = obj ()
    let credentials: Map<string, StoredCredential> ref = ref Map.empty

    let guarded (cancellation: CancellationToken) (body: unit -> 'T) : Task<'T> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<'T> cancellation
        else
            Task.FromResult(lock gate body)

    {
        Kind = Kind
        Protection = "memory"
        Probe = fun cancellation -> guarded cancellation (fun () -> StoreStatus.Ready)
        Read = fun id cancellation -> guarded cancellation (fun () -> Ok(credentials.Value.TryFind id.Name))
        Write =
            fun id credential cancellation ->
                guarded cancellation (fun () ->
                    credentials.Value <- credentials.Value.Add(id.Name, credential)
                    Ok())
        Delete =
            fun id cancellation ->
                guarded cancellation (fun () ->
                    let existed = credentials.Value.ContainsKey id.Name
                    credentials.Value <- credentials.Value.Remove id.Name
                    Ok existed)
        List =
            fun cancellation ->
                guarded cancellation (fun () ->
                    credentials.Value
                    |> Map.toList
                    |> List.choose (fun (name, _) -> CredentialId.tryCreate name |> Result.toOption)
                    |> Ok)
    }

let def: CredentialStoreDef =
    {
        Kind = Kind
        Description = "Keeps credentials in the host's memory: forgotten when the host stops, never written to disk."
        Open = fun _ -> Task.FromResult(create ())
    }

/// The extension's id: `fx.store.ephemeral`.
let id = Product.extensionId "store.ephemeral"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "A credential store in the host's memory, forgotten when the host stops."
        Contributions = [ Declare.contribution CredentialStores.contributionType Kind ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate = Eff.ret (Activation.create [ Contribution.create CredentialStores.contributionType Kind def ] [])
    }
