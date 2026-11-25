/// The local listener's token in a file only the user can read (spec §18; docs/spikes/kestrel.md):
/// local clients read it to connect, so presenting it proves they run as the user, or that the user
/// gave it to them (`fx.identity.local-os-user`'s loopback proof). An imperative shell with its own
/// entry in analyzers.json.
module Fx.Transport.Http.TokenFile

open System
open System.IO
open System.Security.AccessControl
open System.Security.Principal
open System.Text

/// Unix mode `0600`.
let private ownerReadWrite = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

/// Only the current user, nothing inherited from the directory.
let private ownerOnlySecurity () : FileSecurity =
    use identity = WindowsIdentity.GetCurrent()

    let user =
        match identity.User with
        | null -> invalidOp "The process has no user SID."
        | sid -> sid

    let security = FileSecurity()
    security.SetAccessRuleProtection(true, false)
    security.SetOwner user
    security.AddAccessRule(FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow))
    security

let private create (path: string) : FileStream =
    if OperatingSystem.IsWindows() then
        FileSystemAclExtensions.Create(
            FileInfo path,
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            4096,
            FileOptions.None,
            ownerOnlySecurity ()
        )
    else
        // Created 0600, never widened later: no moment when others could open it.
        new FileStream(
            path,
            FileStreamOptions(
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = ownerReadWrite
            )
        )

/// Writes the token to `path`, replacing the file, readable and writable by the current user only
/// (mode `0600` on Unix, an ACL with only the user on Windows). The file appears whole: it is written
/// next to `path` and renamed over it. Put it in a directory only the user can enter (`0700`).
let write (path: string) (token: string) : unit =
    let staging = path + ".new"

    if File.Exists staging then
        File.Delete staging

    do
        use stream = create staging
        let bytes = Encoding.UTF8.GetBytes token
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush true

    File.Move(staging, path, true)

/// The token in a file `write` wrote (a client reads it to connect).
let read (path: string) : string = File.ReadAllText(path).Trim()

/// Whether only the current user may access the file: no group or other bits on Unix; on Windows,
/// no inherited entries and every entry for the user.
let isOwnerOnly (path: string) : bool =
    if OperatingSystem.IsWindows() then
        use identity = WindowsIdentity.GetCurrent()

        match identity.User with
        | null -> false
        | user ->
            let security = FileSystemAclExtensions.GetAccessControl(FileInfo path)

            let rules =
                security.GetAccessRules(true, true, typeof<SecurityIdentifier>)
                |> Seq.cast<AuthorizationRule>
                |> List.ofSeq

            security.AreAccessRulesProtected
            && not rules.IsEmpty
            && rules
               |> List.forall (fun rule -> not rule.IsInherited && rule.IdentityReference.Value = user.Value)
    else
        (File.GetUnixFileMode path &&& ~~~ownerReadWrite) = enum<UnixFileMode> 0
