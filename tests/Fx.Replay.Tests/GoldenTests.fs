/// The golden recorded session (`golden/session-v1.jsonl`): a session recorded once and checked in, which
/// today's code must keep replaying exactly. A change that makes it diverge changes what fx does with
/// the same world: if that is intended, record it again deliberately with
/// `RERECORD_GOLDEN=1 dotnet test --project tests/Fx.Replay.Tests` and review the diff (docs/modules/replay.md).
/// Also: replays that must diverge, and do, naming the first difference; a session not recorded.
module Fx.Replay.Tests.GoldenTests

open System
open System.Text
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Sessions
open Fx.Kernel
open Fx.Replay
open Fx.Replay.Tests.Support
open Fx.Replay.Tests.Harness
open Fx.Aot.Scenarios.Kernel

[<Literal>]
let private File = "session-v1.jsonl"

/// What the golden session does: a hybrid turn (read, edit, a command, a cell, finish), then a turn
/// whose cell recalls the earlier cell's value.
let private golden (f: Fixture) : Task<string> =
    task {
        let! session = SessionTests.hybridTurn f

        f.Model.AddAll
            [
                Say.cell "// intent: recall\ncall v = recall [\"call-4\"]\nv"
                Say.finish "Recalled"
            ]

        do! prompt f session "p2" "Show what the cell read"
        let! ending = ended f session 2
        SessionTests.completed ending
        return session
    }

let private rerecording () =
    Environment.GetEnvironmentVariable "RERECORD_GOLDEN" = "1"

/// The same recording with one journaled value changed: the file content a binding read.
let private tampered (f: Fixture) : RecordedSession =
    let original = Convert.ToBase64String(Encoding.UTF8.GetBytes "# Notes\nfirst\n")
    let changed = Convert.ToBase64String(Encoding.UTF8.GetBytes "# Notes\nfirsT\n")
    let text = Exported.write f.Store f.Blobs
    Expect.stringContains text original "the read is journaled"
    Exported.read (text.Replace(original, changed))

[<Tests>]
let tests =
    testList
        "golden and divergences"
        [
            testTask "the golden recorded session replays exactly" {
                if rerecording () then
                    let f = create SessionTests.hybrid

                    try
                        let! _ = golden f
                        do! close f
                        Golden.write File (Exported.write f.Store f.Blobs)
                    finally
                        (f :> IDisposable).Dispose()

                let recording = Exported.read (Golden.read File)
                let! report = replay recording "s1"
                expectMatched report
                Expect.equal report.Turns 2 "two turns"
                Expect.isGreaterThan report.Requests 5 "the model's requests"
            }

            testTask "a recording with a different value diverges at the first call it changes" {
                let f = create []

                try
                    let! session = SessionTests.cellsOnly f
                    do! close f
                    let! report = replay (tampered f) session

                    match report.Divergence with
                    | Some divergence ->
                        // The cell's read gave other text, so the host answered the kernel otherwise.
                        Expect.stringContains divergence.Lane "proc" "the kernel's process"
                        Expect.stringContains divergence.Actual "Proc.Write" "a write to it"
                        Expect.stringContains divergence.Expected "Proc.Write" "the recorded write"
                    | None -> failtest "replay matched a tampered recording"
                finally
                    (f :> IDisposable).Dispose()
            }

            testTask "changed code diverges at the first thing it writes differently" {
                let f = create []

                try
                    let! session = SessionTests.cellsOnly f
                    do! close f

                    let changed =
                        { settings with
                            Interpreter =
                                fun _ ->
                                    { ScriptProc.interpreter kernelOptions with
                                        Prompt = fun _ -> "Cells are scripts, said differently."
                                    }
                        }

                    let! report =
                        (Fx.Effects.Eff.run driver (SessionReplay.run changed f.Recording session))
                            .WaitAsync(TimeSpan.FromSeconds 120.0)

                    // The prompt's frame is recorded before the model is called: that append is the
                    // first thing replay does differently, and the report shows both events.
                    match report.Divergence with
                    | Some divergence ->
                        Expect.equal divergence.Lane "store" "the context log"
                        Expect.stringContains divergence.Actual "/frame" "the frame replay wrote"
                        Expect.stringContains divergence.Actual "said differently" "with the new prompt"
                        Expect.stringContains divergence.Expected "/frame" "the frame recorded"
                        Expect.stringContains (string report) "replay's last steps" "what replay did before"
                    | None -> failtest "replay matched with another prompt"
                finally
                    (f :> IDisposable).Dispose()
            }

            testTask "a session with recording off has no journal, and says so" {
                let f = create [ "session.record", Json.String "off" ]

                try
                    let! session = SessionTests.cellsOnly f
                    do! close f
                    Expect.isEmpty (f.Store.Events(JournalLog.stream session)) "no journal"
                    let! report = replay f.Recording session

                    match report.Divergence with
                    | Some divergence -> Expect.stringContains divergence.Reason "not recorded" "why"
                    | None -> failtest "replayed a session that was not recorded"
                finally
                    (f :> IDisposable).Dispose()
            }
        ]
