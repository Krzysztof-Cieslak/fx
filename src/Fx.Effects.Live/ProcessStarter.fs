namespace Fx.Effects.Live

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// The one thread this process starts processes on, which lives as long as the process (T135).
///
/// On Linux a child can ask to be killed when its parent dies (`PR_SET_PDEATHSIG`, which bubblewrap's
/// `--die-with-parent` sets), and its parent is the thread that started it, not the process. Started
/// from a thread-pool thread, a sandboxed process was killed (SIGKILL, exit code 137) when the pool
/// retired that thread after it had been idle for 20 seconds: a session's kernel lost its names between
/// turns and while a cell waited for a person. `Fx.Pty` has the same starter for terminals.
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
            (let thread = Thread(ThreadStart loop, IsBackground = true, Name = "process starter")

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
