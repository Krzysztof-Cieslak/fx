/// Helpers for the tests of fx.model.openai.
module Fx.Ext.ModelOpenAI.Tests.Support

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Ext.ModelOpenAI
open Fx.Models
open Fx.Sdk

/// The repository root: the directory with Fx.slnx above the test binaries in artifacts/.
let root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
        | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
        | dir -> up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// A directory of this test project in the source tree.
let sourceDirectory (name: string) =
    Path.Combine(root, "tests", "Fx.Ext.ModelOpenAI.Tests", name)

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

/// A source file of the cases (`sources/`), with LF line ends.
let source (name: string) : string =
    File.ReadAllText(Path.Combine(sourceDirectory "sources", name)).Replace("\r\n", "\n")

/// JSON text as a value (failing the test if it is not JSON).
let json (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failwith $"not JSON: {e}"

let utf8 = UTF8Encoding(false)

/// Waits for a task, failing instead of hanging.
let wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let astra = ModelRef.create Provider.Id "gpt-6.2-astra"
let sol = ModelRef.create Provider.Id "gpt-6.2-sol"
let terra = ModelRef.create Provider.Id "gpt-6.1-terra"
let luna = ModelRef.create Provider.Id "gpt-6-luna"
let gpt55 = ModelRef.create Provider.Id "gpt-5.5"

/// The catalog entry of a model.
let info (model: ModelRef) : ModelInfo =
    match ProviderDef.model model Provider.def with
    | Some info -> info
    | None -> failwith $"{model} is not in the catalog"

/// A wire context for pure tests: a provider, a model, no network.
let ctx (model: ModelRef) (provider: ProviderDef) : WireCtx =
    {
        Http =
            {
                Send = fun _ _ -> Task.FromResult(Error(HttpError.Refused "no network in this test"))
            }
        Provider = provider
        Family = Provider.Family
        Endpoint = Provider.Endpoint
        Model = model
        Info = ProviderDef.model model provider
        Timeouts = HttpTimeouts.none
        Clock = (FakeClock.Create()).Clock
        Trace = Trace.none
        Scope = Scope.root
    }

/// The provider state the wire stamps on a part.
let state (model: ModelRef) (kind: string) (data: string) : ProviderState =
    {
        Family = Provider.Family
        Model = model
        Kind = kind
        Data = data
    }

/// The events of a whole stream text, decoded in pieces of `size` bytes.
let decodeIn (size: int) (ctx: WireCtx) (text: string) : ChatEvent list =
    let parser = SseParser()
    let mutable state = Decoder.initial ctx
    let events = ResizeArray<ChatEvent>()

    for piece in Chunking.split (if size <= 0 then Chunking.Whole else Chunking.Every size) (utf8.GetBytes text) do
        match parser.Push piece with
        | Ok parsed ->
            for event in parsed do
                let decoded = Decoder.decode (state, event)
                state <- decoded.State
                events.AddRange decoded.Events
        | Error e -> failwith (string e)

    parser.Finish() |> ignore
    events.AddRange(OpenAI.adapter.End state)
    List.ofSeq events

/// The events of a whole stream text.
let decode (ctx: WireCtx) (text: string) : ChatEvent list = decodeIn 0 ctx text

/// What the events fold to.
let fold (events: ChatEvent list) =
    ChatAggregate.result (ChatAggregate.ofEvents events)

/// The request body a chat request becomes for a model, with the provider's settings.
let bodyFor (provider: ProviderDef) (chat: ChatRequest) : Result<Json, ChatError> =
    Request.body (Provider.Settings.ofProvider provider) (ProviderDef.model chat.Model provider) chat

/// Expects two JSON values to be equal (member order and number notation aside).
let expectJson (actual: Json) (expected: Json) (message: string) =
    Expect.equal (Json.serializeCanonical actual) (Json.serializeCanonical expected) message
