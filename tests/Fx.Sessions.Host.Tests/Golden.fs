/// Golden files of the session host tests, found from the test binaries (CI maps
/// `__SOURCE_DIRECTORY__` to `/_/`, so the repository is found by walking up to Fx.slnx).
module Fx.Sessions.Host.Tests.Golden

open System
open System.IO
open Expecto

let private root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
        | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
        | dir -> up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// The path of a file in `tests/Fx.Sessions.Host.Tests/golden`.
let path (name: string) =
    Path.Combine(root, "tests", "Fx.Sessions.Host.Tests", "golden", name)

/// Expects `actual` to equal the golden file; `UPDATE_GOLDEN=1` rewrites it (review the diff).
let expect (name: string) (actual: string) =
    let file = path name

    if Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1" then
        Directory.CreateDirectory(Path.GetDirectoryName file |> nonNull) |> ignore
        File.WriteAllText(file, actual)
    else
        Expect.isTrue (File.Exists file) $"missing golden file {name}; run the tests with UPDATE_GOLDEN=1"
        Expect.equal (File.ReadAllText file) actual $"{name} (rerun the tests with UPDATE_GOLDEN=1 to update)"
