namespace Fx.Keyring

open System
open System.IO
open System.Security.AccessControl
open System.Security.Principal
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// Private files on this machine (`IPrivateFiles`): the `file` credential store's storage
/// (docs/spikes/keychains.md, recommendation 3). An imperative shell with its own entry in
/// analyzers.json; only composition roots use it.
///
/// - **Unix:** the directory is created `0700` (and tightened to it if it is wider), each file
///   `0600` from its creation; protection is the permissions, as with `gh`, `aws` and `gcloud`.
/// - **Windows:** each file gets an ACL with only the user and nothing inherited, and its content is
///   encrypted with DPAPI for the current user, with the product's entropy. A logon session without
///   DPAPI keys (an SSH key logon, a service) writes with the ACL only (`Protection = "acl"`).
/// - **On disk:** one marker byte, then the content: `P` as written, `D` DPAPI-protected. A file
///   protected with DPAPI cannot be read where DPAPI is unavailable, and says so.
/// - **Atomic:** written to `<name>.new` next to the file (synced), then renamed over it. One host
///   writes a data directory at a time (the host's lock), so the staging name is fixed; one left by a
///   crash is replaced.
module LocalPrivateFiles =
    let private plainMarker = byte 'P'
    let private dpapiMarker = byte 'D'

    let private ownerDirectory =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    let private ownerFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    let private classify (name: string) (e: exn) : FsError option =
        match e with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Some(NotFound name)
        | :? UnauthorizedAccessException -> Some(AccessDenied name)
        | :? PathTooLongException -> Some(InvalidPath(name, "too long"))
        | :? IOException as e -> Some(IoFailed(name, e.Message))
        | _ -> None

    let private attempt
        (name: string)
        (cancellation: CancellationToken)
        (operation: unit -> Result<'T, FsError>)
        : Task<Result<'T, FsError>> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<'T, FsError>> cancellation
        else
            match PrivateFiles.checkName name with
            | Error e -> Task.FromResult(Error e)
            | Ok() ->
                try
                    Task.FromResult(operation ())
                with e ->
                    match classify name e with
                    | Some error -> Task.FromResult(Error error)
                    | None -> Task.FromException<Result<'T, FsError>> e

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

    let private createExclusive (path: string) : FileStream =
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
                    UnixCreateMode = ownerFile
                )
            )

    let private ensureDirectory (root: string) =
        if OperatingSystem.IsWindows() then
            Directory.CreateDirectory root |> ignore
        else
            Directory.CreateDirectory(root, ownerDirectory) |> ignore

            if File.GetUnixFileMode root <> ownerDirectory then
                File.SetUnixFileMode(root, ownerDirectory)

    let private seal (entropy: byte array) (useDpapi: bool) (name: string) (content: byte array) =
        if useDpapi then
            match Dpapi.protect entropy content with
            | Ok sealedBytes -> Ok(Array.append [| dpapiMarker |] sealedBytes)
            | Error e -> Error(IoFailed(name, string e))
        else
            Ok(Array.append [| plainMarker |] content)

    let private unseal (entropy: byte array) (name: string) (stored: byte array) : Result<byte array, FsError> =
        if stored.Length = 0 then
            Error(IoFailed(name, "the file is empty: it was not written by this store"))
        elif stored[0] = plainMarker then
            Ok(Array.sub stored 1 (stored.Length - 1))
        elif stored[0] = dpapiMarker then
            match Dpapi.unprotect entropy (Array.sub stored 1 (stored.Length - 1)) with
            | Ok content -> Ok content
            | Error DpapiError.NotSupported ->
                Error(IoFailed(name, "the file is encrypted with Windows DPAPI and cannot be read on this OS"))
            | Error DpapiError.NoLogonSession ->
                Error(IoFailed(name, "the file is encrypted with DPAPI, which this logon session cannot use"))
            | Error e -> Error(IoFailed(name, string e))
        else
            Error(IoFailed(name, "the file was not written by this store"))

    /// Whether this logon session can encrypt with DPAPI (Windows only).
    let private dpapiWorks (entropy: byte array) =
        OperatingSystem.IsWindows()
        && (match Dpapi.protect entropy [| 0uy |] with
            | Ok _ -> true
            | Error _ -> false)

    /// Private files in `root`, an absolute directory only this user should enter (created on the
    /// first write). `owner` names whose files they are: DPAPI's second key part, so another program
    /// of the user must know it too (the composition root passes the product's name). On Windows it
    /// checks once whether DPAPI works in this logon session.
    let create (root: string) (owner: string) : PrivateFiles =
        if not (Path.IsPathFullyQualified root) then
            invalidArg (nameof root) "The private files' directory must be an absolute path."

        let entropy = Encoding.UTF8.GetBytes(owner + ".private-files.v1")
        let useDpapi = dpapiWorks entropy

        let protection =
            if not (OperatingSystem.IsWindows()) then "owner-only"
            elif useDpapi then "dpapi"
            else "acl"

        let pathOf (name: string) = Path.Combine(root, name)

        {
            Protection = protection
            Read =
                fun name cancellation ->
                    attempt name cancellation (fun () ->
                        let path = pathOf name

                        if File.Exists path then
                            unseal entropy name (File.ReadAllBytes path) |> Result.map Some
                        else
                            Ok None)
            Write =
                fun name content cancellation ->
                    attempt name cancellation (fun () ->
                        match seal entropy useDpapi name content with
                        | Error e -> Error e
                        | Ok bytes ->
                            ensureDirectory root
                            let path = pathOf name
                            let staging = path + ".new"

                            if File.Exists staging then
                                File.Delete staging

                            do
                                use stream = createExclusive staging
                                stream.Write(bytes, 0, bytes.Length)
                                stream.Flush true

                            File.Move(staging, path, true)
                            Ok())
            Delete =
                fun name cancellation ->
                    attempt name cancellation (fun () ->
                        let path = pathOf name

                        if File.Exists path then
                            File.Delete path
                            Ok true
                        else
                            Ok false)
        }

    /// Whether only the current user may access a file: no group or other bits on Unix; on Windows,
    /// no inherited entries and every entry for the user. For tests and `auth status`.
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
            (File.GetUnixFileMode path &&& ~~~ownerFile) = enum<UnixFileMode> 0
