namespace Fx.Host

open System
open System.IO
open System.Text
open Fx.Effects

/// The branch a workspace has checked out, from git's own files (`.git/HEAD`, or the `gitdir:` a
/// worktree's `.git` file names), read through the environment's files: no git process.
[<RequireQualifiedAccess>]
module GitHead =
    /// The branch `HEAD`'s text names (`ref: refs/heads/<branch>`), or a detached commit's short id.
    let parse (head: string) : string option =
        let text = head.Trim()

        if text.StartsWith("ref: refs/heads/", StringComparison.Ordinal) then
            Some(text.Substring "ref: refs/heads/".Length)
        elif text.StartsWith("ref: ", StringComparison.Ordinal) then
            Some(text.Substring "ref: ".Length)
        elif text.Length >= 7 && text |> Seq.forall Uri.IsHexDigit then
            Some(text.Substring(0, 7))
        else
            None

    /// Where a worktree's `.git` file says its git directory is (`gitdir: <path>`).
    let gitDir (dotGit: string) : string option =
        let text = dotGit.Trim()

        if text.StartsWith("gitdir:", StringComparison.Ordinal) then
            Some(text.Substring("gitdir:".Length).Trim())
        else
            None

    let private branchOf
        (fs: FsRead)
        (join: string -> string -> string)
        (workspace: string)
        (cancellation: System.Threading.CancellationToken)
        : System.Threading.Tasks.Task<string option> =
        task {
            let text (path: string) =
                task {
                    match! fs.ReadFile path cancellation with
                    | Ok bytes -> return Some(Encoding.UTF8.GetString bytes)
                    | Error _ -> return None
                }

            let dotGit = join workspace ".git"

            match! fs.Stat dotGit cancellation with
            | Ok entry when entry.Kind = EntryKind.Directory ->
                let! head = text (join dotGit "HEAD")
                return head |> Option.bind parse
            | Ok entry when entry.Kind = EntryKind.File ->
                match! text dotGit with
                | Some content ->
                    match gitDir content with
                    | Some dir ->
                        let dir = if Path.IsPathRooted dir then dir else join workspace dir
                        let! head = text (join dir "HEAD")
                        return head |> Option.bind parse
                    | None -> return None
                | None -> return None
            | _ -> return None
        }

    /// The workspace's branch, if it is a git checkout. `join` builds paths in the environment's
    /// syntax.
    let read<'R when 'R :> IScope>
        (fs: FsRead)
        (join: string -> string -> string)
        (workspace: string)
        : Eff<'R, string option> =
        fun env -> branchOf fs join workspace env.Scope.Cancellation
