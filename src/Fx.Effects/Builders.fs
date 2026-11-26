namespace Fx.Effects

// The `eff { }` and `effResult { }` builders, as F# resumable state machines (like `task { }`): an
// `eff` block compiles to one state machine, so sequential `let!`s allocate no closures or tasks,
// and `while`/`for` loops run without growing the stack. The machine carries the environment, which
// every `let!` passes to the effect it binds. See docs/modules/eff.md for the semantics and the
// benchmark that chose this design over closure composition.
//
// In resumable code, pass the machine (`&sm`) only to a resumable code's `Invoke` and to the method
// builder's generic `Start`/`AwaitUnsafeOnCompleted`; give any other helper the data it needs (see
// `EffMachine.IsError`). A static machine's layout is not `ResumableStateMachine`'s.
//
// Only effects can be bound: there is deliberately no overload for raw `Task`, `ValueTask` or
// `Async`, so every effect shows up in the environment type. Lift other awaitables with `Eff.ofTask`.

#nowarn "3513" // Resumable code invocation: this file defines the builders' low-level resumable code.

open System
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Core.CompilerServices.StateMachineHelpers

/// The data an `eff` state machine carries. An implementation detail of the builders.
[<Struct; NoComparison; NoEquality>]
type EffStateMachineData<'R, 'T> =
    /// The environment the effect was invoked with.
    [<DefaultValue(false)>]
    val mutable Env: 'R

    [<DefaultValue(false)>]
    val mutable Result: 'T

    [<DefaultValue(false)>]
    val mutable MethodBuilder: AsyncTaskMethodBuilder<'T>

and EffStateMachine<'R, 'TOverall> = ResumableStateMachine<EffStateMachineData<'R, 'TOverall>>
and EffResumptionFunc<'R, 'TOverall> = ResumptionFunc<EffStateMachineData<'R, 'TOverall>>
and EffResumptionDynamicInfo<'R, 'TOverall> = ResumptionDynamicInfo<EffStateMachineData<'R, 'TOverall>>
and EffCode<'R, 'TOverall, 'T> = ResumableCode<EffStateMachineData<'R, 'TOverall>, 'T>

/// Code of an `effResult` block: the machine's result is `Result<'TOverall, 'E>`.
type EffResultCode<'R, 'TOverall, 'E, 'T> = ResumableCode<EffStateMachineData<'R, Result<'TOverall, 'E>>, 'T>

/// The parts of the builders that are not inlined. An implementation detail of the builders, public
/// only because inlined builder code calls it.
[<AbstractClass; Sealed>]
type EffMachine =
    /// Runs a block whose state machine could not be compiled statically (warning FS3511).
    static member RunDynamic(code: ResumableCode<EffStateMachineData<'R, 'T>, 'X>, env: 'R) : Task<'T> =
        let mutable sm = EffStateMachine<'R, 'T>()
        let initial = EffResumptionFunc<'R, 'T>(fun sm -> code.Invoke(&sm))

        let info =
            { new EffResumptionDynamicInfo<'R, 'T>(initial) with
                member info.MoveNext(sm) =
                    let mutable savedExn: exn | null = null

                    try
                        sm.ResumptionDynamicInfo.ResumptionData <- null
                        let step = info.ResumptionFunc.Invoke(&sm)

                        if step then
                            sm.Data.MethodBuilder.SetResult(sm.Data.Result)
                        else
                            match sm.ResumptionDynamicInfo.ResumptionData with
                            | :? ICriticalNotifyCompletion as awaiter ->
                                let mutable awaiter = awaiter
                                sm.Data.MethodBuilder.AwaitUnsafeOnCompleted(&awaiter, &sm)
                            | _ -> invalidOp "An effect yielded without an awaiter."

                    with exn ->
                        savedExn <- exn
                    // Outside the stack unwind, as in FSharp.Core (dotnet/roslyn#26567).
                    match savedExn with
                    | null -> ()
                    | exn -> sm.Data.MethodBuilder.SetException exn

                member _.SetStateMachine(sm, state) =
                    sm.Data.MethodBuilder.SetStateMachine(state)
            }

        sm.ResumptionDynamicInfo <- info
        sm.Data.Env <- env
        sm.Data.MethodBuilder <- AsyncTaskMethodBuilder<'T>.Create()
        sm.Data.MethodBuilder.Start(&sm)
        sm.Data.MethodBuilder.Task

    static member BindDynamic
        (sm: byref<EffStateMachine<'R, 'TOverall>>, eff: Eff<'R, 'T1>, continuation: 'T1 -> EffCode<'R, 'TOverall, 'T2>)
        : bool =
        let mutable awaiter = (eff sm.Data.Env).GetAwaiter()

        let cont =
            EffResumptionFunc<'R, 'TOverall>(fun sm ->
                let result = awaiter.GetResult()
                (continuation result).Invoke(&sm))

        if awaiter.IsCompleted then
            cont.Invoke(&sm)
        else
            sm.ResumptionDynamicInfo.ResumptionData <- (awaiter :> ICriticalNotifyCompletion)
            sm.ResumptionDynamicInfo.ResumptionFunc <- cont
            false

    static member BindResultDynamic
        (
            sm: byref<EffStateMachine<'R, Result<'TOverall, 'E>>>,
            eff: Eff<'R, Result<'T1, 'E>>,
            continuation: 'T1 -> EffResultCode<'R, 'TOverall, 'E, 'T2>
        ) : bool =
        let mutable awaiter = (eff sm.Data.Env).GetAwaiter()

        let cont =
            EffResumptionFunc<'R, Result<'TOverall, 'E>>(fun sm ->
                match awaiter.GetResult() with
                | Ok result -> (continuation result).Invoke(&sm)
                | Error e ->
                    sm.Data.Result <- Error e
                    true)

        if awaiter.IsCompleted then
            cont.Invoke(&sm)
        else
            sm.ResumptionDynamicInfo.ResumptionData <- (awaiter :> ICriticalNotifyCompletion)
            sm.ResumptionDynamicInfo.ResumptionFunc <- cont
            false

    /// Whether an `effResult` block has short-circuited with an error: pass it the machine's result
    /// (`sm.Data.Result`), never the machine. A static machine is a struct the compiler generates,
    /// with fields of its own; handed to a method as `&sm`, it arrives reinterpreted as a
    /// `ResumableStateMachine`, whose layout differs. Optimized builds inline `inline` helpers, which
    /// hides that; non-optimized ones (Debug, since F# 11) call them, and the callee reads garbage.
    static member inline IsError(result: Result<'T, 'E>) : bool =
        match result with
        | Error _ -> true
        | Ok _ -> false

