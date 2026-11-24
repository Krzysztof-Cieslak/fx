/// Compares three ways to write the same effectful code:
///
/// - `task`: hand-written `task { }` that passes the environment explicitly (the floor);
/// - `eff`: the resumable `eff { }` builder from Fx.Effects;
/// - `compose`: a closure-composition builder (`Bind(m, f) = fun env -> ...`), the simple design
///   that was not chosen.
///
/// Prints a Markdown table of the median time and allocations per run. Numbers vary by machine; the
/// ratios are what matter.
module Fx.Effects.Bench.Program

open System
open System.Diagnostics
open System.Threading.Tasks
open Fx.Effects

/// The rejected design: each bind is a closure, with a fast path for completed tasks.
type ComposeBuilder() =
    member inline _.Return(value: 'T) : Eff<'R, 'T> = fun _ -> Task.FromResult value

    member inline _.Bind(effect: Eff<'R, 'T>, [<InlineIfLambda>] continuation: 'T -> Eff<'R, 'U>) : Eff<'R, 'U> =
        fun env ->
            let started = effect env

            if started.IsCompletedSuccessfully then
                continuation started.Result env
            else
                task {
                    let! value = started
                    return! continuation value env
                }

    member inline _.Delay([<InlineIfLambda>] build: unit -> Eff<'R, 'T>) : Eff<'R, 'T> = fun env -> build () env

    member inline _.Zero() : Eff<'R, unit> = fun _ -> Task.FromResult()

    member inline this.Combine(first: Eff<'R, unit>, second: Eff<'R, 'T>) : Eff<'R, 'T> =
        this.Bind(first, fun () -> second)

    member _.While(condition: unit -> bool, body: Eff<'R, unit>) : Eff<'R, unit> =
        fun env ->
            task {
                while condition () do
                    do! body env
            }

let compose = ComposeBuilder()

type Env = { Value: int }

let private completed = Task.FromResult 1

/// A synchronous effect that reads the environment.
let read: Eff<Env, int> =
    fun env ->
        if env.Value = 1 then
            completed
        else
            Task.FromResult env.Value

/// An asynchronous effect.
let yielding: Eff<Env, int> =
    fun env ->
        task {
            do! Task.Yield()
            return env.Value
        }

let taskTen (step: Eff<Env, int>) (env: Env) : Task<int> =
    task {
        let! a = step env
        let! b = step env
        let! c = step env
        let! d = step env
        let! e = step env
        let! f = step env
        let! g = step env
        let! h = step env
        let! i = step env
        let! j = step env
        return a + b + c + d + e + f + g + h + i + j
    }

let effTen (step: Eff<Env, int>) : Eff<Env, int> =
    eff {
        let! a = step
        let! b = step
        let! c = step
        let! d = step
        let! e = step
        let! f = step
        let! g = step
        let! h = step
        let! i = step
        let! j = step
        return a + b + c + d + e + f + g + h + i + j
    }

let composeTen (step: Eff<Env, int>) : Eff<Env, int> =
    compose {
        let! a = step
        let! b = step
        let! c = step
        let! d = step
        let! e = step
        let! f = step
        let! g = step
        let! h = step
        let! i = step
        let! j = step
        return a + b + c + d + e + f + g + h + i + j
    }

let taskLoop (env: Env) : Task<int> =
    task {
        let mutable total = 0

        while total < 1000 do
            let! v = read env
            total <- total + v

        return total
    }

let effLoop: Eff<Env, int> =
    eff {
        let mutable total = 0

        while total < 1000 do
            let! v = read
            total <- total + v

        return total
    }

let composeLoop: Eff<Env, int> =
    let total = ref 0

    compose {
        total.Value <- 0

        while total.Value < 1000 do
            let! v = read
            total.Value <- total.Value + v

        return total.Value
    }

/// Median nanoseconds and bytes allocated per run of `run`.
let measure (iterations: int) (run: unit -> Task<int>) : float * float =
    for _ in 1 .. iterations / 10 do
        run().GetAwaiter().GetResult() |> ignore

    let samples =
        [
            for _ in 1..7 ->
                GC.Collect()
                let bytes = GC.GetTotalAllocatedBytes true
                let start = Stopwatch.GetTimestamp()

                for _ in 1..iterations do
                    run().GetAwaiter().GetResult() |> ignore

                let elapsed = Stopwatch.GetElapsedTime start
                let allocated = GC.GetTotalAllocatedBytes true - bytes
                elapsed.TotalNanoseconds / float iterations, float allocated / float iterations
        ]

    let median (values: float list) =
        List.sort values |> List.item (values.Length / 2)

    median (List.map fst samples), median (List.map snd samples)

[<EntryPoint>]
let main _ =
    let env = { Value = 1 }
    let effRead, composeRead = effTen read, composeTen read
    let effYielding, composeYielding = effTen yielding, composeTen yielding

    let cases =
        [
            "10 sequential binds, synchronous",
            1_000_000,
            [
                "task", (fun () -> taskTen read env)
                "eff", (fun () -> effRead env)
                "eff, built per run", (fun () -> effTen read env)
                "compose", (fun () -> composeRead env)
                "compose, built per run", (fun () -> composeTen read env)
            ]
            "10 sequential binds, asynchronous (Task.Yield)",
            50_000,
            [
                "task", (fun () -> taskTen yielding env)
                "eff", (fun () -> effYielding env)
                "compose", (fun () -> composeYielding env)
            ]
            "while loop, 1000 synchronous iterations",
            10_000,
            [
                "task", (fun () -> taskLoop env)
                "eff", (fun () -> effLoop env)
                "compose", (fun () -> composeLoop env)
            ]
        ]

    Console.WriteLine
        $".NET {Environment.Version}, {Environment.ProcessorCount} cores, {Runtime.InteropServices.RuntimeInformation.OSDescription}"

    Console.WriteLine ""
    Console.WriteLine "| Case | Builder | Time per run | Allocated per run |"
    Console.WriteLine "|---|---|---|---|"

    for name, iterations, builders in cases do
        for builder, run in builders do
            let nanoseconds, bytes = measure iterations run
            Console.WriteLine $"| {name} | {builder} | {nanoseconds:F0} ns | {bytes:F0} B |"

    0
