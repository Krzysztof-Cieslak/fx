/// Helpers for the models' tests.
module Fx.Models.Tests.Support

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Models

/// The repository root: the directory with Fx.slnx above the test binaries in artifacts/ (CI maps
/// `__SOURCE_DIRECTORY__` to `/_/`, so it cannot be used).
let root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
        | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
        | dir -> up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// A directory of this test project in the source tree.
let sourceDirectory (name: string) =
    Path.Combine(root, "tests", "Fx.Models.Tests", name)

let updating = Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1"

/// Expects `actual` to equal the file at `path`; with `UPDATE_GOLDEN=1` the file is rewritten.
let expectFile (path: string) (actual: string) =
    let actual = actual + "\n"

    if updating then
        Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
        File.WriteAllText(path, actual)
    else
        Expect.isTrue (File.Exists path) $"missing {path}; run the tests with UPDATE_GOLDEN=1"

        Expect.equal
            (File.ReadAllText(path).Replace("\r\n", "\n"))
            actual
            $"{Path.GetFileName path} (rerun with UPDATE_GOLDEN=1 to update)"

/// Expects `actual` to equal `golden/<name>`.
let golden (name: string) (actual: string) =
    expectFile (Path.Combine(sourceDirectory "golden", name)) actual

let utf8 = UTF8Encoding(false)

let bytes (text: string) = utf8.GetBytes text

/// Waits for a task, failing instead of hanging.
let wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

/// An environment with a scope only.
type ScopeEnv =
    {
        Scope: Scope
    }

    interface IScopeWith<ScopeEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { Scope = scope }

    override _.ToString() = "ScopeEnv"

let scope: ScopeEnv = { Scope = Scope.root }

/// An environment with a model.
type LlmEnv =
    {
        Llm: Llm
        Scope: Scope
    }

    interface ILlm with
        member env.Llm = env.Llm

    interface IScopeWith<LlmEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "LlmEnv"

/// The model of the examples.
let model = ModelRef.create "example" "echo-1"

let request (messages: Message list) : ChatRequest = ChatRequest.create model messages

/// Every event of a stream, then closes it.
let drain (stream: ChatStream) : ChatEvent list =
    let events = ResizeArray<ChatEvent>()
    let mutable reading = true

    while reading do
        match wait (stream.Next CancellationToken.None) with
        | Some event -> events.Add event
        | None -> reading <- false

    wait (stream.Close())
    List.ofSeq events
