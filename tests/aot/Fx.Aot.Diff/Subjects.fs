/// What the diff runs: Native AOT executables (subjects) and the command lines (cases) to run them with.
module Fx.Aot.Diff.Subjects

open Fx

/// One command line, run on the JIT and natively.
type Case =
    {
        Name: string
        Args: string list
        /// The JIT run must exit with 0. Off for cases that test failure exit codes.
        MustSucceed: bool
    }

    override this.ToString() = this.Name

/// How a subject's cases are found.
type Cases =
    /// A fixed list of cases.
    | Fixed of Case list
    /// Ask the program: run `list` on both sides (which must agree), then one case per listed name.
    | Listed of list: string list * run: (string -> string list)

    override this.ToString() =
        match this with
        | Fixed cases -> $"fixed {cases.Length}"
        | Listed(list, _) -> "listed by " + String.concat " " list

/// A Native AOT executable project.
type Subject =
    {
        /// Display name.
        Name: string
        /// Project directory, relative to the repository root, as path segments.
        ProjectDir: string list
        /// MSBuild project name: the directory under artifacts/bin and artifacts/publish.
        Project: string
        /// Assembly name: `<Assembly>.dll` on the JIT, `<Assembly>[.exe]` natively.
        Assembly: string
        Cases: Cases
    }

    override this.ToString() = this.Name

let private case name args =
    {
        Name = name
        Args = args
        MustSucceed = true
    }

/// The product binary itself.
let cli: Subject =
    {
        Name = Product.binaryName
        ProjectDir = [ "src"; "Fx.Cli" ]
        Project = "Fx.Cli"
        Assembly = Product.binaryName
        Cases =
            Fixed
                [
                    case "--version" [ "--version" ]
                    case "--help" [ "--help" ]
                    case "(no arguments)" []
                    { case "unknown arguments" [ "--bogus"; "x" ] with
                        MustSucceed = false
                    }
                ]
    }

let private scenariosProject (name: string) (list: string list) : Subject =
    {
        Name = name
        ProjectDir = [ "tests"; "aot"; "Fx.Aot.Scenarios" ]
        Project = "Fx.Aot.Scenarios"
        Assembly = "Fx.Aot.Scenarios"
        Cases = Listed(list, (fun scenario -> [ "run"; scenario ]))
    }

/// The scenarios in tests/aot/Fx.Aot.Scenarios that run on this platform.
let scenarios: Subject = scenariosProject "scenarios" [ "list" ]

/// Scenarios that must differ: the harness's self-test.
let canaries: Subject = scenariosProject "canaries" [ "list"; "--canaries" ]

/// Every subject the diff runs. Add new Native AOT executables here.
let all: Subject list = [ cli; scenarios ]
