namespace Fx.Effects

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Combinators for `Eff<'R, 'T>`.
///
/// Concurrency is structured: `all`, `both`, `race` and `timeout` start their children in a child
/// scope, cancel it when the outcome is decided, and complete only after every child has finished.
/// Children start on the calling thread and run up to their first incomplete await, so a fake clock
/// sees their timers in a deterministic order. CPU-bound work does not run in parallel.
module Eff =
    /// An effect that returns the value.
    let ret (value: 'T) : Eff<'R, 'T> = fun _ -> Task.FromResult value

    /// An effect that does nothing.
    let unit<'R> : Eff<'R, unit> = fun _ -> Internal.completedUnit

    /// Invokes the effect with the environment. A synchronous exception becomes a faulted task, so
    /// the caller always gets a task.
    let run (env: 'R) (effect: Eff<'R, 'T>) : Task<'T> =
        try
            effect env
        with e ->
            Task.FromException<'T> e

    /// Delays building the effect until it runs (e.g. for recursive effects).
    let delay (build: unit -> Eff<'R, 'T>) : Eff<'R, 'T> = fun env -> build () env

    /// Lifts a task-returning function. Use it only in capability modules and interpreters: an effect
    /// should show what it needs in its environment type.
    let ofTask (start: unit -> Task<'T>) : Eff<'R, 'T> = fun _ -> start ()

    /// Lifts a cancellable task-returning function; it gets the scope's cancellation token.
    let ofCancellableTask<'R, 'T when 'R :> IScope> (start: CancellationToken -> Task<'T>) : Eff<'R, 'T> =
        fun env -> start env.Scope.Cancellation

    let map (mapping: 'T -> 'U) (effect: Eff<'R, 'T>) : Eff<'R, 'U> =
        eff {
            let! value = effect
            return mapping value
        }

    let bind (binder: 'T -> Eff<'R, 'U>) (effect: Eff<'R, 'T>) : Eff<'R, 'U> =
        eff {
            let! value = effect
            return! binder value
        }

    let ignore (effect: Eff<'R, 'T>) : Eff<'R, unit> = map ignore effect

    /// The whole environment.
    let ask<'R> : Eff<'R, 'R> = fun env -> Task.FromResult env

    /// A value computed from the environment.
    let asks (read: 'R -> 'T) : Eff<'R, 'T> = fun env -> Task.FromResult(read env)

    /// Runs an effect in an environment derived from the current one. This is how environments are
    /// attenuated: `local` with a function that returns an environment exposing fewer (or wrapped)
    /// capabilities. Types are not a security boundary; the sandbox is (spec §18).
    let local (derive: 'R -> 'R1) (effect: Eff<'R1, 'T>) : Eff<'R, 'T> = fun env -> effect (derive env)

    /// Runs an effect in a fixed environment, whatever the caller's environment is.
    let provide (env: 'R1) (effect: Eff<'R1, 'T>) : Eff<'R, 'T> = fun _ -> effect env

    /// The scope the effect runs in.
    let scope<'R when 'R :> IScope> : Eff<'R, Scope> =
        fun env -> Task.FromResult env.Scope

    /// The cancellation token of the scope.
    let cancellation<'R when 'R :> IScope> : Eff<'R, CancellationToken> =
        fun env -> Task.FromResult env.Scope.Cancellation

    /// Runs the effect in a scope derived from the current one.
    let withScope<'R, 'T when 'R :> IScopeWith<'R>> (derive: Scope -> Scope) (effect: Eff<'R, 'T>) : Eff<'R, 'T> =
        fun env -> effect (env.WithScope(derive env.Scope))

    /// Runs the effect in a scope that is not cancelled with its parent, e.g. for clean-up that must
    /// finish after a cancellation.
    let shield (effect: Eff<'R, 'T>) : Eff<'R, 'T> =
        withScope
            (fun scope ->
                { scope with
                    Cancellation = CancellationToken.None
                })
            effect

    /// Captures a defect (any exception, cancellation included) as an `Error`. Use it at boundaries
    /// that report defects; expected failures belong in `Result` from the start.
    let catch (effect: Eff<'R, 'T>) : Eff<'R, Result<'T, exn>> =
        eff {
            try
                let! value = effect
                return Ok value
            with e ->
                return Error e
        }

    /// Acquires a resource, uses it and always releases it, also after an exception or cancellation.
    /// The release runs shielded from cancellation.
    let bracket<'R, 'A, 'T when 'R :> IScopeWith<'R>>
        (acquire: Eff<'R, 'A>)
        (release: 'A -> Eff<'R, unit>)
        (body: 'A -> Eff<'R, 'T>)
        : Eff<'R, 'T> =
        eff {
            let! resource = acquire
            let! outcome = catch (body resource)
            do! shield (release resource)

            match outcome with
            | Ok value -> return value
            | Error e -> return Internal.rethrow e
        }

    /// Runs the effects one after another and collects their results.
    let traverse (effect: 'A -> Eff<'R, 'T>) (items: 'A list) : Eff<'R, 'T list> =
        eff {
            let results = ResizeArray()

            for item in items do
                let! result = effect item
                results.Add result

            return List.ofSeq results
        }

    /// Runs the effects one after another and collects their results.
    let sequence (effects: Eff<'R, 'T> list) : Eff<'R, 'T list> = traverse id effects

    let private start (env: 'R) (effect: Eff<'R, 'T>) : Task<'T> = run env effect

    /// A child scope of the environment's scope, and the source that cancels it.
    let private childScope<'R when 'R :> IScopeWith<'R>> (env: 'R) : CancellationTokenSource * 'R =
        let source = CancellationTokenSource.CreateLinkedTokenSource(env.Scope.Cancellation)

        source,
        env.WithScope
            { env.Scope with
                Cancellation = source.Token
            }

    /// Waits for every task. The first to fail cancels the source; it is returned once all are done.
    let private settle (source: CancellationTokenSource) (tasks: Task array) : Task<Task option> =
        task {
            let pending = HashSet<Task>(tasks)
            let mutable failure = None

            while pending.Count > 0 do
                let! finished = Task.WhenAny pending
                pending.Remove finished |> Operators.ignore

                if failure.IsNone && not finished.IsCompletedSuccessfully then
                    failure <- Some finished
                    source.Cancel()

            return failure
        }

    /// Runs the effects concurrently and returns their results in order. If one fails, the others are
    /// cancelled; once all have finished, the first failure is rethrown.
    let all<'R, 'T when 'R :> IScopeWith<'R>> (effects: Eff<'R, 'T> list) : Eff<'R, 'T list> =
        fun env ->
            match effects with
            | [] -> Task.FromResult []
            | _ ->
                let source, child = childScope env
                let tasks = effects |> List.map (start child) |> Array.ofList

                task {
                    try
                        match! settle source (tasks |> Array.map (fun t -> t :> Task)) with
                        | Some failed ->
                            do! failed
                            return []
                        | None -> return tasks |> Array.map (fun t -> t.Result) |> List.ofArray
                    finally
                        source.Dispose()
                }

    /// Runs two effects concurrently, like `all`.
    let both<'R, 'A, 'B when 'R :> IScopeWith<'R>> (first: Eff<'R, 'A>) (second: Eff<'R, 'B>) : Eff<'R, 'A * 'B> =
        fun env ->
            let source, child = childScope env
            let a = start child first
            let b = start child second

            task {
                try
                    match! settle source [| a; b |] with
                    | Some failed ->
                        do! failed
                        return Unchecked.defaultof<'A>, Unchecked.defaultof<'B>
                    | None -> return a.Result, b.Result
                finally
                    source.Dispose()
            }

    /// Runs the effects concurrently; the first to finish, with a value or an exception, decides the
    /// outcome. The others are cancelled, and their outcomes ignored once they have finished.
    let race<'R, 'T when 'R :> IScopeWith<'R>> (effects: Eff<'R, 'T> list) : Eff<'R, 'T> =
        fun env ->
            match effects with
            | [] -> invalidArg (nameof effects) "Eff.race needs at least one effect."
            | _ ->
                let source, child = childScope env
                let tasks = effects |> List.map (start child) |> Array.ofList

                task {
                    try
                        let! first = Task.WhenAny tasks
                        source.Cancel()

                        for t in tasks do
                            try
                                do! (t :> Task)
                            with _ ->
                                ()

                        return! first
                    finally
                        source.Dispose()
                }

    /// Runs the effect with a time limit on the clock: `None` if the time ran out first, in which case
    /// the effect is cancelled (and awaited).
    let timeout<'R, 'T when 'R :> IClock and 'R :> IScopeWith<'R>>
        (limit: TimeSpan)
        (effect: Eff<'R, 'T>)
        : Eff<'R, 'T option> =
        race [ map Some effect; map (fun () -> None) (Clock.sleep limit) ]
