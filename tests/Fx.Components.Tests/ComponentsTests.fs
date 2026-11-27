module Fx.Components.Tests.ComponentsTests

open System.Threading.Tasks
open Expecto
open Fx.Components
open Fx.Components.Tests.Support
open Fx.Effects
open Fx.Sdk

type private Env =
    {
        Components: Components
        Scope: Scope
    }

    interface IComponents with
        member env.Components = env.Components

    interface IScope with
        member env.Scope = env.Scope

[<Tests>]
let tests =
    testList
        "Components capability"
        [
            test "the components phase installs what a manifest requires, held by the extension" {
                let requests = ResizeArray<EnsureRequest>()

                let env answer =
                    {
                        Components =
                            {
                                Ensure =
                                    fun request _ ->
                                        requests.Add request
                                        Task.FromResult answer
                            }
                        Scope = Scope.root
                    }

                let manifest =
                    { Manifest.create "acme.tools" "1.0.0" Runtimes.Process with
                        Requires = [ { Id = "bun"; Version = "^1.4" } ]
                    }

                Expect.equal (runIn (env (Ok [])) (Components.installFor manifest)) (Ok []) "installed"

                Expect.equal
                    (List.ofSeq requests)
                    [
                        {
                            Holder = "extension:acme.tools"
                            Requires = manifest.Requires
                        }
                    ]
                    "the request"

                Expect.equal
                    (runIn
                        (env (Error(NotLocked "the lockfile has no component bun; plan it again")))
                        (Components.installFor manifest))
                    (Error "components: the lockfile has no component bun; plan it again")
                    "the reason"
            }

            test "the default install root is under the per-user data directory" {
                Expect.equal
                    (InstallRoot.defaultFor Os.Linux "/home/u" "")
                    (System.IO.Path.Combine("/home/u", Fx.Product.homeDirName, "runtimes"))
                    "Unix"

                Expect.equal
                    (InstallRoot.defaultFor Os.Windows "" @"C:\Users\u\AppData\Local")
                    (System.IO.Path.Combine(@"C:\Users\u\AppData\Local", Fx.Product.windowsDataDirName, "runtimes"))
                    "Windows"
            }
        ]
