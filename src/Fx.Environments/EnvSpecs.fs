namespace Fx.Environments

open System.Text.RegularExpressions
open Fx.Contracts
open Fx.Sdk

/// Environment specs as data (spec §9.4): read from config files, and checked against the provider
/// that is to create them (capability negotiation) before anything is created.
[<RequireQualifiedAccess>]
module EnvSpecs =
    /// The member of a config file that holds environment specs, by name.
    [<Literal>]
    let ConfigMember = "environments"

    /// The sandbox driver that confines nothing (spec §9.1, `fx.sandbox.none`).
    [<Literal>]
    let NoSandbox = SandboxDrivers.NoSandbox

    /// The network policy that allows the network.
    [<Literal>]
    let OpenNetwork = "open"

    /// The network policy that denies the network (enforced by a sandbox driver that confines).
    [<Literal>]
    let NoNetwork = "none"

    /// What a network policy (`network`, `setup.network`) gives sandboxed processes; `None` is open.
    let networkAccess (policy: string option) : NetworkAccess =
        match policy with
        | Some NoNetwork -> NetworkAccess.Denied
        | _ -> NetworkAccess.Allowed

    let private key (name: string) = PathSegment.Key name

    /// The environment specs of a config file (`{"environments": {"<name>": {…}}}`), in the file's
    /// order; other members belong to other readers. A spec's name is its key. Issues have paths
    /// into the file.
    let ofConfig (config: Json) : Validation<EnvSpec list, Issue> =
        match config with
        | Json.Object members ->
            match List.tryFind (fun (name, _) -> name = ConfigMember) members with
            | None -> Ok []
            | Some(_, Json.Object specs) ->
                specs
                |> Validation.traverse (fun (name, body) ->
                    let decoded =
                        match body with
                        | Json.Object fields when fields |> List.exists (fun (field, _) -> field = "name") ->
                            Error
                                [
                                    Issue.at [ key "name" ] "the environment's name is its key: leave \"name\" out"
                                ]
                        | Json.Object fields ->
                            Codec.decode EnvSpec.codec (Json.Object(("name", Json.String name) :: fields))
                        | other ->
                            Error
                                [
                                    Issue.create $"expected an object (an environment spec), got {Json.describe other}"
                                ]

                    decoded
                    |> Result.mapError (List.map (Issue.under (key name) >> Issue.under (key ConfigMember))))
            | Some(_, other) ->
                Error
                    [
                        Issue.at
                            [ key ConfigMember ]
                            $"expected an object of environment specs by name, got {Json.describe other}"
                    ]
        | other -> Error [ Issue.create $"expected a config object, got {Json.describe other}" ]

    /// `ofConfig` of a config file's text.
    let parse (text: string) : Validation<EnvSpec list, Issue> =
        match Json.parse text with
        | Error e -> Error [ Issue.create $"invalid JSON: {e}" ]
        | Ok json -> ofConfig json

    /// The specs of config layers, lowest first (host, then project once it is trusted): a later
    /// layer's spec replaces an earlier one of the same name, in its place.
    let merge (layers: EnvSpec list list) : EnvSpec list =
        layers
        |> List.fold
            (fun (specs: EnvSpec list) layer ->
                layer
                |> List.fold
                    (fun specs spec ->
                        if specs |> List.exists (fun s -> s.Name = spec.Name) then
                            specs |> List.map (fun s -> if s.Name = spec.Name then spec else s)
                        else
                            specs @ [ spec ])
                    specs)
            []

    let tryFind (name: string) (specs: EnvSpec list) : EnvSpec option =
        specs |> List.tryFind (fun s -> s.Name = name)

    /// The transport the spec asks for, or the provider's default (its first).
    let transport (provider: EnvProvider) (spec: EnvSpec) : TransportKind =
        match spec.Transport with
        | Some transport -> transport
        | None ->
            match provider.Caps.Transports with
            | first :: _ -> first
            | [] -> TransportKind.Piped

    /// Capability negotiation: every reason the provider cannot create the spec, with paths into the
    /// spec. Empty when it can.
    let check (provider: EnvProvider) (spec: EnvSpec) : Issue list =
        let caps = provider.Caps
        let at (path: string list) (message: string) = Issue.at (path |> List.map key) message
        let transport = transport provider spec

        [
            if spec.Provider <> provider.Kind then
                at [ "provider" ] $"expected {Json.quote provider.Kind}, the provider asked to create it"

            match provider.Config.Decode spec.Config with
            | Ok _ -> ()
            | Error issues -> yield! issues |> List.map (Issue.under (key "config"))

            if not (List.contains transport caps.Transports) then
                let offered = caps.Transports |> List.map string |> Issue.quoteAll
                at [ "transport" ] $"{Json.quote (string transport)}: the {provider.Kind} provider offers {offered}"

            if transport = TransportKind.Direct || transport = TransportKind.Dialback then
                at [ "transport" ] $"{Json.quote (string transport)}: not supported by this host yet (T501, T504)"

            if not (List.contains spec.Workspace.Source caps.Materialize) then
                let offered = caps.Materialize |> List.map string |> Issue.quoteAll

                at
                    [ "workspace"; "source" ]
                    $"{Json.quote (string spec.Workspace.Source)}: the {provider.Kind} provider materializes {offered}"

            if spec.Workspace.Export.IsSome then
                at [ "workspace"; "export" ] "exporting work is not supported yet (T503)"

            match spec.Sandbox with
            | Some driver when not (Regex.IsMatch(driver, SandboxDrivers.IdPattern)) || driver = "auto" ->
                at
                    [ "sandbox" ]
                    $"{Json.quote driver}: expected a sandbox driver's id (lowercase letters, digits and \"-\")"
            | _ -> ()

            match spec.Network with
            | None
            | Some OpenNetwork
            | Some NoNetwork -> ()
            | Some policy ->
                at
                    [ "network" ]
                    $"{Json.quote policy}: expected \"open\" or \"none\"; allowlists need the egress proxy (not yet)"

            match spec.Setup.Network with
            | None
            | Some OpenNetwork
            | Some NoNetwork -> ()
            | Some policy ->
                at
                    [ "setup"; "network" ]
                    $"{Json.quote policy}: expected \"open\" or \"none\"; allowlists need the egress proxy (not yet)"

            if not spec.Setup.Secrets.IsEmpty then
                at [ "setup"; "secrets" ] "secrets for setup need the egress proxy's credential injection (not yet)"

            match spec.Components.Placement with
            | ComponentPlacement.EnvFetch -> ()
            | other -> at [ "components"; "placement" ] $"{Json.quote (string other)}: not supported yet (T503)"
        ]

    /// Whether the spec's sandbox driver is one of those in effect (by id). Whether it runs on the
    /// environment's machine, and whether this host lets a spec choose it, is decided when the
    /// environment connects (`EnvSandbox.choose`).
    let checkSandbox (drivers: string list) (spec: EnvSpec) : Issue list =
        match spec.Sandbox with
        | Some driver when not (List.contains driver drivers) ->
            let known = drivers |> Issue.quoteAll

            [
                Issue.at
                    [ key "sandbox" ]
                    $"{Json.quote driver}: no such sandbox driver is in effect (the host has {known})"
            ]
        | _ -> []
