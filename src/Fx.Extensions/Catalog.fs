namespace Fx.Extensions

open Fx.Contracts
open Fx.Sdk

/// The contribution types the host knows, by name: the core's, the host's own hook points, and the
/// types native extensions define (`ContributionTypes.contributionType`). Admission checks declared
/// contributions against it; it loads what declarative manifests and extension processes send.
[<NoEquality; NoComparison>]
type Catalog =
    {
        Types: Map<string, ContributionTypeInfo>
    }

    override this.ToString() = $"Catalog({this.Types.Count} types)"

[<RequireQualifiedAccess>]
module Catalog =
    /// The extension system's own types, and component definitions (spec §6.1) and runtimes (spec
    /// §6.2), which declarative extensions such as `fx.runtime.bun` contribute.
    let core: ContributionTypeInfo list =
        [
            ContributionType.info ExtensionRuntimes.contributionType
            ContributionType.info ContributionTypes.contributionType
            ContributionType.info ComponentDefinitions.contributionType
            ContributionType.info RuntimeSpecs.contributionType
        ]

    /// Adds a type; `Error` if another type has its name.
    let add (info: ContributionTypeInfo) (catalog: Catalog) : Result<Catalog, string> =
        if catalog.Types.ContainsKey info.Name then
            Error $"two contribution types are named {Json.quote info.Name}"
        else
            Ok
                {
                    Types = catalog.Types.Add(info.Name, info)
                }

    /// The core types and these. Two types with one name are a defect.
    let create (types: ContributionTypeInfo list) : Catalog =
        core @ types
        |> List.fold
            (fun catalog info ->
                match add info catalog with
                | Ok catalog -> catalog
                | Error message -> invalidArg (nameof types) message)
            { Types = Map.empty }

    let tryFind (name: string) (catalog: Catalog) : ContributionTypeInfo option = catalog.Types.TryFind name

    /// Loads a contribution from its declaration: its value (declarative manifests), or its settings
    /// and a remote (extension processes).
    let load (catalog: Catalog) (decl: ContributionDecl) (remote: Remote option) : Validation<Contribution, Issue> =
        match catalog.Types.TryFind decl.Type with
        | None ->
            Error
                [
                    Issue.at [ PathSegment.Key "type" ] $"this host knows no contribution type {Json.quote decl.Type}"
                ]
        | Some info ->
            info.Load decl.Id decl.Value remote
            |> Result.mapError (List.map (Issue.under (PathSegment.Key "value")))
