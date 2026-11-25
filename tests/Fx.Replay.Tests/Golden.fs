/// Golden files of the replay tests, found from the test binaries (CI maps `__SOURCE_DIRECTORY__` to
/// `/_/`, so the repository is found by walking up to Fx.slnx).
module Fx.Replay.Tests.Golden

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

/// The path of a file in `tests/Fx.Replay.Tests/golden`.
let path (name: string) =
    Path.Combine(root, "tests", "Fx.Replay.Tests", "golden", name)

let updating () =
    Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1"

/// Writes a golden file (`UPDATE_GOLDEN=1`).
let write (name: string) (text: string) =
    let file = path name
    Directory.CreateDirectory(Path.GetDirectoryName file |> nonNull) |> ignore
    File.WriteAllText(file, text)

/// The content of a golden file.
let read (name: string) =
    let file = path name
    Expect.isTrue (File.Exists file) $"missing golden file {name}; run the tests with UPDATE_GOLDEN=1"
    File.ReadAllText file

/// The content of a file that records what an old version wrote. It is written once, when missing, and
/// never rewritten: it is what readers must keep reading.
let kept (name: string) (written: string) : string =
    let file = path name

    if not (File.Exists file) then
        if updating () then
            write name written
        else
            failtest $"missing kept file {name}; run the tests with UPDATE_GOLDEN=1 once"

    File.ReadAllText file
