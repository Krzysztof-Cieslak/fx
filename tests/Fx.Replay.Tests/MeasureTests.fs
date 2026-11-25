/// What recording costs (docs/modules/replay.md, "Cost"): with `REPLAY_MEASURE=<file>`, records sessions
/// with `session.record` on and off and writes their durations and the sizes of the journal, its blobs
/// and the session's logs to the file. A measurement, not a check: skipped without the variable.
module Fx.Replay.Tests.MeasureTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Machines.Host
open Fx.Store
open Fx.Replay
open Fx.Replay.Tests.Support

let private steps = 20
let private turns = 3

/// `turns` turns of `steps` cells each: every cell reads a 4 KiB file and prints 2 KiB.
let private session (f: Fixture) : Task<TimeSpan> =
    task {
        f.Fs.AddText(file "data.txt", String('d', 4096))
        let print = String('p', 2048)
        do! start f "s1"
        let clock = Stopwatch.StartNew()

        for turn in 1..turns do
            f.Model.AddAll(
                [
                    for step in 1..steps do
                        Say.cell $"// intent: step {step}\ncall d = fs.read [\"data.txt\"]\nprint {print}\nd"
                ]
                @ [ Say.finish $"Turn {turn}" ]
            )

            do! prompt f "s1" $"p{turn}" $"Turn {turn}"
            let! _ = ended f "s1" turn
            ()

        clock.Stop()
        return clock.Elapsed
    }

let private bytesOf (f: Fixture) (prefix: string) =
    f.Store.Streams
    |> List.filter (fun s -> s.Value.StartsWith(prefix, StringComparison.Ordinal))
    |> List.collect f.Store.Events
    |> List.sumBy (fun e -> int64 (Encoding.UTF8.GetByteCount(Json.serialize e.Event.Data)))

[<Tests>]
let tests =
    testTask "measure: what recording costs" {
        match Environment.GetEnvironmentVariable "REPLAY_MEASURE" with
        | null
        | "" -> skiptest "set REPLAY_MEASURE=<file> to measure"
        | output ->
            let lines = ResizeArray<string>()

            for mode in [ "off"; "on"; "off"; "on"; "off"; "on" ] do
                let f = create [ "session.record", Json.String mode ]

                try
                    let! (elapsed: TimeSpan) = session f
                    do! close f
                    let logs = bytesOf f "session/" + bytesOf f "context/" + bytesOf f "steps/"
                    let journal = bytesOf f "journal/"
                    let blobs = f.Blobs.All |> List.sumBy (fun (_, b) -> int64 b.Length)

                    let! (segments: JournalSegment list) =
                        SessionReplay.segments f.Recording "s1" Threading.CancellationToken.None

                    let count = segments |> List.sumBy (fun s -> s.Entries.Length)

                    lines.Add
                        $"record {mode}: {turns} turns x {steps} cells in {elapsed.TotalMilliseconds:F0} ms; logs {logs} B; journal {journal} B in {count} entries; blobs {blobs} B in {f.Blobs.All.Length}"

                    if mode = "on" then
                        let clock = Stopwatch.StartNew()
                        let! (report: ReplayReport) = replay f.Recording "s1"
                        clock.Stop()
                        lines.Add $"  replay: matched {report.Matched} in {clock.Elapsed.TotalMilliseconds:F0} ms"
                finally
                    (f :> IDisposable).Dispose()

            File.WriteAllLines(output, lines)
    }
