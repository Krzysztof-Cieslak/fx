namespace Fx.Sdk

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts

/// Names one contribution of one extension: `<extension>/<type>/<id>`.
type Address =
    {
        Extension: string
        Type: string
        Id: string
    }

    override this.ToString() =
        this.Extension + "/" + this.Type + "/" + this.Id

/// Why the host could not get an answer from an extension's code in another process.
[<RequireQualifiedAccess>]
type RemoteFailure =
    /// The extension does not know the extension, contribution or method that was named.
    | Unknown of what: string
    /// The extension could not decode what the host sent.
    | InvalidInput of Issue list
    /// The extension's code failed (a defect there), or its runtime refused.
    | Failed of reason: string
    /// The connection to the extension is gone, or it answered something that does not decode.
    | Unreachable of reason: string

    override this.ToString() =
        match this with
        | Unknown what -> "unknown " + what
        | InvalidInput issues -> "invalid input:\n" + Issue.renderAtMost 10 issues
        | Failed reason -> "failed: " + reason
        | Unreachable reason -> "unreachable: " + reason

/// Why a call to an extension's method returned no result.
[<RequireQualifiedAccess>]
type MethodFailure =
    /// The extension has no such method.
    | UnknownMethod of name: string
    /// The params do not decode with the method's codec.
    | InvalidParams of Issue list
    /// The method failed with one of its typed errors (an expected failure): its text and encoding.
    | Failed of message: string * data: Json
    /// The method failed with a defect (an exception), in process or in the extension's process. The
    /// detail is for diagnostics: it differs between runtimes.
    | Crashed of detail: string
    /// The extension's process is gone or answered something that does not decode.
    | Unreachable of reason: string

    override this.ToString() =
        match this with
        | UnknownMethod name -> "unknown method " + name
        | InvalidParams issues -> "invalid params:\n" + Issue.renderAtMost 10 issues
        | Failed(message, _) -> message
        | Crashed detail -> "crashed: " + detail
        | Unreachable reason -> "unreachable: " + reason

/// What the host asks an extension process to activate: one of the extensions it serves (by id), or,
/// for an extension runtime it contributes (e.g. `bun`), a package it should load.
type ActivateParams =
    {
        Extension: string
        /// The manifest of a package to load with a runtime the process contributes.
        Manifest: Manifest option
        /// That package's directory.
        Root: string option
    }

    override this.ToString() = "activate " + this.Extension

/// What an extension process answers when it activated an extension: its manifest (id and version
/// are checked against the admitted one), the contributions it provides with their JSON forms, and
/// its methods.
type Activated =
    {
        Manifest: Manifest
        Contributions: ContributionDecl list
        Methods: string list
    }

    override this.ToString() =
        $"activated {this.Manifest.Id} ({this.Contributions.Length} contributions, {this.Methods.Length} methods)"

/// The host's handle on an extension process (spec §16.1 `process` runtime, and any runtime hosted
/// by one, such as `bun`): the extension protocol as functions with data in and out, like a
/// capability. The core implements it over JSON-RPC; tests can implement it in memory.
type RemotePeer =
    {
        Activate: ActivateParams -> CancellationToken -> Task<Result<Activated, RemoteFailure>>
        /// Runs a contribution's code (a gate, a transform, …) with JSON in and out.
        Invoke: Address -> Json -> CancellationToken -> Task<Result<Json, RemoteFailure>>
        /// Calls a method of an extension's RPC contract.
        Call: string -> string -> Json -> CancellationToken -> Task<Result<Json, MethodFailure>>
        Deactivate: string -> CancellationToken -> Task<Result<unit, RemoteFailure>>
    }

    override _.ToString() = "RemotePeer"

/// One contribution's code in an extension process: where it is, and how to reach it.
type Remote =
    {
        Peer: RemotePeer
        Address: Address
    }

    override this.ToString() = "remote " + string this.Address

/// A contribution's code failed: in process (an exception) or in an extension process (a
/// `RemoteFailure`). Hooks turn it into what their kind says: a gate denies, a transform fails the
/// pipeline, an observer's failure is reported. The message never carries the cause's text, which
/// differs between runtimes; `Detail` has it for diagnostics.
type ContributionFailedException(address: Address, detail: string, inner: exn | null) =
    inherit Exception($"{address} failed", inner)

    member _.Address = address
    member _.Detail = detail

module Remote =
    /// Runs the remote code with JSON in and out, in the caller's scope. A failure is a defect
    /// (`ContributionFailedException`): the hook that called it decides what that means.
    let invoke<'R when 'R :> Fx.Effects.IScope> (remote: Remote) (input: Json) : Fx.Effects.Eff<'R, Json> =
        fun env ->
            task {
                match! remote.Peer.Invoke remote.Address input env.Scope.Cancellation with
                | Ok output -> return output
                | Error failure -> return raise (ContributionFailedException(remote.Address, string failure, null))
            }

    /// Decodes what remote code returned; a value that does not decode is the extension's defect.
    let decodeOutput (remote: Remote) (codec: Codec<'T>) (json: Json) : 'T =
        match codec.Decode json with
        | Ok value -> value
        | Error issues ->
            raise (
                ContributionFailedException(remote.Address, "invalid output:\n" + Issue.renderAtMost 10 issues, null)
            )
