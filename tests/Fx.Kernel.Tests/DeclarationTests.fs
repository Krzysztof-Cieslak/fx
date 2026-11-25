module Fx.Kernel.Tests.DeclarationTests

open Expecto
open Fx.Kernel

[<Tests>]
let tests =
    testList
        "declarations"
        [
            test "the reference kernel's protocol.d.ts is generated from the codecs" {
                Repo.expectGenerated
                    [ "packages"; "kernel-bun-ts"; "src"; "protocol.d.ts" ]
                    (KernelDeclarations.render ())
            }
        ]
