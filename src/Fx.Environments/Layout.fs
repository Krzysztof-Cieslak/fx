namespace Fx.Environments

open Fx.Components
open Fx.Sdk

/// Where things live in an environment, as paths in the environment's syntax: what host code puts
/// there (envd's state, interpreters' assets, runtimes' owned directories) stays out of the
/// workspace, under the lease's home; components go to the machine's install root.
type EnvLayout =
    {
        /// The OS of the environment, whose path syntax these are.
        Os: Os
        Workspace: string
        /// The environment's own directory (`Lease.Home`).
        Home: string
        /// envd's state: blobs, spilled output, the shadow repository (`<home>/envd`).
        EnvdState: string
        /// Interpreters' assets (`<home>/assets`; `InterpreterSession.AssetsRoot`).
        Assets: string
        /// Owned directories of runtime launches, one per launch (`<home>/owned/<launch>`).
        Owned: string
        /// The component install root of the machine (`<data dir>/runtimes`).
        Components: string
    }

    override this.ToString() = $"layout (home {this.Home})"

[<RequireQualifiedAccess>]
module EnvLayout =
    /// `dir` and `name` joined with the OS's separator.
    let join (os: Os) (dir: string) (name: string) : string =
        let separator = if os = Os.Windows then '\\' else '/'

        if dir.EndsWith separator || dir.EndsWith '/' then
            dir + name
        else
            dir + string separator + name

    /// The layout of a leased environment on a machine.
    let create (machine: EnvMachine) (lease: Lease) : EnvLayout =
        let os = machine.Target.Os
        let home = lease.Home

        {
            Os = os
            Workspace = lease.Workspace
            Home = home
            EnvdState = join os home "envd"
            Assets = join os home "assets"
            Owned = join os home "owned"
            Components = join os machine.DataDir InstallRoot.DirName
        }

    /// Fresh owned directories for one launch (a kernel, a runtime), named by the caller: unique per
    /// launch, a plain name.
    let owned (layout: EnvLayout) (launch: string) : OwnedDirs =
        let root = join layout.Os layout.Owned launch
        let sub = join layout.Os root

        {
            Root = root
            Cwd = sub "cwd"
            Home = sub "home"
            Config = sub "config"
            Cache = sub "cache"
            Temp = sub "tmp"
        }
