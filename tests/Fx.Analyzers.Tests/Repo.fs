/// The repository checkout the tests run in.
module Fx.Analyzers.Tests.Repo

open System.IO
open Fx.Analyzers

/// The repository root: the directory with analyzers.json above the test binaries in artifacts/.
let root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith $"no {Config.FileName} above {System.AppContext.BaseDirectory}"
        | dir when File.Exists(Path.Combine(dir.FullName, Config.FileName)) -> dir.FullName
        | dir -> up dir.Parent

    up (DirectoryInfo System.AppContext.BaseDirectory)

/// Repository files (relative, `/`-separated), skipping build output and dot-directories.
let files () =
    let skipped (name: string) =
        name.StartsWith '.' || name = "artifacts" || name = "node_modules"

    let rec walk (dir: string) =
        [
            for file in Directory.GetFiles dir do
                Path.GetRelativePath(root, file).Replace('\\', '/')
            for sub in Directory.GetDirectories dir do
                if not (skipped (Path.GetFileName sub)) then
                    yield! walk sub
        ]

    walk root
