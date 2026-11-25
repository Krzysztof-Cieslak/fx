/// Paths in the repository, found from the test binaries in artifacts/, and generated files kept in
/// it (CI builds map `__SOURCE_DIRECTORY__` to `/_/`, deterministic paths).
module Fx.Sdk.Process.Tests.Repo

open System
open System.IO
open Expecto

/// The repository root: the directory with Fx.slnx above the test binaries.
let root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
        | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
        | dir -> up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// A path below the root.
let path (segments: string list) =
    Path.Combine(Array.ofList (root :: segments))

/// Expects `actual` to equal a generated file in the repository. With `UPDATE_GOLDEN=1` the file is
/// rewritten instead; review the diff before committing it.
let expectGenerated (segments: string list) (actual: string) =
    let file = path segments

    if Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1" then
        Directory.CreateDirectory(Path.GetDirectoryName file |> nonNull) |> ignore
        File.WriteAllText(file, actual)
    else
        let name = String.Join("/", segments)
        Expect.isTrue (File.Exists file) $"missing generated file {name}; run the tests with UPDATE_GOLDEN=1"
        Expect.equal (File.ReadAllText file) actual $"{name} (rerun the tests with UPDATE_GOLDEN=1 to update)"
