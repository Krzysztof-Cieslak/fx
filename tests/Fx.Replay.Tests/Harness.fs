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
            let! session = (record f).WaitAsync(TimeSpan.FromSeconds 120.0)
            do! close f
            let! report = replay f.Recording session
            expectMatched report
            check f report
        finally
            (f :> IDisposable).Dispose()
    }

/// No further checks.
let ignore2 (_: Fixture) (_: ReplayReport) = ()
