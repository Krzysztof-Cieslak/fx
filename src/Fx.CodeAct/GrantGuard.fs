namespace Fx.CodeAct

open System
open System.Security.Cryptography
open System.Text
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Kernel
open Fx.Sdk
open Fx.Sessions
open Fx.Grants

/// What a turn's grants guard works with. The composition root builds it for each turn's hands
/// (`GrantGuard.attach`), with the variants of the turn's profile.
[<NoEquality; NoComparison>]
type GrantSetup =
    {
        /// The grants capability (`GrantsService.grants`): it checks and requests, and never grants.
        Grants: Grants
        /// The session events: a steer that arrives while a person is asked withdraws the request.
        Sessions: EventLog<Event>
        /// The turn the guard serves: its requests name its session, and their keys derive from its
        /// start event.
        Turn: TurnStart
        /// `GrantSettings.ofVariants` of the turn's profile.
        Settings: GrantSettings
        /// The workspace's repository, as the host names it, for repository-scoped grants; `None` if
        /// it has none.
        Repo: string option
        /// The agent that asks, as requests name it.
        Agent: string
        /// How bindings map to grants: `Permissions.core`, and the rules of other bindings (network
        /// bindings: `Permissions.connects`).
        Rules: BindingRule list
    }

    override this.ToString() =
        $"grant setup ({this.Turn}, {this.Settings})"

module GrantSetup =
    /// A turn's setup with the core bindings' rules, no repository and the main agent.
    let create (grants: Grants) (sessions: EventLog<Event>) (turn: TurnStart) (settings: GrantSettings) : GrantSetup =
        {
            Grants = grants
            Sessions = sessions
            Turn = turn
            Settings = settings
            Repo = None
            Agent = "main"
            Rules = Permissions.core
        }

/// How a wait for people ended.
[<RequireQualifiedAccess>]
type private Waited =
    | Resolved of GrantNeed * RequestId * Resolution
    | Failed of GrantNeed * RequestId * GrantError
    /// A steer arrived meanwhile.
    | Steered

    override this.ToString() =
        match this with
        | Resolved(need, _, resolution) -> $"{need}: {resolution}"
        | Failed(need, _, error) -> $"{need}: {error}"
        | Steered -> "steered"

