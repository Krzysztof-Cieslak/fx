/// Environments and helpers for the SDK's tests.
module Fx.Sdk.Tests.Support

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Sdk

/// An extension's environment: its id, a fake clock and a scope.
type ExtEnv =
    {
        Extension: string
        Clock: Clock
        Scope: Scope
    }

    interface IExtensionEnv with
        member env.Extension = env.Extension
        member env.Capabilities = ExtensionCapabilities.none env.Extension

    interface Fx.Variants.IVariants with
        member env.Variants = (ExtensionCapabilities.none env.Extension).Variants

    interface IPackages with
        member env.Packages = (ExtensionCapabilities.none env.Extension).Packages

    interface IGrants with
        member env.Grants = (ExtensionCapabilities.none env.Extension).Grants

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Scope

        member env.WithScope scope =
            { env with Scope = scope } :> IExtensionEnv

    override env.ToString() = $"ExtEnv({env.Extension})"

/// The caller's environment: only a scope.
type CallerEnv =
    {
        Scope: Scope
    }

    interface IScopeWith<CallerEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { Scope = scope }

    override env.ToString() = "CallerEnv"

let extEnv (extension: string) : IExtensionEnv =
    ({
        Extension = extension
        Clock = (FakeClock.Create()).Clock
        Scope = Scope.root
    }
    : ExtEnv)
    :> IExtensionEnv

let caller: CallerEnv = { Scope = Scope.root }

/// Runs an effect in the caller's environment, failing the test instead of hanging.
let run (effect: Eff<CallerEnv, 'T>) : 'T =
    (Eff.run caller effect).WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

/// A contribution of `extension`/`id` with a value, in its own environment.
let contributed (extension: string) (id: string) (value: 'T) : Contributed<'T> =
    {
        Extension = extension
        Type = "test"
        Id = id
        Value = value
        Env = extEnv extension
    }

/// 300 tests per property; set PROPERTY_TESTS to run more.
let config =
    { FsCheckConfig.defaultConfig with
        maxTest =
            match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
            | null -> 300
            | count -> int count
    }

let property name test = testPropertyWithConfig config name test

/// Golden files in `golden/` next to these tests (found from the binaries in artifacts/).
module Golden =
    let private directory =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
            | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
            | dir -> up dir.Parent

        Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "tests", "Fx.Sdk.Tests", "golden")

    /// Expects `actual` to equal `golden/<name>`; `UPDATE_GOLDEN=1` rewrites it instead.
    let expect (name: string) (actual: string) =
        let path = Path.Combine(directory, name)
        let actual = actual + "\n"

        if Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1" then
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(path, actual)
        else
            Expect.isTrue (File.Exists path) $"missing golden file {path}; run the tests with UPDATE_GOLDEN=1"
            Expect.equal actual (File.ReadAllText path) $"golden/{name} (rerun with UPDATE_GOLDEN=1 to update)"

/// Whether a task-returning function throws `'E` (synchronously or in the task).
let throws<'E when 'E :> exn> (work: unit -> unit) : 'E option =
    try
        work ()
        None
    with
    | :? 'E as e -> Some e
    | :? AggregateException as e ->
        match e.InnerException with
        | :? 'E as inner -> Some inner
        | _ -> None

let ignoreTask (_: Task) = ()
