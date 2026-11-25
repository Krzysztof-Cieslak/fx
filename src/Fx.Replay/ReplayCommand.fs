namespace Fx.Replay

open System
open System.Globalization
open Fx
open Fx.Contracts
open Fx.Effects

/// What `replay` was asked: the session, how to print the report, how long replay may stall.
type ReplayArgs =
    {
        Session: string
        Json: bool
        StallSeconds: int
    }

    override this.ToString() = $"replay {this.Session}"

/// What `replay` printed, and its exit code: 0 when replay matched the recording, 1 when it diverged
/// (or the session was not recorded), 2 for invalid arguments.
type ReplayOutput =
    {
        ExitCode: int
        Stdout: string
        Stderr: string
    }

    override this.ToString() = $"exit {this.ExitCode}"

/// `fx replay <session>` (spec §15): parsing and the operation, pure; the host's command
/// (`Commands.replay`) opens the stores under the data directory's lock, builds `ReplaySettings` from
/// the interpreters it has, and the entry point prints the output.
[<RequireQualifiedAccess>]
module ReplayCommand =
    let usage =
        $"Usage: {Product.binaryName} replay <session> [--json] [--stall <seconds>]\n\n"
        + "Replays a recorded session exactly, without the model, kernels, files or clock, and reports the\n"
        + "first difference from the recording. Exit code 0: replay matched; 1: it diverged.\n"

    /// The arguments after `replay`.
    let parse (args: string list) : Result<ReplayArgs, string> =
        let rec loop (parsed: ReplayArgs) (session: string option) (args: string list) =
            match args with
            | [] ->
                match session with
                | Some session -> Ok { parsed with Session = session }
                | None -> Error "replay needs a session"
            | "--json" :: rest -> loop { parsed with Json = true } session rest
            | "--stall" :: seconds :: rest ->
                match Int32.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, n when n > 0 -> loop { parsed with StallSeconds = n } session rest
                | _ -> Error $"invalid --stall: {seconds}"
            | option :: _ when option.StartsWith("-", StringComparison.Ordinal) -> Error $"unknown option: {option}"
            | id :: rest when session.IsNone -> loop parsed (Some id) rest
            | other :: _ -> Error $"unexpected argument: {other}"

        loop
            {
                Session = ""
                Json = false
                StallSeconds = 30
            }
            None
            args

    let private divergence: Codec<Divergence> =
        Codec.record {
            let! segment = Codec.field "segment" (fun (d: Divergence) -> d.Segment) Codec.int
            and! lane = Codec.field "lane" (fun d -> d.Lane) Codec.string
            and! expected = Codec.field "expected" (fun d -> d.Expected) Codec.string
            and! actual = Codec.field "actual" (fun d -> d.Actual) Codec.string
            and! reason = Codec.field "reason" (fun d -> d.Reason) Codec.string

            return
                {
                    Segment = segment
                    Lane = lane
                    Expected = expected
                    Actual = actual
                    Reason = reason
                }
        }
        |> Codec.describe "The first difference between a replay and its recording."

    /// The report as JSON (for evals and dashboards).
    let reportCodec: Codec<ReplayReport> =
        Codec.record {
            let! session =
                Codec.field "session" (fun (r: ReplayReport) -> r.Session) Codec.string

            and! segments = Codec.field "segments" (fun r -> r.Segments) Codec.int
            and! turns = Codec.field "turns" (fun r -> r.Turns) Codec.int
            and! entries = Codec.field "entries" (fun r -> r.Entries) Codec.int
            and! requests = Codec.field "requests" (fun r -> r.Requests) Codec.int
            and! contextEvents = Codec.field "contextEvents" (fun r -> r.ContextEvents) Codec.int
            and! stepEvents = Codec.field "stepEvents" (fun r -> r.StepEvents) Codec.int
            and! diverged = Codec.optionalField "divergence" (fun r -> r.Divergence) divergence

            and! trace =
                Codec.defaultField "trace" (fun r -> r.Trace) [] (Codec.list Codec.string)

            return
                {
                    Session = session
                    Segments = segments
                    Turns = turns
                    Entries = entries
                    Requests = requests
                    ContextEvents = contextEvents
                    StepEvents = stepEvents
                    Divergence = diverged
                    Trace = trace
                }
        }
        |> Codec.describe "What exact replay of a session found."

    /// What `replay` prints for a report.
    let output (args: ReplayArgs) (report: ReplayReport) : ReplayOutput =
        let text =
            if args.Json then
                Json.serialize (reportCodec.Encode report) + "\n"
            else
                string report + "\n"

        {
            ExitCode = if report.Matched then 0 else 1
            Stdout = text
            Stderr = ""
        }

    /// Replays the session `args` names, on a recording.
    let replay<'E when 'E :> IClock and 'E :> IScopeWith<'E>>
        (settings: ReplaySettings)
        (recording: RecordedSession)
        (args: ReplayArgs)
        : Eff<'E, ReplayOutput> =
        eff {
            let settings =
                { settings with
                    StallAfter = TimeSpan.FromSeconds(float args.StallSeconds)
                }

            let! report = SessionReplay.run settings recording args.Session
            return output args report
        }

    /// Runs `replay` with the arguments after it, on a recording.
    let run<'E when 'E :> IClock and 'E :> IScopeWith<'E>>
        (settings: ReplaySettings)
        (recording: RecordedSession)
        (args: string list)
        : Eff<'E, ReplayOutput> =
        match parse args with
        | Error message ->
            Eff.ret
                {
                    ExitCode = 2
                    Stdout = ""
                    Stderr = $"{Product.Name}: {message}\n\n{usage}"
                }
        | Ok parsed -> replay settings recording parsed
