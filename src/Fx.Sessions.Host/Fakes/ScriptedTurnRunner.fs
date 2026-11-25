namespace Fx.Sessions.Host.Fakes

open System
open Fx.Effects
open Fx.Sessions
open Fx.Sessions.Host

/// One thing a scripted turn does.
[<RequireQualifiedAccess>]
type TurnStep =
    /// Works for a while (the model thinking, a tool running), on the clock.
    | Think of TimeSpan
    /// Reaches a step boundary and takes the inputs steered in meanwhile.
    | Boundary
    /// Opens a request and waits for its resolution (a tool call waiting for a grant).
    | Ask of RequestSpec
    /// Opens a request and goes on (a decision card it reads about later).
    | Post of RequestSpec
    /// Ends the turn with a result.
    | Finish of TurnResult
    /// Ends the turn with an expected failure.
    | Fail of reason: string
    /// Throws: a defect in the runner.
    | Throw

    override this.ToString() =
        match this with
        | Think duration -> "think " + string duration.TotalSeconds + "s"
        | Boundary -> "boundary"
        | Ask request -> "ask " + request.Id
        | Post request -> "post " + request.Id
        | Finish result -> "finish " + string result
        | Fail reason -> "fail " + reason
        | Throw -> "throw"

/// The defect `TurnStep.Throw` raises.
type ScriptedDefectException() =
    inherit Exception("A scripted turn threw.")

/// A turn runner that plays scripts, for tests of sessions and of what drives them: each turn runs the
/// steps `script` gives for it, and the transcript says what the turn saw (its inputs, what each
/// boundary delivered, each resolution) and how it ended. A script without `Finish` ends `Done`.
/// Thread-safe.
[<Sealed>]
type ScriptedTurnRunner<'R when 'R :> IClock and 'R :> IScope>(script: TurnStart -> TurnStep list) =
    let gate = obj ()
    let transcript = ResizeArray<string>()

    let note (line: string) =
        lock gate (fun () -> transcript.Add line)

    let inputs (items: Input list) =
        "[" + (items |> List.map string |> String.concat "; ") + "]"

    let mutable recovery: TurnStart -> TurnStep list option = fun _ -> None

    let play
        (started: string)
        (script: TurnStep list)
        (context: TurnContext<'R>)
        : Eff<'R, Result<TurnResult, string>> =
        let turn = "turn " + string context.Turn.Turn

        eff {
            note (turn + " " + started + " with " + inputs context.Turn.Inputs)
            let mutable steps = script
            let mutable step = 0
            let mutable ended = None

            try
                while ended.IsNone do
                    match steps with
                    | [] -> ended <- Some(Ok(TurnResult.Done("done", [], None)))
                    | next :: rest ->
                        steps <- rest

                        match next with
                        | TurnStep.Think duration -> do! Clock.sleep duration
                        | TurnStep.Boundary ->
                            step <- step + 1
                            let! delivered = context.Boundary step
                            note (turn + " step " + string step + " took " + inputs delivered)
                        | TurnStep.Ask request ->
                            match! TurnContext.ask context request with
                            | Ok resolution -> note (turn + " asked " + request.Id + ": " + string resolution)
                            | Error rejection -> note (turn + " could not ask " + request.Id + ": " + string rejection)
                        | TurnStep.Post request ->
                            match! context.Open request with
                            | Ok _ -> note (turn + " posted " + request.Id)
                            | Error rejection -> note (turn + " could not post " + request.Id + ": " + string rejection)
                        | TurnStep.Finish result -> ended <- Some(Ok result)
                        | TurnStep.Fail reason -> ended <- Some(Error reason)
                        | TurnStep.Throw -> raise (ScriptedDefectException())
            with :? OperationCanceledException as e ->
                note (turn + " cancelled")
                raise e

            match ended.Value with
            | Ok result -> note (turn + " finished: " + string result)
            | Error reason -> note (turn + " failed: " + reason)

            return ended.Value
        }

    let run (context: TurnContext<'R>) =
        play "started" (script context.Turn) context

    let recover (context: TurnContext<'R>) : Eff<'R, Result<TurnResult, string> option> =
        match recovery context.Turn with
        | Some steps -> play "recovered" steps context |> Eff.map Some
        | None -> Eff.ret None

    member val Runner: TurnRunner<'R> = { Run = run; Recover = recover }

    /// The steps of each turn.
    member _.Script = script

    /// What a turn the host stopped during does: `None` (the default) abandons it; steps play as the
    /// turn's recovery (e.g. asking what happened to a tool call in doubt), and its result ends it.
    member _.Recovery
        with get () = recovery
        and set value = recovery <- value

    /// What the turns saw and did, in order.
    member _.Transcript: string list = lock gate (fun () -> List.ofSeq transcript)

    override _.ToString() = "ScriptedTurnRunner"

module ScriptedTurnRunner =
    /// Plays the steps for each turn number; turns without a script end `Done` at once.
    let byTurn<'R when 'R :> IClock and 'R :> IScope> (turns: (int * TurnStep list) list) : ScriptedTurnRunner<'R> =
        ScriptedTurnRunner(fun start ->
            turns
            |> List.tryFind (fun (turn, _) -> turn = start.Turn)
            |> Option.map snd
            |> Option.defaultValue [])
