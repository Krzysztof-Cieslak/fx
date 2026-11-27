namespace Fx.CodeAct

open System
open Fx.Contracts
open Fx.Effects
open Fx.Envd
open Fx.Interpreters
open Fx.Kernel
open Fx.Sdk

/// The sandbox at the type level (spec §7.1: attenuation is enforced twice, by types and by the
/// sandbox): the core bindings' file operations run in envd, outside the sandbox, so before each
/// host call this refuses what the sandbox would refuse a process, by the same policy. A read needs
/// read access to its path, a write write access (`SandboxPolicy.access`). On Windows, where no
/// driver confines processes yet, it is the only attenuation.
///
/// A sandboxed process can make symbolic links where it may write, and the check here reads paths as
/// written: so the session's file operations resolve paths beneath the policy's writable roots
/// (`roots`, `EnvClient.beneath`), where envd follows no link (T113b). A path through a link there
/// fails in envd (read it with the shell, which runs in the sandbox), however late the link appears.
[<RequireQualifiedAccess>]
module SandboxGuard =
    let private text (value: Value) : string option =
        match value with
        | Value.Inline(Json.String text) -> Some text
        | _ -> None

    /// The paths a host call of a core binding reads or writes, absolute and normalized, with the
    /// access it needs. Arguments that do not decode name no path (the binding refuses them).
    let paths (workspace: string) (os: Os) (call: HostCall) : (string * PathAccess) list =
        let settings = CoreSettings.defaults workspace os

        let resolve (path: string) =
            SandboxPath.normalize (CoreBindings.resolve settings path)

        let first (access: PathAccess) =
            match call.Args with
            | arg :: _ ->
                match text arg with
                | Some path -> [ resolve path, access ]
                | None -> []
            | [] -> []

        match call.Binding with
        | "fs.read"
        | "fs.stat" -> first PathAccess.ReadOnly
        | "fs.list" ->
            match call.Args with
            | [] -> [ resolve ".", PathAccess.ReadOnly ]
            | _ -> first PathAccess.ReadOnly
        | "fs.write" -> first PathAccess.ReadWrite
        | "fs.patch" ->
            match call.Args with
            | Value.Inline json :: _ ->
                match Codec.decode (Codec.list CoreBindings.Codecs.filePatch) json with
                | Ok patches ->
                    patches
                    |> List.map (fun patch ->
                        match patch with
                        | Create(path, _)
                        | Edit(path, _, _)
                        | Delete(path, _) -> resolve path, PathAccess.ReadWrite)
                | Error _ -> []
            | _ -> []
        | _ -> []

    /// The roots a session's file operations resolve beneath: the policy's writable ones, where a
    /// sandboxed process can make links.
    let roots (policy: SandboxPolicy) : string list =
        SandboxPolicy.rules policy
        |> List.filter (fun rule -> rule.Access = PathAccess.ReadWrite)
        |> List.map (fun rule -> rule.Root)

    let private rank (access: PathAccess) =
        match access with
        | PathAccess.NoAccess -> 0
        | PathAccess.ReadOnly -> 1
        | PathAccess.ReadWrite -> 2

    /// Refuses a host call that touches a path the policy does not give it.
    let check<'R when 'R :> IFsOps and 'R :> IScope>
        (policy: SandboxPolicy)
        (os: Os)
        (call: HostCall)
        : Eff<'R, Result<unit, HostCallError>> =
        eff {
            let needs = paths policy.Workspace os call

            let refused =
                needs
                |> List.tryPick (fun (path, needed) ->
                    let has = SandboxPolicy.access policy path

                    if rank has < rank needed then
                        let what =
                            match needed with
                            | PathAccess.ReadWrite -> "change"
                            | _ -> "read"

                        Some $"the sandbox does not let this session {what} {path} ({has})"
                    else
                        None)

            match refused with
            | Some reason -> return Error(HostCallError.Denied reason)
            | None -> return Ok()
        }
