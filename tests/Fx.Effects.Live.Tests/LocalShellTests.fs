module Fx.Effects.Live.Tests.LocalShellTests

open Expecto
open Fx.Effects.Live
open Fx.Effects.Live.Tests.Conformance

[<Tests>]
let tests =
    ShellConformance.tests "LocalShell" (fun cwd -> LocalShell.create (LocalShell.defaultShell ()) cwd) ignore
