/// `LocalPrivateFiles`: this machine's private files pass the private-files suite on every OS, are
/// owner-only, and are sealed with DPAPI on Windows.
module Fx.Keyring.Tests.PrivateFilesTests

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx
open Fx.Effects
open Fx.Keyring
open Fx.Effects.Tests.Conformance

let private root () =
    Path.Combine(Path.GetTempPath(), Product.Name + "-private-files-" + Guid.NewGuid().ToString("N"), "credentials")

let private sync (task: Threading.Tasks.Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "LocalPrivateFiles"
        [
            PrivateFilesConformance.tests "this machine" (fun () ->
                LocalPrivateFiles.create (root ()) (Product.Name + "-tests"))

            test "files are owner-only, in an owner-only directory, and sealed on Windows" {
                let dir = root ()
                let files = LocalPrivateFiles.create dir (Product.Name + "-tests")
                let secret = "sk-" + Guid.NewGuid().ToString("N")

                match sync (files.Write "secret.json" (Encoding.UTF8.GetBytes secret) CancellationToken.None) with
                | Ok() -> ()
                | Error e -> failtest (string e)

                let path = Path.Combine(dir, "secret.json")
                Expect.isTrue (LocalPrivateFiles.isOwnerOnly path) "owner-only"
                let onDisk = Encoding.UTF8.GetString(File.ReadAllBytes path)

                if OperatingSystem.IsWindows() then
                    Expect.equal files.Protection "dpapi" "DPAPI"
                    Expect.isFalse (onDisk.Contains secret) "encrypted"
                else
                    Expect.equal files.Protection "owner-only" "modes"

                    Expect.equal
                        (File.GetUnixFileMode dir)
                        (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                        "0700"
            }

            test "a wider directory is tightened, and a file another owner sealed is refused" {
                let dir = root ()

                if not (OperatingSystem.IsWindows()) then
                    Directory.CreateDirectory(
                        dir,
                        UnixFileMode.UserRead
                        ||| UnixFileMode.UserWrite
                        ||| UnixFileMode.UserExecute
                        ||| UnixFileMode.GroupRead
                        ||| UnixFileMode.GroupExecute
                        ||| UnixFileMode.OtherRead
                        ||| UnixFileMode.OtherExecute
                    )
                    |> ignore

                let files = LocalPrivateFiles.create dir (Product.Name + "-tests")
                sync (files.Write "a" "x"B CancellationToken.None) |> ignore

                if not (OperatingSystem.IsWindows()) then
                    Expect.equal
                        (File.GetUnixFileMode dir)
                        (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                        "tightened"

                File.WriteAllBytes(Path.Combine(dir, "foreign"), "Xnot ours"B)

                match sync (files.Read "foreign" CancellationToken.None) with
                | Error(IoFailed _) -> ()
                | other -> failtest $"expected IoFailed, got {other}"

                if OperatingSystem.IsWindows() then
                    let other = LocalPrivateFiles.create dir "another-owner"

                    match sync (other.Read "a" CancellationToken.None) with
                    | Error(IoFailed _) -> ()
                    | result -> failtest $"expected IoFailed, got {result}"
            }
        ]
