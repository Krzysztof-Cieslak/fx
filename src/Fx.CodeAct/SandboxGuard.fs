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
/// envd follows symbolic links, and a sandboxed process can make them where it may write: a path
/// through a link inside a writable root is refused (read it with the shell, which runs in the
/// sandbox). A process that swaps a directory for a link between this check and the operation is
/// not stopped: resolving paths beneath their roots in envd is follow-up work (docs/modules/sandbox.md).
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

    let private rank (access: PathAccess) =
        match access with
        | PathAccess.NoAccess -> 0
        | PathAccess.ReadOnly -> 1
        | PathAccess.ReadWrite -> 2

    /// The components of a path that lie inside a writable root of the policy (where a sandboxed
    /// process can make links), outermost first.
    let private insideWritable (policy: SandboxPolicy) (path: string) : string list =
        SandboxPolicy.rules policy
        |> List.filter (fun rule -> rule.Access = PathAccess.ReadWrite && SandboxPath.isUnder rule.Root path)
        |> List.collect (fun rule -> SandboxPath.between rule.Root path)
        |> List.distinct

    /// Refuses a host call that touches a path the policy does not give it, or goes through a link
    /// inside a writable root.
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
            | None ->
                let components =
                    needs
                    |> List.collect (fun (path, _) -> insideWritable policy path)
                    |> List.distinct

                if components.IsEmpty then
                    return Ok()
                else
                    let! entries = FsOps.statMany components

                    let link =
                        List.zip components entries
                        |> List.tryPick (fun (path, entry) ->
                            match entry with
                            | Ok entry when entry.Kind = EntryKind.Symlink -> Some path
                            | _ -> None)

                    match link with
                    | Some path ->
                        return
                            Error(
                                HostCallError.Denied
                                    $"{path} is a symbolic link: file bindings do not follow links in a sandboxed environment; use the shell, which runs in the sandbox"
                            )
                    | None -> return Ok()
        }
