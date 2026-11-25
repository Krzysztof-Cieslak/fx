namespace Fx.Host

open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Sdk
open Fx.Environments

/// A config file the host read: the host's (`<data>/config.json`) or a trusted project's
/// (`.fx/config.json`). JSON, read strictly: unknown members are issues, so a typo never goes unseen.
type ConfigFile =
    {
        /// Where it came from, for every value it selects (`Source.Origin`).
        Origin: string
        /// The `variants` member as a layer.
        Variants: Selections
        /// The `environments` member: specs by name.
        Environments: EnvSpec list
        /// `extensions.disabled`: extensions host config turns off (host config only).
        Disabled: string list
    }

    override this.ToString() = $"config {this.Origin}"

[<RequireQualifiedAccess>]
module ConfigFile =
    /// The members a host config file may have.
    let hostMembers = [ "variants"; "environments"; "extensions" ]

    /// The members a project's config file may have: no extensions until project extensions load (they
    /// are refused before trust anyway).
    let projectMembers = [ "variants"; "environments" ]

    let private disabledCodec: Codec<string list> =
        Codec.record {
            let! disabled = Codec.defaultField "disabled" id [] (Codec.list Codec.string)
            return disabled
        }

    /// An empty config of a layer (no file).
    let empty (layer: Layer) (origin: string) : ConfigFile =
        {
            Origin = origin
            Variants = Selections.create (Source.create layer origin) []
            Environments = []
            Disabled = []
        }

    /// A config file's JSON for a layer (`Host` or `Project`), or its issues with paths into the file.
    let ofJson (layer: Layer) (origin: string) (json: Json) : Validation<ConfigFile, Issue> =
        let allowed =
            if layer = Layer.Project then
                projectMembers
            else
                hostMembers

        match json with
        | Json.Object members ->
            let unknown =
                members
                |> List.filter (fun (name, _) -> not (List.contains name allowed))
                |> List.map (fun (name, _) ->
                    Issue.at [ PathSegment.Key name ] $"unknown member; a config file has {Issue.quoteAll allowed}")

            let variants = Selections.ofConfig (Source.create layer origin) json
            let environments = EnvSpecs.ofConfig json

            let disabled =
                match Json.tryField "extensions" json with
                | None
                | Some Json.Null -> Ok []
                | Some extensions ->
                    Codec.decode disabledCodec extensions
                    |> Result.mapError (List.map (Issue.under (PathSegment.Key "extensions")))

            let issues =
                unknown
                @ (match variants with
                   | Error issues -> issues
                   | Ok _ -> [])
                @ (match environments with
                   | Error issues -> issues
                   | Ok _ -> [])
                @ (match disabled with
                   | Error issues -> issues
                   | Ok _ -> [])

            match variants, environments, disabled with
            | Ok variants, Ok environments, Ok disabled when issues.IsEmpty ->
                Ok
                    {
                        Origin = origin
                        Variants = variants
                        Environments = environments
                        Disabled = disabled
                    }
            | _ -> Error issues
        | other -> Error [ Issue.expected "an object" other ]

    /// Reads a config file through the filesystem: an empty config if there is none, its issues
    /// (prefixed with the file) if it does not read.
    let read<'R when 'R :> IFsRead and 'R :> IScope>
        (layer: Layer)
        (path: string)
        : Eff<'R, Result<ConfigFile, string list>> =
        eff {
            match! Fs.readText path with
            | Error(FsError.NotFound _) -> return Ok(empty layer path)
            | Error e -> return Error [ $"{path}: {e}" ]
            | Ok text ->
                match Json.parse text with
                | Error e -> return Error [ $"{path}: {e}" ]
                | Ok json ->
                    match ofJson layer path json with
                    | Ok config -> return Ok config
                    | Error issues -> return Error(issues |> List.map (fun issue -> $"{path}: {issue}"))
        }

/// The host's policy file: pins and forbids that only tighten (`Policy.ofJson`).
[<RequireQualifiedAccess>]
module PolicyFile =
    /// Reads a policy file: no policy if there is none.
    let read<'R when 'R :> IFsRead and 'R :> IScope> (path: string) : Eff<'R, Result<Policy list, string list>> =
        eff {
            match! Fs.readText path with
            | Error(FsError.NotFound _) -> return Ok []
            | Error e -> return Error [ $"{path}: {e}" ]
            | Ok text ->
                match Json.parse text with
                | Error e -> return Error [ $"{path}: {e}" ]
                | Ok json ->
                    match Policy.ofJson path json with
                    | Ok policy -> return Ok [ policy ]
                    | Error issues -> return Error(issues |> List.map (fun issue -> $"{path}: {issue}"))
        }
