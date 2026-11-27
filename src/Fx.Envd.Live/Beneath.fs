namespace Fx.Envd.Live

open System
open System.IO
open Fx.Effects

/// A path beneath a root (T113b): the directories from the root to its parent, and its name. The
/// root is trusted (nothing below the sandbox's writable roots can change what it resolves to); no
/// symbolic link below it is followed.
type internal BeneathTarget =
    {
        /// The root, absolute and normalized.
        Root: string
        /// The directories between the root and the entry, outermost first.
        Directories: string list
        /// The entry's name.
        Name: string
        /// The path as the caller named it (normalized), for errors and entries.
        Path: string
        /// Whether the directories may be opened with one call (`openat2`, `O_NOFOLLOW_ANY`) before
        /// one call per component; tests turn it off to cover the fallback.
        Fast: bool
    }

    /// Every component below the root, the entry's own name last.
    member this.Components = this.Directories @ [ this.Name ]

    /// The path of the root and the first `count` components.
    member this.Prefix(count: int) =
        Path.Combine(Array.ofList (this.Root :: List.truncate count this.Components))

    override this.ToString() = $"{this.Path} beneath {this.Root}"

[<RequireQualifiedAccess>]
module internal Beneath =
    /// The reason of the `InvalidPath` for a path through a symbolic link.
    [<Literal>]
    let LinkReason = "through a symbolic link"

    /// A path refused because `link`, one of its components, is a symbolic link.
    let link (path: string) (link: string) : FsError =
        InvalidPath(path, $"{LinkReason}: {link}")

    let entry (path: string) (name: string) (kind: EntryKind) (size: int64) (modified: Instant) : Entry =
        {
            Path = path
            Name = name
            Kind = kind
            Size = (if kind = EntryKind.File then size else 0L)
            Modified = modified
        }

    /// A temporary file's name next to `name`, as `LocalFsOps.writeAtomic` names them.
    let tempName (name: string) (random: byte array) =
        "."
        + (if name.Length > 40 then name.Substring(0, 40) else name)
        + "."
        + Convert.ToHexStringLower random
        + ".tmp"