/// The grants guard (docs/modules/codeact.md, "Grants"; spec §13, §18): before a host call runs, from
/// a cell or a direct tool, what it needs (`Permissions.needs`) is checked against the grants log.
/// Allowed: it runs. Denied: it fails `denied`, which its receipt shows. Otherwise a person is asked:
/// the requests (all of the call's at once, keyed by the turn, the call and the resource) wait in
/// the grants log, the call waits for their answers, an interrupt withdraws them, and so does a steer
/// (the call fails, and the steer comes at the next boundary). A guardrail, not a boundary: the
/// sandbox is the boundary, and Windows has none in v1 (spec §18).
[<RequireQualifiedAccess>]
module GrantGuard =
    /// What `Rules.toCheck` says when nothing but the lack of a grant asks a person.
    let private noGrant = string Because.NoGrant

    let private hex (text: string) =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))

    /// The request key of a need: the turn's start event, the calling cell or tool call, the action
    /// and a digest of the resource. A retried call is the same request; another cell, another one.
    let key (turn: TurnStart) (call: HostCall) (need: GrantNeed) : string =
        let started = EventRef.create (Session.stream turn.Session) turn.Seq
        let caller = call.CellId |> Option.defaultValue "host"

        let key =
            $"{started}/{caller}/{need.Action.Value}/{(hex need.Resource).Substring(0, 16)}"

        if key.Length <= GrantLog.MaxId then
            key
        else
            $"{started}/{hex key}"

    /// Who a turn asks on behalf of: the sender of its first input.
    let private principal (turn: TurnStart) : string =
        turn.Inputs
        |> List.tryHead
        |> Option.map (fun input -> input.By.Principal)
        |> Option.filter (fun p -> not (String.IsNullOrWhiteSpace p))
        |> Option.defaultValue GrantLog.SystemPrincipal

    let private context (setup: GrantSetup) (need: GrantNeed) : GrantContext =
        {
            Session = Some setup.Turn.Session
            Run = None
            Repo = setup.Repo |> Option.map (fun repo -> { Repo = repo; Path = need.Place })
        }

    let private query (setup: GrantSetup) (need: GrantNeed) : GrantQuery =
        {
            Action = need.Action
            Resource = need.Resource
            Context = context setup need
        }

    let private ask (setup: GrantSetup) (call: HostCall) (expires: int64 option) (need: GrantNeed) : GrantAsk =
        let where =
            match call.CellId with
            | Some caller -> $"session {setup.Turn.Session}, turn {setup.Turn.Turn}, {caller}"
            | None -> $"session {setup.Turn.Session}, turn {setup.Turn.Turn}"

        {
            Key = key setup.Turn call need
            Action = need.Action
            Resource = need.Resource
            Context = context setup need
            Requester =
                {
                    Principal = principal setup.Turn
                    Agent = Some setup.Agent
                    Extension = None
                }
            Summary = need.Summary
            Evidence = need.Evidence @ [ { Label = "asked by"; Text = where } ]
            Suggestions = need.Suggestions
            Scopes = []
            Expires = expires
        }

    let private named (need: GrantNeed) =
        $"{need.Action.Value} `{need.Resource}`"

    let private denied (need: GrantNeed) (reasons: string list) : HostCallError =
        let why = String.concat "; " reasons

        HostCallError.Denied(
            $"{named need} was denied ({why}). Do not work around a denial: if you need it, say so in your reply."
        )

    let private unanswered (need: GrantNeed) (reason: string) : HostCallError =
        HostCallError.Denied($"{named need} was not run: its grant request was not answered ({reason}).")

    let private failed (need: GrantNeed) (error: GrantError) : HostCallError =
        HostCallError.Denied($"{named need} could not be checked: {error.Message}")

    let private steeredAway (needs: GrantNeed list) : HostCallError =
        let what = needs |> List.map named |> String.concat ", "

        HostCallError.Denied(
            $"{what} was not run: a message from the user arrived while it waited for permission. Read it before trying again."
        )

    let private check
        (setup: GrantSetup)
        (need: GrantNeed)
        : Eff<'K, Result<GrantCheck, GrantError>> when 'K :> IScope =
        fun env -> setup.Grants.Check (query setup need) env.Scope.Cancellation

    let private request
        (setup: GrantSetup)
        (ask: GrantAsk)
        : Eff<'K, Result<AskOutcome, GrantError>> when 'K :> IScope =
        fun env -> setup.Grants.Request ask env.Scope.Cancellation

    let private await (setup: GrantSetup) (id: RequestId) : Eff<'K, Result<Resolution, GrantError>> when 'K :> IScope =
        fun env -> setup.Grants.Await id env.Scope.Cancellation

    /// Withdraws requests, shielded from cancellation; requests resolved meanwhile stay as they are.
    let private withdraw<'K when 'K :> IScopeWith<'K>>
        (setup: GrantSetup)
        (reason: string)
        (ids: RequestId list)
        : Eff<'K, unit> =
        Eff.shield (
            eff {
                for id in ids do
                    let! _ =
                        Eff.catch (fun (env: 'K) -> setup.Grants.Withdraw id reason env.Scope.Cancellation)

                    ()
            }
        )

    /// Completes once a steer arrives in the session after `after`.
    let private steered<'K when 'K :> IScope> (setup: GrantSetup) (after: int64) : Eff<'K, unit> =
        eff {
            let stream = Session.stream setup.Turn.Session
            let mutable seq = after
            let mutable found = false

            while not found do
                let! page = EventLog.read setup.Sessions stream seq EventLog.PageSize

                match page with
                | [] ->
                    let! _ = EventLog.waitPast setup.Sessions stream seq
                    ()
                | _ ->
                    for recorded in page do
                        match recorded.Event with
                        | Event.PromptSteered _ -> found <- true
                        | _ -> ()

                    seq <- (List.last page).Seq
        }

    /// Waits until people answered every pending request (the first denial ends it), or a steer came.
    let private wait<'K when 'K :> IScopeWith<'K>>
        (setup: GrantSetup)
        (head: int64)
        (pending: (GrantNeed * RequestId) list)
        : Eff<'K, Result<unit, HostCallError>> =
        eff {
            // The requests still pending: withdrawn if the wait is cancelled.
            let left = ref pending

            let outcome =
                eff {
                    let mutable result = None

                    while result.IsNone do
                        match left.Value with
                        | [] -> result <- Some(Ok())
                        | current ->
                            let awaits =
                                current
                                |> List.map (fun (need, id) ->
                                    await setup id
                                    |> Eff.map (fun resolved ->
                                        match resolved with
                                        | Ok resolution -> Waited.Resolved(need, id, resolution)
                                        | Error e -> Waited.Failed(need, id, e)))

                            let! first =
                                Eff.race ((steered setup head |> Eff.map (fun () -> Waited.Steered)) :: awaits)

                            let others (id: RequestId) =
                                current |> List.map snd |> List.filter (fun other -> other <> id)

                            match first with
                            | Waited.Resolved(_, id, Resolution.Allowed _) ->
                                left.Value <- current |> List.filter (fun (_, other) -> other <> id)
                            | Waited.Resolved(need, id, Resolution.Denied reasons) ->
                                left.Value <- []
                                do! withdraw setup "another part of the call was denied" (others id)
                                result <- Some(Error(denied need reasons))
                            | Waited.Resolved(need, id, Resolution.Cancelled reason) ->
                                left.Value <- []
                                do! withdraw setup "another part of the call was not answered" (others id)
                                result <- Some(Error(unanswered need reason))
                            | Waited.Failed(need, id, error) ->
                                left.Value <- []
                                do! withdraw setup "the call could not be checked" (others id)
                                result <- Some(Error(failed need error))
                            | Waited.Steered ->
                                left.Value <- []
                                do! withdraw setup "a message from the user came first" (current |> List.map snd)
                                result <- Some(Error(steeredAway (current |> List.map fst)))

                    return result.Value
                }

            match! Eff.catch outcome with
            | Ok outcome -> return outcome
            | Error e ->
                // Interrupted (or the host stopped): the requests are withdrawn, the cancellation goes on.
                do! withdraw setup "the turn was interrupted" (left.Value |> List.map snd)
                return raise e
        }

    /// Asks for what the call needs: checks first (nothing is recorded for a call the rules settle),
    /// then requests what a person must decide, all at once, and waits for the answers.
    let private authorize<'K when 'K :> IClock and 'K :> IScopeWith<'K>>
        (setup: GrantSetup)
        (call: HostCall)
        (needs: GrantNeed list)
        : Eff<'K, Result<unit, HostCallError>> =
        eff {
            let mutable result: Result<unit, HostCallError> option = None
            let asking = ResizeArray<GrantNeed * string list>()

            for need in needs do
                if result.IsNone then
                    match! check setup need with
                    | Error e -> result <- Some(Error(failed need e))
                    | Ok(GrantCheck.Allowed _) -> ()
                    | Ok(GrantCheck.Denied reasons) -> result <- Some(Error(denied need reasons))
                    | Ok(GrantCheck.MustAsk reasons) when need.Listed && reasons = [ noGrant ] -> ()
                    | Ok(GrantCheck.MustAsk reasons) -> asking.Add(need, reasons)

            match result with
            | Some outcome -> return outcome
            | None when asking.Count = 0 -> return Ok()
            | None when setup.Settings.Mode = GrantMode.Deny ->
                let need, reasons = asking[0]
                let why = String.concat "; " reasons

                return
                    Error(
                        HostCallError.Denied(
                            $"{named need} needs a grant, and nobody is asked here (codeact.grants.mode is deny): {why}."
                        )
                    )
            | None ->
                let! head = EventLog.head setup.Sessions (Session.stream setup.Turn.Session)
                let! now = Clock.now

                let expires =
                    setup.Settings.Timeout
                    |> Option.map (fun timeout -> now.UnixMicroseconds + int64 timeout.TotalMicroseconds)

                let pending = ResizeArray<GrantNeed * RequestId>()

                for need, _ in asking do
                    if result.IsNone then
                        match! request setup (ask setup call expires need) with
                        | Error e -> result <- Some(Error(failed need e))
                        | Ok(AskOutcome.Settled(Resolution.Allowed _)) -> ()
                        | Ok(AskOutcome.Settled(Resolution.Denied reasons)) ->
                            result <- Some(Error(denied need reasons))
                        | Ok(AskOutcome.Settled(Resolution.Cancelled reason)) ->
                            result <- Some(Error(unanswered need reason))
                        | Ok(AskOutcome.Pending id) -> pending.Add(need, id)

                match result with
                | Some outcome ->
                    do! withdraw setup "another part of the call was refused" (pending |> Seq.map snd |> List.ofSeq)
                    return outcome
                | None -> return! wait setup head (List.ofSeq pending)
        }

    /// The guard for a turn's calls in a workplace.
    let create<'K when 'K :> IClock and 'K :> IScopeWith<'K>>
        (setup: GrantSetup)
        (place: Workplace)
        : HostCall -> Eff<'K, Result<unit, HostCallError>> =
        fun call ->
            match setup.Settings.Mode with
            | GrantMode.Off -> EffResult.ok ()
            | GrantMode.Ask
            | GrantMode.Deny ->
                match Permissions.needs setup.Rules setup.Settings place call with
                | CallNeeds.Refused reason ->
                    EffResult.error (HostCallError.Denied $"{call.Binding} was refused: {reason}.")
                | CallNeeds.Needs [] -> EffResult.ok ()
                | CallNeeds.Needs needs -> authorize setup call needs

    /// Hands whose host calls also pass the grants guard (after the hands' own guard), for a turn.
    let attach<'K when 'K :> IClock and 'K :> IScopeWith<'K>> (setup: GrantSetup) (hands: Hands<'K>) : Hands<'K> =
        let grants = create setup (Workplace.create hands.Workspace hands.Os)

        { hands with
            Guard =
                fun call ->
                    eff {
                        match! hands.Guard call with
                        | Error e -> return Error e
                        | Ok() -> return! grants call
                    }
        }
