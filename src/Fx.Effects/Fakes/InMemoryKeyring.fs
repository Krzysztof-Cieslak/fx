namespace Fx.Effects.Fakes

open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// A keyring held in memory, for tests and `fx test` environments. It can play a locked or
/// unavailable keyring, so code that falls back (spec §11.4: `keyring`, then `file`) can be tested
/// without a headless machine.
type InMemoryKeyring(maxValueBytes: int) =
    let gate = obj ()
    let mutable secrets: Map<string * string, string> = Map.empty
    let mutable status = KeyringStatus.Ready

    let guarded (cancellation: CancellationToken) (body: unit -> Result<'T, KeyringError>) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<'T, KeyringError>> cancellation
        else
            lock gate (fun () ->
                match status with
                | KeyringStatus.Ready -> body ()
                | KeyringStatus.Locked -> Error KeyringError.Locked
                | KeyringStatus.Unavailable reason -> Error(KeyringError.Unavailable reason)
                | KeyringStatus.Failed(code, message) -> Error(KeyringError.Failed(code, message)))
            |> Task.FromResult

    let id (key: KeyringKey) = key.Service.Name, key.Account

    new() = InMemoryKeyring 65536

    /// What `Probe` reports, and how operations fail when it is not `Ready`.
    member _.Status
        with get () = lock gate (fun () -> status)
        and set value = lock gate (fun () -> status <- value)

    member _.Keyring: Keyring =
        {
            Backend = "memory"
            MaxValueBytes = maxValueBytes
            Probe =
                fun cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<KeyringStatus> cancellation
                    else
                        Task.FromResult(lock gate (fun () -> status))
            Read =
                fun key cancellation ->
                    guarded cancellation (fun () -> Ok(secrets.TryFind(id key) |> Option.map SecretText))
            Write =
                fun key value cancellation ->
                    guarded cancellation (fun () ->
                        if Keyring.byteCount value > maxValueBytes then
                            Error(KeyringError.TooLarge maxValueBytes)
                        else
                            secrets <- secrets.Add(id key, value.Reveal())
                            Ok())
            Delete =
                fun key cancellation ->
                    guarded cancellation (fun () ->
                        let existed = secrets.ContainsKey(id key)
                        secrets <- secrets.Remove(id key)
                        Ok existed)
            List =
                fun service cancellation ->
                    guarded cancellation (fun () ->
                        secrets
                        |> Map.toList
                        |> List.choose (fun ((s, account), _) -> if s = service.Name then Some account else None)
                        |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))
                        |> Ok)
        }

    override _.ToString() = "InMemoryKeyring"
