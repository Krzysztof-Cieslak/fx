namespace Fx.Pty

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// The one thread this process starts terminals' processes on, which lives as long as the process
/// (T135). On Linux a child can ask to be killed when its parent dies (`PR_SET_PDEATHSIG`, which
/// bubblewrap's `--die-with-parent` sets), and its parent is the thread that started it: started
/// from a thread-pool thread, a sandboxed process was killed when the pool retired that idle thread.
/// `Fx.Effects.Live`'s `LocalShell` has the same starter.
module internal ProcessStarter =
    let private work = new BlockingCollection<unit -> unit>()

    let private loop () =
        for item in work.GetConsumingEnumerable() do
            // Each item catches its own failures; the thread must never end.
            try
                item ()
            with _ ->
                ()

    let private thread =
        lazy
            (let thread =
                Thread(ThreadStart loop, IsBackground = true, Name = "terminal starter")

             thread.Start()
             thread)

    /// Runs `start` on the starter thread. The task's continuations never run there.
    let run (start: unit -> 'T) : Task<'T> =
        thread.Force() |> ignore

        let result =
            TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

        work.Add(fun () ->
            try
                result.SetResult(start ())
            with e ->
                result.SetException e)

        result.Task
