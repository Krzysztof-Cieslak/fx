namespace Fx.Effects

open System
open System.Threading
open System.Threading.Tasks

/// Files only this OS user can read: where the `file` credential store keeps credentials (spec §11.4,
/// docs/spikes/keychains.md). Files are named, not pathed: the host roots the interpreter in a
/// directory of its own (`<data dir>/credentials`), so code holding the capability can touch nothing
/// else. Host-only: environments never get it (spec §18).
///
/// - **Owner-only:** on Unix the directory is mode `0700` and each file `0600`, created so (never
///   widened later); on Windows each file has an ACL with only the user, nothing inherited, and its
///   content is encrypted with the user's DPAPI key.
/// - **Atomic:** a write is staged next to the file and renamed over it, so a reader sees the old
///   content or the new, never part of either.
///
/// Interpreters: `LocalPrivateFiles` (Fx.Keyring) on this machine, `InMemoryPrivateFiles`
/// (Fx.Effects.Fakes). Contents are secrets: a recording interpreter journals names and outcomes,
/// never bytes.
type PrivateFiles =
    {
        /// How the files are protected, for display (`auth status`): `dpapi` (Windows: encrypted
        /// with the user's DPAPI key, owner-only ACL), `acl` (Windows without DPAPI keys: the ACL
        /// only), `owner-only` (Unix: modes `0700` and `0600`), `memory` (the fake).
        Protection: string
        /// The content of the named file; `None` if there is none.
        Read: string -> CancellationToken -> Task<Result<byte array option, FsError>>
        /// Creates or replaces the named file, atomically.
        Write: string -> byte array -> CancellationToken -> Task<Result<unit, FsError>>
        /// Deletes the named file; `false` if there was none.
        Delete: string -> CancellationToken -> Task<Result<bool, FsError>>
    }

    override this.ToString() = "PrivateFiles(" + this.Protection + ")"

/// An environment with files only this OS user can read.
type IPrivateFiles =
    abstract PrivateFiles: PrivateFiles

module PrivateFiles =
    let private isNameChar (c: char) =
        Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-'

    /// Whether a name is a private file's: 1–64 characters of `A-Z a-z 0-9 . _ -`, starting with a
    /// letter or digit. No directories, no `..`, nothing a filesystem treats specially.
    let isName (name: string) : bool =
        not (String.IsNullOrEmpty name)
        && name.Length <= 64
        && Char.IsAsciiLetterOrDigit name[0]
        && Seq.forall isNameChar name

    /// `InvalidPath` for a name `isName` refuses.
    let checkName (name: string) : Result<unit, FsError> =
        if isName name then
            Ok()
        else
            Error(
                InvalidPath(
                    name,
                    "a private file's name has 1 to 64 letters, digits, '.', '_' or '-', and no directories"
                )
            )

    let read<'R when 'R :> IPrivateFiles and 'R :> IScope>
        (name: string)
        : Eff<'R, Result<byte array option, FsError>> =
        fun env -> env.PrivateFiles.Read name env.Scope.Cancellation

    let write<'R when 'R :> IPrivateFiles and 'R :> IScope>
        (name: string)
        (content: byte array)
        : Eff<'R, Result<unit, FsError>> =
        fun env -> env.PrivateFiles.Write name content env.Scope.Cancellation

    let delete<'R when 'R :> IPrivateFiles and 'R :> IScope> (name: string) : Eff<'R, Result<bool, FsError>> =
        fun env -> env.PrivateFiles.Delete name env.Scope.Cancellation
