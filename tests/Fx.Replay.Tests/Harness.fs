/// The replay test harness (docs/modules/replay.md): a recorded session (its journal and logs) becomes a
/// test that replays it exactly and fails with the first divergence.
module Fx.Replay.Tests.Harness

open System
open System.Threading.Tasks
open Expecto
open Fx.Replay
open Fx.Replay.Tests.Support

/// Fails with the report unless replay matched the recording.
let expectMatched (report: ReplayReport) =
    match report.Divergence with
    | None -> ()
    | Some _ -> failtest (string report)

/// With `REPLAY_DUMP=<directory>`, a diverging replay writes the recording's journal there, one entry a
/// line in `Seq` order, to compare with the report's last steps.
let dump (f: Fixture) (session: string) (report: ReplayReport) =
    match Environment.GetEnvironmentVariable "REPLAY_DUMP", report.Divergence with
    | null, _
    | "", _
    | _, None -> ()
    | directory, Some _ ->
        let lines =
            f.Store.Events(JournalLog.stream session)
            |> List.map (Fx.Store.EventLog.decode JournalLog.schema)
            |> List.collect (fun r ->
                match r.Event with
                | JournalEvent.Recorded(segment, entries) -> entries |> List.map (fun e -> segment, e)
                | _ -> [])
            |> List.sortBy (fun (segment, e) -> segment, e.Seq)
            |> List.map (fun (segment, e) -> $"{segment} {e}")

        IO.Directory.CreateDirectory directory |> ignore
        IO.File.WriteAllLines(IO.Path.Combine(directory, $"journal-{DateTime.UtcNow.Ticks}.txt"), lines)

/// A test that replays `session` of a recording exactly.
let replays (name: string) (recording: unit -> RecordedSession) (session: string) : Test =
    testTask name {
        let! report = replay (recording ()) session
        expectMatched report
    }

/// A test that records a session (`record` runs it on a fixture and gives the session's id), closes
/// the host, and replays the recording exactly; `check` sees the report too.
let recordsAndReplays
    (name: string)
    (fixture: unit -> Fixture)
    (record: Fixture -> Task<string>)
    (check: Fixture -> ReplayReport -> unit)
    : Test =
    testTask name {
        let f = fixture ()

        try
            let! session =
                task {
                    try
                        return! (record f).WaitAsync(TimeSpan.FromSeconds 120.0)
                    with :? TimeoutException ->
                        return failtest "recording the session timed out"
                }

            do! close f

            let! report =
                task {
                    try
                        return! replay f.Recording session
                    with :? TimeoutException ->
                        return failtest "replaying the session timed out"
                }

            dump f session report
            expectMatched report
            check f report
        finally
            (f :> IDisposable).Dispose()
    }

/// No further checks.
let ignore2 (_: Fixture) (_: ReplayReport) = ()
