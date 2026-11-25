/// A host on scratch directories with the live interpreters, except the model, which is scripted
/// HTTP in the Claude API's documented stream format (no API key leaves), and downloads, which are
/// cached across test runs.
module Fx.Host.Tests.Support

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Components
open Fx.Components.Live
open Fx.Sdk
open Fx.Host

/// Temporary directories, deleted when the process exits.
module Scratch =
    let root =
        Path.Combine(Path.GetTempPath(), "host-tests-" + Guid.NewGuid().ToString("N").Substring(0, 12))

    let deleteTree (path: string) =
        if Directory.Exists path then
            for file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) do
                try
                    File.SetAttributes(file, FileAttributes.Normal)
                with _ ->
                    ()

            Directory.Delete(path, true)

    do
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
            try
                deleteTree root
            with _ ->
                ())

    let directory (name: string) =
        let dir =
            Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        Directory.CreateDirectory dir |> ignore
        dir

let wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

let waitFor (seconds: float) (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds seconds).GetAwaiter().GetResult()

/// The process's variables, with a test API key for the model's provider.
let variables () : (string * string) list =
    [
        for entry in Environment.GetEnvironmentVariables() |> Seq.cast<Collections.DictionaryEntry> do
            match entry.Key, entry.Value with
            | (:? string as name), (:? string as value) when name <> "ANTHROPIC_API_KEY" -> name, value
            | _ -> ()
    ]
    |> List.sortBy fst
    |> fun vars -> vars @ [ "ANTHROPIC_API_KEY", "test-key" ]

let private sse (events: (string * string) list) : string =
    events
    |> List.map (fun (name, data) -> $"event: {name}\ndata: {data}\n\n")
    |> String.concat ""

/// A Claude Messages stream with one tool call (the documented format).
let anthropicCall (n: int) (name: string) (arguments: string) : string =
    sse
        [
            "message_start",
            $"{{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_{n}\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":400,\"output_tokens\":1}}}}}}"
            "content_block_start",
            $"{{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{{\"type\":\"tool_use\",\"id\":\"toolu_{n}\",\"name\":\"{name}\",\"input\":{{}}}}}}"
            "content_block_delta",
            $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{Json.quote arguments}}}}}"
            "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}"
            "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":30}}"
            "message_stop", "{\"type\":\"message_stop\"}"
        ]

/// The model, as HTTP: each request to the Messages API gets the next cell, as a tool call. A request
/// can be held until the test lets it go. Records every request.
[<Sealed>]
type Model() =
    let gate = obj ()
    let cells = Queue<string>()
    let requests = ResizeArray<HttpRequest>()
    let holds = Dictionary<int, TaskCompletionSource>()
    let asked = Dictionary<int, TaskCompletionSource>()

    let signal (n: int) =
        match asked.TryGetValue n with
        | true, s -> s
        | _ ->
            let s = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            asked[n] <- s
            s

    let inner (n: int) (cell: string option) =
        InMemoryHttp(
            SystemClock.clock,
            fun _ ->
                match cell with
                | Some code ->
                    let arguments = Json.serialize (Json.Object [ "code", Json.String code ])

                    HttpReply.text 200 (anthropicCall n "cell" arguments)
                    |> HttpReply.withHeader "content-type" "text/event-stream"
                    |> Ok
                    |> Some
                | None ->
                    HttpReply.text
                        400
                        "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"the test's model has no reply\"}}"
                    |> HttpReply.withHeader "content-type" "application/json"
                    |> Ok
                    |> Some
        )

    let send (request: HttpRequest) (cancellation: CancellationToken) =
        task {
            let n, hold, cell =
                lock gate (fun () ->
                    requests.Add request
                    let n = requests.Count
                    (signal n).TrySetResult() |> ignore

                    let hold =
                        match holds.TryGetValue n with
                        | true, h -> Some h.Task
                        | _ -> None

                    n, hold, (if cells.Count > 0 then Some(cells.Dequeue()) else None))

            match hold with
            | Some hold -> do! hold.WaitAsync cancellation
            | None -> ()

            return! (inner n cell).Http.Send request cancellation
        }

    member _.Http: Http = { Send = send }

    member _.Add(cell: string) =
        lock gate (fun () -> cells.Enqueue cell)

    member this.AddAll(items: string list) =
        for cell in items do
            this.Add cell

    /// Holds request `n` (1-based) until the returned source is set.
    member _.Hold(n: int) : TaskCompletionSource =
        lock gate (fun () ->
            let h = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            holds[n] <- h
            h)

    /// Completes when request `n` has been made.
    member _.Asked(n: int) : Task = lock gate (fun () -> (signal n).Task)

    member _.Requests: HttpRequest list = lock gate (fun () -> List.ofSeq requests)

    override _.ToString() = "Model"

