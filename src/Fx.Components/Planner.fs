namespace Fx.Components

open System
open Fx.Contracts
open Fx.Sdk

/// A component someone needs: the reference and who asked (an extension id, `project`, …), for
/// error messages.
type Requirement =
    {
        Ref: ComponentRef
        By: string
    }

    override this.ToString() = $"{this.Ref} (required by {this.By})"

/// Why the planner could not write a lockfile. Each says what to change.
type PlanError =
    /// A version range that does not parse.
    | InvalidRange of requirement: Requirement * reason: string
    /// No definition of the component at all.
    | UnknownComponent of id: string * requiredBy: string list
    /// No version of the component is in every range asked for.
    | NoVersion of id: string * constraints: Requirement list * available: string list
    /// Two different definitions of one version.
    | Conflicting of id: string * version: string
    /// The chosen version cannot be installed on a target.
    | NoMethod of id: string * version: string * target: TargetTriple * reason: string
    /// The chosen versions depend on each other in a cycle.
    | Cycle of ids: string list
    /// Resolution did not settle (dependencies of the chosen versions keep changing the choice).
    | Unresolved of ids: string list

    override this.ToString() =
        match this with
        | InvalidRange(requirement, reason) -> $"{requirement}: {reason}"
        | UnknownComponent(id, by) ->
            let by = String.Join(", ", by)
            $"no component {id} is defined (required by {by})"
        | NoVersion(id, constraints, available) ->
            let asked = constraints |> List.map string |> String.concat "; "

            let available =
                if available.IsEmpty then
                    "none"
                else
                    String.Join(", ", available)

            $"no version of {id} satisfies every requirement: {asked}; available: {available}"
        | Conflicting(id, version) -> $"{id} {version} is defined twice, differently"
        | NoMethod(id, version, target, reason) -> $"{id} {version} cannot be installed on {target}: {reason}"
        | Cycle ids ->
            let ids = String.Join(" -> ", ids)
            $"components depend on each other in a cycle: {ids}"
        | Unresolved ids ->
            let ids = String.Join(", ", ids)
            $"the versions of {ids} did not settle; pin them in the requirements"

/// What the planner may choose.
type PlanSettings =
    {
        /// The install methods (their JSON `kind`s) the installer implements. Others are skipped
        /// when choosing a method; a component with nothing else cannot be planned.
        Methods: string list
    }

    override this.ToString() =
        $"""plan settings (methods: {String.Join(", ", this.Methods)})"""

module PlanSettings =
    /// The methods the installer implements today: `archive` (T211 adds the others).
    let defaults: PlanSettings = { Methods = [ "archive" ] }

