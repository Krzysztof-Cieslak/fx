module Fx.Pty.Tests.LocalTerminalTests

open Expecto
open Fx.Pty
open Fx.Pty.Tests.Conformance

[<Tests>]
let tests =
    TerminalConformance.tests
        "LocalTerminal"
        (fun dir -> LocalTerminal.create (LocalTerminal.defaultShell ()) dir)
        ignore
