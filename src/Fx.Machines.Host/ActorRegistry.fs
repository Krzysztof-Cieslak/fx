namespace Fx.Machines.Host

open System.Collections.Generic
open System.Threading.Tasks
open Fx.Effects
open Fx.Machines

/// One actor per stream (spec §3): starts an actor the first time a stream is addressed, and again
/// after it stopped, so a stream never has two writers in this process. Thread-safe.
[<Sealed>]
type ActorRegistry<'Cmd, 'Evt, 'S> internal (activate: StreamId -> Task<Actor<'Cmd, 'Evt, 'S>>) =
    let gate = obj ()
    let actors = Dictionary<StreamId, Task<Actor<'Cmd, 'Evt, 'S>>>()
    let mutable closed = false

    let alive (starting: Task<Actor<'Cmd, 'Evt, 'S>>) =
        not starting.IsCompleted
        || starting.IsCompletedSuccessfully && not starting.Result.Completion.IsCompleted

    /// The stream's actor, started if it is not running; `None` once the registry is stopped. A
    /// failed start fails this call and is retried by the next one.
    member _.Get(stream: StreamId) : Task<Actor<'Cmd, 'Evt, 'S> option> =
        let existing, starting =
            lock gate (fun () ->
                if closed then
                    None, None
                else
                    match actors.TryGetValue stream with
                    | true, running when alive running -> Some running, None
                    | _ ->
                        let starting = TaskCompletionSource<Actor<'Cmd, 'Evt, 'S>>()
                        actors[stream] <- starting.Task
                        Some starting.Task, Some starting)

        // Outside the lock: starting an actor replays its log and runs its recovery.
        match starting with
        | Some completion ->
            task {
                try
                    let! actor = activate stream
                    completion.SetResult actor
                with e ->
                    completion.SetException e
            }
            |> ignore
        | None -> ()

        match existing with
        | Some running ->
            task {
                let! actor = running
                return Some actor
            }
        | None -> Task.FromResult None

    /// The streams whose actors are running or starting.
    member _.Active: StreamId list =
        lock gate (fun () ->
            actors
            |> Seq.filter (fun pair -> alive pair.Value)
            |> Seq.map (fun pair -> pair.Key)
            |> Seq.sortBy (fun stream -> stream.Value)
            |> List.ofSeq)

    /// Stops every actor and refuses to start new ones.
    member _.Stop() : Task<unit> =
        let running =
            lock gate (fun () ->
                closed <- true
                List.ofSeq actors.Values)

        task {
            for starting in running do
                try
                    let! actor = starting
                    do! actor.Stop()
                with _ ->
                    ()
        }

    override _.ToString() = "ActorRegistry"

module ActorRegistry =
    /// A registry that starts actors with `start stream` (e.g. from a snapshot, `Actor.startFrom`), in
    /// the environment it is created in: actors outlive the scopes of the calls that start them.
    let createWith<'R, 'Cmd, 'Evt, 'S>
        (start: StreamId -> Eff<'R, Actor<'Cmd, 'Evt, 'S>>)
        : Eff<'R, ActorRegistry<'Cmd, 'Evt, 'S>> =
        fun env -> Task.FromResult(ActorRegistry(fun stream -> Eff.run env (Eff.delay (fun () -> start stream))))

    /// A registry that starts actors from `spec stream`, in the environment it is created in: actors
    /// outlive the scopes of the calls that start them.
    let create<'R, 'Cmd, 'Evt, 'S, 'Intent when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (spec: StreamId -> ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent>)
        : Eff<'R, ActorRegistry<'Cmd, 'Evt, 'S>> =
        createWith (fun stream -> Actor.start (spec stream))

    /// The stream's actor, started if needed; `None` once the registry is stopped.
    let get<'R, 'Cmd, 'Evt, 'S>
        (registry: ActorRegistry<'Cmd, 'Evt, 'S>)
        (stream: StreamId)
        : Eff<'R, Actor<'Cmd, 'Evt, 'S> option> =
        fun _ -> registry.Get stream

    /// Sends a command to the stream's actor, starting it if needed.
    let send<'R, 'Cmd, 'Evt, 'S when 'R :> IScope>
        (registry: ActorRegistry<'Cmd, 'Evt, 'S>)
        (stream: StreamId)
        (envelope: Envelope<'Cmd>)
        : Eff<'R, Outcome> =
        eff {
            match! get registry stream with
            | Some actor -> return! Actor.send actor envelope
            | None -> return Stopped
        }

    /// Interrupts the stream's actor (`Actor.interrupt`), starting it if needed.
    let interrupt<'R, 'Cmd, 'Evt, 'S when 'R :> IScope>
        (registry: ActorRegistry<'Cmd, 'Evt, 'S>)
        (stream: StreamId)
        (envelope: Envelope<'Cmd>)
        : Eff<'R, Outcome> =
        eff {
            match! get registry stream with
            | Some actor -> return! Actor.interrupt actor envelope
            | None -> return Stopped
        }

    /// Stops every actor; later calls get `None` or `Stopped`.
    let stop<'R, 'Cmd, 'Evt, 'S> (registry: ActorRegistry<'Cmd, 'Evt, 'S>) : Eff<'R, unit> = fun _ -> registry.Stop()