/// The component planner (spec §6.1, host side; pure): resolves requirements against component
/// definitions and writes the lockfile.
///
/// - For each component, the highest version that is in every range asked for, by the
///   requirements and by the dependencies of the versions chosen for other components, repeated
///   until the choice settles. When the dependencies of a chosen version leave a component with no
///   version, that version is set aside and a lower one tried.
/// - For each target, the first method of the chosen version that is for the target and that the
///   installer implements; URL templates are expanded.
/// - The same inputs, in any order, give the same lockfile, byte for byte.
module Planner =

    let private semverText (versions: SemVer list) =
        versions |> List.sortWith SemVer.compare |> List.map string

    /// The definitions of each component, by version, without duplicates; conflicting duplicates are
    /// errors.
    let private index (catalog: Component list) : Map<string, Component list> * PlanError list =
        let groups = catalog |> List.groupBy (fun c -> c.Id)

        let mutable errors = []

        let byId =
            groups
            |> List.map (fun (id, definitions) ->
                let versions =
                    definitions
                    |> List.groupBy (fun c -> string c.Version)
                    |> List.map (fun (version, same) ->
                        let texts =
                            same
                            |> List.map (fun c -> Json.serializeCanonical (Component.codec.Encode c))
                            |> List.distinct

                        if texts.Length > 1 then
                            errors <- Conflicting(id, version) :: errors

                        List.head same)
                    |> List.sortWith (fun a b -> SemVer.compare b.Version a.Version)

                id, versions)
            |> Map.ofList

        byId, List.rev errors

    let private satisfiesAll (ranges: (VersionRange * Requirement) list) (version: SemVer) =
        ranges |> List.forall (fun (range, _) -> VersionRange.satisfies range version)

    /// The method of a component for a target, with its URL expanded.
    let private methodFor
        (settings: PlanSettings)
        (definition: Component)
        (target: TargetTriple)
        : Result<InstallMethod, PlanError> =
        let fail reason =
            Error(NoMethod(definition.Id, string definition.Version, target, reason))

        if
            not definition.Platforms.IsEmpty
            && not (List.contains target definition.Platforms)
        then
            fail "it does not support that target"
        else
            let forTarget =
                definition.Methods
                |> List.filter (fun m -> m.Platforms.IsEmpty || List.contains target m.Platforms)

            match
                forTarget
                |> List.tryFind (fun m -> List.contains m.Method.Kind settings.Methods)
            with
            | None when forTarget.IsEmpty -> fail "it has no install method for that target"
            | None ->
                let kinds =
                    forTarget
                    |> List.map (fun m -> m.Method.Kind)
                    |> List.distinct
                    |> Issue.quoteAll

                fail $"its methods for that target ({kinds}) are not implemented by this installer yet (T211)"
            | Some choice ->
                let values = [ "version", string definition.Version ]

                let expand (url: string) =
                    match Component.expand values url with
                    | Ok url -> Ok url
                    | Error e -> fail e

                match choice.Method with
                | Archive spec -> expand spec.Url |> Result.map (fun url -> Archive { spec with Url = url })
                | Script spec -> expand spec.Url |> Result.map (fun url -> Script { spec with Url = url })
                | other -> Ok other

    /// Resolves the requirements against the definitions for the targets.
    let plan
        (settings: PlanSettings)
        (catalog: Component list)
        (requires: Requirement list)
        (targets: TargetTriple list)
        : Result<Lockfile, PlanError list> =
        let definitions, conflicts = index catalog
        let targets = targets |> List.distinct |> List.sort

        let parsed =
            requires
            |> List.map (fun r ->
                match VersionRange.parse r.Ref.Version with
                | Ok range -> Ok(r.Ref.Id, (range, r))
                | Error e -> Error(InvalidRange(r, e)))

        let invalid =
            parsed
            |> List.choose (function
                | Error e -> Some e
                | Ok _ -> None)

        if not invalid.IsEmpty then
            Error invalid
        else
            let roots =
                parsed
                |> List.choose (function
                    | Ok pair -> Some pair
                    | Error _ -> None)

            /// The constraints of the roots and of the dependencies of chosen versions.
            let constraints
                (chosen: Map<string, Component>)
                : Result<Map<string, (VersionRange * Requirement) list>, PlanError list> =
                let fromDependencies =
                    chosen
                    |> Map.toList
                    |> List.collect (fun (_, c) ->
                        c.DependsOn
                        |> List.map (fun d ->
                            let requirement = { Ref = d; By = $"{c.Id} {c.Version}" }

                            match VersionRange.parse d.Version with
                            | Ok range -> Ok(d.Id, (range, requirement))
                            | Error e -> Error(InvalidRange(requirement, e))))

                match
                    fromDependencies
                    |> List.choose (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | [] ->
                    let all =
                        roots
                        @ (fromDependencies
                           |> List.choose (function
                               | Ok pair -> Some pair
                               | Error _ -> None))

                    Ok(
                        all
                        |> List.groupBy fst
                        |> List.map (fun (id, pairs) -> id, List.map snd pairs)
                        |> Map.ofList
                    )
                | errors -> Error errors

            let choose
                (excluded: Set<string * string>)
                (constraints: Map<string, (VersionRange * Requirement) list>)
                : Result<Map<string, Component>, PlanError list> =
                let picks =
                    constraints
                    |> Map.toList
                    |> List.map (fun (id, ranges) ->
                        match definitions.TryFind id with
                        | None ->
                            Error(
                                UnknownComponent(
                                    id,
                                    ranges |> List.map (fun (_, r) -> r.By) |> List.distinct |> List.sort
                                )
                            )
                        | Some versions ->
                            let allowed =
                                versions
                                |> List.filter (fun c -> not (excluded.Contains(c.Id, string c.Version)))

                            match allowed |> List.tryFind (fun c -> satisfiesAll ranges c.Version) with
                            | Some c -> Ok(id, c)
                            | None ->
                                let asked =
                                    ranges
                                    |> List.map snd
                                    |> List.distinct
                                    |> List.sortBy (fun r -> r.By, r.Ref.Version)

                                Error(NoVersion(id, asked, semverText (versions |> List.map (fun c -> c.Version)))))

                match
                    picks
                    |> List.choose (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | [] ->
                    Ok(
                        picks
                        |> List.choose (function
                            | Ok pair -> Some pair
                            | Error _ -> None)
                        |> Map.ofList
                    )
                | errors -> Error errors

            // Choose until the choice settles. Each round adds the dependencies of the last choice;
            // a choice can only change when a dependency adds a constraint, so this is bounded. When
            // no version of a component fits and a chosen version's dependencies are to blame, that
            // version is excluded and resolution starts again (so a lower version of the dependent
            // can be found); each exclusion removes a definition, so this ends too.
            let limit = (definitions |> Map.toList |> List.sumBy (fun (_, v) -> v.Length)) + 2
            let mutable excluded = Set.empty
            // The conflict that started the exclusions: reported if they do not help.
            let mutable first = None
            let mutable chosen = Map.empty
            let mutable outcome = None
            let mutable round = 0

            while outcome.IsNone do
                round <- round + 1

                match constraints chosen |> Result.bind (choose excluded) with
                | Error errors ->
                    let blamed =
                        errors
                        |> List.collect (function
                            | NoVersion(id, _, _) ->
                                chosen
                                |> Map.toList
                                |> List.filter (fun (_, c) -> c.DependsOn |> List.exists (fun d -> d.Id = id))
                                |> List.map (fun (_, c) -> c.Id, string c.Version)
                            | _ -> [])
                        |> List.distinct
                        |> List.sort

                    match blamed with
                    | culprit :: _ when not (excluded.Contains culprit) ->
                        if first.IsNone then
                            first <- Some errors

                        excluded <- excluded.Add culprit
                        chosen <- Map.empty
                        round <- 0
                    | _ -> outcome <- Some(Error(Option.defaultValue errors first))
                | Ok next when next = chosen -> outcome <- Some(Ok chosen)
                | Ok next when round > limit ->
                    let changed =
                        next
                        |> Map.toList
                        |> List.filter (fun (id, c) -> chosen.TryFind id <> Some c)
                        |> List.map fst

                    outcome <- Some(Error [ Unresolved changed ])
                | Ok next -> chosen <- next

            match outcome with
            | Some(Error errors) -> Error(conflicts @ errors)
            | None -> Error [ Unresolved [] ]
            | Some(Ok chosen) ->
                let conflicts =
                    conflicts
                    |> List.filter (function
                        | Conflicting(id, version) ->
                            match chosen.TryFind id with
                            | Some c -> string c.Version = version
                            | None -> false
                        | _ -> true)

                // Cycles among the chosen versions.
                let cycle =
                    let mutable found = None
                    let mutable finished = Set.empty

                    let rec visit (path: string list) (id: string) =
                        if found.IsNone && not (finished.Contains id) then
                            if List.contains id path then
                                // `path` is innermost first: a -> b -> c, back to a, is [c; b; a].
                                let between = List.rev (List.takeWhile (fun x -> x <> id) path)
                                found <- Some(Cycle(id :: between @ [ id ]))
                            else
                                match chosen.TryFind id with
                                | Some c ->
                                    for d in c.DependsOn |> List.map (fun d -> d.Id) |> List.sort do
                                        visit (id :: path) d
                                | None -> ()

                                finished <- finished.Add id

                    for id in chosen |> Map.toList |> List.map fst do
                        visit [] id

                    found

                let locked =
                    chosen
                    |> Map.toList
                    |> List.map (fun (_, c) ->
                        let installs =
                            targets
                            |> List.map (fun t -> methodFor settings c t |> Result.map (fun m -> t, m))

                        match
                            installs
                            |> List.choose (function
                                | Error e -> Some e
                                | Ok _ -> None)
                        with
                        | [] ->
                            Ok
                                {
                                    Id = c.Id
                                    Version = c.Version
                                    DependsOn = c.DependsOn |> List.map (fun d -> d.Id) |> List.distinct |> List.sort
                                    Install =
                                        installs
                                        |> List.choose (function
                                            | Ok pair -> Some pair
                                            | Error _ -> None)
                                    Layout = c.Layout
                                    Exports = c.Exports
                                    Probe = c.Probe
                                    Size = c.Size
                                }
                        | errors -> Error errors)

                let errors =
                    conflicts
                    @ Option.toList cycle
                    @ (locked
                       |> List.collect (function
                           | Error e -> e
                           | Ok _ -> []))

                if not errors.IsEmpty then
                    Error errors
                else
                    Ok
                        {
                            Format = Lockfile.FormatVersion
                            Requires = Lockfile.normalizeRequires (requires |> List.map (fun r -> r.Ref))
                            Targets = targets
                            Components =
                                locked
                                |> List.choose (function
                                    | Ok c -> Some c
                                    | Error _ -> None)
                                |> List.sortWith (fun a b -> String.CompareOrdinal(a.Id, b.Id))
                        }
