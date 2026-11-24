/// task and async computation expressions, cancellation, agents, channels and parallelism.
module Fx.Aot.Scenarios.Concurrency

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type Message =
    | Increment of int
    | Get of AsyncReplyChannel<int>

    override this.ToString() =
        match this with
        | Increment n -> $"Increment {n}"
        | Get _ -> "Get"

let private yieldThen (value: 'T) : Task<'T> =
    task {
        do! Task.Yield()
        return value
    }

/// A generic, non-inlined task: exercises the resumable-code state machine per instantiation.
let private roundTrip<'T> (value: 'T) : Task<string> =
    task {
        let! v = yieldThen value
        do! Task.Delay 1
        return $"{typeof<'T>.Name} {v}"
    }

let tasks =
    Scenario.task "tasks" (fun () ->
        task {
            let log = ResizeArray<string>()
            let! a = yieldThen 20
            let! b = Task.Run(fun () -> 22)
            log.Add $"sequential = {a + b}"

            let! all = Task.WhenAll [ for i in 1..5 -> yieldThen (i * i) ]
            log.Add $"whenAll = {List.ofArray all}"

            let! generic =
                Task.WhenAll [ roundTrip 1; roundTrip "s"; roundTrip 2.5; roundTrip [ 1 ] ]

            log.Add $"generic = {List.ofArray generic}"

            let mutable i = 0
            let mutable total = 0

            while i < 5 do
                let! v = yieldThen i
                total <- total + v
                i <- i + 1

            log.Add $"while loop = {total}"

            try
                let! (_: int) =
                    task {
                        do! Task.Yield()
                        return invalidOp "inside task"
                    }

                log.Add "not reached"
            with e ->
                log.Add $"caught {e.GetType().Name}: {e.Message}"

            let tcs = TaskCompletionSource<string>()
            let _ = Task.Run(fun () -> tcs.SetResult "completed elsewhere")
            let! fromTcs = tcs.Task
            log.Add fromTcs

            let! v = ValueTask<int>(7)
            log.Add $"value task = {v}"

            let! bg = backgroundTask { return Thread.CurrentThread.IsThreadPoolThread }
            log.Add $"background task on pool = {bg}"

            use cts = new CancellationTokenSource()
            cts.Cancel()

            try
                do! Task.Delay(1000, cts.Token)
                log.Add "not cancelled"
            with :? OperationCanceledException as e ->
                log.Add $"cancelled {e.GetType().Name}"

            return List.ofSeq log
        })

let asyncWorkflows =
    Scenario.task "async-workflows" (fun () ->
        let log = ResizeArray<string>()

        let square x =
            async {
                do! Async.Sleep 1
                return x * x
            }

        let failing =
            async {
                do! Async.SwitchToThreadPool()
                return failwith "async failure"
            }

        let withFinally =
            async {
                try
                    log.Add "async body"
                    return 1
                finally
                    log.Add "async finally"
            }

        let agent =
            MailboxProcessor.Start(fun inbox ->
                let rec loop total =
                    async {
                        match! inbox.Receive() with
                        | Increment n -> return! loop (total + n)
                        | Get reply ->
                            reply.Reply total
                            return! loop total
                    }

                loop 0)

        let workflow =
            async {
                let! squares = [ 1..6 ] |> List.map square |> Async.Parallel
                log.Add $"parallel = {List.ofArray squares}"
                let! sequential = [ 3; 2; 1 ] |> List.map square |> Async.Sequential
                log.Add $"sequential = {List.ofArray sequential}"

                match! Async.Catch failing with
                | Choice1Of2 _ -> log.Add "no failure"
                | Choice2Of2 e -> log.Add $"catch = {e.Message}"

                let! f = withFinally
                log.Add $"finally result = {f}"

                for n in 1..10 do
                    agent.Post(Increment n)

                let! total = agent.PostAndAsyncReply Get
                log.Add $"agent total = {total}"

                let! fromTask = Async.AwaitTask(Task.FromResult "awaited task")
                log.Add fromTask

                use cts = new CancellationTokenSource()
                cts.Cancel()

                try
                    do!
                        Async.StartAsTask(Async.Sleep 1000, cancellationToken = cts.Token)
                        |> Async.AwaitTask

                    log.Add "not cancelled"
                with e ->
                    log.Add $"cancelled {e.GetType().Name}"

                return List.ofSeq log
            }

        Async.StartAsTask workflow)

let parallelism =
    Scenario.task "parallelism" (fun () ->
        task {
            let sum = ref 0L

            Parallel.For(1, 10_001, (fun i -> Interlocked.Add(&sum.contents, int64 i) |> ignore))
            |> ignore

            let channel = Channel.CreateBounded<int>(4)

            let producer =
                task {
                    for i in 1..100 do
                        do! channel.Writer.WriteAsync i

                    channel.Writer.Complete()
                }

            let consumer =
                task {
                    let mutable total = 0
                    let mutable count = 0

                    while! channel.Reader.WaitToReadAsync() do
                        match channel.Reader.TryRead() with
                        | true, v ->
                            total <- total + v
                            count <- count + 1
                        | _ -> ()

                    return total, count
                }

            do! producer
            let! total, count = consumer

            let locked = obj ()
            let counter = ref 0

            let workers =
                [|
                    for _ in 1..8 ->
                        Task.Run(fun () ->
                            for _ in 1..1000 do
                                lock locked (fun () -> counter.Value <- counter.Value + 1))
                |]

            do! Task.WhenAll workers

            return
                [
                    $"parallel for sum = {sum.Value}"
                    $"channel total = {total}, count = {count}"
                    $"locked counter = {counter.Value}"
                ]
        })

let scenarios = [ tasks; asyncWorkflows; parallelism ]
