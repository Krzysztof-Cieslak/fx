/// Golden files: expected output kept next to the tests, in `golden/`.
module Fx.Variants.Tests.Golden

open System
open System.IO
open Expecto

/// `golden/` in the source tree, found from the test binaries in artifacts/: CI builds map
/// `__SOURCE_DIRECTORY__` to `/_/` (deterministic paths).
let private directory =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
        | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
        | dir -> up dir.Parent

    Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "tests", "Fx.Variants.Tests", "golden")

/// Expects `actual` to equal the file `golden/<name>`. With `UPDATE_GOLDEN=1` the file is rewritten
/// instead; review the diff before committing it.
let expect (name: string) (actual: string) =
    let path = Path.Combine(directory, name)
    let actual = actual + "\n"

    if Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1" then
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(path, actual)
    else
        Expect.isTrue (File.Exists path) $"missing golden file {path}; run the tests with UPDATE_GOLDEN=1"
        Expect.equal actual (File.ReadAllText path) $"golden/{name} (rerun with UPDATE_GOLDEN=1 to update)"