/// The `eff { }` computation expression. Binds `Eff<'R, 'T>` values only.
type EffBuilder() =

    member inline _.Delay(generator: unit -> EffCode<'R, 'TOverall, 'T>) : EffCode<'R, 'TOverall, 'T> =
        EffCode<'R, 'TOverall, 'T>(fun sm -> (generator ()).Invoke(&sm))

    [<DefaultValue>]
    member inline _.Zero() : EffCode<'R, 'TOverall, unit> = ResumableCode.Zero()

    member inline _.Return(value: 'T) : EffCode<'R, 'T, 'T> =
        EffCode<'R, 'T, _>(fun sm ->
            sm.Data.Result <- value
            true)

    member inline _.Combine
        (code1: EffCode<'R, 'TOverall, unit>, code2: EffCode<'R, 'TOverall, 'T>)
        : EffCode<'R, 'TOverall, 'T> =
        ResumableCode.Combine(code1, code2)

    member inline _.While
        ([<InlineIfLambda>] condition: unit -> bool, body: EffCode<'R, 'TOverall, unit>)
        : EffCode<'R, 'TOverall, unit> =
        ResumableCode.While(condition, body)

    member inline _.TryWith
        (body: EffCode<'R, 'TOverall, 'T>, catch: exn -> EffCode<'R, 'TOverall, 'T>)
        : EffCode<'R, 'TOverall, 'T> =
        ResumableCode.TryWith(body, catch)

    member inline _.TryFinally
        (body: EffCode<'R, 'TOverall, 'T>, [<InlineIfLambda>] compensation: unit -> unit)
        : EffCode<'R, 'TOverall, 'T> =
        ResumableCode.TryFinally(
            body,
            ResumableCode<_, _>(fun _ ->
                compensation ()
                true)
        )

    member inline _.For(sequence: seq<'T>, body: 'T -> EffCode<'R, 'TOverall, unit>) : EffCode<'R, 'TOverall, unit> =
        ResumableCode.For(sequence, body)

    member inline _.Using<'Resource, 'R, 'TOverall, 'T when 'Resource :> IDisposable | null>
        (resource: 'Resource, body: 'Resource -> EffCode<'R, 'TOverall, 'T>)
        : EffCode<'R, 'TOverall, 'T> =
        ResumableCode.Using(resource, body)

    member inline _.Bind
        (eff: Eff<'R, 'T1>, continuation: 'T1 -> EffCode<'R, 'TOverall, 'T2>)
        : EffCode<'R, 'TOverall, 'T2> =
        EffCode<'R, 'TOverall, _>(fun sm ->
            if __useResumableCode then
                let mutable awaiter = (eff sm.Data.Env).GetAwaiter()
                let mutable __stack_fin = true

                if not awaiter.IsCompleted then
                    let __stack_yield_fin = ResumableCode.Yield().Invoke(&sm)
                    __stack_fin <- __stack_yield_fin

                if __stack_fin then
                    let result = awaiter.GetResult()
                    (continuation result).Invoke(&sm)
                else
                    sm.Data.MethodBuilder.AwaitUnsafeOnCompleted(&awaiter, &sm)
                    false
            else
                EffMachine.BindDynamic(&sm, eff, continuation))

    member inline this.ReturnFrom(eff: Eff<'R, 'T>) : EffCode<'R, 'T, 'T> = this.Bind(eff, this.Return)

    member inline _.Run(code: EffCode<'R, 'T, 'T>) : Eff<'R, 'T> =
        if __useResumableCode then
            __stateMachine<EffStateMachineData<'R, 'T>, Eff<'R, 'T>>
                (MoveNextMethodImpl<_>(fun sm ->
                    __resumeAt sm.ResumptionPoint
                    let mutable __stack_exn: exn | null = null

                    try
                        let __stack_code_fin = code.Invoke(&sm)

                        if __stack_code_fin then
                            sm.Data.MethodBuilder.SetResult(sm.Data.Result)
                    with exn ->
                        __stack_exn <- exn

                    match __stack_exn with
                    | null -> ()
                    | exn -> sm.Data.MethodBuilder.SetException exn))
                (SetStateMachineMethodImpl<_>(fun sm state -> sm.Data.MethodBuilder.SetStateMachine(state)))
                (AfterCode<_, _>(fun sm ->
                    // The initial machine, copied for every invocation.
                    let initial = sm

                    fun (env: 'R) ->
                        let mutable sm = initial
                        sm.Data.Env <- env
                        sm.Data.MethodBuilder <- AsyncTaskMethodBuilder<'T>.Create()
                        sm.Data.MethodBuilder.Start(&sm)
                        sm.Data.MethodBuilder.Task))
        else
            fun (env: 'R) -> EffMachine.RunDynamic(code, env)

/// The `effResult { }` computation expression: an `eff` whose result is a `Result<'T, 'E>`. A `let!`
/// of an `Error` ends the block with that error. `let!` also accepts plain effects (`Eff<'R, 'T>`) and
/// pure `Result` values.
type EffResultBuilder() =

    member inline _.Delay
        (generator: unit -> EffResultCode<'R, 'TOverall, 'E, 'T>)
        : EffResultCode<'R, 'TOverall, 'E, 'T> =
        EffResultCode<'R, 'TOverall, 'E, 'T>(fun sm -> (generator ()).Invoke(&sm))

    [<DefaultValue>]
    member inline _.Zero() : EffResultCode<'R, 'TOverall, 'E, unit> = ResumableCode.Zero()

    member inline _.Return(value: 'T) : EffResultCode<'R, 'T, 'E, 'T> =
        EffResultCode<'R, 'T, 'E, _>(fun sm ->
            sm.Data.Result <- Ok value
            true)

    member inline _.Combine
        (code1: EffResultCode<'R, 'TOverall, 'E, unit>, code2: EffResultCode<'R, 'TOverall, 'E, 'T>)
        : EffResultCode<'R, 'TOverall, 'E, 'T> =
        ResumableCode.Combine(
            code1,
            EffResultCode<'R, 'TOverall, 'E, 'T>(fun sm ->
                if EffMachine.IsError sm.Data.Result then
                    true
                else
                    code2.Invoke(&sm))
        )

    member inline _.While
        ([<InlineIfLambda>] condition: unit -> bool, body: EffResultCode<'R, 'TOverall, 'E, unit>)
        : EffResultCode<'R, 'TOverall, 'E, unit> =
        let mutable keepGoing = true

        ResumableCode.While(
            (fun () -> keepGoing && condition ()),
            ResumableCode.Combine(
                body,
                EffResultCode<'R, 'TOverall, 'E, unit>(fun sm ->
                    if EffMachine.IsError sm.Data.Result then
                        keepGoing <- false

                    true)
            )
        )

    member inline _.TryWith
        (body: EffResultCode<'R, 'TOverall, 'E, 'T>, catch: exn -> EffResultCode<'R, 'TOverall, 'E, 'T>)
        : EffResultCode<'R, 'TOverall, 'E, 'T> =
        ResumableCode.TryWith(body, catch)

    member inline _.TryFinally
        (body: EffResultCode<'R, 'TOverall, 'E, 'T>, [<InlineIfLambda>] compensation: unit -> unit)
        : EffResultCode<'R, 'TOverall, 'E, 'T> =
        ResumableCode.TryFinally(
            body,
            ResumableCode<_, _>(fun _ ->
                compensation ()
                true)
        )

    member inline this.For
        (sequence: seq<'T>, body: 'T -> EffResultCode<'R, 'TOverall, 'E, unit>)
        : EffResultCode<'R, 'TOverall, 'E, unit> =
        ResumableCode.Using(
            sequence.GetEnumerator(),
            fun e ->
                this.While(
                    (fun () -> e.MoveNext()),
                    EffResultCode<'R, 'TOverall, 'E, unit>(fun sm -> (body e.Current).Invoke(&sm))
                )
        )

    member inline _.Using<'Resource, 'R, 'TOverall, 'E, 'T when 'Resource :> IDisposable | null>
        (resource: 'Resource, body: 'Resource -> EffResultCode<'R, 'TOverall, 'E, 'T>)
        : EffResultCode<'R, 'TOverall, 'E, 'T> =
        ResumableCode.Using(resource, body)

    member inline _.Bind
        (eff: Eff<'R, Result<'T1, 'E>>, continuation: 'T1 -> EffResultCode<'R, 'TOverall, 'E, 'T2>)
        : EffResultCode<'R, 'TOverall, 'E, 'T2> =
        EffResultCode<'R, 'TOverall, 'E, _>(fun sm ->
            if __useResumableCode then
                let mutable awaiter = (eff sm.Data.Env).GetAwaiter()
                let mutable __stack_fin = true

                if not awaiter.IsCompleted then
                    let __stack_yield_fin = ResumableCode.Yield().Invoke(&sm)
                    __stack_fin <- __stack_yield_fin

                if __stack_fin then
                    match awaiter.GetResult() with
                    | Ok result -> (continuation result).Invoke(&sm)
                    | Error e ->
                        sm.Data.Result <- Error e
                        true
                else
                    sm.Data.MethodBuilder.AwaitUnsafeOnCompleted(&awaiter, &sm)
                    false
            else
                EffMachine.BindResultDynamic(&sm, eff, continuation))

    member inline this.ReturnFrom(eff: Eff<'R, Result<'T, 'E>>) : EffResultCode<'R, 'T, 'E, 'T> =
        this.Bind(eff, this.Return)

    member inline _.Source(eff: Eff<'R, Result<'T, 'E>>) : Eff<'R, Result<'T, 'E>> = eff

    member inline _.Source(result: Result<'T, 'E>) : Eff<'R, Result<'T, 'E>> = fun _ -> Task.FromResult result

    member inline _.Source(sequence: seq<'T>) : seq<'T> = sequence

    member inline _.Run(code: EffResultCode<'R, 'T, 'E, 'T>) : Eff<'R, Result<'T, 'E>> =
        if __useResumableCode then
            __stateMachine<EffStateMachineData<'R, Result<'T, 'E>>, Eff<'R, Result<'T, 'E>>>
                (MoveNextMethodImpl<_>(fun sm ->
                    __resumeAt sm.ResumptionPoint
                    let mutable __stack_exn: exn | null = null

                    try
                        let __stack_code_fin = code.Invoke(&sm)

                        if __stack_code_fin then
                            sm.Data.MethodBuilder.SetResult(sm.Data.Result)
                    with exn ->
                        __stack_exn <- exn

                    match __stack_exn with
                    | null -> ()
                    | exn -> sm.Data.MethodBuilder.SetException exn))
                (SetStateMachineMethodImpl<_>(fun sm state -> sm.Data.MethodBuilder.SetStateMachine(state)))
                (AfterCode<_, _>(fun sm ->
                    let initial = sm

                    fun (env: 'R) ->
                        let mutable sm = initial
                        sm.Data.Env <- env
                        sm.Data.MethodBuilder <- AsyncTaskMethodBuilder<Result<'T, 'E>>.Create()
                        sm.Data.MethodBuilder.Start(&sm)
                        sm.Data.MethodBuilder.Task))
        else
            fun (env: 'R) -> EffMachine.RunDynamic(code, env)

/// Plain effects in `effResult`, at a lower priority than `Eff<'R, Result<'T, 'E>>` so that an
/// effect that already returns a `Result` is bound as one.
[<AutoOpen>]
module EffResultBuilderLowPriority =
    type EffResultBuilder with
        member inline _.Source(eff: Eff<'R, 'T>) : Eff<'R, Result<'T, 'E>> =
            fun env ->
                let task = eff env

                if task.IsCompletedSuccessfully then
                    Task.FromResult(Ok task.Result)
                else
                    task.ContinueWith(
                        (fun (t: Task<'T>) -> Ok(t.GetAwaiter().GetResult())),
                        TaskContinuationOptions.ExecuteSynchronously
                    )

[<AutoOpen>]
module EffBuilders =
    /// Builds an `Eff<'R, 'T>`. See `EffBuilder`.
    let eff = EffBuilder()

    /// Builds an `Eff<'R, Result<'T, 'E>>` that stops at the first `Error`. See `EffResultBuilder`.
    let effResult = EffResultBuilder()
