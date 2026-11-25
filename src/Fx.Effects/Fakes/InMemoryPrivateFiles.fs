namespace Fx.Effects.Fakes

open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// Private files held in memory, for tests and `fx test` environments (`Protection = "memory"`).
/// Thread-safe. It can play a disk that refuses writes, to test how stores report failures.
type InMemoryPrivateFiles() =
    let gate = obj ()
    let mutable files: Map<string, byte array> = Map.empty
    let mutable failure: FsError option = None

    let guarded (name: string) (cancellation: CancellationToken) (body: unit -> Result<'T, FsError>) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<'T, FsError>> cancellation
        else
            match PrivateFiles.checkName name with
            | Error e -> Task.FromResult(Error e)
            | Ok() ->
                lock gate (fun () ->
                    match failure with
                    | Some e -> Error e
                    | None -> body ())
                |> Task.FromResult

    /// When set, every operation fails with it.
    member _.Failure
        with get () = lock gate (fun () -> failure)
        and set value = lock gate (fun () -> failure <- value)

    /// The files and their contents, as stored.
    member _.Files = lock gate (fun () -> files)

    /// Replaces a file's content behind the capability's back (e.g. to corrupt it).
    member _.Set (name: string) (content: byte array) =
        lock gate (fun () -> files <- files.Add(name, Array.copy content))

    member _.PrivateFiles: PrivateFiles =
        {
            Protection = "memory"
            Read =
                fun name cancellation ->
                    guarded name cancellation (fun () -> Ok(files.TryFind name |> Option.map Array.copy))
            Write =
                fun name content cancellation ->
                    guarded name cancellation (fun () ->
                        files <- files.Add(name, Array.copy content)
                        Ok())
            Delete =
                fun name cancellation ->
                    guarded name cancellation (fun () ->
                        let existed = files.ContainsKey name
                        files <- files.Remove name
                        Ok existed)
        }

    override _.ToString() = "InMemoryPrivateFiles"

/// Environment variables held in memory, for tests and `fx test` environments.
module InMemoryVariables =
    /// Variables from name/value pairs; empty values read as unset, as on a real host.
    let create (variables: (string * string) list) : HostVariables =
        let values = Map.ofList variables

        {
            Read =
                fun name cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<SecretText option> cancellation
                    else
                        match values.TryFind name with
                        | Some value when value <> "" -> Task.FromResult(Some(SecretText value))
                        | _ -> Task.FromResult None
        }
