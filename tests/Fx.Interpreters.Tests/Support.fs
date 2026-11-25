/// Environments and values the interpreter tests share: capabilities interpreted by fakes, a
/// workspace in the host's path syntax, and a result shaped like spec §4.5's `TurnResult`.
module Fx.Interpreters.Tests.Support

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Envd
open Fx.Sdk

/// The environment bindings and launches run in.
type Env =
    {
        Clock: Clock
        FsRead: FsRead
        Fs: Fs
        FsOps: FsOps
        Shell: Shell
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IFsOps with
        member env.FsOps = env.FsOps

    interface IShell with
        member env.Shell = env.Shell

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

/// This machine's operating system, as targets name it.
let os =
    if OperatingSystem.IsWindows() then Os.Windows
    elif OperatingSystem.IsMacOS() then Os.MacOs
    else Os.Linux

/// An absolute path in this machine's syntax.
let path (segments: string list) =
    if OperatingSystem.IsWindows() then
        @"C:\" + String.Join(@"\", segments)
    else
        "/" + String.Join("/", segments)

let workspace = path [ "work" ]

/// The fakes behind an `Env`.
type Fakes =
    {
        Env: Env
        Fs: InMemoryFs
        Shell: ScriptedShell
    }

    override _.ToString() = "Fakes"

/// A fresh environment over an in-memory filesystem with the workspace, and a scripted shell.
let fakes (respond: Command -> Result<Output, ShellError> option) : Fakes =
    let clock = FakeClock(Instant.ofUnixMicroseconds 1_700_000_000_000_000L)

    let fs =
        InMemoryFs(clock.Clock, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())

    fs.AddDirectory workspace
    let shell = ScriptedShell respond

    {
        Env =
            {
                Clock = clock.Clock
                FsRead = fs.FsRead
                Fs = fs.Fs
                FsOps = FsOpsOver.create fs.FsRead fs.Fs
                Shell = shell.Shell
                Scope = Scope.root
            }
        Fs = fs
        Shell = shell
    }

let run (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let runTask (env: 'R) (effect: Eff<'R, 'T>) : Task<'T> =
    (Eff.run env effect).WaitAsync(TimeSpan.FromSeconds 30.0)

let parse (text: string) =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failtest (string e)

let canonical (json: Json) = Json.serializeCanonical json

/// A result shaped like `TurnResult` (spec §4.5); the session's own comes with T127.
[<RequireQualifiedAccess>]
type Turn =
    | Done of summary: string * verify: string option
    | NeedInput of requests: string list
    | Waiting of jobs: string list * note: string

    override this.ToString() =
        match this with
        | Done(summary, _) -> "done: " + summary
        | NeedInput requests -> $"need input ({requests.Length})"
        | Waiting(_, note) -> "waiting: " + note

let turnCodec: Codec<Turn> =
    Codec.union "kind" {
        let! isDone =
            Codec.case2
                "done"
                (fun summary verify -> Turn.Done(summary, verify))
                (Field.required "summary" Codec.string)
                (Field.optional "verify" Codec.string)

        and! needInput =
            Codec.case1
                "needInput"
                Turn.NeedInput
                (Field.required "requests" (Codec.list Codec.string |> Codec.minItems 1))

        and! waiting =
            Codec.case2
                "waiting"
                (fun jobs note -> Turn.Waiting(jobs, note))
                (Field.required "jobs" (Codec.list Codec.string |> Codec.minItems 1))
                (Field.required "note" Codec.string)

        return
            fun turn ->
                match turn with
                | Turn.Done(summary, verify) -> isDone summary verify
                | Turn.NeedInput requests -> needInput requests
                | Turn.Waiting(jobs, note) -> waiting jobs note
    }
    |> Codec.describe "How the turn ends."
    |> Codec.named "TurnResult"

/// `Ensure`: a summary says something.
let summaryEnsure (turn: Turn) : Issue list =
    match turn with
    | Turn.Done(summary, _) when String.IsNullOrWhiteSpace summary ->
        [ Issue.at [ PathSegment.Key "summary" ] "the summary must say what was done" ]
    | _ -> []