/// Downloads through the live fetcher, kept in artifacts/ by URL so a test run downloads each once.
let cachedFetch: Fetch =
    let live = HttpFetch.create (HttpFetch.client ())

    let cache = Fx.Kernel.Tests.Repo.path [ "artifacts"; "host-tests"; "downloads" ]

    {
        Download =
            fun request cancellation ->
                task {
                    Directory.CreateDirectory cache |> ignore

                    let name =
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes request.Url))

                    let cached = Path.Combine(cache, name)

                    let! fetched =
                        if File.Exists cached then
                            Task.FromResult(Ok())
                        else
                            task {
                                let partial = cached + ".partial-" + string Environment.ProcessId

                                match! live.Download { request with Destination = partial } cancellation with
                                | Error e -> return Error e
                                | Ok _ ->
                                    File.Move(partial, cached, true)
                                    return Ok()
                            }

                    match fetched with
                    | Error e -> return Error e
                    | Ok() ->
                        File.Copy(cached, request.Destination, true)
                        let bytes = File.ReadAllBytes request.Destination

                        return
                            Ok
                                {
                                    Size = int64 bytes.Length
                                    Digest = Digest.Sha256(Convert.ToHexStringLower(SHA256.HashData bytes))
                                }
                }
    }

/// A data directory and a workspace for one test.
type Place =
    {
        Data: string
        Workspace: string
    }

    member this.File(name: string) = Path.Combine(this.Workspace, name)

    member this.Write(name: string, text: string) =
        let path = this.File name
        Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
        File.WriteAllText(path, text)

    member this.Read(name: string) = File.ReadAllText(this.File name)

    override this.ToString() = this.Data

let place (name: string) : Place =
    let root = Scratch.directory name
    let data = Path.Combine(root, "data")
    let workspace = Path.Combine(root, "workspace")
    Directory.CreateDirectory workspace |> ignore

    { Data = data; Workspace = workspace }

/// The host's options on a place: live interpreters, the scripted model, cached downloads.
let options (place: Place) (model: Model) (explicit: (string * Json) list) : HostOptions =
    let facts: ProcessFacts =
        {
            Variables = variables ()
            CurrentDirectory = place.Workspace
            Binary = Path.Combine(AppContext.BaseDirectory, Product.binaryName)
            BinaryArgs = []
        }

    match Live.options facts place.Workspace explicit with
    | Error e -> failtest e
    | Ok options ->
        { options with
            Paths = HostPaths.under place.Data
            Http = model.Http
            Fetch = cachedFetch
        }

/// Starts a host, failing the test if it does not start.
let start (options: HostOptions) : Host =
    match waitFor 180.0 (Host.start options) with
    | Ok host -> host
    | Error errors -> failtest ("the host did not start:\n" + String.concat "\n" errors)

let stop (host: Host) =
    (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore

/// The model's choice for the tests: a model of the Claude API's catalog.
let modelId = "anthropic/claude-sonnet-5-5"

let g = Product.kernelGlobal
